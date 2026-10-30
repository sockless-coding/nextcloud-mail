using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Tags;

public sealed record TagScannerOptions
{
    /// <summary>How often every account of a user with tags is scanned; zero or less turns the scanner off.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The first round after startup.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Accounts scanned at the same time, each with one IMAP connection.</summary>
    public int MaxConcurrency { get; init; } = 4;
}

/// <summary>
/// Runs the tag scan (<see cref="TagScan"/>) of every account whose user has tags, every
/// <see cref="TagScannerOptions.Interval"/>, and of one user's accounts soon after
/// <see cref="Request"/> (a tag was created, a tag folder opened after a while). Folders whose
/// tagged messages changed are pushed to the user's tabs, so open tag folders refresh.
/// </summary>
public sealed class TagScanner(
    TagScannerOptions options, IServiceScopeFactory scopes, ImapConnections imap, MailLogins logins, MailPush push,
    TimeProvider clock, ILogger<TagScanner> logger) : BackgroundService
{
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _scanned = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _running = new();

    /// <summary>Asks for a scan of the user's accounts; ignored while the scanner is off.</summary>
    public void Request(string userId) => _requests.Writer.TryWrite(userId);

    /// <summary>Asks for a scan unless the user's accounts were scanned within <paramref name="maxAge"/>.</summary>
    public void RequestIfOlder(string userId, TimeSpan maxAge)
    {
        if (!_scanned.TryGetValue(userId, out var at) || clock.GetUtcNow() - at > maxAge)
        {
            _scanned[userId] = clock.GetUtcNow();
            Request(userId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            return;
        }

        await Task.WhenAll(PeriodicAsync(stoppingToken), RequestsAsync(stoppingToken));
    }

    private async Task PeriodicAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(options.FirstDelay, clock, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunAllAsync(ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Scanning tags failed");
                }

                await Task.Delay(options.Interval, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task RequestsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var userId in _requests.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ScanUserAsync(userId, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Scanning tags failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>One round over every account of every user with tags.</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        List<(Guid Account, string Owner)> accounts;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            // Deliberately across users: this decides which accounts get scanned. Users of app-kept
            // tags are included without tags too, so lost rows still expire.
            var owners = db.Tags.IgnoreQueryFilters().Select(t => t.OwnerUserId)
                .Union(db.TaggedMessages.IgnoreQueryFilters().Select(m => m.OwnerUserId));
            var rows = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => owners.Contains(a.OwnerUserId))
                .Select(a => new { a.Id, a.OwnerUserId })
                .ToListAsync(ct);
            accounts = rows.Select(r => (r.Id, r.OwnerUserId)).ToList();
        }

        await Parallel.ForEachAsync(accounts,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = ct },
            async (a, token) => await ScanAccountAsync(a.Account, a.Owner, token));
        foreach (var owner in accounts.Select(a => a.Owner).Distinct())
        {
            _scanned[owner] = clock.GetUtcNow();
        }
    }

    /// <summary>Scans every account of one user, one after the other.</summary>
    public async Task ScanUserAsync(string userId, CancellationToken ct)
    {
        List<Guid> accounts;
        await using (var scope = scopes.CreateUserScope(userId))
        {
            accounts = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.Select(a => a.Id).ToListAsync(ct);
        }

        _scanned[userId] = clock.GetUtcNow();
        foreach (var account in accounts)
        {
            await ScanAccountAsync(account, userId, ct);
        }
    }

    private async Task ScanAccountAsync(Guid accountId, string userId, CancellationToken ct)
    {
        // A periodic round and a request may meet on one account; the second waits instead of scanning twice at once.
        var gate = _running.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateUserScope(userId);
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null)
            {
                return;
            }

            var keywords = await db.Tags.Select(t => t.Keyword).ToListAsync(ct);
            var scan = scope.ServiceProvider.GetRequiredService<TagScan>();
            var changed = await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct),
                (client, token) => scan.RunAsync(client, account, keywords, token), ct, retry: false);
            foreach (var folder in changed)
            {
                await push.FolderChangedAsync(userId, accountId, folder);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CredentialUnavailableException)
        {
            logger.LogDebug("Not scanning the tags of account {AccountId}: its stored credential cannot be decrypted", accountId);
        }
        catch (Exception e)
        {
            logger.LogInformation("Scanning the tags of account {AccountId} failed ({ExceptionType})", accountId, e.GetType().Name);
        }
        finally
        {
            gate.Release();
        }
    }
}
