using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Tags;

/// <summary>
/// One account's tag scan, over one connection: makes the tag index (<see cref="TaggedMessage"/>)
/// complete beyond the folders the cache holds.
/// <list type="bullet">
/// <item>Where the server keeps keywords: every folder but Trash, Junk, Drafts and the virtual
/// ones (Gmail's All Mail, Starred, Important, which repeat other folders) is searched for the
/// user's tag keywords; a folder unchanged since (CONDSTORE) is skipped.</item>
/// <item>Where the app keeps the tags: messages whose place is known are checked to still be
/// there, and lost ones are looked for by Message-ID in every folder; a message lost for
/// <see cref="LostRetention"/> is taken as deleted. Microsoft accounts with Graph take in
/// Outlook's categories first (<see cref="OutlookCategories.ReconcileAsync"/>).</item>
/// </list>
/// Reads run without the folders' sync locks (the connection is leased already, see SyncLocks); a
/// row a sync writes meanwhile is set right by the next scan or sync.
/// </summary>
public sealed class TagScan(MailDbContext db, TagIndex index, OutlookCategories outlook, BimiSenders bimi, TimeProvider clock, ILogger<TagScan> logger)
{
    public static readonly TimeSpan LostRetention = TimeSpan.FromDays(7);

    private const int Chunk = 500;
    private const int MessageIdsPerSearch = 25;

    private static readonly HashSet<string> SkippedUses = ["trash", "junk", "drafts", "all", "flagged"];

