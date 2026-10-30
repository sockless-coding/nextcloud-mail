using MailKit;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Sync;
using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Mailbox;

/// <summary>Add and remove tags (by tag id); a tag may not be in both.</summary>
public sealed record TagChangeInput(IReadOnlyList<string>? MessageIds, IReadOnlyList<Guid>? Add, IReadOnlyList<Guid>? Remove);

/// <summary>The messages' keywords after the change, by message id.</summary>
public sealed record TagChangeResult(IReadOnlyDictionary<string, string[]> Keywords);

public sealed partial class MailboxService
{
    /// <summary>
    /// Adds and removes tags. Microsoft accounts with Graph consent keep them as Outlook categories
    /// (<see cref="OutlookCategories"/>). Otherwise, where the server keeps keywords they are stored
    /// there. When the account's first tagged folder turns out not to (PERMANENTFLAGS without \*),
    /// the account switches to tags the app keeps itself. Either way the tag index and the cache
    /// follow at once.
    /// </summary>
    public async Task<TagChangeResult> SetTagsAsync(MailAccount account, string folderId, TagChangeInput input, CancellationToken ct)
    {
        var tags = await tagStore.ListAsync(account.OwnerUserId, ct);
        string[] KeywordsOf(IReadOnlyList<Guid>? ids, string field) => (ids ?? []).Distinct()
            .Select(id => tags.FirstOrDefault(t => t.Id == id)?.Keyword ?? throw new MailboxValidationException(field, "There is no such tag."))
            .ToArray();
        var add = KeywordsOf(input.Add, "add");
        var remove = KeywordsOf(input.Remove, "remove");
        if (add.Length + remove.Length == 0)
        {
            throw new MailboxValidationException("add", "Add or remove at least one tag.");
        }

        if (add.Intersect(remove, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new MailboxValidationException("remove", "A tag cannot be added and removed at once.");
        }

        return await ChangeKeywordsAsync(account, folderId, input.MessageIds, add, remove, ct);
    }

    /// <summary>Adds and removes keywords of tags (see <see cref="SetTagsAsync"/>); also for tags being deleted.</summary>
    public async Task<TagChangeResult> ChangeKeywordsAsync(MailAccount account, string folderId, IReadOnlyList<string>? messageIds,
        string[] add, string[] remove, CancellationToken ct)
    {
        var storage = await outlook.DecideAsync(account, ct);
        var (uidValidity, summaries, onServer) = await Run(account, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderId, token, FolderAccess.ReadWrite);
            var uids = ParseMessageIds(folder, messageIds);
            var onServer = !storage.IsAppKept() && AcceptsKeywords(folder, add.Concat(remove));
            if (!onServer && storage == TagStorage.Keywords)
            {
                // The account's other folders keep keywords; one that does not (read-only, shared) cannot take tags.
                throw new MailboxConflictException("tags_unsupported", "This folder cannot keep tags.");
            }

            if (onServer)
            {
                if (add.Length > 0)
                {
                    await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, add) { Silent = true }, token);
                }

                if (remove.Length > 0)
                {
                    await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Remove, remove) { Silent = true }, token);
                }
            }

            var summaries = await folder.FetchAsync(uids, FolderCache.ListRequest(), token);
            return (folder.UidValidity, summaries, onServer);
        }, ct, retry: false);

        if (!onServer && !storage.IsAppKept())
        {
            logger.LogInformation("Account {AccountId}: the server keeps no keywords; tags are kept by the app", account.Id);
            await tagIndex.SetStorageAsync(account.Id, TagStorage.Local, ct);
        }
        else if (onServer && storage == TagStorage.Unknown)
        {
            await tagIndex.SetStorageAsync(account.Id, TagStorage.Keywords, ct);
        }

        var fullName = FullNameOf(folderId);
        var keywords = new Dictionary<uint, string>();
        // The index is the record for app-kept tags, so it is written under the folder's sync lock
        // (a sync claims rows) and its failure fails the request.
        using (await syncLocks.AcquireAsync(account.Id, fullName, ct))
        {
            var current = onServer
                ? summaries.ToDictionary(s => s.UniqueId.Id, s => Keywords.Format(s.Keywords))
                : await tagIndex.KeywordsAsync(account.Id, fullName, uidValidity, summaries.Select(s => s.UniqueId.Id).ToList(), ct);
            foreach (var summary in summaries)
            {
                var uid = summary.UniqueId.Id;
                keywords[uid] = onServer ? current[uid] : Keywords.Change(current.GetValueOrDefault(uid), add, remove);
            }

            if (storage == TagStorage.Graph)
            {
                // Outlook first: when Graph refuses, nothing changes here either.
                await outlook.ApplyAsync(account,
                    summaries.Select(s => TagIndex.MessageIdOf(s.Envelope?.MessageId)).OfType<string>(), add, remove, ct);
            }

            await tagIndex.RecordAsync(account, fullName, uidValidity,
                summaries.Select(s => (s, keywords[s.UniqueId.Id], bimi.Find(s))), ct);
        }

        await UpdateCacheAsync(account, fullName, () =>
            cache.ApplyKeywordsAsync(account.Id, fullName, uidValidity, keywords, CancellationToken.None));
        return new TagChangeResult(keywords.ToDictionary(k => MailboxIds.MessageId(uidValidity, k.Key), k => Keywords.ForClient(k.Value)));
    }

    /// <summary>
    /// Whether the open folder stores these keywords: \* in PERMANENTFLAGS allows new ones; without
    /// a PERMANENTFLAGS answer at all, everything may be stored (RFC 3501).
    /// </summary>
    internal static bool AcceptsKeywords(IMailFolder folder, IEnumerable<string> keywords) =>
        folder.PermanentFlags == MessageFlags.None
        || folder.PermanentFlags.HasFlag(MessageFlags.UserDefined)
        || keywords.All(k => folder.PermanentKeywords.Contains(k));
}
