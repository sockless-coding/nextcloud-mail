using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// A list across all of a user's accounts: a unified folder holds the folder of one special use
/// (<see cref="Roles"/>) of every account; a smart folder is a quick filter over some of them,
/// asked of each mail server. Accounts without a folder of that use are left out.
/// </summary>
public sealed record UnifiedView(string Name, IReadOnlyList<string> Roles, MessageFilter Filter)
{
    public static readonly IReadOnlyList<UnifiedView> All =
    [
        new("inbox", ["inbox"], MessageFilter.None),
        new("drafts", ["drafts"], MessageFilter.None),
        new("sent", ["sent"], MessageFilter.None),
        new("archive", ["archive"], MessageFilter.None),
        new("junk", ["junk"], MessageFilter.None),
        new("trash", ["trash"], MessageFilter.None),
        // Unread mail is what arrived; unread copies in Sent, Junk or Trash are not news.
        new("unread", ["inbox"], MessageFilter.Unread),
        // Where flagged mail is usually kept: still in the inbox, filed away, or one's own.
        new("flagged", ["inbox", "archive", "sent"], MessageFilter.Flagged),
    ];

    public bool IsSmart => Filter != MessageFilter.None;

    public static UnifiedView Parse(string name) =>
        All.FirstOrDefault(v => v.Name == name) ?? throw new MailboxNotFoundException("The unified folder");
}

/// <summary>
/// A position in a unified folder, which is ordered by date, then account id, then UID (all
/// descending, so the order is total: a unified folder has one folder per account). Sent as
/// "{utc ticks}.{account id}.{uid}".
/// </summary>
public readonly record struct UnifiedCursor(DateTimeOffset Date, Guid AccountId, uint Uid)
{
    public override string ToString() => $"{Date.UtcTicks}.{AccountId:N}.{Uid}";

    public static UnifiedCursor? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split('.');
        if (parts.Length == 3
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParseExact(parts[1], "N", out var accountId)
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
        {
            return new UnifiedCursor(new DateTimeOffset(ticks, TimeSpan.Zero), accountId, uid);
        }

        throw new MailboxValidationException("before", "The cursor is not valid.");
    }

    public static UnifiedCursor Of(Guid accountId, CachedMessage message) => new(message.Date, accountId, message.Uid);
}

/// <summary>
/// The unified and smart folders (<see cref="UnifiedView"/>). Each account's part is read in its
/// own service scope, all accounts at once, so a slow or unreachable server delays the list by at
/// most its own timeout and does not fail it: its messages show as last cached and the account is
/// reported in the errors. An account's folders of a view are read one after the other; after one
/// failed, the rest of that account is read from the cache only.
/// <para>
/// A unified folder is read from the header cache. The cache holds a window of each folder by UID,
/// while the list is ordered by date. A page is complete when every account either contributes
/// more than a page of cached messages below the cursor or has its whole folder cached; an account
/// short of that has its window extended on the server first. This relies on UID order following
/// date order, which holds for delivered mail; messages appended with an old date (imports, mail
/// filed into Archive) can show up later than their date suggests.
/// </para>
/// </summary>
public sealed class UnifiedFolders(AccountService accounts, IServiceScopeFactory scopes, FolderRoles roles, ILogger<UnifiedFolders> logger)
{
    private static readonly string InboxId = MailboxIds.FolderId("INBOX");

    /// <summary>One account's folder in a view.</summary>
    private sealed record Source(MailAccount Account, string FolderId)
    {
        public string FullName { get; } = MailboxIds.FolderFullName(FolderId)!;

        /// <summary>"{account id}.{folder id}": folder ids are base64url, so neither "." nor "~" occur in them.</summary>
        public string Key => $"{Account.Id:N}.{FolderId}";
    }

    private interface IPart
    {
        AccountError? Error { get; }
    }

    private sealed record FolderResult(Source Source, CachedFolder? State, AccountError? Error, List<CachedMessage> Rows, FolderChanges? Changes) : IPart;

    private sealed record FilterResult(Source Source, CachedFolder? State, uint? Before, SearchPage? Page, AccountError? Error) : IPart;

