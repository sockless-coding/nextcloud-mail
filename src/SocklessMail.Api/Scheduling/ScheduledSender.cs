using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Scheduling;

public sealed record ScheduledSenderOptions
{
    /// <summary>The longest wait between looks at the table, whatever is due next.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How far ahead a message has to be scheduled.</summary>
    public TimeSpan MinimumLead { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Waits after failed attempts that may work later (server down, login refused); then it fails.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public int MaxConcurrency { get; init; } = 4;
}

/// <summary>
/// Sends scheduled messages when they are due, whether or not anybody has the app open. Each
/// message is claimed first (status Sending, one UPDATE), so a "send now" or a cancel never
/// races the timer. A message still Sending at startup was cut off mid-way and may have gone
/// out; it fails rather than risk sending it twice.
/// </summary>
public sealed class ScheduledSender(
    ScheduledSenderOptions options, IServiceScopeFactory scopes, ScheduledMessageFiles files, MailPush push,
    TimeProvider clock, ILogger<ScheduledSender> logger) : BackgroundService
{
    /// <summary>Errors that will not go away by trying again.</summary>
    private static readonly HashSet<string> Permanent =
    [
        "sender_rejected", "message_rejected", "send_interrupted", "oauth_signin_required", "oauth_not_configured",
        "address_not_allowed", "tls_failed", "starttls_unsupported", "auth_unsupported",
    ];

    private static readonly TimeSpan OrphanSweep = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Something was scheduled or rescheduled: look again now rather than at the next poll.</summary>
    public void Wake()
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
        try
        {
            await RecoverAsync(stoppingToken);
            var nextSweep = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    if (clock.GetUtcNow() >= nextSweep)
                    {
                        await SweepOrphansAsync(stoppingToken);
                        nextSweep = clock.GetUtcNow() + OrphanSweep;
                    }

                    wait = await RunDueAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Sending scheduled messages failed");
                    wait = options.PollInterval;
                }

                await _wake.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Sends what is due; returns how long until the next one (at most the poll interval).</summary>
    public async Task<TimeSpan> RunDueAsync(CancellationToken ct)
    {
        List<(Guid Id, string Owner)> due;
        DateTimeOffset? next;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var now = clock.GetUtcNow();
            // Deliberately across users: this decides what gets sent.
            var waiting = db.ScheduledMessages.IgnoreQueryFilters().AsNoTracking().Where(m => m.Status == ScheduledStatus.Scheduled);
            var rows = await waiting.Where(m => m.DueAt <= now).OrderBy(m => m.DueAt).Take(50)
                .Select(m => new { m.Id, m.OwnerUserId }).ToListAsync(ct);
            due = rows.Select(r => (r.Id, r.OwnerUserId)).ToList();
            next = await waiting.Where(m => m.DueAt > now).OrderBy(m => m.DueAt).Select(m => (DateTimeOffset?)m.DueAt).FirstOrDefaultAsync(ct);
        }

        await Parallel.ForEachAsync(due,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = ct },
            async (item, token) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await TryClaimAsync(scope.ServiceProvider.GetRequiredService<MailDbContext>(), item.Id, clock.GetUtcNow(), token, dueOnly: true))
                {
                    await SendClaimedAsync(item.Id, item.Owner, token);
                }
            });

        if (due.Count == 50)
        {
            return TimeSpan.Zero;
        }

        var untilNext = next is { } at ? at - clock.GetUtcNow() : options.PollInterval;
        return untilNext < TimeSpan.Zero ? TimeSpan.Zero : untilNext < options.PollInterval ? untilNext : options.PollInterval;
    }

    /// <summary>
    /// Marks the message Sending if nobody else has. <paramref name="dueOnly"/>: only a Scheduled
    /// message that is due (the timer); otherwise also a failed or not yet due one (the user).
    /// </summary>
    public static async Task<bool> TryClaimAsync(MailDbContext db, Guid id, DateTimeOffset now, CancellationToken ct, bool dueOnly = false)
    {
        var query = db.ScheduledMessages.IgnoreQueryFilters().Where(m => m.Id == id);
        query = dueOnly
            ? query.Where(m => m.Status == ScheduledStatus.Scheduled && m.DueAt <= now)
            : query.Where(m => m.Status != ScheduledStatus.Sending);
        return await query.ExecuteUpdateAsync(set => set
            .SetProperty(m => m.Status, ScheduledStatus.Sending)
            .SetProperty(m => m.UpdatedAt, now), ct) == 1;
    }

    /// <summary>Sends a message this caller claimed (<see cref="TryClaimAsync"/>), and records how it went.</summary>
    public async Task<SendNowResult> SendClaimedAsync(Guid id, string userId, CancellationToken ct)
    {
        await using var scope = scopes.CreateUserScope(userId);
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MailDbContext>();
        var row = await db.ScheduledMessages.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (row is null)
        {
            return new SendNowResult(false, null, null);
        }

        // Deleting an account deletes its rows; this only happens when that raced the claim.
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == row.AccountId, ct);
        if (account is null)
        {
            db.ScheduledMessages.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
            files.Delete(id);
            return new SendNowResult(false, null, null);
        }

        var message = await files.LoadAsync(id, ct);
        if (message is null)
        {
            return await FailAsync(services, db, row, "unreadable", "The stored message can no longer be read, so it was not sent.", retry: false);
        }

        var delivering = false;
        try
        {
            var result = await services.GetRequiredService<MailboxService>().SendStoredAsync(account, message, row.Source, () => delivering = true, ct);
            db.ScheduledMessages.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
            files.Delete(id);
            await push.ScheduledChangedAsync(userId);
            return new SendNowResult(true, result.Warning, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. Before the transfer began, nothing went out: it is simply due again.
            if (!delivering)
            {
                row.Status = ScheduledStatus.Scheduled;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            throw;
        }
        catch (Exception e)
        {
            var (code, text, retry) = Classify(e, delivering);
            logger.LogInformation("Sending scheduled message {ScheduledId} of account {AccountId} failed: {ErrorCode}", id, account.Id, code);
            return await FailAsync(services, db, row, code, text, retry);
        }
    }

    private static (string Code, string Message, bool Retry) Classify(Exception e, bool delivering) => e switch
    {
        MailboxValidationException v => ("recipient_rejected", v.Message, false),
        MailConnectionException c => (c.Code, c.Message, !Permanent.Contains(c.Code)),
        CredentialUnavailableException => ("credential_unavailable",
            "The account's stored password cannot be decrypted. Enter it again in the account settings.", false),
        // Cut off during the transfer without a server answer: it may have gone out.
        _ when delivering => ("send_interrupted", "The connection broke while sending. Check Sent before trying again.", false),
        _ => ("unknown", "Sending failed unexpectedly.", true),
    };

    private async Task<SendNowResult> FailAsync(IServiceProvider services, MailDbContext db, ScheduledMessage row, string code, string text, bool retry)
    {
        var now = clock.GetUtcNow();
        row.Attempts++;
        row.ErrorCode = code;
        row.UpdatedAt = now;
        if (retry && row.Attempts <= options.RetryDelays.Count)
        {
            row.Status = ScheduledStatus.Scheduled;
            row.DueAt = now + options.RetryDelays[row.Attempts - 1];
            row.Error = $"{text} Trying again shortly.";
        }
        else
        {
            row.Status = ScheduledStatus.Failed;
            row.Error = text;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        if (row.Status == ScheduledStatus.Failed)
        {
            await services.GetRequiredService<ScheduledFailureNotices>().NotifyAsync(row, CancellationToken.None);
        }
        else
        {
            Wake();
        }

        await push.ScheduledChangedAsync(row.OwnerUserId);
        return new SendNowResult(false, null, ScheduledMessageDto.From(row));
    }

    /// <summary>Cut off mid-way by a restart: may have gone out, so it fails instead of being sent again.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        List<ScheduledMessage> stuck;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            stuck = await db.ScheduledMessages.IgnoreQueryFilters().Where(m => m.Status == ScheduledStatus.Sending).ToListAsync(ct);
        }

        foreach (var row in stuck)
        {
            await using var scope = scopes.CreateUserScope(row.OwnerUserId);
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            db.Attach(row);
            logger.LogWarning("Scheduled message {ScheduledId} was being sent when the app stopped", row.Id);
            await FailAsync(scope.ServiceProvider, db, row, "send_interrupted",
                "The app stopped while sending this message. Check Sent before trying again.", retry: false);
        }
    }

    private async Task SweepOrphansAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ids = await scope.ServiceProvider.GetRequiredService<MailDbContext>().ScheduledMessages
            .IgnoreQueryFilters().Select(m => m.Id).ToListAsync(ct);
        files.DeleteOrphans(ids.ToHashSet());
    }
}