    /// <returns>The folders (cache keys) whose tagged messages changed.</returns>
    public async Task<HashSet<string>> RunAsync(ImapClient client, MailAccount account, IReadOnlyCollection<string> keywords, CancellationToken ct)
    {
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = (await client.GetFoldersAsync(personal, subscribedOnly: false, cancellationToken: ct))
            .Where(MailboxService.IsSelectable)
            .ToList();
        if (!folders.Any(f => MailboxService.IsInbox(f.FullName)))
        {
            folders.Insert(0, client.Inbox);
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        var names = folders.Select(f => FolderCache.Key(f.FullName)).ToHashSet(StringComparer.Ordinal);
        var stale = await db.TaggedMessages.Where(m => m.AccountId == account.Id && m.Uid != 0)
            .Select(m => m.FolderName).Distinct().ToListAsync(ct);
        foreach (var gone in stale.Where(f => !names.Contains(f)))
        {
            await index.LoseFolderAsync(account.Id, gone, ct);
            changed.Add(gone);
        }

        await db.TagScanStates.Where(s => s.AccountId == account.Id && !names.Contains(s.FolderName)).ExecuteDeleteAsync(ct);

        var storage = await index.StorageAsync(account.Id, ct);
        try
        {
            if (storage == TagStorage.Local)
            {
                // Signed in again with the Graph consent: the app-kept tags move to Outlook.
                storage = await outlook.DecideAsync(account, ct);
            }

            if (storage == TagStorage.Graph)
            {
                // Before the search by Message-ID below, which places the messages Outlook tagged.
                changed.UnionWith(await outlook.ReconcileAsync(account, ct));
            }
        }
        catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
        {
            logger.LogInformation("Outlook categories of account {AccountId} could not be read ({ExceptionType})", account.Id, e.GetType().Name);
        }

        if (storage.IsAppKept())
        {
            await ScanLocalAsync(folders, account, changed, ct);
        }
        else if (keywords.Count > 0)
        {
            var condStore = client.Capabilities.HasFlag(ImapCapabilities.CondStore);
            var keywordSet = string.Join(' ', keywords.Select(k => k.ToLowerInvariant()).Order(StringComparer.Ordinal));
            foreach (var folder in folders.Where(f => !SkippedUses.Contains(FolderOrdering.SpecialUse(f) ?? "")
                                                      && !f.Attributes.HasFlag(FolderAttributes.Important)))
            {
                try
                {
                    if (await ScanFolderAsync(folder, account, keywords, keywordSet, condStore, ct))
                    {
                        changed.Add(FolderCache.Key(folder.FullName));
                    }
                }
                catch (Exception e) when (e is ImapCommandException or FolderNotFoundException)
                {
                    // One folder that cannot be read (deleted meanwhile, no rights) does not stop the scan.
                    logger.LogInformation("Tag scan of a folder of account {AccountId} failed ({ExceptionType})", account.Id, e.GetType().Name);
                }
            }
        }

        return changed;
    }

    /// <summary>Searches one folder for the tag keywords and brings its index rows in line.</summary>
    private async Task<bool> ScanFolderAsync(IMailFolder folder, MailAccount account, IReadOnlyCollection<string> keywords, string keywordSet,
        bool condStore, CancellationToken ct)
    {
        var key = FolderCache.Key(folder.FullName);
        var state = await db.TagScanStates.SingleOrDefaultAsync(s => s.AccountId == account.Id && s.FolderName == key, ct);
        if (condStore)
        {
            await folder.StatusAsync(StatusItems.HighestModSeq | StatusItems.UidValidity, ct);
            if (state is not null && folder.HighestModSeq > 0 && state.HighestModSeq == folder.HighestModSeq
                && state.UidValidity == folder.UidValidity && state.KeywordSet == keywordSet)
            {
                return false;
            }
        }

        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uidValidity = folder.UidValidity;
        var modSeq = condStore ? folder.HighestModSeq : 0;
        var query = keywords.Select(k => (SearchQuery)SearchQuery.HasKeyword(k)).Aggregate((a, b) => a.Or(b));
        var found = folder.Count == 0 ? [] : await folder.SearchAsync(query, ct);
        var indexed = await db.TaggedMessages.AsNoTracking()
            .Where(m => m.AccountId == account.Id && m.FolderName == key && m.UidValidity == uidValidity && m.Uid != 0)
            .Select(m => m.Uid).ToListAsync(ct);
        var check = found.Select(u => u.Id).Concat(indexed).Distinct().Select(u => new UniqueId(uidValidity, u)).ToList();
        var summaries = new List<IMessageSummary>();
        foreach (var chunk in check.Chunk(Chunk))
        {
            summaries.AddRange(await folder.FetchAsync(chunk, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct));
        }

        await index.LoseAsync(account.Id, key, uidValidity, null, ct);
        var (needed, changed) = await index.ApplyScanAsync(account.Id, key, uidValidity, summaries, ct);
        foreach (var chunk in needed.Chunk(Chunk))
        {
            var full = await folder.FetchAsync(chunk.Select(u => new UniqueId(uidValidity, u)).ToList(), FolderCache.ListRequest(), ct);
            await index.RecordAsync(account, key, uidValidity, full.Select(s => (s, Keywords.Format(s.Keywords), bimi.Find(s))), ct);
            changed = true;
        }

        if (state is null)
        {
            state = new TagScanState { AccountId = account.Id, OwnerUserId = account.OwnerUserId, FolderName = key, KeywordSet = keywordSet };
            db.TagScanStates.Add(state);
        }

        state.UidValidity = uidValidity;
        state.HighestModSeq = modSeq;
        state.KeywordSet = keywordSet;
        state.ScannedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>App-kept tags: checks that located messages are still there and finds lost ones by Message-ID.</summary>
    private async Task ScanLocalAsync(List<IMailFolder> folders, MailAccount account, HashSet<string> changed, CancellationToken ct)
    {
        var lost = await db.TaggedMessages
            .Where(m => m.AccountId == account.Id && m.Uid == 0 && m.MessageIdHeader != null)
            .ToListAsync(ct);
        var wanted = lost.GroupBy(m => m.MessageIdHeader!).ToDictionary(g => g.Key, g => g.First());
        var located = await db.TaggedMessages.AsNoTracking()
            .Where(m => m.AccountId == account.Id && m.Uid != 0)
            .Select(m => new { m.FolderName, m.UidValidity, m.Uid })
            .ToListAsync(ct);
        var byFolder = located.GroupBy(m => m.FolderName).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var folder in folders)
        {
            var key = FolderCache.Key(folder.FullName);
            var here = byFolder.GetValueOrDefault(key) ?? [];
            if (here.Count == 0 && wanted.Count == 0)
            {
                continue;
            }

            try
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                if (here.Count > 0)
                {
                    await index.LoseAsync(account.Id, key, folder.UidValidity, null, ct);
                    var uids = here.Where(m => m.UidValidity == folder.UidValidity).Select(m => new UniqueId(folder.UidValidity, m.Uid)).ToList();
                    var present = new List<IMessageSummary>();
                    foreach (var chunk in uids.Chunk(Chunk))
                    {
                        present.AddRange(await folder.FetchAsync(chunk, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct));
                    }

                    var (rowsChanged, gone) = await index.ApplyLocalScanAsync(account.Id, key, folder.UidValidity, present, ct);
                    if (rowsChanged)
                    {
                        changed.Add(key);
                    }

                    // Looked for in this folder and the ones after it; folders before it, next round.
                    foreach (var row in gone.Where(r => r.MessageIdHeader is not null))
                    {
                        wanted.TryAdd(row.MessageIdHeader!, row);
                    }
                }

                foreach (var batch in wanted.Keys.ToList().Chunk(MessageIdsPerSearch))
                {
                    if (folder.Count == 0)
                    {
                        break;
                    }

                    var query = batch.Select(id => (SearchQuery)SearchQuery.HeaderContains("Message-ID", id)).Aggregate((a, b) => a.Or(b));
                    var hits = await folder.SearchAsync(query, ct);
                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    var envelopes = await folder.FetchAsync(hits, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, ct);
                    foreach (var summary in envelopes)
                    {
                        if (TagIndex.MessageIdOf(summary.Envelope?.MessageId) is { } id && wanted.Remove(id, out var row))
                        {
                            await index.RelocateAsync(row, key, folder.UidValidity, summary.UniqueId.Id, ct);
                            changed.Add(key);
                        }
                    }
                }
            }
            catch (Exception e) when (e is ImapCommandException or FolderNotFoundException)
            {
                logger.LogInformation("Tag scan of a folder of account {AccountId} failed ({ExceptionType})", account.Id, e.GetType().Name);
            }
        }

        // Not found anywhere for a week: deleted (or moved to another account).
        var cutoff = clock.GetUtcNow() - LostRetention;
        await db.TaggedMessages.Where(m => m.AccountId == account.Id && m.Uid == 0 && m.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }
}