    /// <summary>
    /// A page of the view, newest first. A unified folder is paged with <see cref="UnifiedCursor"/>;
    /// a smart folder with the filter cursor (see <see cref="FilterAsync"/>), and counts its matches
    /// instead of the folders' messages.
    /// </summary>
    public async Task<UnifiedPage> GetPageAsync(string userId, UnifiedView view, string? before, int limit, CancellationToken ct)
    {
        if (view.IsSmart)
        {
            var (filtered, matches) = await FilterAsync(userId, view, view.Filter, before, limit, withStates: before is null, ct);
            return new UnifiedPage(filtered.Messages, filtered.NextBefore, filtered.Total, null, Token(matches.Select(r => (r.Source, r.State))),
                filtered.Errors);
        }

        var cursor = UnifiedCursor.Parse(before);
        // The first page brings every folder up to date; older pages read what the first one synced.
        var (results, errors) = await PerFolderAsync(userId, view, async (mailbox, cache, source, offline, token) =>
        {
            var (state, error) = await StateAsync(mailbox, cache, source, sync: cursor is null, offline, token);
            if (state is null)
            {
                return new FolderResult(source, null, error, [], null);
            }

            var rows = await cache.PageByDateAsync(state, cursor, limit + 1, token);
            if (rows.Count <= limit && state.WindowStart > 1 && error is null && !offline)
            {
                try
                {
                    // Extended by at least a page, so the next pages find it cached.
                    state = await mailbox.ExtendFolderAsync(source.Account, source.FolderId,
                        Math.Max(limit + 1 - rows.Count, MailboxService.DefaultPageSize), token);
                    rows = await cache.PageByDateAsync(state, cursor, limit + 1, token);
                }
                catch (Exception e) when (Describe(source.Account, e) is { } failure)
                {
                    error = failure;
                }
            }

            return new FolderResult(source, state, error, rows, null);
        }, ct);

        var merged = results
            .SelectMany(r => r.Rows.Select(m => (Result: r, Message: m)))
            .OrderByDescending(x => x.Message.Date).ThenByDescending(x => x.Result.Source.Account.Id).ThenByDescending(x => x.Message.Uid)
            .Take(limit + 1)
            .ToList();
        var page = merged.Take(limit).ToList();
        var states = results.Where(r => r.State is not null).Select(r => r.State!).ToList();
        return new UnifiedPage(
            page.Select(x => Hit(x.Result.Source, x.Result.State!.UidValidity, x.Message)).ToList(),
            merged.Count > limit ? UnifiedCursor.Of(page[^1].Result.Source.Account.Id, page[^1].Message).ToString() : null,
            states.Sum(s => s.Total),
            Unread(states),
            Token(results.Select(r => (r.Source, r.State))),
            errors);
    }

    /// <summary>A quick filter over a unified folder, asked of each mail server.</summary>
    public async Task<UnifiedFilterPage> GetFilteredAsync(string userId, UnifiedView view, MessageFilter filter, string? cursor, int limit, CancellationToken ct)
    {
        if (view.IsSmart)
        {
            throw new MailboxValidationException("filter", "Smart folders are filtered already.");
        }

        return (await FilterAsync(userId, view, filter, cursor, limit, withStates: false, ct)).Page;
    }

