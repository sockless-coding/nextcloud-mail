using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Tasks;

namespace SocklessMail.Api.Api;

/// <summary>Nextcloud Tasks: whether the user has it, and tasks made from messages.</summary>
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/tasks", async (ClaimsPrincipal user, TaskService tasks, CancellationToken ct) =>
                Results.Ok(await tasks.GetAsync(user.GetNextcloudUserId(), ct)))
            .AddEndpointFilter<CalendarEndpoints.CalendarErrorFilter>();

        api.MapPost("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/task", async (
                Guid accountId, string folderId, string messageId, CreateTaskInput input,
                ClaimsPrincipal user, AccountService accounts, TaskService tasks, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await tasks.CreateAsync(userId, a, folderId, messageId, input, ct))
                    : MailboxEndpoints.NotFound();
            })
            .AddEndpointFilter<CalendarEndpoints.CalendarErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();
    }
}
