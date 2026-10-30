using MailKit;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Tags;

/// <summary>
/// Keeps <see cref="TaggedMessage"/> rows in step with what the app sees: tag changes it makes,
/// folder syncs, moves and removals. Where the server keeps keywords, a row is a copy that can be
/// dropped and found again; for <see cref="TagStorage.Local"/> accounts it is the only record, so
/// a message that leaves its place is kept with an unknown place (UID 0) until a folder sync meets
/// its Message-ID again. Callers hold the folder's sync lock, as for the cache.
/// </summary>
public sealed class TagIndex(MailDbContext db, TimeProvider clock)
{
    private const int Chunk = 500;

    private readonly Dictionary<Guid, TagStorage> _storage = [];

    public async Task<TagStorage> StorageAsync(Guid accountId, CancellationToken ct)
    {
        if (!_storage.TryGetValue(accountId, out var storage))
        {
            storage = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.TagStorage).FirstOrDefaultAsync(ct);
            _storage[accountId] = storage;
        }

        return storage;
    }

    public async Task SetStorageAsync(Guid accountId, TagStorage storage, CancellationToken ct)
    {
        await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.TagStorage, storage), ct);
        _storage[accountId] = storage;
    }

    /// <summary>The keywords of messages of a folder, as the index has them; for accounts whose server keeps none.</summary>
    public async Task<Dictionary<uint, string>> KeywordsAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids,
        CancellationToken ct)
    {
        var found = new Dictionary<uint, string>();
        foreach (var chunk in uids.Distinct().Chunk(Chunk))
        {
            var rows = await db.TaggedMessages.AsNoTracking()
                .Where(m => m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && chunk.Contains(m.Uid))
                .Select(m => new { m.Uid, m.Keywords })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                found[row.Uid] = row.Keywords;
            }
        }

        return found;
    }

    /// <summary>
    /// Records messages just fetched from the server with their keywords after a tag change: a
    /// row for each that has a tag keyword, none for the others.
    /// </summary>
    public async Task RecordAsync(MailAccount account, string fullName, uint uidValidity,
        IEnumerable<(IMessageSummary Summary, string Keywords, BimiDto? Bimi)> messages, CancellationToken ct)
    {
        var list = messages.ToList();
        var uids = list.Select(m => m.Summary.UniqueId.Id).ToList();
        var existing = await RowsAsync(account.Id, fullName, uidValidity, uids, ct);
        var now = clock.GetUtcNow();
        foreach (var (summary, keywords, sender) in list)
        {
            var uid = summary.UniqueId.Id;
            if (!Keywords.HasTagKeyword(keywords))
            {
                if (existing.Remove(uid, out var gone))
                {
                    db.TaggedMessages.Remove(gone);
                }

                continue;
            }

            if (!existing.TryGetValue(uid, out var row))
            {
                row = NewRow(account, fullName, uidValidity, uid, keywords);
                db.TaggedMessages.Add(row);
                existing[uid] = row;
            }

            var envelope = summary.Envelope;
            row.Keywords = keywords;
            row.MessageIdHeader = MessageIdOf(envelope?.MessageId) ?? row.MessageIdHeader;
            row.Subject = envelope?.Subject ?? "";
            row.From = AddressDto.From(envelope?.From);
            row.To = AddressDto.From(envelope?.To);
            row.Date = envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.UnixEpoch;
            row.Seen = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false;
            row.Flagged = summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false;
            row.Answered = summary.Flags?.HasFlag(MessageFlags.Answered) ?? false;
            row.HasAttachments = summary.Attachments.Any();
            row.Size = (long)(summary.Size ?? 0);
            row.Preview = summary.PreviewText is null ? row.Preview : MessagePreview.Normalize(summary.PreviewText);
            row.BimiDomain = sender?.Domain ?? "";
            row.BimiSelector = sender?.Selector;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// For messages a sync is about to cache in an account without server keywords: the keywords
    /// the index has for them, found by their place or, for rows whose place was lost, by
    /// Message-ID (which also gives those rows their new place).
    /// </summary>
    public async Task<Dictionary<uint, string>> ClaimAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<IMessageSummary> inserts,
        CancellationToken ct)
    {
        if (inserts.Count == 0 || !(await StorageAsync(accountId, ct)).IsAppKept())
        {
            return [];
        }

        var found = (await RowsAsync(accountId, fullName, uidValidity, inserts.Select(s => s.UniqueId.Id).ToList(), ct))
            .ToDictionary(r => r.Key, r => r.Value.Keywords);
        var byMessageId = inserts
            .Where(s => !found.ContainsKey(s.UniqueId.Id) && MessageIdOf(s.Envelope?.MessageId) is not null)
            .GroupBy(s => MessageIdOf(s.Envelope!.MessageId)!)
            .ToDictionary(g => g.Key, g => g.First().UniqueId.Id);
        foreach (var chunk in byMessageId.Keys.Chunk(Chunk))
        {
            var lost = await db.TaggedMessages
                .Where(m => m.AccountId == accountId && m.Uid == 0 && m.MessageIdHeader != null && chunk.Contains(m.MessageIdHeader))
                .ToListAsync(ct);
            foreach (var row in lost.DistinctBy(r => r.MessageIdHeader))
            {
                var uid = byMessageId[row.MessageIdHeader!];
                row.FolderName = fullName;
                row.UidValidity = uidValidity;
                row.Uid = uid;
                row.UpdatedAt = clock.GetUtcNow();
                found[uid] = row.Keywords;
            }
        }

        return found;
    }

    /// <summary>
    /// After a sync wrote cached rows: for accounts whose server keeps keywords, the index follows
    /// the cache (a row for each cached message with a tag keyword); for the others, the list
    /// fields of rows already there are refreshed.
    /// </summary>
    public async Task FollowCacheAsync(Guid accountId, CachedFolder state, IReadOnlyCollection<uint> uids, CancellationToken ct)
    {
        if (uids.Count == 0)
        {
            return;
        }

        var local = (await StorageAsync(accountId, ct)).IsAppKept();
        var now = clock.GetUtcNow();
        foreach (var chunk in uids.Distinct().Chunk(Chunk))
        {
            var cached = await db.Messages.AsNoTracking().Where(m => m.FolderId == state.Id && chunk.Contains(m.Uid)).ToListAsync(ct);
            var rows = await RowsAsync(accountId, state.FullName, state.UidValidity, chunk, ct);
            foreach (var message in cached)
            {
                rows.TryGetValue(message.Uid, out var row);
                if (!local && !Keywords.HasTagKeyword(message.Keywords))
                {
                    if (row is not null)
                    {
                        db.TaggedMessages.Remove(row);
                    }

                    continue;
                }

                if (row is null)
                {
                    if (local)
                    {
                        continue;
                    }

                    row = NewRow(state.AccountId, state.OwnerUserId, state.FullName, state.UidValidity, message.Uid, message.Keywords!);
                    db.TaggedMessages.Add(row);
                }

                if (!local)
                {
                    row.Keywords = message.Keywords!;
                }

                row.Subject = message.Subject;
                row.From = message.From;
                row.To = message.To;
                row.Date = message.Date;
                row.Seen = message.Flags.HasFlag(MessageFlags.Seen);
                row.Flagged = message.Flags.HasFlag(MessageFlags.Flagged);
                row.Answered = message.Flags.HasFlag(MessageFlags.Answered);
                row.HasAttachments = message.HasAttachments;
                row.Size = message.Size;
                row.Preview = message.Preview ?? row.Preview;
                row.BimiDomain = message.BimiDomain ?? row.BimiDomain;
                row.BimiSelector = message.BimiDomain is null ? row.BimiSelector : message.BimiSelector;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Messages that left a folder without the app knowing where to: dropped where the server
    /// keeps keywords (a sync or scan finds them again), kept with an unknown place otherwise.
    /// A null <paramref name="uids"/> means every message of the folder not under <paramref name="uidValidity"/>.
    /// </summary>
    public async Task LoseAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint>? uids, CancellationToken ct)
    {
        var local = (await StorageAsync(accountId, ct)).IsAppKept();
        foreach (var chunk in uids is null ? [null] : uids.Chunk(Chunk).Select(c => (uint[]?)c))
        {
            var rows = db.TaggedMessages.Where(m => m.AccountId == accountId && m.FolderName == fullName && m.Uid != 0);
            rows = chunk is null
                ? rows.Where(m => m.UidValidity != uidValidity)
                : rows.Where(m => m.UidValidity == uidValidity && chunk.Contains(m.Uid));
            // UpdatedAt of a lost row says since when it is lost (see TagScan).
            var now = clock.GetUtcNow();
            await (local
                ? rows.ExecuteUpdateAsync(s => s.SetProperty(m => m.Uid, 0u).SetProperty(m => m.UpdatedAt, now), ct)
                : rows.ExecuteDeleteAsync(ct));
        }
    }

    /// <summary>Every message of a folder that is gone (deleted, or renamed: then a sync of the new name finds local tags again).</summary>
    public Task LoseFolderAsync(Guid accountId, string fullName, CancellationToken ct) =>
        LoseAsync(accountId, fullName, 0, null, ct);

    /// <summary>Messages deleted for good.</summary>
    public async Task ForgetAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids, CancellationToken ct)
    {
        foreach (var chunk in uids.Chunk(Chunk))
        {
            await db.TaggedMessages
                .Where(m => m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && chunk.Contains(m.Uid))
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Messages the app moved. With the server's UID map (UIDPLUS) each row follows its message,
    /// and a local account's tags are copied to the target's cached row if it is there already;
    /// without one, rows are lost (see <see cref="LoseAsync"/>) and found again in the target.
    /// </summary>
    public async Task MoveAsync(Guid accountId, string from, uint uidValidity, IReadOnlyCollection<uint> uids, string to, UniqueIdMap map,
        CancellationToken ct)
    {
        var moved = new Dictionary<uint, UniqueId>();
        for (var i = 0; i < map.Source.Count && i < map.Destination.Count; i++)
        {
            moved[map.Source[i].Id] = map.Destination[i];
        }

        var rows = await RowsAsync(accountId, from, uidValidity, uids.ToList(), ct);
        var lost = rows.Keys.Where(u => !moved.ContainsKey(u)).ToList();
        var targets = new Dictionary<uint, string>();
        foreach (var (uid, row) in rows.Where(r => moved.ContainsKey(r.Key)))
        {
            var target = moved[uid];
            row.FolderName = to;
            row.UidValidity = target.Validity;
            row.Uid = target.Id;
            row.UpdatedAt = clock.GetUtcNow();
            targets[target.Id] = row.Keywords;
        }

        await db.SaveChangesAsync(ct);
        await LoseAsync(accountId, from, uidValidity, lost, ct);
        if (targets.Count > 0 && (await StorageAsync(accountId, ct)).IsAppKept())
        {
            var folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(f => f.AccountId == accountId && f.FullName == to, ct);
            if (folder is not null && folder.UidValidity == moved.Values.First().Validity)
            {
                foreach (var (uid, keywords) in targets)
                {
                    await db.Messages.Where(m => m.FolderId == folder.Id && m.Uid == uid)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Keywords, keywords), ct);
                }
            }
        }
    }

    /// <summary>Flags the app changed, for the list fields.</summary>
    public async Task ApplyFlagsAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids, bool? seen, bool? flagged,
        CancellationToken ct)
    {
        foreach (var chunk in uids.Chunk(Chunk))
        {
            var rows = db.TaggedMessages.Where(m =>
                m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && chunk.Contains(m.Uid));
            if (seen is { } s)
            {
                await rows.ExecuteUpdateAsync(u => u.SetProperty(m => m.Seen, s), ct);
            }

            if (flagged is { } f)
            {
                await rows.ExecuteUpdateAsync(u => u.SetProperty(m => m.Flagged, f), ct);
            }
        }
    }

    /// <summary>
    /// A scan's answer for one folder (server keywords): the flags of every message that has a tag
    /// keyword or an index row. Rows whose message is gone or has no tag keyword any more are
    /// dropped, the others updated.
    /// </summary>
    /// <returns>Messages that need a row (or lack their Message-ID), to be fetched in full; and whether rows changed.</returns>
    public async Task<(List<uint> Needed, bool Changed)> ApplyScanAsync(Guid accountId, string fullName, uint uidValidity,
        IReadOnlyCollection<IMessageSummary> summaries, CancellationToken ct)
    {
        var rows = await db.TaggedMessages
            .Where(m => m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && m.Uid != 0)
            .ToListAsync(ct);
        var byUid = new Dictionary<uint, TaggedMessage>();
        foreach (var row in rows)
        {
            if (!byUid.TryAdd(row.Uid, row))
            {
                db.TaggedMessages.Remove(row);
            }
        }

        var fetched = summaries.ToDictionary(s => s.UniqueId.Id);
        var needed = new List<uint>();
        var changed = false;
        foreach (var (uid, row) in byUid)
        {
            var keywords = fetched.TryGetValue(uid, out var summary) ? Keywords.Format(summary.Keywords) : "";
            if (!Keywords.HasTagKeyword(keywords))
            {
                db.TaggedMessages.Remove(row);
                changed = true;
                continue;
            }

            changed |= SetFlags(row, summary!.Flags ?? MessageFlags.None) | row.Keywords != keywords;
            row.Keywords = keywords;
            if (row.MessageIdHeader is null)
            {
                // Rows made from the cache have no Message-ID, which tag folders use to show a message once.
                needed.Add(uid);
            }
        }

        needed.AddRange(fetched.Keys.Where(u => !byUid.ContainsKey(u) && Keywords.HasTagKeyword(Keywords.Format(fetched[u].Keywords))));
        await db.SaveChangesAsync(ct);
        return (needed, changed);
    }

    /// <summary>A scan's check of app-kept rows in a folder: rows whose message is gone lose their place, the others get its flags.</summary>
    /// <returns>Whether rows changed, and the rows that lost their place (tracked, for <see cref="RelocateAsync"/>).</returns>
    public async Task<(bool Changed, List<TaggedMessage> Lost)> ApplyLocalScanAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<IMessageSummary> present,
        CancellationToken ct)
    {
        var rows = await db.TaggedMessages
            .Where(m => m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && m.Uid != 0)
            .ToListAsync(ct);
        var fetched = present.ToDictionary(s => s.UniqueId.Id);
        var changed = false;
        var lost = new List<TaggedMessage>();
        foreach (var row in rows)
        {
            if (fetched.TryGetValue(row.Uid, out var summary))
            {
                changed |= SetFlags(row, summary.Flags ?? MessageFlags.None);
            }
            else
            {
                row.Uid = 0;
                row.UpdatedAt = clock.GetUtcNow();
                lost.Add(row);
                changed = true;
            }
        }

        await db.SaveChangesAsync(ct);
        return (changed, lost);
    }

    /// <summary>Gives a lost app-kept row the place a scan found its message at, and the cached row there its tags.</summary>
    public async Task RelocateAsync(TaggedMessage row, string fullName, uint uidValidity, uint uid, CancellationToken ct)
    {
        row.FolderName = fullName;
        row.UidValidity = uidValidity;
        row.Uid = uid;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await SetCachedKeywordsAsync(row.AccountId, fullName, uidValidity, uid, row.Keywords, ct);
    }

    /// <summary>An app-kept message's keywords in its folder's cache, if the cache holds it; the folder's tabs are told by the caller's push.</summary>
    public async Task SetCachedKeywordsAsync(Guid accountId, string fullName, uint uidValidity, uint uid, string keywords, CancellationToken ct)
    {
        var folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(f => f.AccountId == accountId && f.FullName == fullName, ct);
        if (folder is not null && folder.UidValidity == uidValidity)
        {
            var seq = folder.ChangeSeq + 1;
            var updated = await db.Messages.Where(m => m.FolderId == folder.Id && m.Uid == uid && m.Keywords != keywords)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Keywords, keywords).SetProperty(m => m.ChangeSeq, seq), ct);
            if (updated > 0)
            {
                await db.Folders.Where(f => f.Id == folder.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.ChangeSeq, seq), ct);
            }
        }
    }

    private static bool SetFlags(TaggedMessage row, MessageFlags flags)
    {
        var (seen, flagged, answered) = (flags.HasFlag(MessageFlags.Seen), flags.HasFlag(MessageFlags.Flagged), flags.HasFlag(MessageFlags.Answered));
        if (row.Seen == seen && row.Flagged == flagged && row.Answered == answered)
        {
            return false;
        }

        (row.Seen, row.Flagged, row.Answered) = (seen, flagged, answered);
        return true;
    }

    /// <summary>Where the user's messages with a keyword are, by account and folder; rows without a place are left out.</summary>
    public async Task<List<(Guid AccountId, string FolderName, uint UidValidity, List<uint> Uids)>> PlacesAsync(string userId, string keyword,
        CancellationToken ct)
    {
        var rows = await db.TaggedMessages.AsNoTracking()
            .Where(m => m.OwnerUserId == userId && m.Uid != 0 && EF.Functions.Like(m.Keywords, $"% {Like(keyword)} %", "\\"))
            .Select(m => new { m.AccountId, m.FolderName, m.UidValidity, m.Uid })
            .ToListAsync(ct);
        return rows.GroupBy(r => (r.AccountId, r.FolderName, r.UidValidity))
            .Select(g => (g.Key.AccountId, g.Key.FolderName, g.Key.UidValidity, g.Select(r => r.Uid).ToList()))
            .ToList();
    }

    /// <summary>Takes a keyword off rows without a place (app-kept tags of messages not found again yet).</summary>
    public async Task RemoveFromLostAsync(string userId, string keyword, CancellationToken ct)
    {
        var rows = await db.TaggedMessages
            .Where(m => m.OwnerUserId == userId && m.Uid == 0 && EF.Functions.Like(m.Keywords, $"% {Like(keyword)} %", "\\"))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Keywords = Keywords.Change(row.Keywords, [], [keyword]);
            if (!Keywords.HasTagKeyword(row.Keywords))
            {
                db.TaggedMessages.Remove(row);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A keyword as a literal LIKE pattern ("_" is a wildcard otherwise).</summary>
    public static string Like(string keyword) => keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>The Message-ID without angle brackets and blanks; null when there is none.</summary>
    public static string? MessageIdOf(string? header)
    {
        var id = header?.Trim().Trim('<', '>').Trim();
        return string.IsNullOrEmpty(id) || id.Length > 900 ? null : id;
    }

    private async Task<Dictionary<uint, TaggedMessage>> RowsAsync(Guid accountId, string fullName, uint uidValidity, IReadOnlyCollection<uint> uids,
        CancellationToken ct)
    {
        var rows = new Dictionary<uint, TaggedMessage>();
        foreach (var chunk in uids.Distinct().Chunk(Chunk))
        {
            var found = await db.TaggedMessages
                .Where(m => m.AccountId == accountId && m.FolderName == fullName && m.UidValidity == uidValidity && chunk.Contains(m.Uid))
                .ToListAsync(ct);
            foreach (var row in found)
            {
                // Two rows for one message would be a bug; keep one.
                if (!rows.TryAdd(row.Uid, row))
                {
                    db.TaggedMessages.Remove(row);
                }
            }
        }

        return rows;
    }

    private TaggedMessage NewRow(MailAccount account, string fullName, uint uidValidity, uint uid, string keywords) =>
        NewRow(account.Id, account.OwnerUserId, fullName, uidValidity, uid, keywords);

    private TaggedMessage NewRow(Guid accountId, string ownerUserId, string fullName, uint uidValidity, uint uid, string keywords) => new()
    {
        AccountId = accountId,
        OwnerUserId = ownerUserId,
        FolderName = fullName,
        UidValidity = uidValidity,
        Uid = uid,
        Keywords = keywords,
        Subject = "",
        From = [],
        To = [],
        UpdatedAt = clock.GetUtcNow(),
    };
}
