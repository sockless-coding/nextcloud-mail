using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// A position in the unified inbox, which is ordered by date, then account id, then UID (all
/// descending, so the order is total). Sent as "{utc ticks}.{account id}.{uid}".
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
/// The inboxes of all of a user's accounts as one list, read from the header cache. Each account's
/// inbox is synced (or taken as is while an IDLE watcher keeps it current) in its own service
/// scope, so a slow or unreachable server delays the list by at most its own timeout and does not
/// fail it: its messages show as last cached and the account is reported in the errors.
/// <para>
/// The cache holds a window of each inbox by UID, while the list is ordered by date. A page is
/// complete when every account either contributes more than a page of cached messages below the
/// cursor or has its whole inbox cached; an account short of that has its window extended on the
/// server first. This relies on UID order following date order, which holds for delivered mail;
/// messages appended with an old date (imports) can show up later than their date suggests.
/// </para>
/// </summary>
public sealed class UnifiedInbox(AccountService accounts, IServiceScopeFactory scopes, ILogger<UnifiedInbox> logger)
{
    private const string Inbox = "INBOX";
    private static readonly string InboxId = MailboxIds.FolderId(Inbox);

    private sealed record InboxResult(MailAccount Account, CachedFolder? State, AccountError? Error, List<CachedMessage> Rows, FolderChanges? Changes);

    public async Task<UnifiedPage> GetPageAsync(string userId, UnifiedCursor? before, int limit, CancellationToken ct)
    {
        // The first page brings every inbox up to date; older pages read what the first one synced.
        var results = await PerAccountAsync(userId, async (mailbox, cache, account, token) =>
        {
            var (state, error) = await InboxStateAsync(mailbox, cache, account, sync: before is null, token);
            if (state is null)
            {
                return new InboxResult(account, null, error, [], null);
            }

            var rows = await cache.PageByDateAsync(state, before, limit + 1, token);
            if (rows.Count <= limit && state.WindowStart > 1 && error is null)
            {
                try
                {
                    // Extended by at least a page, so the next pages find it cached.
                    state = await mailbox.ExtendInboxAsync(account, Math.Max(limit + 1 - rows.Count, MailboxService.DefaultPageSize), token);
                    rows = await cache.PageByDateAsync(state, before, limit + 1, token);
                }
                catch (Exception e) when (Describe(account, e) is { } failure)
                {
                    error = failure;
                }
            }

            return new InboxResult(account, state, error, rows, null);
        }, ct);

        var merged = results
            .SelectMany(r => r.Rows.Select(m => (Result: r, Message: m)))
            .OrderByDescending(x => x.Message.Date).ThenByDescending(x => x.Result.Account.Id).ThenByDescending(x => x.Message.Uid)
            .Take(limit + 1)
            .ToList();
        var page = merged.Take(limit).ToList();
        var states = results.Where(r => r.State is not null).Select(r => r.State!).ToList();
        return new UnifiedPage(
            page.Select(x => Hit(x.Result.Account, x.Result.State!.UidValidity, x.Message)).ToList(),
            merged.Count > limit ? UnifiedCursor.Of(page[^1].Result.Account.Id, page[^1].Message).ToString() : null,
            states.Sum(s => s.Total),
            Unread(states),
            Token(states),
            Errors(results));
    }

    /// <summary>What changed in any inbox since <paramref name="since"/> (a token from a page or an earlier call).</summary>
    public async Task<UnifiedChanges> GetChangesAsync(string userId, string? since, CancellationToken ct)
    {
        var seen = ParseToken(since);
        var results = await PerAccountAsync(userId, async (mailbox, cache, account, token) =>
        {
            var (state, error) = await InboxStateAsync(mailbox, cache, account, sync: true, token);
            var changes = state is not null && seen.TryGetValue(account.Id, out var seq) ? await cache.ChangesAsync(state, seq, token) : null;
            return new InboxResult(account, state, error, [], changes);
        }, ct);

        var states = results.Where(r => r.State is not null).Select(r => r.State!).ToList();
        // An inbox that appeared (new account, first successful sync) or went away (deleted account)
        // since the token cannot be merged: the client reloads.
        var reset = states.Count != seen.Count || results.Any(r => r.State is not null && r.Changes is not { Reset: false });
        var changed = results.Where(r => r.Changes is not null).ToList();
        return new UnifiedChanges(
            Token(states),
            reset,
            states.Sum(s => s.Total),
            Unread(states),
            reset ? [] : changed.SelectMany(r => r.Changes!.Messages.Select(m => new SearchHit(r.Account.Id, InboxId, Inbox, m))).ToList(),
            reset ? [] : changed.SelectMany(r => r.Changes!.Removed.Select(id => new RemovedHit(r.Account.Id, InboxId, id))).ToList(),
            Errors(results));
    }

    /// <summary>Runs <paramref name="work"/> for every account of the user at once, each in its own scope (DbContext, IMAP lease).</summary>
    private async Task<InboxResult[]> PerAccountAsync(
        string userId, Func<MailboxService, FolderCache, MailAccount, CancellationToken, Task<InboxResult>> work, CancellationToken ct)
    {
        var list = await accounts.ListAsync(userId, ct);
        return await Task.WhenAll(list.Select(async account =>
        {
            await using var scope = scopes.CreateUserScope(userId);
            var services = scope.ServiceProvider;
            return await work(services.GetRequiredService<MailboxService>(), services.GetRequiredService<FolderCache>(), account, ct);
        }));
    }

    /// <summary>The inbox's cache state, synced first when <paramref name="sync"/>; after a failed sync, the stale state.</summary>
    private async Task<(CachedFolder? State, AccountError? Error)> InboxStateAsync(
        MailboxService mailbox, FolderCache cache, MailAccount account, bool sync, CancellationToken ct)
    {
        try
        {
            var state = sync ? null : await cache.FindAsync(account.Id, Inbox, ct);
            return (state ?? await mailbox.InboxStateAsync(account, ct), null);
        }
        catch (Exception e) when (Describe(account, e) is { } error)
        {
            return (await cache.FindAsync(account.Id, Inbox, ct), error);
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
            logger.LogInformation("Inbox of account {AccountId} unavailable for the unified inbox: {Error}", account.Id, error.Error);
        }

        return error;
    }

    private static SearchHit Hit(MailAccount account, uint uidValidity, CachedMessage message) =>
        new(account.Id, InboxId, Inbox, FolderCache.ToSummaryDto(uidValidity, message));

    private static int? Unread(List<CachedFolder> states) =>
        states.Any(s => s.Unread is not null) ? states.Sum(s => s.Unread ?? 0) : null;

    private static List<AccountError> Errors(InboxResult[] results) =>
        results.Where(r => r.Error is not null).Select(r => r.Error!).ToList();

    /// <summary>Each inbox's change token: "{account id}.{token}", joined by "_".</summary>
    private static string Token(IEnumerable<CachedFolder> states) =>
        string.Join('_', states.OrderBy(s => s.AccountId).Select(s => $"{s.AccountId:N}.{s.ChangeSeq}"));

    private static Dictionary<Guid, long> ParseToken(string? token)
    {
        var result = new Dictionary<Guid, long>();
        if (string.IsNullOrEmpty(token))
        {
            return result;
        }

        foreach (var entry in token.Split('_'))
        {
            var parts = entry.Split('.');
            if (parts.Length != 2
                || !Guid.TryParseExact(parts[0], "N", out var accountId)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                || !result.TryAdd(accountId, seq))
            {
                throw new MailboxValidationException("since", "The token is not valid.");
            }
        }

        return result;
    }
}
