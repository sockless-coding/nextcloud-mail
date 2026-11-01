using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>A message's conversation, for the conversation view and the Assistant's summaries.</summary>
public sealed partial class MailboxService
{
    /// <summary>At most this many messages of one conversation are returned (the newest).</summary>
    public const int MaxConversationMessages = 100;

    /// <summary>At most this many matches of one folder's search are read (the newest).</summary>
    private const int MaxSearchMatches = 200;

    /// <summary>Folders whose messages join a conversation only when it was opened there.</summary>
    private static readonly HashSet<string> SetAsideRoles = ["trash", "junk"];

    private sealed record ConversationMember(IMailFolder Folder, IMessageSummary Summary, ConversationHeaders Headers)
    {
        public ConversationMember(IMailFolder folder, IMessageSummary summary) : this(folder, summary, ConversationHeaders.Of(summary))
        {
        }
    }

    private sealed record FoundConversation(ConversationMember Opened, IReadOnlyList<ConversationMember> Members, bool Truncated);

    /// <summary>The message's conversation in all folders of the account, oldest first.</summary>
    public async Task<ConversationDto> GetConversationAsync(MailAccount account, string folderId, string messageId, CancellationToken ct)
    {
        var roles = await FolderRolesAsync(account, ct);
        var found = await Run(account, (client, token) =>
            FindConversationAsync(client, account, folderId, messageId, roles, FolderCache.ListRequest(), MaxConversationMessages, token), ct);
        var roleOf = roles.GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.First().Key);
        return new ConversationDto(
            found.Members.Select(m =>
            {
                var id = MailboxIds.FolderId(m.Folder.FullName);
                return new ConversationMessageDto(
                    id,
                    m.Folder.FullName,
                    roleOf.GetValueOrDefault(id),
                    ToSummaryDto(m.Folder.UidValidity, m.Summary),
                    AddressDto.From(m.Summary.Envelope?.Cc),
                    m.Headers.MessageId == "" ? null : m.Headers.MessageId,
                    m.Headers.ParentId);
            }).ToList(),
            found.Truncated);
    }

    /// <summary>
    /// The message and the messages it belongs with as text, oldest first, at most
    /// <paramref name="max"/> (the newest ones; the opened message is always among them).
    /// </summary>
    public async Task<IReadOnlyList<MessageText>> GetConversationTextAsync(
        MailAccount account, string folderId, string messageId, int max, CancellationToken ct)
    {
        var roles = max <= 1 ? new Dictionary<string, string>() : await FolderRolesAsync(account, ct);
        return await Run<IReadOnlyList<MessageText>>(account, async (client, token) =>
        {
            var request = new FetchRequest(
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate
                | MessageSummaryItems.BodyStructure | MessageSummaryItems.References);
            var found = await FindConversationAsync(client, account, folderId, messageId, roles, request, max, token);

            var texts = new Dictionary<ConversationMember, MessageText>();
            foreach (var group in found.Members.GroupBy(m => m.Folder))
            {
                if (!group.Key.IsOpen)
                {
                    await group.Key.OpenAsync(FolderAccess.ReadOnly, token);
                }

                foreach (var member in group)
                {
                    var (text, _) = await BodiesAsync(group.Key, member.Summary.UniqueId, member.Summary, token);
                    texts[member] = ToText(member.Summary, text);
                }
            }

            return found.Members.Select(m => texts[m]).ToList();
        }, ct);
    }

    /// <summary>
    /// Finds the conversation of a message: what the cache links to it (<see cref="ConversationIndex"/>)
    /// in any folder, and what a header search finds in its own folder, the inbox and Sent beyond the
    /// cached windows. Trash and Junk count only when the message is there. A message kept in several
    /// folders appears once, preferring the opened one, then its folder, the inbox, others, Sent.
    /// </summary>
    /// <param name="roles">The account's folder id per special use (<see cref="FolderRoles"/>).</param>
    /// <param name="request">What to fetch per message; must include the envelope and references.</param>
    private async Task<FoundConversation> FindConversationAsync(
        ImapClient client, MailAccount account, string folderId, string messageId,
        IReadOnlyDictionary<string, string> roles, IFetchRequest request, int max, CancellationToken ct)
    {
        var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, ct);
        var opened = new ConversationMember(folder, await FetchOneAsync(folder, uid, request, ct));
        if (max <= 1 || !opened.Headers.Keys.Any())
        {
            return new FoundConversation(opened, [opened], false);
        }

        var ownKey = FolderCache.Key(folder.FullName);
        string? RoleOf(string fullName) =>
            fullName == "INBOX" ? "inbox" : roles.FirstOrDefault(r => r.Value == MailboxIds.FolderId(fullName)).Key;

        // Full name → (UIDVALIDITY, UIDs) of the messages to fetch.
        var wanted = new Dictionary<string, (uint UidValidity, HashSet<uint> Uids)>(StringComparer.Ordinal);
        void Want(string fullName, uint uidValidity, IEnumerable<uint> uids)
        {
            if (!wanted.TryGetValue(fullName, out var entry) || entry.UidValidity != uidValidity)
            {
                wanted[fullName] = entry = (uidValidity, []);
            }

            entry.Uids.UnionWith(uids);
        }

        foreach (var (fullName, uidValidity, cachedUid) in await cache.ConversationAsync(account.Id, opened.Headers.Keys, MaxConversationMessages * 2, ct))
        {
            if (fullName == ownKey || !SetAsideRoles.Contains(RoleOf(fullName) ?? ""))
            {
                Want(fullName, uidValidity, [cachedUid]);
            }
        }

        var query = ConversationQuery(opened.Headers);
        var searched = new List<IMailFolder> { folder };
        foreach (var role in new[] { "inbox", "sent" })
        {
            if (roles.GetValueOrDefault(role) is { } id
                && MailboxIds.FolderFullName(id) is { } name
                && searched.All(f => FolderCache.Key(f.FullName) != FolderCache.Key(name))
                && await FolderByNameAsync(client, name, ct) is { } other)
            {
                searched.Add(other);
            }
        }

        foreach (var candidate in searched)
        {
            if (!candidate.IsOpen)
            {
                await candidate.OpenAsync(FolderAccess.ReadOnly, ct);
            }

            var search = query;
            // Messages in the cached window are linked already (above).
            if (await cache.ConversationsKnownAsync(account.Id, FolderCache.Key(candidate.FullName), candidate.UidValidity, ct) is { } known)
            {
                search = search.And(SearchQuery.Not(SearchQuery.Uids(known)));
            }

            try
            {
                var matches = await candidate.SearchAsync(search, ct);
                Want(FolderCache.Key(candidate.FullName), candidate.UidValidity, matches.Select(u => u.Id).OrderDescending().Take(MaxSearchMatches));
            }
            catch (ImapCommandException)
            {
                // A server that cannot search headers this way: the conversation stays what the cache knows.
            }
        }

        var members = new List<ConversationMember> { opened };
        foreach (var (fullName, (uidValidity, uids)) in wanted)
        {
            var target = fullName == ownKey ? folder : await FolderByNameAsync(client, fullName, ct);
            uids.Remove(target == folder ? uid.Id : 0);
            if (target is null || uids.Count == 0)
            {
                continue;
            }

            if (!target.IsOpen)
            {
                await target.OpenAsync(FolderAccess.ReadOnly, ct);
            }

            // The folder was renumbered since it was cached: its UIDs mean other messages now.
            if (target.UidValidity != uidValidity)
            {
                continue;
            }

            var summaries = await target.FetchAsync(uids.Select(u => new UniqueId(uidValidity, u)).ToList(), request, ct);
            members.AddRange(summaries.Select(s => new ConversationMember(target, s)));
        }

        int Preference(ConversationMember m) =>
            m == opened ? 0
            : m.Folder == folder ? 1
            : RoleOf(FolderCache.Key(m.Folder.FullName)) switch { "inbox" => 2, "sent" => 4, _ => 3 };

        var unique = members
            .OrderBy(Preference)
            .DistinctBy(m => m.Headers.MessageId == "" ? $"{m.Folder.FullName}\n{m.Summary.UniqueId.Id}" : m.Headers.MessageId)
            .ToList();

        // The newest ones, and never without the message that was asked about.
        var newest = unique.OrderByDescending(m => MessageDate(m.Summary)).Take(max).ToList();
        if (!newest.Contains(opened))
        {
            newest[^1] = opened;
        }

        return new FoundConversation(opened, newest.OrderBy(m => MessageDate(m.Summary)).ToList(), unique.Count > newest.Count);
    }

    /// <summary>The selectable folder with this full name; null when there is none (any more).</summary>
    private static async Task<IMailFolder?> FolderByNameAsync(ImapClient client, string fullName, CancellationToken ct)
    {
        if (IsInbox(fullName))
        {
            return client.Inbox;
        }

        try
        {
            var folder = await client.GetFolderAsync(fullName, ct);
            return IsSelectable(folder) ? folder : null;
        }
        catch (FolderNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Messages that reference the conversation's first message or this one, and those whose
    /// Message-ID this one references.
    /// </summary>
    private static SearchQuery ConversationQuery(ConversationHeaders headers)
    {
        var root = headers.Related.FirstOrDefault() ?? headers.MessageId;
        SearchQuery query = SearchQuery.HeaderContains("References", root).Or(SearchQuery.HeaderContains("Message-ID", root));
        if (headers.MessageId != "" && headers.MessageId != root)
        {
            query = query.Or(SearchQuery.HeaderContains("In-Reply-To", headers.MessageId)).Or(SearchQuery.HeaderContains("References", headers.MessageId));
        }

        foreach (var id in headers.Related.Where(r => r != root))
        {
            query = query.Or(SearchQuery.HeaderContains("Message-ID", id));
        }

        return query;
    }

    private static MessageText ToText(IMessageSummary summary, string text)
    {
        var envelope = summary.Envelope ?? new Envelope();
        return new MessageText(envelope.Subject ?? "", envelope.From.Mailboxes.FirstOrDefault(), MessageDate(summary), text, envelope.MessageId);
    }
}
