using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Sync;

public sealed record WatcherOptions
{
    /// <summary>Accounts of users who used the app within this time are watched.</summary>
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Watched accounts across all users; each holds one IMAP connection.</summary>
    public int MaxWatchers { get; init; } = 50;

    /// <summary>How often the set of watched accounts is checked (a newly active user wakes it at once).</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>IDLE is re-issued after this; RFC 2177 asks for less than 29 minutes.</summary>
    public TimeSpan IdleRestart { get; init; } = TimeSpan.FromMinutes(9);

    /// <summary>Sync interval for servers without IDLE.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Keeps the INBOX cache of recently active users' accounts current with IMAP IDLE, one dedicated
/// connection per account (outside the pool, so watchers never take connections that requests
/// need). While a watcher is idling with nothing pending, <see cref="IsCurrent"/> lets requests
/// serve the INBOX from the cache without asking the server. Servers without IDLE are synced
/// every <see cref="WatcherOptions.PollInterval"/> instead, and requests keep syncing themselves.
/// </summary>
public sealed class MailboxWatchers(
    WatcherOptions options, UserActivity activity, IServiceScopeFactory scopes, MailServerConnector connector,
    CredentialProtector credentials, SyncLocks syncLocks, TimeProvider clock, ILogger<MailboxWatchers> logger) : BackgroundService
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private sealed class Watcher(Guid accountId, string userId, DateTimeOffset accountVersion, CancellationToken stopping)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public Guid AccountId { get; } = accountId;
        public string UserId { get; } = userId;

        /// <summary>The account's UpdatedAt when the watcher started; a change (new password, server) restarts it.</summary>
        public DateTimeOffset AccountVersion { get; } = accountVersion;

        public Task Running = Task.CompletedTask;
        public volatile bool Current;
        public volatile bool Dirty;
        public volatile CancellationTokenSource? IdleDone;

