using MailKit;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;

namespace SocklessMail.Api.Sync;

/// <summary>The headers that place a message in a conversation, ids without angle brackets.</summary>
/// <param name="MessageId">"" when the message has none.</param>
/// <param name="ParentId">The message it replies to: In-Reply-To, else the last of References.</param>
/// <param name="Related">Every id it references (the first and the newest ones, see <see cref="ConversationIndex.MaxReferences"/>).</param>
public sealed record ConversationHeaders(string MessageId, string? ParentId, IReadOnlyList<string> Related)
{
    public static ConversationHeaders Of(IMessageSummary summary)
    {
        var own = Clean(summary.Envelope?.MessageId) ?? "";
        var references = (summary.References ?? []).Select(Clean).OfType<string>().ToList();
        var inReplyTo = Clean(summary.Envelope?.InReplyTo);
        var related = references.Append(inReplyTo).OfType<string>().Where(r => r != own).Distinct(StringComparer.Ordinal).ToList();
        if (related.Count > ConversationIndex.MaxReferences)
        {
            // The first is the conversation's start; the newest are what a reply most likely shares.
            related = [related[0], .. related.TakeLast(ConversationIndex.MaxReferences - 1)];
        }

        var parent = inReplyTo ?? references.LastOrDefault();
        return new ConversationHeaders(own, parent == own ? null : parent, related);
    }

    /// <summary>The id without angle brackets or space; null when empty or too long to keep.</summary>
    public static string? Clean(string? id)
    {
        var clean = id?.Trim().TrimStart('<').TrimEnd('>').Trim();
        return string.IsNullOrEmpty(clean) || clean.Length > ConversationIndex.MaxIdLength ? null : clean;
    }

    /// <summary>The ids that tie the message to others: its own and those it references.</summary>
    public IEnumerable<string> Keys => MessageId == "" ? Related : Related.Prepend(MessageId);
}

/// <summary>What <see cref="ConversationIndex.AssignAsync"/> decided.</summary>
/// <param name="Ids">The conversation of each message, in order.</param>
/// <param name="ChangedFolders">Other cached folders whose messages moved to another conversation (a merge); their lists changed.</param>
/// <param name="ChangedHere">Messages of the folder being synced moved to another conversation.</param>
public sealed record ConversationAssignment(IReadOnlyList<Guid> Ids, IReadOnlyList<CachedFolder> ChangedFolders, bool ChangedHere);

/// <summary>
/// Groups an account's messages into conversations by their Message-ID, In-Reply-To and
/// References headers (no subject matching). Every id seen gets a <see cref="ConversationLink"/>;
/// a message joins the conversation of any id it shares, and when it shares ids with several
/// conversations they become one. Runs inside the cache's sync transaction (an immediate one in
/// SQLite), so two syncs never link the same id at once.
/// </summary>
public sealed class ConversationIndex(MailDbContext db)
{
    /// <summary>Longer ids are not kept (RFC 5322 lines stop at 998 characters).</summary>
    public const int MaxIdLength = 1000;

    /// <summary>At most this many referenced ids of one message are linked.</summary>
    public const int MaxReferences = 30;

    /// <summary>Ids per SQL statement, well below SQLite's parameter limit.</summary>
    private const int Chunk = 500;

    /// <summary>
    /// The conversation of each of <paramref name="messages"/>, in order. Adds the links that are
    /// new to the context (the caller saves) and merges conversations in the database right away.
    /// Cached messages that a merge moves are changes their folders' clients must hear about: they
    /// get <paramref name="folderSeq"/> in the folder being synced (<paramref name="folderId"/>, whose
    /// state the caller saves), and the next change number of every other folder.
    /// </summary>
    public async Task<ConversationAssignment> AssignAsync(
        Guid accountId, string ownerUserId, IReadOnlyList<ConversationHeaders> messages, Guid folderId, long folderSeq, CancellationToken ct)
    {
        if (messages.Count == 0)
        {
            return new ConversationAssignment([], [], false);
        }

        var known = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var chunk in messages.SelectMany(m => m.Keys).Distinct(StringComparer.Ordinal).Chunk(Chunk))
        {
            var links = await db.ConversationLinks.AsNoTracking()
                .Where(l => l.AccountId == accountId && chunk.Contains(l.MessageIdHeader))
                .Select(l => new { l.MessageIdHeader, l.ConversationId })
                .ToListAsync(ct);
            foreach (var link in links)
            {
                known[link.MessageIdHeader] = link.ConversationId;
            }
        }

        var stored = known.Keys.ToHashSet(StringComparer.Ordinal);
        // Conversations merged into another one: away → into.
        var merged = new Dictionary<Guid, Guid>();
        Guid Find(Guid id)
        {
            while (merged.TryGetValue(id, out var into))
            {
                id = into;
            }

            return id;
        }

        var assigned = new List<Guid>(messages.Count);
        foreach (var message in messages)
        {
            var keys = message.Keys.ToList();
            var found = keys.Where(known.ContainsKey).Select(k => Find(known[k])).Distinct().Order().ToList();
            var conversation = found.Count == 0 ? Guid.CreateVersion7() : found[0];
            foreach (var other in found.Skip(1))
            {
                merged[other] = conversation;
            }

            foreach (var key in keys)
            {
                known[key] = conversation;
            }

            assigned.Add(conversation);
        }

        var changedFolders = new Dictionary<Guid, CachedFolder>();
        var changedHere = false;
        foreach (var away in merged.Keys)
        {
            var into = Find(away);
            await db.ConversationLinks
                .Where(l => l.AccountId == accountId && l.ConversationId == away)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConversationId, into), ct);
            var folders = await db.Messages.Where(m => m.ConversationId == away).Select(m => m.FolderId).Distinct().ToListAsync(ct);
            foreach (var folder in folders)
            {
                long seq;
                if (folder == folderId)
                {
                    seq = folderSeq;
                    changedHere = true;
                }
                else
                {
                    var state = changedFolders.GetValueOrDefault(folder) ?? await db.Folders.AsNoTracking().SingleAsync(f => f.Id == folder, ct);
                    seq = state.ChangeSeq + 1;
                    await db.Folders.Where(f => f.Id == folder).ExecuteUpdateAsync(s => s.SetProperty(f => f.ChangeSeq, seq), ct);
                    state.ChangeSeq = seq;
                    changedFolders[folder] = state;
                }

                await db.Messages
                    .Where(m => m.FolderId == folder && m.ConversationId == away)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ConversationId, into).SetProperty(m => m.ChangeSeq, seq), ct);
            }
        }

        db.ConversationLinks.AddRange(known.Where(k => !stored.Contains(k.Key)).Select(k => new ConversationLink
        {
            AccountId = accountId,
            MessageIdHeader = k.Key,
            OwnerUserId = ownerUserId,
            ConversationId = Find(k.Value),
        }));

        return new ConversationAssignment(assigned.Select(Find).ToList(), changedFolders.Values.ToList(), changedHere);
    }
}
