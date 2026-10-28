using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Sync;

/// <summary>
/// What changed in a folder after a client's token: messages added or changed (newest first) and
/// ids removed. <see cref="Reset"/> means the token is too old (or from before the window was
/// rebuilt) and the client must reload the list.
/// </summary>
public sealed record FolderChanges(
    long Token, uint UidValidity, int Total, int? Unread, bool Reset,
    IReadOnlyList<MessageSummaryDto> Messages, IReadOnlyList<string> Removed);

/// <summary>
/// The local header cache of IMAP folders (see <see cref="CachedFolder"/>) and the sync that keeps
/// it current. A sync first reads everything it needs from the server and only then writes the
/// database in one transaction, so a connection that breaks halfway leaves the cache untouched
/// and the sync can simply run again. How it finds changes depends on the server:
/// <list type="bullet">
/// <item>QRESYNC: <c>UID FETCH … (CHANGEDSINCE m VANISHED)</c> returns changed flags and removed UIDs.</item>
/// <item>CONDSTORE: <c>CHANGEDSINCE</c> for flags; a <c>UID SEARCH</c> over the window finds removals,
/// and only when the message count or UIDNEXT moved.</item>
/// <item>Neither: the flags of the whole window are fetched and compared.</item>
/// </list>
/// Callers hold the folder's <see cref="SyncLocks"/> lock around every method that writes.
/// </summary>
public sealed class FolderCache(MailDbContext db, TimeProvider clock, ILogger<FolderCache> logger)
{
    /// <summary>More new messages than this since the last sync: rebuild the window from the newest page instead.</summary>
    public const int MaxNewMessages = 500;

    /// <summary>More changes than this since a client's token: the client reloads instead.</summary>
    public const int MaxChanges = 500;

    public static readonly TimeSpan RemovedRetention = TimeSpan.FromDays(1);

    internal const MessageSummaryItems ListItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
        | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure;

    private const MessageFlags SystemFlags =
        MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Deleted | MessageFlags.Draft;

    /// <summary>Ids per SQL statement, well below SQLite's parameter limit.</summary>
    private const int Chunk = 500;

    /// <summary>Everything a sync found on the server, applied to the database in one go.</summary>
    private sealed class SyncPlan
    {
        /// <summary>Drop all cached rows first (UIDVALIDITY changed, or the window is rebuilt).</summary>
        public bool Wipe;

        /// <summary>Rows added by paging further back are not changes: they keep the current token.</summary>
        public bool Quiet;

        public readonly List<IMessageSummary> Inserts = [];
        public readonly Dictionary<uint, MessageFlags> Flags = [];
        public readonly HashSet<uint> Removals = [];
        public uint? WindowStart;
        public uint? LastUid;

        public bool ChangesRows => Wipe || Inserts.Count > 0 || Flags.Count > 0 || Removals.Count > 0;
    }

    /// <summary>The cache key of a folder: its full name, with any spelling of INBOX as "INBOX".</summary>
    public static string Key(string fullName) =>
        fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : fullName;

    public Task<CachedFolder?> FindAsync(Guid accountId, string fullName, CancellationToken ct) =>
        db.Folders.AsNoTracking().SingleOrDefaultAsync(f => f.AccountId == accountId && f.FullName == fullName, ct);