    /// <summary>
    /// The filter over every folder of the view, merged by date. Each folder's matches come newest
    /// first by UID; the merge takes them in that order, so where a folder stands is the UID of the
    /// last match it gave. The cursor lists those positions as "{account id}.{folder id}.{uid}"
    /// joined by "~", with 0 for a folder not read yet; a folder missing from it has no more matches.
    /// With <paramref name="withStates"/>, each folder is also synced, for a change token.
    /// </summary>
    private async Task<(UnifiedFilterPage Page, List<FilterResult> Results)> FilterAsync(
        string userId, UnifiedView view, MessageFilter filter, string? cursor, int limit, bool withStates, CancellationToken ct)
    {
        var positions = ParseFilterCursor(cursor);
        var (results, errors) = await PerFolderAsync(userId, view, async (mailbox, cache, source, offline, token) =>
        {
            if (offline || (positions is not null && !positions.ContainsKey(source.Key)))
            {
                return new FilterResult(source, offline ? await cache.FindAsync(source.Account.Id, FolderCache.Key(source.FullName), token) : null,
                    null, null, null);
            }

            CachedFolder? state = null;
            if (withStates)
            {
                (state, var error) = await StateAsync(mailbox, cache, source, sync: true, offline: false, token);
                if (error is not null)
                {
                    return new FilterResult(source, state, null, null, error);
                }
            }

            uint? before = positions is not null && positions[source.Key] > 0 ? positions[source.Key] : null;
            try
            {
                return new FilterResult(source, state, before, await mailbox.SearchFolderAsync(source.Account, source.FolderId, null, filter, before, limit, token), null);
            }
            catch (Exception e) when (Describe(source.Account, e) is { } error)
            {
                return new FilterResult(source, state, before, null, error);
            }
        }, ct);

        // Merge: repeatedly take the newest of the folders' next matches.
        var read = results.Where(r => r.Page is not null).ToList();
        var queues = read.ToDictionary(r => r.Source.Key, r => new Queue<MessageSummaryDto>(r.Page!.Messages));
        var consumed = new Dictionary<string, uint>();
        var page = new List<SearchHit>();
        while (page.Count < limit)
        {
            var next = read.Where(r => queues[r.Source.Key].Count > 0)
                .OrderByDescending(r => queues[r.Source.Key].Peek().Date).ThenByDescending(r => r.Source.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            var message = queues[next.Source.Key].Dequeue();
            page.Add(new SearchHit(next.Source.Account.Id, next.Source.FolderId, next.Source.FullName, message));
            if (MailboxIds.TryParseMessageId(message.Id, out _, out var uid))
            {
                consumed[next.Source.Key] = uid;
            }
        }

        // Where each folder continues: after its last match taken, or where its page ended.
        var left = new List<string>();
        foreach (var result in read)
        {
            var key = result.Source.Key;
            uint? position = queues[key].Count > 0
                ? consumed.TryGetValue(key, out var last) ? last : result.Before ?? 0
                : result.Page!.NextBefore;
            if (position is { } p)
            {
                left.Add($"{key}.{p}");
            }
        }

        var counted = positions is null && errors.Count == 0 && read.All(r => r.Page!.Total is not null);
        return (new UnifiedFilterPage(
            page,
            left.Count > 0 ? string.Join('~', left) : null,
            counted ? read.Sum(r => r.Page!.Total!.Value) : null,
            errors), results);
    }

    /// <summary>null for the first page; otherwise each folder still to read ("{account id}.{folder id}") and its position.</summary>
    private static Dictionary<string, uint>? ParseFilterCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var result = new Dictionary<string, uint>();
        foreach (var entry in cursor.Split('~'))
        {
            var parts = entry.Split('.');
            if (parts.Length != 3
                || !Guid.TryParseExact(parts[0], "N", out var accountId)
                || MailboxIds.FolderFullName(parts[1]) is null
                || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                || !result.TryAdd($"{accountId:N}.{parts[1]}", uid))
            {
                throw new MailboxValidationException("before", "The cursor is not valid.");
            }
        }

        return result;
    }

    /// <summary>What changed in any folder of the view since <paramref name="since"/> (a token from a page or an earlier call).</summary>
    public async Task<UnifiedChanges> GetChangesAsync(string userId, UnifiedView view, string? since, CancellationToken ct)
    {
        var seen = ParseToken(since);
        var (results, errors) = await PerFolderAsync(userId, view, async (mailbox, cache, source, offline, token) =>
        {
            var (state, error) = await StateAsync(mailbox, cache, source, sync: true, offline, token);
            var changes = state is not null && seen.TryGetValue(source.Key, out var seq) ? await cache.ChangesAsync(state, seq, token) : null;
            return new FolderResult(source, state, error, [], changes);
        }, ct);

        var states = results.Where(r => r.State is not null).Select(r => r.State!).ToList();
        // A folder that appeared (new account, first successful sync, a folder newly found for the
        // use) or went away (deleted account) since the token cannot be merged: the client reloads.
        var reset = states.Count != seen.Count || results.Any(r => r.State is not null && r.Changes is not { Reset: false });
        var changed = results.Where(r => r.Changes is not null).ToList();
        return new UnifiedChanges(
            Token(results.Select(r => (r.Source, r.State))),
            reset,
            states.Sum(s => s.Total),
            Unread(states),
            reset ? [] : changed.SelectMany(r => r.Changes!.Messages.Select(m => new SearchHit(r.Source.Account.Id, r.Source.FolderId, r.Source.FullName, m))).ToList(),
            reset ? [] : changed.SelectMany(r => r.Changes!.Removed.Select(id => new RemovedHit(r.Source.Account.Id, r.Source.FolderId, id))).ToList(),
            errors);
    }