        /// <summary>Called from MailKit's event handlers while IDLE runs: end IDLE so the change gets synced.</summary>
        public void Changed()
        {
            Dirty = true;
            Current = false;
            try
            {
                IdleDone?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>True when the folder is a watched INBOX whose cache reflects everything the server reported.</summary>
    public bool IsCurrent(Guid accountId, string fullName) =>
        fullName == "INBOX" && _watchers.TryGetValue(accountId, out var watcher) && watcher.Current;

    /// <summary>Accounts being watched, for tests and diagnostics.</summary>
    public int Count => _watchers.Count;

    /// <summary>Stops the account's watcher (account changed or deleted); it restarts with fresh settings if still wanted.</summary>
    public void Invalidate(Guid accountId)
    {
        if (_watchers.TryRemove(accountId, out var watcher))
        {
            watcher.Current = false;
            watcher.Stop.Cancel();
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.MaxWatchers <= 0)
        {
            return;
        }

        activity.Activated += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Updating the set of watched accounts failed");
                }

                await _wake.WaitAsync(options.ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            activity.Activated -= Wake;
            var running = _watchers.Values.Select(w => w.Running).ToList();
            foreach (var watcher in _watchers.Values)
            {
                watcher.Stop.Cancel();
            }

            await Task.WhenAll(running);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var users = activity.ActiveUsers();
        var wanted = new Dictionary<Guid, (string UserId, DateTimeOffset Version)>();
        if (users.Count > 0)
        {
            var rank = users.Select((user, i) => (user, i)).ToDictionary(x => x.user, x => x.i);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var accounts = new List<(Guid Id, string Owner, int SortOrder, DateTimeOffset UpdatedAt)>();
            foreach (var chunk in users.Chunk(500))
            {
                // Deliberately across users: this decides which accounts get a connection.
                var rows = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => chunk.Contains(a.OwnerUserId))
                    .Select(a => new { a.Id, a.OwnerUserId, a.SortOrder, a.UpdatedAt })
                    .ToListAsync(ct);
                accounts.AddRange(rows.Select(a => (a.Id, a.OwnerUserId, a.SortOrder, a.UpdatedAt)));
            }

            foreach (var account in accounts.OrderBy(a => rank[a.Owner]).ThenBy(a => a.SortOrder).Take(options.MaxWatchers))
            {
                wanted[account.Id] = (account.Owner, account.UpdatedAt);
            }
        }

        foreach (var (id, watcher) in _watchers)
        {
            if (!wanted.TryGetValue(id, out var account) || account.Version != watcher.AccountVersion)
            {
                Invalidate(id);
            }
        }

        foreach (var (id, account) in wanted)
        {
            if (_watchers.ContainsKey(id))
            {
                continue;
            }

            var watcher = new Watcher(id, account.UserId, account.Version, ct);
            if (_watchers.TryAdd(id, watcher))
            {
                watcher.Running = Task.Run(() => WatchAsync(watcher), CancellationToken.None);
            }
        }
    }

    /// <summary>Connects, syncs and idles until stopped; reconnects with growing delays after failures.</summary>
    private async Task WatchAsync(Watcher watcher)
    {
        var ct = watcher.Stop.Token;
        var delay = options.FirstRetryDelay;
        var parked = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await WatchConnectedAsync(watcher, () => delay = options.FirstRetryDelay, ct))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CredentialUnavailableException)
            {
                // Nothing to retry with until the user enters the password again (which restarts the watcher).
                // It stays registered (and idle), so reconciling does not restart it every 30 seconds.
                logger.LogWarning("Not watching account {AccountId}: its stored credential cannot be decrypted", watcher.AccountId);
                parked = true;
                break;
            }
            catch (Exception e)
            {
                watcher.Current = false;
                logger.LogInformation("Watching account {AccountId} failed ({ExceptionType}); retrying in {Delay}",
                    watcher.AccountId, e.GetType().Name, delay);
                try
                {
                    await Task.Delay(delay, clock, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxRetryDelay.Ticks));
            }
        }

        watcher.Current = false;
        if (!parked)
        {
            _watchers.TryRemove(new KeyValuePair<Guid, Watcher>(watcher.AccountId, watcher));
        }
    }

    /// <returns>false when the account no longer exists.</returns>
    private async Task<bool> WatchConnectedAsync(Watcher watcher, Action connected, CancellationToken ct)
    {
        MailAccount? account;
        await using (var scope = scopes.CreateUserScope(watcher.UserId))
        {
            account = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == watcher.AccountId, ct);
        }

        if (account is null)
        {
            return false;
        }

        var credential = credentials.Unprotect(account.Id, account.ProtectedCredential);
        var client = await connector.ConnectImapAsync(
            new MailEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity), account.Username, credential, ct);
        var inbox = client.Inbox;
        void OnChange(object? sender, EventArgs e) => watcher.Changed();
        inbox.CountChanged += OnChange;
        inbox.MessageExpunged += OnChange;
        inbox.MessagesVanished += OnChange;
        inbox.MessageFlagsChanged += OnChange;
        try
        {
            var idle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
            var resynced = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                watcher.Dirty = false;
                await SyncInboxAsync(client, account, ct);
                connected();
                // A change reported during the sync may or may not be in it: sync once more. Only
                // once, because some servers echo the sync's own fetches as unsolicited updates.
                if (watcher.Dirty && !resynced)
                {
                    resynced = true;
                    continue;
                }

                resynced = false;
                if (!idle)
                {
                    await Task.Delay(options.PollInterval, clock, ct);
                    continue;
                }

                using var done = new CancellationTokenSource(options.IdleRestart, clock);
                watcher.Dirty = false;
                watcher.IdleDone = done;
                watcher.Current = true;
                try
                {
                    await client.IdleAsync(done.Token, ct);
                }
                finally
                {
                    watcher.Current = false;
                    watcher.IdleDone = null;
                }
            }
        }
        finally
        {
            watcher.Current = false;
            inbox.CountChanged -= OnChange;
            inbox.MessageExpunged -= OnChange;
            inbox.MessagesVanished -= OnChange;
            inbox.MessageFlagsChanged -= OnChange;
            await CloseAsync(client);
        }
    }

    private async Task SyncInboxAsync(ImapClient client, MailAccount account, CancellationToken ct)
    {
        using var _ = await syncLocks.AcquireAsync(account.Id, "INBOX", ct);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        await using var scope = scopes.CreateUserScope(account.OwnerUserId);
        await scope.ServiceProvider.GetRequiredService<FolderCache>()
            .SyncAsync(client, account, client.Inbox, MailboxService.DefaultPageSize, ct);
    }

    private static async Task CloseAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await client.DisconnectAsync(true, timeout.Token);
            }
        }
        catch
        {
            // The connection is being dropped anyway.
        }
        finally
        {
            client.Dispose();
        }
    }
}
