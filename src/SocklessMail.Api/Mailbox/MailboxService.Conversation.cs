using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mailbox;

/// <summary>A message's conversation, for the Assistant's summaries.</summary>
public sealed partial class MailboxService
{
    /// <summary>At most this many message ids of a References header are looked for.</summary>
    private const int MaxConversationIds = 30;

    /// <summary>
    /// The message and the messages it belongs with, oldest first, at most <paramref name="max"/>
    /// (the newest ones; the opened message is always among them). Related messages are those
    /// whose Message-ID the message references, and those that reference the conversation's first
    /// message or this one, found in the message's folder, the inbox and Sent.
    /// </summary>
    public Task<IReadOnlyList<MessageText>> GetConversationTextAsync(
        MailAccount account, string folderId, string messageId, int max, CancellationToken ct) =>
        Run<IReadOnlyList<MessageText>>(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate
                | MessageSummaryItems.BodyStructure | MessageSummaryItems.References, token);
            var (text, _) = await BodiesAsync(folder, uid, summary, token);
            var opened = ToText(summary, text);

            var ownId = summary.Envelope?.MessageId;
            var references = (summary.References ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (summary.Envelope?.InReplyTo is { Length: > 0 } inReplyTo && !references.Contains(inReplyTo))
            {
                references.Add(inReplyTo);
            }

            var root = references.FirstOrDefault() ?? ownId;
            if (string.IsNullOrWhiteSpace(root) || max <= 1)
            {
                return [opened];
            }

            var query = ConversationQuery(root, ownId, references.TakeLast(MaxConversationIds));
            var found = new Dictionary<string, MessageText>(StringComparer.Ordinal);
            if (ownId is not null)
            {
                found[ownId] = opened;
            }

            foreach (var candidate in await ConversationFoldersAsync(client, folder, token))
            {
                await CollectAsync(candidate, candidate == folder ? uid : null, query, max, found, token);
            }

            var messages = found.Values.OrderBy(m => m.Date).ToList();
            if (ownId is null)
            {
                messages.Add(opened);
            }

            // The newest ones, and never without the message that was asked about.
            var newest = messages.OrderByDescending(m => m.Date).Take(max).ToList();
            if (!newest.Contains(opened))
            {
                newest[^1] = opened;
            }

            return newest.OrderBy(m => m.Date).ToList();
        }, ct);

    private static SearchQuery ConversationQuery(string root, string? ownId, IEnumerable<string> references)
    {
        SearchQuery query = SearchQuery.HeaderContains("References", root).Or(SearchQuery.HeaderContains("Message-ID", root));
        if (ownId is not null && ownId != root)
        {
            query = query.Or(SearchQuery.HeaderContains("In-Reply-To", ownId)).Or(SearchQuery.HeaderContains("References", ownId));
        }

        foreach (var id in references.Where(r => r != root))
        {
            query = query.Or(SearchQuery.HeaderContains("Message-ID", id));
        }

        return query;
    }

    /// <summary>The message's own folder, then the inbox and Sent when they are other folders.</summary>
    private static async Task<IReadOnlyList<IMailFolder>> ConversationFoldersAsync(ImapClient client, IMailFolder own, CancellationToken ct)
    {
        var folders = new List<IMailFolder> { own };
        foreach (var other in new[] { client.Inbox, await FindSpecialFolderAsync(client, "sent", ct) })
        {
            if (other is not null && IsSelectable(other) && folders.All(f => f.FullName != other.FullName))
            {
                folders.Add(other);
            }
        }

        return folders;
    }

    private async Task CollectAsync(
        IMailFolder folder, UniqueId? skip, SearchQuery query, int max, Dictionary<string, MessageText> found, CancellationToken ct)
    {
        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        }

        IList<UniqueId> uids;
        try
        {
            uids = await folder.SearchAsync(query, ct);
        }
        catch (ImapCommandException)
        {
            // A server that cannot search headers this way: the conversation stays what was found.
            return;
        }

        if (uids.Count == 0)
        {
            return;
        }

        var summaries = await folder.FetchAsync(
            uids.Where(u => u != skip).ToList(),
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure,
            ct);
        foreach (var summary in summaries.OrderByDescending(MessageDate).Take(max))
        {
            var id = summary.Envelope?.MessageId;
            if (id is null || found.ContainsKey(id))
            {
                continue;
            }

            var (text, _) = await BodiesAsync(folder, summary.UniqueId, summary, ct);
            found[id] = ToText(summary, text);
        }
    }

    private static MessageText ToText(IMessageSummary summary, string text)
    {
        var envelope = summary.Envelope ?? new Envelope();
        return new MessageText(envelope.Subject ?? "", envelope.From.Mailboxes.FirstOrDefault(), MessageDate(summary), text, envelope.MessageId);
    }
}