    /// <summary>
    /// Runs <paramref name="work"/> for each folder of the view in every account of the user. The
    /// work is told to stay offline (cache only) once a folder of its account failed, or when the
    /// account's folders could not be listed (an earlier list is used then, if there is one).
    /// Returns the results and one error per failed account.
    /// </summary>
    private async Task<(List<T> Results, List<AccountError> Errors)> PerFolderAsync<T>(
        string userId, UnifiedView view, Func<MailboxService, FolderCache, Source, bool, CancellationToken, Task<T>> work, CancellationToken ct)
        where T : IPart
    {
        var list = await accounts.ListAsync(userId, ct);
        var parts = await Task.WhenAll(list.Select(async account =>
        {
            await using var scope = scopes.CreateUserScope(userId);
            var services = scope.ServiceProvider;
            var mailbox = services.GetRequiredService<MailboxService>();
            var cache = services.GetRequiredService<FolderCache>();

            AccountError? error = null;
            IReadOnlyList<string> folderIds;
            try
            {
                folderIds = view.Roles is ["inbox"] ? [InboxId] : FolderIds(view, await mailbox.FolderRolesAsync(account, ct));
            }
            catch (Exception e) when (Describe(account, e) is { } failure)
            {
                error = failure;
                folderIds = roles.Find(account, anyAge: true) is { } known ? FolderIds(view, known) : [];
            }

            var results = new List<T>();
            foreach (var folderId in folderIds)
            {
                var result = await work(mailbox, cache, new Source(account, folderId), error is not null, ct);
                results.Add(result);
                error ??= result.Error;
            }

            return (Results: results, Error: error);
        }));

        return (parts.SelectMany(p => p.Results).ToList(), parts.Where(p => p.Error is not null).Select(p => p.Error!).ToList());
    }

    private static List<string> FolderIds(UnifiedView view, IReadOnlyDictionary<string, string> known) =>
        view.Roles.Select(known.GetValueOrDefault).OfType<string>().Distinct().ToList();

    /// <summary>
    /// The folder's cache state: synced first when <paramref name="sync"/>, otherwise the cached one
    /// (synced when there is none yet); after a failed sync, or when <paramref name="offline"/>, the
    /// cached one as it is.
    /// </summary>
    private async Task<(CachedFolder? State, AccountError? Error)> StateAsync(
        MailboxService mailbox, FolderCache cache, Source source, bool sync, bool offline, CancellationToken ct)
    {
        var key = FolderCache.Key(source.FullName);
        try
        {
            var state = sync && !offline ? null : await cache.FindAsync(source.Account.Id, key, ct);
            return (state ?? (offline ? null : await mailbox.FolderStateAsync(source.Account, source.FolderId, ct)), null);
        }
        catch (Exception e) when (Describe(source.Account, e) is { } error)
        {
            return (await cache.FindAsync(source.Account.Id, key, ct), error);
        }
    }

    /// <summary>Mail failures become an account error (with the texts the per-account endpoints answer); anything else is a bug and fails the request.</summary>
    private AccountError? Describe(MailAccount account, Exception e)
    {
        var error = e switch
        {
            MailConnectionException c => new AccountError(account.Id, c.Code, c.Message),
            CredentialUnavailableException => new AccountError(account.Id, "credential_unavailable",
                "The stored password cannot be decrypted. Enter it again in the account settings."),
            ImapCommandException or FolderNotFoundException or MailboxNotFoundException => new AccountError(account.Id, "server_error",
                "The mail server refused the request."),
            _ => null,
        };
        if (error is not null)
        {
            logger.LogInformation("Account {AccountId} unavailable for the unified folders: {Error}", account.Id, error.Error);
        }

        return error;
    }

    private static SearchHit Hit(Source source, uint uidValidity, CachedMessage message) =>
        new(source.Account.Id, source.FolderId, source.FullName, FolderCache.ToSummaryDto(uidValidity, message));

    private static int? Unread(List<CachedFolder> states) =>
        states.Any(s => s.Unread is not null) ? states.Sum(s => s.Unread ?? 0) : null;

    /// <summary>Each folder's change token: "{account id}.{folder id}.{token}", joined by "~"; folders without a cache state are left out.</summary>
    private static string Token(IEnumerable<(Source Source, CachedFolder? State)> folders) =>
        string.Join('~', folders.Where(f => f.State is not null).OrderBy(f => f.Source.Key, StringComparer.Ordinal)
            .Select(f => $"{f.Source.Key}.{f.State!.ChangeSeq}"));

    private static Dictionary<string, long> ParseToken(string? token)
    {
        var result = new Dictionary<string, long>();
        if (string.IsNullOrEmpty(token))
        {
            return result;
        }

        foreach (var entry in token.Split('~'))
        {
            var parts = entry.Split('.');
            if (parts.Length != 3
                || !Guid.TryParseExact(parts[0], "N", out var accountId)
                || MailboxIds.FolderFullName(parts[1]) is null
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                || !result.TryAdd($"{accountId:N}.{parts[1]}", seq))
            {
                throw new MailboxValidationException("since", "The token is not valid.");
            }
        }

        return result;
    }
}
