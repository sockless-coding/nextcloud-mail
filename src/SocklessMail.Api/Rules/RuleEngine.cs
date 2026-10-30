using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;
using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Rules;

/// <summary>
/// Runs an account's rules on INBOX mail that arrived since the last run (<see cref="RuleCursor"/>).
/// Called inside every INBOX sync, before the cache sees the new mail, so a message a rule moves
/// never shows up in the inbox first. A failing action is recorded on its rule and the run moves
/// on: the cursor advances anyway, so one broken rule does not hold up the others for ever. Lost
/// connections, on the other hand, propagate and leave the cursor where it was.
/// </summary>
public sealed class RuleEngine(
    MailDbContext db, RuleStore store, FolderCache cache, TagIndex tags, OutlookCategories outlook, MailPush push, ImapConnections imap, MailLogins logins,
    SyncLocks syncLocks, TimeProvider clock, ILogger<RuleEngine> logger)
{
    /// <summary>New messages handled per run; the rest wait for the next one.</summary>
    public const int MaxPerRun = 1000;

    /// <summary>Newest messages of a folder "Run on existing messages" looks at.</summary>
    public const int ExistingWindow = 1000;

    /// <summary>Newest INBOX messages a draft rule is tried against.</summary>
    public const int TestWindow = 200;

    private const int FetchChunk = 200;

    private const MessageSummaryItems Items =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure;

    /// <summary>
    /// Applies the rules to new INBOX mail. The caller holds the INBOX sync lock; the INBOX may be
    /// left open read-write, so the caller opens it again as it needs it.
    /// </summary>
    /// <returns>Whether any message was changed or moved.</returns>
    public async Task<bool> ApplyAsync(ImapClient client, MailAccount account, CancellationToken ct)
    {
        var cursor = await db.RuleCursors.SingleOrDefaultAsync(c => c.AccountId == account.Id, ct);
        if (cursor is null)
        {
            return false;
        }

        var rules = await store.EnabledAsync(account.Id, ct);
        if (rules.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return false;
        }

        // Opened again even when open: UIDNEXT is only fresh from a new SELECT/EXAMINE.
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        if (cursor.UidValidity != inbox.UidValidity)
        {
            // First run, or the server renumbered the INBOX: start after what is there now.
            if (cursor.UidValidity != 0)
            {
                logger.LogInformation("The INBOX of account {AccountId} was renumbered; rules start again with new mail", account.Id);
            }

            await PositionAsync(cursor, inbox, ct);
            await db.SaveChangesAsync(ct);
            return false;
        }

        if (inbox.Count == 0 || inbox.UidNext is { } next && next.Id <= cursor.LastUid + 1)
        {
            return false;
        }

        if (inbox.Access != FolderAccess.ReadWrite)
        {
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        }

        var fresh = (await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(cursor.LastUid + 1), UniqueId.MaxValue)), ct))
            .Where(u => u.Id > cursor.LastUid)
            .OrderBy(u => u.Id)
            .Take(MaxPerRun)
            .ToList();
        if (fresh.Count == 0)
        {
            if (inbox.UidNext is { } uidNext)
            {
                cursor.LastUid = uidNext.Id - 1;
                cursor.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }

            return false;
        }

        var plan = new Plan();
        var request = Request(rules);
        foreach (var chunk in fresh.Chunk(FetchChunk))
        {
            foreach (var summary in await inbox.FetchAsync(chunk, request, ct))
            {
                Evaluate(rules, summary, plan);
            }
        }

        var changed = await ExecuteAsync(client, account, inbox, plan, ct);
        cursor.LastUid = fresh[^1].Id;
        cursor.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        foreach (var target in plan.Targets)
        {
            await push.FolderChangedAsync(account.OwnerUserId, account.Id, target);
        }

        return changed;
    }

    /// <summary>Sets an unpositioned cursor to the INBOX as it is now (rules were just enabled); no-op otherwise.</summary>
    public async Task PositionAsync(ImapClient client, MailAccount account, CancellationToken ct)
    {
        var cursor = await db.RuleCursors.SingleOrDefaultAsync(c => c.AccountId == account.Id, ct);
        if (cursor is not { UidValidity: 0 })
        {
            return;
        }

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        await PositionAsync(cursor, client.Inbox, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Tries a rule on the newest INBOX messages without acting on them.</summary>
    public static async Task<RuleTestResult> TestAsync(ImapClient client, CompiledRule rule, CancellationToken ct)
    {
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        return await MatchNewestAsync(client.Inbox, rule, TestWindow, ct);
    }

    /// <summary>
    /// Which of a folder's newest <see cref="ExistingWindow"/> messages a saved rule matches, so
    /// the user can confirm before <see cref="RunOnExistingAsync"/> acts on them.
    /// </summary>
    public async Task<RuleTestResult> PreviewExistingAsync(MailAccount account, Guid ruleId, string? folderId, CancellationToken ct)
    {
        var rule = await FindAsync(account, ruleId, ct);
        return await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct), async (client, token) =>
        {
            var folder = await SourceAsync(client, folderId, token);
            await folder.OpenAsync(FolderAccess.ReadOnly, token);
            return await MatchNewestAsync(folder, rule, ExistingWindow, token);
        }, ct);
    }

    /// <summary>
    /// Runs one saved rule (enabled or not) on messages already in a folder: those of
    /// <paramref name="messageIds"/> (from <see cref="PreviewExistingAsync"/>) it still matches.
    /// Moving or copying into the folder itself is skipped. The folder's cache is updated as after
    /// the user's own actions.
    /// </summary>
    /// <returns>How many messages the rule matched.</returns>
    public async Task<int> RunOnExistingAsync(
        MailAccount account, Guid ruleId, string? folderId, IReadOnlyList<string>? messageIds, CancellationToken ct)
    {
        var rule = await FindAsync(account, ruleId, ct);
        if (messageIds is null || messageIds.Count == 0)
        {
            throw new MailboxValidationException("messageIds", "Give the messages to run the rule on.");
        }

        if (messageIds.Count > ExistingWindow)
        {
            throw new MailboxValidationException("messageIds", $"At most {ExistingWindow} messages at once.");
        }

        var name = folderId is null ? null : MailboxIds.FolderFullName(folderId);
        var fullName = FolderCache.Key(name ?? throw new MailboxNotFoundException("The folder"));
        var plan = new Plan();
        // Lock before leasing a connection (see SyncLocks).
        using (await syncLocks.AcquireAsync(account.Id, fullName, ct))
        {
            var uidValidity = await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct), async (client, token) =>
            {
                var folder = await SourceAsync(client, folderId, token);
                await folder.OpenAsync(FolderAccess.ReadWrite, token);
                var uids = new List<UniqueId>(messageIds.Count);
                foreach (var id in messageIds.Distinct())
                {
                    if (!MailboxIds.TryParseMessageId(id, out var validity, out var uid))
                    {
                        throw new MailboxValidationException("messageIds", $"\"{id}\" is not a message id.");
                    }

                    if (validity != folder.UidValidity)
                    {
                        throw new MailboxConflictException("stale_messages", "The folder has changed on the server. Check the messages again.");
                    }

                    uids.Add(new UniqueId(validity, uid));
                }

                foreach (var chunk in uids.Chunk(FetchChunk))
                {
                    foreach (var summary in await folder.FetchAsync(chunk, Request([rule]), token))
                    {
                        Evaluate([rule], summary, plan);
                    }
                }

                await ExecuteAsync(client, account, folder, plan, token);
                return folder.UidValidity;
            }, ct, retry: false);

            // The changes happened: a failure from here on must not fail the request.
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                if (plan.Seen.Count > 0)
                {
                    await cache.ApplyFlagsAsync(account.Id, fullName, uidValidity, plan.Seen, true, null, CancellationToken.None);
                }

                if (plan.Flagged.Count > 0)
                {
                    await cache.ApplyFlagsAsync(account.Id, fullName, uidValidity, plan.Flagged, null, true, CancellationToken.None);
                }

                if (plan.Keywords.Count > 0)
                {
                    await cache.ApplyKeywordsAsync(account.Id, fullName, uidValidity, plan.Keywords, CancellationToken.None);
                }

                if (plan.Moved.Count > 0)
                {
                    await cache.RemoveAsync(account.Id, fullName, uidValidity, plan.Moved, CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Updating the cache of account {AccountId} after running a rule failed", account.Id);
            }
        }

        foreach (var target in plan.Targets)
        {
            await push.FolderChangedAsync(account.OwnerUserId, account.Id, target);
        }

        return plan.Matched;
    }

    private async Task<CompiledRule> FindAsync(MailAccount account, Guid ruleId, CancellationToken ct)
    {
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == ruleId && r.AccountId == account.Id, ct)
                   ?? throw new MailboxNotFoundException("The rule");
        return RuleStore.Compile(rule);
    }

    private static async Task<IMailFolder> SourceAsync(ImapClient client, string? folderId, CancellationToken ct)
    {
        var fullName = (folderId is null ? null : MailboxIds.FolderFullName(folderId)) ?? throw new MailboxNotFoundException("The folder");
        try
        {
            var folder = MailboxService.IsInbox(fullName) ? client.Inbox : await client.GetFolderAsync(fullName, ct);
            return MailboxService.IsSelectable(folder) ? folder : throw new MailboxNotFoundException("The folder");
        }
        catch (FolderNotFoundException)
        {
            throw new MailboxNotFoundException("The folder");
        }
    }

    /// <summary>The newest <paramref name="window"/> messages of an open folder that the rule matches.</summary>
    private static async Task<RuleTestResult> MatchNewestAsync(IMailFolder folder, CompiledRule rule, int window, CancellationToken ct)
    {
        if (folder.Count == 0)
        {
            return new RuleTestResult(0, 0, [], []);
        }

        var first = Math.Max(0, folder.Count - window);
        var request = Request([rule]);
        request.Items |= MessageSummaryItems.InternalDate;
        var summaries = await folder.FetchAsync(first, folder.Count - 1, request, ct);
        var matches = summaries
            .Where(s => rule.Matches(ToMessage(s)))
            .OrderByDescending(s => s.UniqueId.Id)
            .ToList();
        return new RuleTestResult(
            summaries.Count,
            matches.Count,
            matches.Take(RuleStore.MaxTestMatches).Select(s => new RuleTestMatch(
                MailboxIds.MessageId(folder.UidValidity, s.UniqueId.Id),
                s.Envelope?.Subject ?? "",
                AddressDto.From(s.Envelope?.From),
                s.Envelope?.Date ?? s.InternalDate ?? DateTimeOffset.MinValue)).ToList(),
            matches.Select(s => MailboxIds.MessageId(folder.UidValidity, s.UniqueId.Id)).ToList());
    }

    private static async Task PositionAsync(RuleCursor cursor, IMailFolder inbox, CancellationToken ct)
    {
        uint last = 0;
        if (inbox.UidNext is { } next)
        {
            last = next.Id - 1;
        }
        else if (inbox.Count > 0)
        {
            last = (await inbox.SearchAsync(SearchQuery.All, ct)).Select(u => u.Id).DefaultIfEmpty().Max();
        }

        cursor.UidValidity = inbox.UidValidity;
        cursor.LastUid = last;
    }

    private static FetchRequest Request(IEnumerable<CompiledRule> rules)
    {
        var headers = rules.SelectMany(r => r.HeaderNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return headers.Count == 0 ? new FetchRequest(Items) : new FetchRequest(Items, new HeaderSet(headers));
    }

    internal static RuleMessage ToMessage(IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var headers = summary.Headers;
        return new RuleMessage(
            envelope?.From.Mailboxes.ToList() ?? [],
            envelope?.To.Mailboxes.ToList() ?? [],
            envelope?.Cc.Mailboxes.ToList() ?? [],
            envelope?.Subject ?? "",
            name => headers is null ? [] : headers.Where(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList(),
            summary.Size ?? 0,
            summary.Attachments.Any());
    }

    /// <summary>
    /// Rules in order; a rule that moves the message (or sends it to Trash) is the last one
    /// tried, since the message leaves the INBOX, and so is one marked "stop".
    /// </summary>
    private void Evaluate(List<CompiledRule> rules, IMessageSummary summary, Plan plan)
    {
        var message = ToMessage(summary);
        var matched = false;
        foreach (var rule in rules)
        {
            if (!rule.Matches(message))
            {
                continue;
            }

            matched = true;
            rule.Rule.MatchCount++;
            rule.Rule.LastMatchedAt = clock.GetUtcNow();

            foreach (var action in rule.Actions)
            {
                plan.Add(rule, action, summary.UniqueId);
                plan.Summaries[summary.UniqueId.Id] = summary;
            }

            if (rule.Rule.Stop || rule.Actions.Any(a => a.Type is RuleActionTypes.Move or RuleActionTypes.Trash))
            {
                break;
            }
        }

        if (matched)
        {
            plan.Matched++;
        }
    }

    /// <summary>
    /// Flags and tags first (so moved messages carry them), then copies, then moves. Moving or
    /// copying a message into its own folder (a rule run on that folder) does nothing. The tag
    /// index follows moved messages, as after the user's own moves.
    /// </summary>
    private async Task<bool> ExecuteAsync(ImapClient client, MailAccount account, IMailFolder source, Plan plan, CancellationToken ct)
    {
        var changed = false;
        var folders = new Dictionary<string, IMailFolder?>();
        var sourceKey = FolderCache.Key(source.FullName);
        var keywords = plan.Steps.Keys.Any(k => k.Type == RuleActionTypes.Tag)
            ? await db.Tags.Where(t => t.OwnerUserId == account.OwnerUserId).ToDictionaryAsync(t => t.Id, t => t.Keyword, ct)
            : [];
        foreach (var step in plan.Steps.OrderBy(s => s.Key.Type switch
                 {
                     RuleActionTypes.MarkRead or RuleActionTypes.Flag or RuleActionTypes.Tag => 0,
                     RuleActionTypes.Copy => 1,
                     _ => 2,
                 }))
        {
            var (rule, type, folderId, tagId) = step.Key;
            var uids = step.Value;
            try
            {
                switch (type)
                {
                    case RuleActionTypes.MarkRead:
                    case RuleActionTypes.Flag:
                        var flag = type == RuleActionTypes.MarkRead ? MessageFlags.Seen : MessageFlags.Flagged;
                        await source.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, flag) { Silent = true }, ct);
                        (flag == MessageFlags.Seen ? plan.Seen : plan.Flagged).UnionWith(uids.Select(u => u.Id));
                        break;
                    case RuleActionTypes.Tag:
                        var keyword = keywords.GetValueOrDefault(tagId ?? Guid.Empty)
                                      ?? throw new RuleActionException("The rule's tag was deleted. Choose another tag.");
                        await TagAsync(account, source, uids, keyword, plan, ct);
                        break;
                    case RuleActionTypes.Copy:
                        var copyTarget = await TargetAsync(client, folders, folderId!, ct);
                        if (copyTarget.FullName == source.FullName)
                        {
                            continue;
                        }

                        await source.CopyToAsync(uids, copyTarget, ct);
                        plan.Targets.Add(FolderCache.Key(copyTarget.FullName));
                        break;
                    default:
                        var moveTarget = type == RuleActionTypes.Trash
                            ? await MailboxService.FindSpecialFolderAsync(client, "trash", ct)
                              ?? throw new RuleActionException("This account has no Trash folder.")
                            : await TargetAsync(client, folders, folderId!, ct);
                        if (moveTarget.FullName == source.FullName)
                        {
                            continue;
                        }

                        // MOVE where supported; MailKit falls back to COPY + \Deleted + (UID) EXPUNGE.
                        var map = await source.MoveToAsync(uids, moveTarget, ct);
                        await tags.MoveAsync(account.Id, sourceKey, source.UidValidity, uids.Select(u => u.Id).ToList(),
                            FolderCache.Key(moveTarget.FullName), map, ct);
                        plan.Moved.UnionWith(uids.Select(u => u.Id));
                        plan.Targets.Add(FolderCache.Key(moveTarget.FullName));
                        break;
                }

                changed = true;
                rule.Rule.LastError = null;
                rule.Rule.LastErrorAt = null;
            }
            catch (Exception e) when (e is RuleActionException or ImapCommandException or FolderNotFoundException)
            {
                logger.LogInformation("Rule {RuleId} could not {Action} {Count} messages: {ExceptionType}",
                    rule.Rule.Id, type, uids.Count, e.GetType().Name);
                rule.Rule.LastError = e is RuleActionException ? e.Message : "The mail server refused the rule's action.";
                rule.Rule.LastErrorAt = clock.GetUtcNow();
            }
        }

        await RecordTagsAsync(account, source, plan, ct);
        return changed;
    }

    /// <summary>
    /// Adds a tag's keyword: as an Outlook category (and in the tag index) for Microsoft accounts
    /// with Graph, on the server where it keeps keywords (the account's first tag decides, as for
    /// the user's own tagging), in the tag index otherwise.
    /// </summary>
    private async Task TagAsync(MailAccount account, IMailFolder source, IList<UniqueId> uids, string keyword, Plan plan, CancellationToken ct)
    {
        TagStorage storage;
        try
        {
            storage = await outlook.DecideAsync(account, ct);
        }
        catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
        {
            throw new RuleActionException(e.Message);
        }

        var onServer = !storage.IsAppKept() && MailboxService.AcceptsKeywords(source, [keyword]);
        if (!onServer && storage == TagStorage.Keywords)
        {
            throw new RuleActionException("This folder cannot keep tags.");
        }

        if (storage == TagStorage.Unknown)
        {
            await tags.SetStorageAsync(account.Id, onServer ? TagStorage.Keywords : TagStorage.Local, ct);
        }

        if (onServer)
        {
            await source.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, [keyword]) { Silent = true }, ct);
        }
        else
        {
            // Recorded before any move, so the index row follows the message.
            var key = FolderCache.Key(source.FullName);
            var current = await tags.KeywordsAsync(account.Id, key, source.UidValidity, uids.Select(u => u.Id).ToList(), ct);
            if (storage == TagStorage.Graph)
            {
                try
                {
                    await outlook.ApplyAsync(account,
                        uids.Select(u => TagIndex.MessageIdOf(plan.Summaries[u.Id].Envelope?.MessageId)).OfType<string>(), [keyword], [], ct);
                }
                catch (Exception e) when (e is GraphUnavailableException or GraphConsentRequiredException)
                {
                    throw new RuleActionException(e.Message);
                }
            }

            await tags.RecordAsync(account, key, source.UidValidity,
                uids.Select(u => (plan.Summaries[u.Id], Keywords.Change(current.GetValueOrDefault(u.Id), [keyword], []), (BimiDto?)null)), ct);
        }

        plan.Tagged.UnionWith(uids.Select(u => u.Id));
    }

    /// <summary>
    /// The keywords of tagged messages still in the folder, for its cache (<see cref="Plan.Keywords"/>)
    /// and, where the server keeps them, the tag index. Mail that moved is found there by the
    /// target folder's sync.
    /// </summary>
    private async Task RecordTagsAsync(MailAccount account, IMailFolder source, Plan plan, CancellationToken ct)
    {
        var stayed = plan.Tagged.Where(u => !plan.Moved.Contains(u)).Select(u => new UniqueId(source.UidValidity, u)).ToList();
        if (stayed.Count == 0)
        {
            return;
        }

        var key = FolderCache.Key(source.FullName);
        try
        {
            if ((await tags.StorageAsync(account.Id, ct)).IsAppKept())
            {
                foreach (var (uid, keywords) in await tags.KeywordsAsync(account.Id, key, source.UidValidity, stayed.Select(u => u.Id).ToList(), ct))
                {
                    plan.Keywords[uid] = keywords;
                }

                return;
            }

            var summaries = await source.FetchAsync(stayed, FolderCache.ListRequest(), ct);
            await tags.RecordAsync(account, key, source.UidValidity, summaries.Select(s => (s, Keywords.Format(s.Keywords), (BimiDto?)null)), ct);
            foreach (var summary in summaries)
            {
                plan.Keywords[summary.UniqueId.Id] = Keywords.Format(summary.Keywords);
            }
        }
        catch (Exception e) when (e is ImapCommandException or DbUpdateException)
        {
            // The tags are set; the next sync or scan brings the cache and index up to date.
            logger.LogInformation("Recording rule tags of account {AccountId} failed ({ExceptionType})", account.Id, e.GetType().Name);
        }
    }

    private static async Task<IMailFolder> TargetAsync(
        ImapClient client, Dictionary<string, IMailFolder?> folders, string folderId, CancellationToken ct)
    {
        if (!folders.TryGetValue(folderId, out var folder))
        {
            var fullName = MailboxIds.FolderFullName(folderId) ?? "";
            try
            {
                folder = await client.GetFolderAsync(fullName, ct);
                folder = MailboxService.IsSelectable(folder) ? folder : null;
            }
            catch (FolderNotFoundException)
            {
                folder = null;
            }

            folders[folderId] = folder;
        }

        return folder ?? throw new RuleActionException($"The folder \"{MailboxIds.FolderFullName(folderId)}\" does not exist.");
    }

    private sealed class Plan
    {
        public readonly Dictionary<(CompiledRule Rule, string Type, string? FolderId, Guid? TagId), List<UniqueId>> Steps = [];

        /// <summary>The fetched messages the steps act on, by UID (tags kept by the app need their envelope).</summary>
        public readonly Dictionary<uint, IMessageSummary> Summaries = [];

        public readonly HashSet<uint> Tagged = [];

        /// <summary>Keywords of tagged messages that stayed, for the source folder's cache.</summary>
        public readonly Dictionary<uint, string> Keywords = [];

        /// <summary>Folders (cache keys) that received messages, for nudging open tabs.</summary>
        public readonly HashSet<string> Targets = [];

        /// <summary>UIDs the actions changed in the source folder, for its cache.</summary>
        public readonly HashSet<uint> Seen = [];

        public readonly HashSet<uint> Flagged = [];
        public readonly HashSet<uint> Moved = [];

        /// <summary>Messages at least one rule matched.</summary>
        public int Matched;

        public void Add(CompiledRule rule, RuleAction action, UniqueId uid)
        {
            var key = (rule, action.Type, action.FolderId, action.TagId);
            if (!Steps.TryGetValue(key, out var uids))
            {
                Steps[key] = uids = [];
            }

            uids.Add(uid);
        }
    }

    private sealed class RuleActionException(string message) : Exception(message);
}
