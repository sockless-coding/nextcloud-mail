using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MimeKit.Text;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Sync;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Rules;
using SocklessMail.Api.Senders;
using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Mailbox;

/// <summary>A message part ready to stream to the client after the IMAP connection was returned.</summary>
public sealed record PartContent(string FileName, string ContentType, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>
/// Read access to an account's mailbox over pooled IMAP connections. Callers pass an account
/// that was already looked up for the current user (<see cref="AccountService.GetAsync"/>), which
/// is what scopes every operation here to its owner. Folders are opened read-only (EXAMINE), and
/// bodies are fetched with BODY.PEEK, so reading does not change \Seen; marking messages as read
/// is an explicit action. Message lists come from the header cache (<see cref="FolderCache"/>)
/// after a sync; the sync is skipped while an IDLE watcher keeps the folder current.
/// </summary>
public sealed partial class MailboxService(
    ImapConnections imap, MailLogins logins, ImageProxyTokens proxyTokens,
    MailServerConnector connector, AttachmentUploads uploads, FolderCache cache, SyncLocks syncLocks,
    MailboxWatchers watchers, RuleEngine rules, BimiSenders bimi, FolderRoles roles, SenderChecks senderChecks, TagIndex tagIndex, TagStore tagStore, OutlookCategories outlook,
    TimeProvider clock, ILogger<MailboxService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<IReadOnlyList<FolderDto>> GetFoldersAsync(MailAccount account, CancellationToken ct)
    {
        var list = await Run(account, async (client, token) =>
        {
            var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            List<IMailFolder> folders;
            try
            {
                folders = [.. await client.GetFoldersAsync(personal, StatusItems.Count | StatusItems.Unread, subscribedOnly: false, token)];
            }
            catch (ImapCommandException)
            {
                // Some servers fail STATUS on individual folders; the list is still useful without counts.
                folders = [.. await client.GetFoldersAsync(personal, subscribedOnly: false, cancellationToken: token)];
            }

            if (!folders.Any(f => IsInbox(f.FullName)))
            {
                await client.Inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, token);
                folders.Insert(0, client.Inbox);
            }

            return FolderOrdering.Order(folders.Select(ToDto).ToList());
        }, ct);
        roles.Remember(account, list);

        // Folders deleted or renamed elsewhere leave their cache behind; this list is the authority.
        try
        {
            await cache.ForgetMissingFoldersAsync(account.Id, list.Select(f => FolderCache.Key(f.FullName)), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Cleaning up the folder cache of account {AccountId} failed", account.Id);
        }

        return list;
    }

    public async Task<MessagePage> GetMessagesAsync(MailAccount account, string folderId, uint? before, int limit, CancellationToken ct)
    {
        var fullName = FullNameOf(folderId);
        var state = await CurrentStateAsync(account, folderId, fullName, ct);
        var rows = await cache.PageAsync(state, before, limit + 1, ct);
        if (rows.Count <= limit && state.WindowStart > 1)
        {
            // The page reaches below the cached window: sync again and page further back on the server.
            state = await SyncAsync(account, folderId, fullName, (before, limit + 1 - rows.Count), ct);
            rows = await cache.PageAsync(state, before, limit + 1, ct);
        }

        var page = rows.Take(limit).ToList();
        uint? next = rows.Count > limit ? page[^1].Uid : null;
        return new MessagePage(
            page.Select(m => FolderCache.ToSummaryDto(state.UidValidity, m)).ToList(),
            next, state.Total, state.UidValidity, state.Unread, state.ChangeSeq);
    }

    /// <summary>Candidates checked per round when a filter needs each message's structure (attachments).</summary>
    private const int MaxStructureRounds = 5;

    /// <summary>
    /// Searches one folder on the server for messages containing every term (IMAP <c>SEARCH TEXT</c>,
    /// headers and body) and matching <paramref name="filter"/>; either may be left out, not both.
    /// Not cached: matches may lie anywhere in the folder.
    /// </summary>
    public async Task<SearchPage> SearchFolderAsync(
        MailAccount account, string folderId, string? query, MessageFilter filter, uint? before, int limit, CancellationToken ct)
    {
        var page = await SearchServerAsync(account, folderId, query, filter, before, limit, ct);
        if (page.Messages.Count == 0 || !(await tagIndex.StorageAsync(account.Id, ct)).IsAppKept())
        {
            return page;
        }

        // The server keeps no keywords for this account: the tags come from the index.
        var uids = page.Messages.Select(m => MailboxIds.TryParseMessageId(m.Id, out _, out var uid) ? uid : 0).ToList();
        var local = await tagIndex.KeywordsAsync(account.Id, FullNameOf(folderId), page.UidValidity, uids, ct);
        return page with
        {
            Messages = page.Messages.Zip(uids, (m, uid) => m with { Keywords = Tags.Keywords.ForClient(local.GetValueOrDefault(uid)) }).ToList(),
        };
    }

    private Task<SearchPage> SearchServerAsync(
        MailAccount account, string folderId, string? query, MessageFilter filter, uint? before, int limit, CancellationToken ct)
    {
        var terms = filter == MessageFilter.None || !string.IsNullOrWhiteSpace(query) ? SearchTerms.Parse(query) : [];
        var criteria = terms.Select(SearchQuery.MessageContains).Cast<SearchQuery>().ToList();
        if (MessageFilters.ImapQuery(filter) is { } filterQuery)
        {
            criteria.Add(filterQuery);
        }

        return Run(account, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderId, token);
            var search = criteria.Aggregate((a, b) => a.And(b));
            if (before is { } b)
            {
                if (b <= 1)
                {
                    return new SearchPage([], null, 0, folder.UidValidity);
                }

                search = search.And(SearchQuery.Uids(new UniqueIdRange(new UniqueId(folder.UidValidity, 1), new UniqueId(folder.UidValidity, b - 1))));
            }

            var matches = (await folder.SearchAsync(search, token)).OrderByDescending(u => u.Id).ToList();
            if (!MessageFilters.NeedsStructureCheck(filter))
            {
                var page = matches.Take(limit).ToList();
                var summaries = page.Count == 0 ? [] : await folder.FetchAsync(page, FolderCache.ListRequest(), token);
                return new SearchPage(
                    summaries.OrderByDescending(s => s.UniqueId.Id).Select(s => ToSummaryDto(folder.UidValidity, s)).ToList(),
                    matches.Count > limit ? page[^1].Id : null,
                    matches.Count,
                    folder.UidValidity);
            }

            // Candidates only: keep those with an attachment, reading further until the page is full
            // (or a few rounds went by, so one request stays short).
            var kept = new List<IMessageSummary>();
            var read = 0;
            for (var round = 0; round < MaxStructureRounds && kept.Count < limit && read < matches.Count; round++)
            {
                var batch = matches.Skip(read).Take(limit - kept.Count).ToList();
                read += batch.Count;
                var structures = await folder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
                kept.AddRange(structures.Where(s => s.Attachments.Any()));
            }

            var withAttachments = kept.Count == 0 ? [] : await folder.FetchAsync(kept.Select(s => s.UniqueId).ToList(), FolderCache.ListRequest(), token);
            var more = read < matches.Count;
            return new SearchPage(
                withAttachments.OrderByDescending(s => s.UniqueId.Id).Select(s => ToSummaryDto(folder.UidValidity, s)).ToList(),
                more ? matches[read - 1].Id : null,
                // Counted only when the first page checked every candidate; later pages leave the count as it was.
                more || before is not null ? null : kept.Count,
                folder.UidValidity);
        }, ct);
    }

    /// <summary>What changed since <paramref name="since"/> (a token from a page or an earlier call).</summary>
    public async Task<FolderChanges> GetChangesAsync(MailAccount account, string folderId, long since, CancellationToken ct)
    {
        var fullName = FullNameOf(folderId);
        return await cache.ChangesAsync(await CurrentStateAsync(account, folderId, fullName, ct), since, ct);
    }

    /// <summary>The folder's cache state for the unified folders, synced now unless an IDLE watcher already keeps it current.</summary>
    public Task<CachedFolder> FolderStateAsync(MailAccount account, string folderId, CancellationToken ct) =>
        CurrentStateAsync(account, folderId, FullNameOf(folderId), ct);

    /// <summary>Syncs the folder and caches at least <paramref name="need"/> more messages below its window.</summary>
    public Task<CachedFolder> ExtendFolderAsync(MailAccount account, string folderId, int need, CancellationToken ct) =>
        SyncAsync(account, folderId, FullNameOf(folderId), (null, need), ct);

    /// <summary>The account's folder id for each special use (see <see cref="FolderRoles"/>), listed on the server when not known.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FolderRolesAsync(MailAccount account, CancellationToken ct)
    {
        if (roles.Find(account) is { } known)
        {
            return known;
        }

        var list = await Run(account, async (client, token) =>
        {
            var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            var folders = await client.GetFoldersAsync(personal, subscribedOnly: false, cancellationToken: token);
            return FolderOrdering.Order(folders.Select(ToDto).ToList());
        }, ct);
        return roles.Remember(account, list);
    }

    /// <summary>The folder's cache state, synced now unless an IDLE watcher already keeps it current.</summary>
    private async Task<CachedFolder> CurrentStateAsync(MailAccount account, string folderId, string fullName, CancellationToken ct) =>
        (watchers.IsCurrent(account.Id, fullName) ? await cache.FindAsync(account.Id, fullName, ct) : null)
        ?? await SyncAsync(account, folderId, fullName, null, ct);

    private async Task<CachedFolder> SyncAsync(
        MailAccount account, string folderId, string fullName, (uint? Before, int Need)? extend, CancellationToken ct)
    {
        // Lock before leasing a connection (see SyncLocks).
        using var _ = await syncLocks.AcquireAsync(account.Id, fullName, ct);
        return await Run(account, async (client, token) =>
        {
            if (fullName == "INBOX")
            {
                // Rules first, so mail they move away never reaches the cached INBOX.
                await rules.ApplyAsync(client, account, token);
            }

            var folder = await OpenFolderAsync(client, folderId, token);
            var state = await cache.SyncAsync(client, account, folder, DefaultPageSize, token);
            return extend is { } e ? await cache.ExtendAsync(account, folder, state, e.Before, e.Need, token) : state;
        }, ct);
    }

    /// <summary>
    /// Brings the cache in line with a change the app just made on the server. The change has
    /// happened, so a failure here must not fail the request; the next sync repairs the cache.
    /// </summary>
    private async Task UpdateCacheAsync(MailAccount account, string fullName, Func<Task> update)
    {
        try
        {
            using var _ = await syncLocks.AcquireAsync(account.Id, fullName, CancellationToken.None);
            await update();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Updating the cache of account {AccountId} after a change failed", account.Id);
        }
    }

    /// <summary>The folder's name as the cache keys it.</summary>
    private static string FullNameOf(string folderId) =>
        MailboxIds.FolderFullName(folderId) is { } name ? FolderCache.Key(name) : throw new MailboxNotFoundException("The folder");

    public async Task<MessageDto> GetMessageAsync(MailAccount account, string folderId, string messageId, bool allowRemote, CancellationToken ct)
    {
        var (message, dmarcFailed, links) = await Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            // The view shows the text itself, not a preview of it.
            var summary = await FetchOneAsync(folder, uid, FolderCache.ListRequest(FolderCache.ListItems & ~MessageSummaryItems.PreviewText), token);

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
            return (new MessageDto(
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
                summary.Attachments.Select(ToAttachmentDto).ToArray(),
                bimi.Find(summary),
                LeafParts(summary.Body).Where(IsCalendarPart)
                    .Select(p => new CalendarPartDto(MailboxIds.PartId(p.PartSpecifier), p.ContentType.Parameters["method"]?.Trim().ToUpperInvariant()))
                    .ToArray(),
                null,
                Tags.Keywords.ForClient(summary.Keywords)), bimi.FailedDmarc(envelope.From, summary.Headers), sanitized.LinkMismatches);
        }, ct);

        if ((await tagIndex.StorageAsync(account.Id, ct)).IsAppKept()
            && MailboxIds.TryParseMessageId(messageId, out var uidValidity, out var uid))
        {
            var local = await tagIndex.KeywordsAsync(account.Id, FullNameOf(folderId), uidValidity, [uid], ct);
            message = message with { Keywords = Tags.Keywords.ForClient(local.GetValueOrDefault(uid)) };
        }

        return message with { SenderWarning = await senderChecks.CheckMessageAsync(account.OwnerUserId, message.From, message.ReplyTo, dmarcFailed, links, ct) };
    }

    /// <summary>Calendar data travels as text/calendar (iMIP), sometimes as application/ics or just an .ics file.</summary>
    private static bool IsCalendarPart(BodyPartBasic part) =>
        part.ContentType.IsMimeType("text", "calendar") || part.ContentType.IsMimeType("application", "ics")
        || (part.FileName ?? part.ContentType.Name ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text of a calendar part (at most <see cref="MaxCalendarBytes"/>).</summary>
    public Task<string> GetCalendarTextAsync(MailAccount account, string folderId, string messageId, string partId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
            var specifier = MailboxIds.PartSpecifier(partId);
            var part = LeafParts(summary.Body).FirstOrDefault(p => p.PartSpecifier == specifier && IsCalendarPart(p))
                       ?? throw new MailboxNotFoundException("The calendar part");
            if (part.Octets > MaxCalendarBytes * 2)
            {
                throw new MailboxValidationException("partId", "The calendar data is too large.");
            }

            if (await FetchPartAsync(folder, uid, part, token) is not MimePart mime || mime.Content is null)
            {
                throw new MailboxNotFoundException("The calendar part");
            }

            using var buffer = new MemoryStream();
            await mime.Content.DecodeToAsync(buffer, token);
            if (buffer.Length > MaxCalendarBytes)
            {
                throw new MailboxValidationException("partId", "The calendar data is too large.");
            }

            // A text part decodes its charset; an application/ics file is UTF-8 by definition.
            return mime is TextPart text ? text.Text : System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }, ct);

    public const int MaxCalendarBytes = 1024 * 1024;

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
    private async Task<T> Run<T>(MailAccount account, Func<ImapClient, CancellationToken, Task<T>> action, CancellationToken ct, bool retry = true) =>
        await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct), action, ct, retry);

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

    internal static bool IsSelectable(IMailFolder folder) =>
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

    private static Task<IMessageSummary> FetchOneAsync(IMailFolder folder, UniqueId uid, MessageSummaryItems items, CancellationToken ct) =>
        FetchOneAsync(folder, uid, new FetchRequest(items), ct);

    private static async Task<IMessageSummary> FetchOneAsync(IMailFolder folder, UniqueId uid, IFetchRequest request, CancellationToken ct)
    {
        var summaries = await folder.FetchAsync([uid], request, ct);
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

    internal static bool IsInbox(string fullName) => fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);

    private MessageSummaryDto ToSummaryDto(uint uidValidity, IMessageSummary s) => new(
        MailboxIds.MessageId(uidValidity, s.UniqueId.Id),
        s.Envelope?.Subject ?? "",
        AddressDto.From(s.Envelope?.From),
        AddressDto.From(s.Envelope?.To),
        MessageDate(s),
        s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
        s.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
        s.Flags?.HasFlag(MessageFlags.Answered) ?? false,
        s.Attachments.Any(),
        (long)(s.Size ?? 0),
        MessagePreview.Normalize(s.PreviewText),
        bimi.Find(s),
        Tags.Keywords.ForClient(s.Keywords));

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
