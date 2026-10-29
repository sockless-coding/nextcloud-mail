using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Calendar;

namespace SocklessMail.Api.Api;

/// <summary>Nextcloud Calendar: the user's calendars and the calendar data in messages (invitations, .ics files).</summary>
public static class CalendarEndpoints
{
    public const string SendRateLimit = ComposeEndpoints.SendRateLimit;

    public static void MapCalendarEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/calendars", async (ClaimsPrincipal user, InvitationService invitations, CancellationToken ct) =>
                Results.Ok(await invitations.ListCalendarsAsync(user.GetNextcloudUserId(), ct)))
            .AddEndpointFilter<CalendarErrorFilter>();

        var part = api.MapGroup("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/invitations/{partId}")
            .AddEndpointFilter<CalendarErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        part.MapGet("", async (
                Guid accountId, string folderId, string messageId, string partId,
                ClaimsPrincipal user, AccountService accounts, InvitationService invitations, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await invitations.GetAsync(userId, a, folderId, messageId, partId, ct))
                    : MailboxEndpoints.NotFound();
            });

        // Answering sends mail, so it counts against the sending limit.
        part.MapPost("/respond", async (
                Guid accountId, string folderId, string messageId, string partId, RespondInput input,
                ClaimsPrincipal user, AccountService accounts, InvitationService invitations, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await invitations.RespondAsync(userId, a, folderId, messageId, partId, input, ct))
                    : MailboxEndpoints.NotFound();
            })
            .RequireRateLimiting(SendRateLimit);

        part.MapPost("/import", async (
                Guid accountId, string folderId, string messageId, string partId, ImportInput input,
                ClaimsPrincipal user, AccountService accounts, InvitationService invitations, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await invitations.ImportAsync(userId, a, folderId, messageId, partId, input, ct))
                    : MailboxEndpoints.NotFound();
            });

        part.MapPost("/apply", async (
                Guid accountId, string folderId, string messageId, string partId,
                ClaimsPrincipal user, AccountService accounts, InvitationService invitations, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await invitations.ApplyAsync(userId, a, folderId, messageId, partId, ct))
                    : MailboxEndpoints.NotFound();
            });
    }

    private sealed class CalendarErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (CalendarException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.StatusCode);
            }
        }
    }
}
