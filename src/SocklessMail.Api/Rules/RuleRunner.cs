using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Rules;

public sealed record RuleRunnerOptions
{
    /// <summary>How often accounts with enabled rules are checked; zero or less turns the runner off.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The first round after startup, so a restart catches up soon without slowing the start.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Accounts checked at the same time, each with one IMAP connection.</summary>
    public int MaxConcurrency { get; init; } = 4;
}

/// <summary>
/// Runs the rules of accounts nobody has open: an IDLE watcher (<see cref="MailboxWatchers"/>)
/// keeps only recently active users' INBOXes current, and their syncs run the rules within
/// seconds. Every <see cref="RuleRunnerOptions.Interval"/> this goes through every other account
/// with an enabled rule, over a pooled connection.
/// </summary>
public sealed class RuleRunner(
    RuleRunnerOptions options, IServiceScopeFactory scopes, ImapConnections imap, MailLogins logins,
    MailboxWatchers watchers, SyncLocks syncLocks, TimeProvider clock, ILogger<RuleRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(options.FirstDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunAllAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Running mail rules failed");
                }

                await Task.Delay(options.Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One round over every account with an enabled rule.</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        List<(Guid Account, string Owner)> accounts;
        await using (var scope = scopes.CreateAsyncScope())
        {
            // Deliberately across users: this decides which accounts get checked.
            var rows = await scope.ServiceProvider.GetRequiredService<MailDbContext>().RuleCursors
                .IgnoreQueryFilters().AsNoTracking()
                .Select(c => new { c.AccountId, c.OwnerUserId })
                .ToListAsync(ct);
            accounts = rows.Select(r => (r.AccountId, r.OwnerUserId)).ToList();
        }

        await Parallel.ForEachAsync(
            accounts.Where(a => !watchers.IsCurrent(a.Account, "INBOX")),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = ct },
            async (a, token) => await RunAsync(a.Account, a.Owner, token));
    }

    private async Task RunAsync(Guid accountId, string userId, CancellationToken ct)
    {
        try
        {
            // Lock before leasing a connection (see SyncLocks).
            using var _ = await syncLocks.AcquireAsync(accountId, "INBOX", ct);
            await using var scope = scopes.CreateUserScope(userId);
            var account = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null)
            {
                return;
            }

            var engine = scope.ServiceProvider.GetRequiredService<RuleEngine>();
            await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct),
                (client, token) => engine.ApplyAsync(client, account, token), ct, retry: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CredentialUnavailableException)
        {
            logger.LogDebug("Not running the rules of account {AccountId}: its stored credential cannot be decrypted", accountId);
        }
        catch (Exception e)
        {
            logger.LogInformation("Running the rules of account {AccountId} failed ({ExceptionType})", accountId, e.GetType().Name);
        }
    }
}
