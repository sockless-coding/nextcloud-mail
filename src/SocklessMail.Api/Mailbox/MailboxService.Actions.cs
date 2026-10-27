using MailKit;
using MailKit.Net.Imap;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// Changes to the mailbox. These run without the reconnect-and-retry of reads: if a connection
/// dies halfway through a move, repeating it blindly could act twice.
/// </summary>
public sealed partial class MailboxService
{
    public const int MaxMessagesPerAction = 500;
    public const int MaxFolderNameLength = 200;

    public async Task<int> SetFlagsAsync(MailAccount account, string folderId, FlagsInput input, CancellationToken ct)
    {
        if (input.Seen is null && input.Flagged is null)
        {
            throw new MailboxValidationException("seen", "Set seen, flagged or both.");
        }

        var (uidValidity, uids) = await Run(account, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderId, token, FolderAccess.ReadWrite);
            var uids = ParseMessageIds(folder, input.MessageIds);

            async Task Store(MessageFlags flag, bool? set)
            {
                if (set is { } value)
                {
                    await folder.StoreAsync(uids, new StoreFlagsRequest(value ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true }, token);
                }
            }

            await Store(MessageFlags.Seen, input.Seen);
            await Store(MessageFlags.Flagged, input.Flagged);
            return (folder.UidValidity, uids);
        }, ct, retry: false);

        var fullName = FullNameOf(folderId);
        await UpdateCacheAsync(account, fullName, () =>
            cache.ApplyFlagsAsync(account.Id, fullName, uidValidity, uids.Select(u => u.Id).ToList(), input.Seen, input.Flagged, CancellationToken.None));
        return uids.Count;
    }

    public async Task<int> MoveAsync(MailAccount account, string folderId, MoveInput input, CancellationToken ct)
    {
        var (uidValidity, uids) = await Run(account, async (client, token) =>
        {
            if (string.IsNullOrEmpty(input.TargetFolderId) || input.TargetFolderId == folderId)
            {
                throw new MailboxValidationException("targetFolderId", "Choose a different folder.");
            }

            var target = await FindFolderAsync(client, input.TargetFolderId, token);
            if (!IsSelectable(target))
            {
                throw new MailboxValidationException("targetFolderId", "Messages cannot be moved into this folder.");
            }

            var folder = await OpenFolderAsync(client, folderId, token, FolderAccess.ReadWrite);
            var uids = ParseMessageIds(folder, input.MessageIds);
            // MOVE where supported; MailKit falls back to COPY + \Deleted + (UID) EXPUNGE.
            await folder.MoveToAsync(uids, target, token);
            return (folder.UidValidity, uids);
        }, ct, retry: false);

        await RemoveFromCacheAsync(account, folderId, uidValidity, uids);
        return uids.Count;
    }

    /// <summary>The target folder's cache needs nothing: its next sync finds the new UIDs.</summary>
    private Task RemoveFromCacheAsync(MailAccount account, string folderId, uint uidValidity, List<UniqueId> uids)
    {
        var fullName = FullNameOf(folderId);
        return UpdateCacheAsync(account, fullName, () =>
            cache.RemoveAsync(account.Id, fullName, uidValidity, uids.Select(u => u.Id).ToList(), CancellationToken.None));
    }

    public async Task<DeleteResult> DeleteAsync(MailAccount account, string folderId, DeleteInput input, CancellationToken ct)
    {
        var (result, uidValidity, uids) = await Run(account, async (client, token) =>
        {
            var trash = await FindSpecialFolderAsync(client, "trash", token);
            var inTrash = trash is not null && MailboxIds.FolderId(trash.FullName) == folderId;
            if (!input.Permanent && !inTrash && trash is null)
            {
                throw new MailboxConflictException("no_trash", "This account has no Trash folder. Delete the messages permanently?");
            }

            var folder = await OpenFolderAsync(client, folderId, token, FolderAccess.ReadWrite);
            var uids = ParseMessageIds(folder, input.MessageIds);
            if (input.Permanent || inTrash)
            {
                await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, token);
                if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                {
                    await folder.ExpungeAsync(uids, token);
                }
                else
                {
                    // Without UIDPLUS, EXPUNGE also removes other messages already marked \Deleted
                    // in this folder (by another client); there is no narrower command.
                    await folder.ExpungeAsync(token);
                }

                return (new DeleteResult(false, null), folder.UidValidity, uids);
            }

            await folder.MoveToAsync(uids, trash!, token);
            return (new DeleteResult(true, MailboxIds.FolderId(trash!.FullName)), folder.UidValidity, uids);
        }, ct, retry: false);

        await RemoveFromCacheAsync(account, folderId, uidValidity, uids);
        return result;
    }

    public Task<FolderDto> CreateFolderAsync(MailAccount account, CreateFolderInput input, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var parent = input.ParentId is { Length: > 0 } parentId
                ? await FindFolderAsync(client, parentId, token)
                : client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', ""));
            var name = ValidateFolderName(input.Name, parent.DirectorySeparator);
            await EnsureFolderDoesNotExistAsync(client, parent, name, token);

            var created = await parent.CreateAsync(name, isMessageFolder: true, token)
                          ?? throw new MailboxConflictException("folder_exists", $"A folder named \"{name}\" already exists here.");
            await created.SubscribeAsync(token);
            return await ToDtoWithStatusAsync(created, token);
        }, ct, retry: false);

    public Task<FolderDto> RenameFolderAsync(MailAccount account, string folderId, RenameFolderInput input, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var folder = await FindFolderAsync(client, folderId, token);
            if (IsInbox(folder.FullName))
            {
                throw new MailboxValidationException("name", "The inbox cannot be renamed.");
            }

            var parent = folder.ParentFolder ?? client.GetFolder(client.PersonalNamespaces[0]);
            var name = ValidateFolderName(input.Name, folder.DirectorySeparator);
            if (name == folder.Name)
            {
                return await ToDtoWithStatusAsync(folder, token);
            }

            await EnsureFolderDoesNotExistAsync(client, parent, name, token);
            // A selected folder cannot be renamed on some servers; this connection may still have it open.
            if (folder.IsOpen)
            {
                await folder.CloseAsync(false, token);
            }

            await folder.RenameAsync(parent, name, token);
            return await ToDtoWithStatusAsync(folder, token);
        }, ct, retry: false);

    /// <summary>
    /// Message ids of one folder as UIDs. Ids from before the folder was renumbered (another
    /// UIDVALIDITY) are refused as a whole: acting on them would hit different messages.
    /// </summary>
    private static List<UniqueId> ParseMessageIds(IMailFolder folder, IReadOnlyList<string>? messageIds)
    {
        if (messageIds is null || messageIds.Count == 0)
        {
            throw new MailboxValidationException("messageIds", "Select at least one message.");
        }

        if (messageIds.Count > MaxMessagesPerAction)
        {
            throw new MailboxValidationException("messageIds", $"At most {MaxMessagesPerAction} messages at once.");
        }

        var uids = new List<UniqueId>(messageIds.Count);
        foreach (var id in messageIds.Distinct())
        {
            if (!MailboxIds.TryParseMessageId(id, out var uidValidity, out var uid))
            {
                throw new MailboxValidationException("messageIds", $"\"{id}\" is not a message id.");
            }

            if (uidValidity != folder.UidValidity)
            {
                throw new MailboxConflictException("stale_messages", "The folder has changed on the server. Reload it and try again.");
            }

            uids.Add(new UniqueId(uidValidity, uid));
        }

        return uids;
    }

    private static string ValidateFolderName(string? name, char separator)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new MailboxValidationException("name", "Enter a folder name.");
        }

        if (trimmed.Length > MaxFolderNameLength)
        {
            throw new MailboxValidationException("name", $"Use at most {MaxFolderNameLength} characters.");
        }

        if (trimmed.Contains(separator) || trimmed.Any(char.IsControl) || trimmed.Contains('*') || trimmed.Contains('%'))
        {
            throw new MailboxValidationException("name", $"The name cannot contain \"{separator}\", \"*\" or \"%\".");
        }

        if (trimmed.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            throw new MailboxValidationException("name", "This name is reserved.");
        }

        return trimmed;
    }

    private static async Task EnsureFolderDoesNotExistAsync(ImapClient client, IMailFolder parent, string name, CancellationToken ct)
    {
        var fullName = parent.FullName.Length == 0 ? name : $"{parent.FullName}{parent.DirectorySeparator}{name}";
        try
        {
            await client.GetFolderAsync(fullName, ct);
        }
        catch (FolderNotFoundException)
        {
            return;
        }

        throw new MailboxConflictException("folder_exists", $"A folder named \"{name}\" already exists here.");
    }

    /// <summary>RFC 6154 special-use folder; by common name when the server does not announce them.</summary>
    private static async Task<IMailFolder?> FindSpecialFolderAsync(ImapClient client, string use, CancellationToken ct)
    {
        if (use == "trash" && client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) && client.GetFolder(SpecialFolder.Trash) is { } announced)
        {
            return announced;
        }

        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = await client.GetFoldersAsync(personal, subscribedOnly: false, cancellationToken: ct);
        return folders.FirstOrDefault(f => FolderOrdering.SpecialUse(f) == use && IsSelectable(f));
    }

    private static async Task<FolderDto> ToDtoWithStatusAsync(IMailFolder folder, CancellationToken ct)
    {
        if (IsSelectable(folder))
        {
            await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);
        }

        return ToDto(folder);
    }
}
