using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Scheduling;

namespace SocklessMail.Api.Api;

/// <summary>Send later: scheduling a message, the scheduled messages of all accounts, and the user's presets.</summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/accounts/{accountId:guid}/schedule", async (
                Guid accountId, ScheduleInput input,
                ClaimsPrincipal user, AccountService accounts, ScheduledMail scheduled, CancellationToken ct) =>
                await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                    ? Results.Ok(await scheduled.ScheduleAsync(a, input, ct))
                    : MailboxEndpoints.NotFound())
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>()
            .RequireRateLimiting(ComposeEndpoints.SendRateLimit);

        var presets = api.MapGroup("/send-later/presets").AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        presets.MapGet("", (ClaimsPrincipal user, SendLaterPresets store, CancellationToken ct) =>
            store.GetAsync(user.GetNextcloudUserId(), ct));

        presets.MapPut("", async (SendLaterPresetsInput input, ClaimsPrincipal user, SendLaterPresets store, CancellationToken ct) =>
            Results.Ok(await store.SaveAsync(user.GetNextcloudUserId(), input.Presets, ct)));

        presets.MapDelete("", async (ClaimsPrincipal user, SendLaterPresets store, CancellationToken ct) =>
        {
            await store.ResetAsync(user.GetNextcloudUserId(), ct);
            return Results.NoContent();
        });

        var group = api.MapGroup("/scheduled").AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        group.MapGet("", async (Guid? accountId, ScheduledMail scheduled, CancellationToken ct) =>
            Results.Ok(await scheduled.ListAsync(accountId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, ScheduledMail scheduled, CancellationToken ct) =>
            Results.Ok(await scheduled.GetAsync(id, ct)));

        group.MapPut("/{id:guid}", async (Guid id, RescheduleInput input, ScheduledMail scheduled, CancellationToken ct) =>
            Results.Ok(await scheduled.RescheduleAsync(id, input, ct)));

        // Not the request's token: once asked for, the send finishes even if the tab goes away.
        group.MapPost("/{id:guid}/send-now", async (Guid id, ScheduledMail scheduled) =>
                Results.Ok(await scheduled.SendNowAsync(id, CancellationToken.None)))
            .RequireRateLimiting(ComposeEndpoints.SendRateLimit);

        group.MapDelete("/{id:guid}", async (Guid id, ScheduledMail scheduled, CancellationToken ct) =>
            Results.Ok(await scheduled.CancelAsync(id, ct)));
    }
}