    /// <summary>Brings the cache of <paramref name="folder"/> (just opened on <paramref name="client"/>) up to date.</summary>
    /// <param name="initialWindow">Messages to cache when the folder has no window yet.</param>
    public async Task<CachedFolder> SyncAsync(ImapClient client, MailAccount account, IMailFolder folder, int initialWindow, CancellationToken ct)
    {
        var state = await FindAsync(account.Id, Key(folder.FullName), ct);
        // Values from the SELECT/EXAMINE; changes after that are found by the next sync.
        var modSeq = client.Capabilities.HasFlag(ImapCapabilities.CondStore) ? folder.HighestModSeq : 0;
        var uidNext = folder.UidNext?.Id;
        var count = folder.Count;
        var plan = new SyncPlan();

        if (state?.WindowStart is null || state.UidValidity != folder.UidValidity)
        {
            plan.Wipe = state is not null;
            await LoadNewestAsync(folder, count, uidNext, initialWindow, plan, ct);
        }
        else
        {
            await SyncWindowAsync(client, folder, state, modSeq, count, uidNext, plan, ct);
            await FetchNewAsync(folder, state, count, uidNext, initialWindow, plan, ct);
        }

        // UNSEEN covers the whole folder, not just the window. Without mod-sequences there is no
        // cheap way to know whether it moved, so it is counted on every sync.
        var unread = state?.Unread;
        if (unread is null || plan.ChangesRows || modSeq == 0 || modSeq != state!.HighestModSeq || count != state.Total)
        {
            unread = await CountUnreadAsync(client, folder, ct);
        }

        return await ApplyAsync(account, Key(folder.FullName), plan, s =>
        {
            s.UidValidity = folder.UidValidity;
            s.HighestModSeq = modSeq;
            s.UidNext = uidNext;
            s.Total = count;
            s.Unread = unread;
            s.SyncedAt = clock.GetUtcNow();
        }, ct);
    }

