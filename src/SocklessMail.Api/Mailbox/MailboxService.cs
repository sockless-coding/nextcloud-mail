using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MimeKit.Text;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Mailbox;

/// <summary>A message part ready to stream to the client after the IMAP connection was returned.</summary>
public sealed record PartContent(string FileName, string ContentType, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>
/// Read access to an account's mailbox over pooled IMAP connections. Callers pass an account
/// that was already looked up for the current user (<see cref="AccountService.GetAsync"/>), which
/// is what scopes every operation here to its owner. Folders are opened read-only (EXAMINE), and
/// bodies are fetched with BODY.PEEK, so reading does not change \Seen; marking messages as read
/// is an explicit action.
/// </summary>
public sealed partial class MailboxService(
    ImapConnections imap, CredentialProtector credentials, ImageProxyTokens proxyTokens,
    MailServerConnector connector, AttachmentUploads uploads, TimeProvider clock, ILogger<MailboxService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private const MessageSummaryItems ListItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
        | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure;

    public Task<IReadOnlyList<FolderDto>> GetFoldersAsync(MailAccount account, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            IList<IMailFolder> folders;
            try
            {
                folders = await client.GetFoldersAsync(personal, StatusItems.Count | StatusItems.Unread, subscribedOnly: false, token);
            }
            catch (ImapCommandException)
            {
                // Some servers fail STATUS on individual folders; the list is still useful without counts.
                folders = await client.GetFoldersAsync(personal, subscribedOnly: false, cancellationToken: token);
            }

            if (!folders.Any(f => IsInbox(f.FullName)))
            {
                await client.Inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, token);
                folders.Insert(0, client.Inbox);
            }

            return FolderOrdering.Order(folders.Select(ToDto).ToList());
        }, ct);

    public Task<MessagePage> GetMessagesAsync(MailAccount account, string folderId, uint? before, int limit, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderId, token);
            IList<IMessageSummary> summaries;
            bool more;
            if (before is null)
            {
                var first = Math.Max(0, folder.Count - limit);
                summaries = folder.Count == 0 ? [] : await folder.FetchAsync(first, folder.Count - 1, ListItems, token);
                more = first > 0;
            }
            else if (before.Value <= 1)
            {
                summaries = [];
                more = false;
            }
            else
            {
                var older = await folder.SearchAsync(
                    SearchQuery.Uids(new UniqueIdRange(UniqueId.MinValue, new UniqueId(before.Value - 1))), token);
                var page = older.OrderBy(u => u.Id).Skip(Math.Max(0, older.Count - limit)).ToList();
                summaries = page.Count == 0 ? [] : await folder.FetchAsync(page, ListItems, token);
                more = older.Count > page.Count;
            }

            var messages = summaries
                .OrderByDescending(s => s.UniqueId.Id)
                .Select(s => ToSummaryDto(folder.UidValidity, s))
                .ToList();
            uint? next = more && summaries.Count > 0 ? summaries.Min(s => s.UniqueId.Id) : null;
            return new MessagePage(messages, next, folder.Count, folder.UidValidity);
        }, ct);

    public Task<MessageDto> GetMessageAsync(MailAccount account, string folderId, string messageId, bool allowRemote, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, ListItems, token);

            var html = "";
            BodyPartText? bodyPart = summary.HtmlBody ?? summary.TextBody;
            if (bodyPart is not null && await FetchPartAsync(folder, uid, bodyPart, token) is TextPart text)
            {
                html = text.IsHtml ? text.Text : PlainTextToHtml(text);
            }

            string PartPath(BodyPartBasic part) =>
                $"accounts/{account.Id}/folders/{folderId}/messages/{messageId}/parts/{MailboxIds.PartId(part.PartSpecifier)}";

            var inline = LeafParts(summary.Body)
                .Where(p => !string.IsNullOrEmpty(p.ContentId))
                .GroupBy(p => p.ContentId!.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => PartPath(g.First()), StringComparer.OrdinalIgnoreCase);

            var sanitized = MessageHtmlSanitizer.Sanitize(
                html,
                cid => inline.GetValueOrDefault(cid),
                allowRemote ? url => $"image-proxy?token={proxyTokens.Create(account.OwnerUserId, url)}" : null);

            var envelope = summary.Envelope ?? new Envelope();
            return new MessageDto(
                messageId,
                envelope.Subject ?? "",
                AddressDto.From(envelope.From),
                AddressDto.From(envelope.To),
                AddressDto.From(envelope.Cc),
                AddressDto.From(envelope.ReplyTo),
                MessageDate(summary),
                summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                sanitized.Html,
                sanitized.HasRemoteContent,
                allowRemote,
                summary.Attachments.Select(ToAttachmentDto).ToArray());
        }, ct);

    private static AttachmentDto ToAttachmentDto(BodyPartBasic part) => new(
        MailboxIds.PartId(part.PartSpecifier),
        FileNameOf(part),
        part.ContentType.MimeType.ToLowerInvariant(),
        part.Octets > 0 ? part.Octets : null);

    public Task<PartContent> GetPartAsync(MailAccount account, string folderId, string messageId, string partId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
            var specifier = MailboxIds.PartSpecifier(partId);
            var part = LeafParts(summary.Body).FirstOrDefault(p => p.PartSpecifier == specifier)
                       ?? throw new MailboxNotFoundException("The message part");

            // The whole part is loaded into memory here, so streaming it to the client
            // happens after the IMAP connection went back to the pool.
            var entity = await FetchPartAsync(folder, uid, part, token);
            return entity switch
            {
                MimePart mime => new PartContent(FileNameOf(part), part.ContentType.MimeType.ToLowerInvariant(),
                    (stream, t) => mime.Content?.DecodeToAsync(stream, t) ?? Task.CompletedTask),
                MessagePart message => new PartContent(FileNameOf(part), "message/rfc822",
                    (stream, t) => message.Message?.WriteToAsync(stream, t) ?? Task.CompletedTask),
                _ => throw new MailboxNotFoundException("The message part"),
            };
        }, ct);

    /// <param name="retry">Repeat on a fresh connection if the pooled one turned out dead; only for operations safe to repeat.</param>
    private Task<T> Run<T>(MailAccount account, Func<ImapClient, CancellationToken, Task<T>> action, CancellationToken ct, bool retry = true) =>
        imap.RunAsync(account, credentials.Unprotect(account.Id, account.ProtectedCredential), action, ct, retry);

    private static async Task<IMailFolder> FindFolderAsync(ImapClient client, string folderId, CancellationToken ct)
    {
        var fullName = MailboxIds.FolderFullName(folderId) ?? throw new MailboxNotFoundException("The folder");
        try
        {
            return IsInbox(fullName) ? client.Inbox : await client.GetFolderAsync(fullName, ct);
        }
        catch (FolderNotFoundException)
        {
            throw new MailboxNotFoundException("The folder");
        }
    }

    private static async Task<IMailFolder> OpenFolderAsync(
        ImapClient client, string folderId, CancellationToken ct, FolderAccess access = FolderAccess.ReadOnly)
    {
        var folder = await FindFolderAsync(client, folderId, ct);
        if (!IsSelectable(folder))
        {
            throw new MailboxNotFoundException("The folder");
        }

        await folder.OpenAsync(access, ct);
        return folder;
    }

    private static bool IsSelectable(IMailFolder folder) =>
        !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent);

    private static async Task<(IMailFolder Folder, UniqueId Uid)> OpenMessageAsync(
        ImapClient client, string folderId, string messageId, CancellationToken ct, FolderAccess access = FolderAccess.ReadOnly)
    {
        if (!MailboxIds.TryParseMessageId(messageId, out var uidValidity, out var uid))
        {
            throw new MailboxNotFoundException("The message");
        }

        var folder = await OpenFolderAsync(client, folderId, ct, access);
        // A different UIDVALIDITY means the server renumbered the folder: the id is stale.
        if (folder.UidValidity != uidValidity)
        {
            throw new MailboxNotFoundException("The message");
        }

        return (folder, new UniqueId(uidValidity, uid));
    }

    private static async Task<IMessageSummary> FetchOneAsync(IMailFolder folder, UniqueId uid, MessageSummaryItems items, CancellationToken ct)
    {
        var summaries = await folder.FetchAsync([uid], items, ct);
        return summaries.FirstOrDefault(s => s.UniqueId == uid) ?? throw new MailboxNotFoundException("The message");
    }

    /// <summary>
    /// Fetches one part's content (BODY.PEEK[n], so \Seen is not set) and rebuilds the MIME entity
    /// from the content type and transfer encoding the BODYSTRUCTURE already gave us. MailKit's
    /// GetBodyPartAsync also fetches the part's own headers (BODY[n.MIME]), which not every
    /// server answers correctly (GreenMail's answer does not parse), and costs a round trip more.
    /// </summary>
    private static async Task<MimeEntity> FetchPartAsync(IMailFolder folder, UniqueId uid, BodyPartBasic part, CancellationToken ct)
    {
        var content = new MemoryStream();
        // The root part of a single-part message is the message's TEXT section.
        await using (var stream = await folder.GetStreamAsync(uid, part.PartSpecifier.Length == 0 ? "TEXT" : part.PartSpecifier, ct))
        {
            await stream.CopyToAsync(content, ct);
        }

        content.Position = 0;
        if (part is BodyPartMessage)
        {
            return new MessagePart { Message = await MimeMessage.LoadAsync(content, ct) };
        }

        var encoding = MimeKit.Utils.MimeUtils.TryParse(part.ContentTransferEncoding, out ContentEncoding parsed)
            ? parsed
            : ContentEncoding.Default;
        MimePart entity = part.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
            ? new TextPart(part.ContentType.MediaSubtype)
            : new MimePart(part.ContentType.MediaType, part.ContentType.MediaSubtype);
        // Parameters carry the charset (needed to decode text) and the name.
        foreach (var parameter in part.ContentType.Parameters)
        {
            entity.ContentType.Parameters[parameter.Name] = parameter.Value;
        }

        entity.ContentTransferEncoding = encoding;
        entity.Content = new MimeContent(content, encoding);
        return entity;
    }

    /// <summary>Leaf parts in order; attached messages count as one part (not descended into).</summary>
    private static IEnumerable<BodyPartBasic> LeafParts(BodyPart? part) => part switch
    {
        BodyPartMultipart multipart => multipart.BodyParts.SelectMany(LeafParts),
        BodyPartBasic basic => [basic],
        _ => [],
    };

    private static string PlainTextToHtml(TextPart text)
    {
        TextConverter converter = text.IsFlowed
            ? new FlowedToHtml { DeleteSpace = text.ContentType.Parameters["delsp"]?.Equals("yes", StringComparison.OrdinalIgnoreCase) ?? false }
            : new TextToHtml();
        return converter.Convert(text.Text);
    }

    private static string FileNameOf(BodyPartBasic part)
    {
        var name = part.FileName ?? part.ContentType.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = part is BodyPartMessage ? "message.eml" : $"part-{MailboxIds.PartId(part.PartSpecifier)}";
        }

        // Never trust a sender's file name as a path.
        return string.Concat(name.Where(c => !char.IsControl(c) && c is not ('/' or '\\'))).Trim();
    }

    private static DateTimeOffset MessageDate(IMessageSummary s) => s.Envelope?.Date ?? s.InternalDate ?? DateTimeOffset.UnixEpoch;

    private static bool IsInbox(string fullName) => fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);

    private static MessageSummaryDto ToSummaryDto(uint uidValidity, IMessageSummary s) => new(
        MailboxIds.MessageId(uidValidity, s.UniqueId.Id),
        s.Envelope?.Subject ?? "",
        AddressDto.From(s.Envelope?.From),
        AddressDto.From(s.Envelope?.To),
        MessageDate(s),
        s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
        s.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
        s.Flags?.HasFlag(MessageFlags.Answered) ?? false,
        s.Attachments.Any(),
        (long)(s.Size ?? 0));

    private static FolderDto ToDto(IMailFolder f)
    {
        var parent = f.ParentFolder is { FullName.Length: > 0 } p ? MailboxIds.FolderId(p.FullName) : null;
        return new FolderDto(
            MailboxIds.FolderId(f.FullName),
            parent,
            IsInbox(f.FullName) ? "Inbox" : f.Name,
            f.FullName,
            FolderOrdering.SpecialUse(f),
            !f.Attributes.HasFlag(FolderAttributes.NoSelect) && !f.Attributes.HasFlag(FolderAttributes.NonExistent),
            f.Attributes.HasFlag(FolderAttributes.NoSelect) ? null : f.Count,
            f.Attributes.HasFlag(FolderAttributes.NoSelect) ? null : f.Unread);
    }
}