    /// <summary>
    /// Extends the window downwards so that at least <paramref name="need"/> messages below
    /// <paramref name="before"/> are cached (fewer if the folder has no more). Runs right after
    /// <see cref="SyncAsync"/> on the same open folder.
    /// </summary>
    public async Task<CachedFolder> ExtendAsync(MailAccount account, IMailFolder folder, CachedFolder state, uint? before, int need, CancellationToken ct)
    {
        if (state.WindowStart is not { } start || start <= 1)
        {
            return state;
        }

        var older = await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(state.UidValidity, 1), new UniqueId(state.UidValidity, start - 1))), ct);
        var ids = older.Select(u => u.Id).Where(u => u < start).Order().ToList();
        // The window must stay contiguous: everything between the cursor and the window comes along.
        var belowCursor = before is { } b ? ids.Count(u => u < b) : ids.Count;
        var from = Math.Max(0, belowCursor - need);
        var fetch = ids[from..];

        var plan = new SyncPlan { Quiet = true, WindowStart = from == 0 ? 1 : fetch[0] };
        if (fetch.Count > 0)
        {
            plan.Inserts.AddRange(await folder.FetchAsync(fetch.Select(u => new UniqueId(state.UidValidity, u)).ToList(), ListItems, ct));
        }

        return await ApplyAsync(account, Key(folder.FullName), plan, null, ct);
    }

    /// <summary>Newest first; <paramref name="take"/> rows below <paramref name="before"/> (all when null).</summary>
    public Task<List<CachedMessage>> PageAsync(CachedFolder state, uint? before, int take, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.FolderId == state.Id);
        if (before is { } b)
        {
            query = query.Where(m => m.Uid < b);
        }

        return query.OrderByDescending(m => m.Uid).Take(take).ToListAsync(ct);
    }

    /// <summary>
    /// Newest first by date: <paramref name="take"/> rows that come after <paramref name="cursor"/>
    /// in the unified inbox order (date, account id, UID; all descending).
    /// </summary>
    public Task<List<CachedMessage>> PageByDateAsync(CachedFolder state, UnifiedCursor? cursor, int take, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.FolderId == state.Id);
        if (cursor is { } c)
        {
            // Within one folder the account is fixed, so the account part of the order is a constant.
            var account = state.AccountId.CompareTo(c.AccountId);
            query = account < 0 ? query.Where(m => m.Date <= c.Date)
                : account > 0 ? query.Where(m => m.Date < c.Date)
                : query.Where(m => m.Date < c.Date || (m.Date == c.Date && m.Uid < c.Uid));
        }

        return query.OrderByDescending(m => m.Date).ThenByDescending(m => m.Uid).Take(take).ToListAsync(ct);
    }

    public async Task<FolderChanges> ChangesAsync(CachedFolder state, long since, CancellationToken ct)
    {
        FolderChanges Result(bool reset, IReadOnlyList<MessageSummaryDto> messages, IReadOnlyList<string> removed) =>
            new(state.ChangeSeq, state.UidValidity, state.Total, state.Unread, reset, messages, removed);

        if (since < state.ResetSeq || since > state.ChangeSeq)
        {
            return Result(true, [], []);
        }

        if (since == state.ChangeSeq)
        {
            return Result(false, [], []);
        }

        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.FolderId == state.Id && m.ChangeSeq > since)
            .OrderByDescending(m => m.Uid)
            .Take(MaxChanges + 1)
            .ToListAsync(ct);
        if (rows.Count > MaxChanges)
        {
            return Result(true, [], []);
        }

        var removed = await db.RemovedMessages.AsNoTracking()
            .Where(r => r.FolderId == state.Id && r.ChangeSeq > since)
            .Select(r => r.Uid)
            .ToListAsync(ct);
        return Result(false,
            rows.Select(m => ToSummaryDto(state.UidValidity, m)).ToList(),
            removed.Select(uid => MailboxIds.MessageId(state.UidValidity, uid)).ToList());
    }

    /// <summary>
    /// Cached messages of all of <paramref name="userId"/>'s accounts whose subject, names or
    /// addresses contain every term, newest first. Only what the cache holds is found: messages
    /// of folders the user has opened, as far back as they paged.
    /// </summary>
    public async Task<CachedSearchResult> SearchAsync(string userId, IReadOnlyList<string> terms, int limit, CancellationToken ct)
    {
        var messages = db.Messages.AsNoTracking().Where(m => m.OwnerUserId == userId);
        foreach (var term in terms.Select(SearchTerms.Normalize))
        {
            messages = messages.Where(m => m.SearchText.Contains(term));
        }

        var rows = await messages
            .Join(db.Folders.AsNoTracking(), m => m.FolderId, f => f.Id, (m, f) => new { Message = m, f.AccountId, f.FullName, f.UidValidity })
            .OrderByDescending(x => x.Message.Date)
            .Take(limit + 1)
            .ToListAsync(ct);
        return new CachedSearchResult(
            rows.Take(limit).Select(x => new SearchHit(x.AccountId, MailboxIds.FolderId(x.FullName), x.FullName, ToSummaryDto(x.UidValidity, x.Message))).ToList(),
            rows.Count > limit);
    }

    /// <summary>Applies a flag change the app itself just made on the server.</summary>
    public async Task ApplyFlagsAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids,
        bool? seen, bool? flagged, CancellationToken ct)
    {
        var state = await db.Folders.SingleOrDefaultAsync(f => f.AccountId == accountId && f.FullName == fullName, ct);
        if (state is null || state.UidValidity != uidValidity)
        {
            return;
        }

        var seq = state.ChangeSeq + 1;
        var changed = 0;
        foreach (var chunk in uids.Chunk(Chunk))
        {
            var rows = await db.Messages.Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid)).ToListAsync(ct);
            foreach (var row in rows)
            {
                var flags = row.Flags;
                flags = seen is { } s ? (s ? flags | MessageFlags.Seen : flags & ~MessageFlags.Seen) : flags;
                flags = flagged is { } f ? (f ? flags | MessageFlags.Flagged : flags & ~MessageFlags.Flagged) : flags;
                if (flags == row.Flags)
                {
                    continue;
                }

                if (state.Unread is { } unread && (flags & MessageFlags.Seen) != (row.Flags & MessageFlags.Seen))
                {
                    state.Unread = Math.Max(0, unread + (seen == true ? -1 : 1));
                }

                row.Flags = flags;
                row.ChangeSeq = seq;
                changed++;
            }
        }

        if (changed > 0)
        {
            state.ChangeSeq = seq;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Removes messages the app itself just moved or deleted.</summary>
    public async Task RemoveAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids, CancellationToken ct)
    {
        var state = await db.Folders.SingleOrDefaultAsync(f => f.AccountId == accountId && f.FullName == fullName, ct);
        if (state is null || state.UidValidity != uidValidity)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var seq = state.ChangeSeq + 1;
        var removed = await RemoveRowsAsync(state, uids, seq, ct);
        if (removed.Count > 0)
        {
            state.Total = Math.Max(0, state.Total - removed.Count);
            if (state.Unread is { } unread)
            {
                state.Unread = Math.Max(0, unread - removed.Count(r => !r.Flags.HasFlag(MessageFlags.Seen)));
            }

            state.ChangeSeq = seq;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Drops the cache of folders that no longer exist on the server (deleted or renamed).</summary>
    public async Task ForgetMissingFoldersAsync(Guid accountId, IEnumerable<string> existing, CancellationToken ct)
    {
        var names = existing.ToHashSet(StringComparer.Ordinal);
        var cached = await db.Folders.Where(f => f.AccountId == accountId).Select(f => new { f.Id, f.FullName }).ToListAsync(ct);
        var stale = cached.Where(f => !names.Contains(f.FullName)).Select(f => f.Id).ToList();
        if (stale.Count > 0)
        {
            await db.Folders.Where(f => stale.Contains(f.Id)).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Deletes removal records older than <see cref="RemovedRetention"/>, for every user. The
    /// folder's <see cref="CachedFolder.ResetSeq"/> moves up first, so a client whose token predates
    /// a pruned removal reloads rather than keep a message that is gone.
    /// </summary>
    public async Task PruneRemovedAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - RemovedRetention;
        var horizons = await db.RemovedMessages.IgnoreQueryFilters()
            .Where(r => r.RemovedAt < cutoff)
            .GroupBy(r => r.FolderId)
            .Select(g => new { FolderId = g.Key, Seq = g.Max(r => r.ChangeSeq) })
            .ToListAsync(ct);
        foreach (var horizon in horizons)
        {
            await db.Folders.IgnoreQueryFilters()
                .Where(f => f.Id == horizon.FolderId && f.ResetSeq < horizon.Seq)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ResetSeq, horizon.Seq), ct);
        }

        await db.RemovedMessages.IgnoreQueryFilters().Where(r => r.RemovedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    public static MessageSummaryDto ToSummaryDto(uint uidValidity, CachedMessage m) => new(
        MailboxIds.MessageId(uidValidity, m.Uid),
        m.Subject,
        m.From,
        m.To,
        m.Date,
        m.Flags.HasFlag(MessageFlags.Seen),
        m.Flags.HasFlag(MessageFlags.Flagged),
        m.Flags.HasFlag(MessageFlags.Answered),
        m.HasAttachments,
        m.Size);

    private static async Task LoadNewestAsync(IMailFolder folder, int count, uint? uidNext, int window, SyncPlan plan, CancellationToken ct)
    {
        var first = Math.Max(0, count - window);
        var summaries = count == 0 ? [] : await folder.FetchAsync(first, count - 1, ListItems, ct);
        plan.Inserts.AddRange(summaries);
        plan.WindowStart = first == 0 || summaries.Count == 0 ? 1 : summaries.Min(s => s.UniqueId.Id);
        plan.LastUid = Math.Max(summaries.Count == 0 ? 0 : summaries.Max(s => s.UniqueId.Id), (uidNext ?? 1) - 1);
    }

    /// <summary>Flag changes and removals inside the cached window.</summary>
    private async Task SyncWindowAsync(ImapClient client, IMailFolder folder, CachedFolder state, ulong modSeq, int count, uint? uidNext,
        SyncPlan plan, CancellationToken ct)
    {
        var cached = await db.Messages.AsNoTracking()
            .Where(m => m.FolderId == state.Id)
            .Select(m => new { m.Uid, m.Flags })
            .ToDictionaryAsync(m => m.Uid, m => m.Flags, ct);
        if (cached.Count == 0)
        {
            return;
        }

        var range = new UniqueIdRange(state.UidValidity, state.WindowStart!.Value, Math.Max(state.WindowStart.Value, state.LastUid));
        var condStore = modSeq > 0 && state.HighestModSeq > 0;
        IList<IMessageSummary> changed;
        HashSet<uint>? present = null;
        List<UniqueId> vanished = [];

        if (condStore && client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
        {
            if (modSeq == state.HighestModSeq)
            {
                return;
            }

            void OnVanished(object? sender, MessagesVanishedEventArgs e) => vanished.AddRange(e.UniqueIds);
            folder.MessagesVanished += OnVanished;
            try
            {
                changed = await folder.FetchAsync(range, ChangedSince(state.HighestModSeq), ct);
            }
            finally
            {
                folder.MessagesVanished -= OnVanished;
            }
        }
        else if (condStore)
        {
            changed = modSeq == state.HighestModSeq ? [] : await folder.FetchAsync(range, ChangedSince(state.HighestModSeq), ct);
            // Without an added or removed message, neither the count nor UIDNEXT moves.
            if (count != state.Total || uidNext is null || uidNext != state.UidNext)
            {
                present = (await folder.SearchAsync(SearchQuery.Uids(range), ct)).Select(u => u.Id).ToHashSet();
            }
        }
        else
        {
            changed = await folder.FetchAsync(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
            present = changed.Select(s => s.UniqueId.Id).ToHashSet();
        }

        foreach (var summary in changed)
        {
            if (summary.Flags is { } flags && cached.TryGetValue(summary.UniqueId.Id, out var old) && (flags & SystemFlags) != old)
            {
                plan.Flags[summary.UniqueId.Id] = flags & SystemFlags;
            }
        }

        foreach (var uid in vanished)
        {
            if (cached.ContainsKey(uid.Id))
            {
                plan.Removals.Add(uid.Id);
                plan.Flags.Remove(uid.Id);
            }
        }

        if (present is not null)
        {
            foreach (var uid in cached.Keys.Where(u => !present.Contains(u)))
            {
                plan.Removals.Add(uid);
                plan.Flags.Remove(uid);
            }
        }
    }

    /// <summary>Messages above <see cref="CachedFolder.LastUid"/>.</summary>
    private async Task FetchNewAsync(IMailFolder folder, CachedFolder state, int count, uint? uidNext, int window, SyncPlan plan, CancellationToken ct)
    {
        if (uidNext is { } next && next - 1 <= state.LastUid)
        {
            return;
        }

        // "n:*" always includes the highest UID, even when it is below n; filter those out.
        var found = await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(state.UidValidity, state.LastUid + 1), UniqueId.MaxValue)), ct);
        var fresh = found.Where(u => u.Id > state.LastUid).ToList();
        if (fresh.Count > MaxNewMessages)
        {
            logger.LogInformation("{Count} new messages in a cached folder; rebuilding its window", fresh.Count);
            plan.Wipe = true;
            plan.Flags.Clear();
            plan.Removals.Clear();
            await LoadNewestAsync(folder, count, uidNext, window, plan, ct);
            return;
        }

        if (fresh.Count > 0)
        {
            plan.Inserts.AddRange(await folder.FetchAsync(fresh, ListItems, ct));
        }

        plan.LastUid = Math.Max(state.LastUid, Math.Max(fresh.Count == 0 ? 0 : fresh.Max(u => u.Id), (uidNext ?? 1) - 1));
    }

    private static FetchRequest ChangedSince(ulong modSeq) =>
        new(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.ModSeq) { ChangedSince = modSeq };

    private static async Task<int> CountUnreadAsync(ImapClient client, IMailFolder folder, CancellationToken ct) =>
        client.Capabilities.HasFlag(ImapCapabilities.ESearch)
            ? (await folder.SearchAsync(SearchOptions.Count, SearchQuery.NotSeen, ct)).Count
            : (await folder.SearchAsync(SearchQuery.NotSeen, ct)).Count;

    private async Task<CachedFolder> ApplyAsync(MailAccount account, string fullName, SyncPlan plan, Action<CachedFolder>? update, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await db.Folders.SingleOrDefaultAsync(f => f.AccountId == account.Id && f.FullName == fullName, ct);
        if (state is null)
        {
            state = new CachedFolder
            {
                Id = Guid.CreateVersion7(),
                AccountId = account.Id,
                OwnerUserId = account.OwnerUserId,
                FullName = fullName,
            };
            db.Folders.Add(state);
        }

        var seq = plan.Quiet ? state.ChangeSeq : state.ChangeSeq + 1;
        if (plan.Wipe)
        {
            await db.Messages.Where(m => m.FolderId == state.Id).ExecuteDeleteAsync(ct);
            await db.RemovedMessages.Where(r => r.FolderId == state.Id).ExecuteDeleteAsync(ct);
            // Clients holding an older token cannot be told what the wipe removed: they reload.
            state.ResetSeq = seq;
        }

        await RemoveRowsAsync(state, plan.Removals, seq, ct);

        foreach (var group in plan.Flags.GroupBy(f => f.Value, f => f.Key))
        {
            foreach (var chunk in group.Chunk(Chunk))
            {
                await db.Messages
                    .Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Flags, group.Key).SetProperty(m => m.ChangeSeq, seq), ct);
            }
        }

        var inserts = plan.Inserts.DistinctBy(s => s.UniqueId.Id).ToList();
        foreach (var chunk in inserts.Select(s => s.UniqueId.Id).Chunk(Chunk))
        {
            await db.Messages.Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid)).ExecuteDeleteAsync(ct);
        }

        db.Messages.AddRange(inserts.Select(s => ToCachedMessage(state, s, seq)));

        if (plan.WindowStart is { } windowStart)
        {
            state.WindowStart = windowStart;
        }

        if (plan.LastUid is { } lastUid)
        {
            state.LastUid = lastUid;
        }

        update?.Invoke(state);
        if (plan.ChangesRows)
        {
            state.ChangeSeq = seq;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return state;
    }

    /// <summary>Deletes rows and records the removals for polling clients; returns the rows that existed.</summary>
    private async Task<List<CachedMessage>> RemoveRowsAsync(CachedFolder state, IReadOnlyCollection<uint> uids, long seq, CancellationToken ct)
    {
        var removed = new List<CachedMessage>();
        var now = clock.GetUtcNow();
        foreach (var chunk in uids.Chunk(Chunk))
        {
            var rows = await db.Messages.AsNoTracking().Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid)).ToListAsync(ct);
            if (rows.Count == 0)
            {
                continue;
            }

            await db.Messages.Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid)).ExecuteDeleteAsync(ct);
            db.RemovedMessages.AddRange(rows.Select(r => new RemovedMessage
            {
                FolderId = state.Id,
                Uid = r.Uid,
                OwnerUserId = state.OwnerUserId,
                ChangeSeq = seq,
                RemovedAt = now,
            }));
            removed.AddRange(rows);
        }

        return removed;
    }

    private static CachedMessage ToCachedMessage(CachedFolder state, IMessageSummary s, long seq)
    {
        var subject = s.Envelope?.Subject ?? "";
        var from = AddressDto.From(s.Envelope?.From);
        var to = AddressDto.From(s.Envelope?.To);
        return new CachedMessage
        {
            FolderId = state.Id,
            Uid = s.UniqueId.Id,
            OwnerUserId = state.OwnerUserId,
            Subject = subject,
            From = from,
            To = to,
            SearchText = SearchText(subject, from.Concat(to)),
            Date = s.Envelope?.Date ?? s.InternalDate ?? DateTimeOffset.UnixEpoch,
            HasAttachments = s.Attachments.Any(),
            Size = (long)(s.Size ?? 0),
            Flags = (s.Flags ?? MessageFlags.None) & SystemFlags,
            ChangeSeq = seq,
        };
    }

    private static string SearchText(string subject, IEnumerable<AddressDto> addresses)
    {
        var text = SearchTerms.Normalize(string.Join('\n', addresses.Select(a => $"{a.Name} {a.Address}").Prepend(subject)));
        return text.Length > 4000 ? text[..4000] : text;
    }
}
