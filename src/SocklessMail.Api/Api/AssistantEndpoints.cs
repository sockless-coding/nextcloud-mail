using System.Security.Claims;
using System.Text.Json;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Assistant;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Api;

/// <summary>The Nextcloud Assistant: what it offers the user, summaries, and the tasks they wait for.</summary>
public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assistant", async (ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
            Results.Ok(await assistant.GetAsync(user.GetNextcloudUserId(), ct)));

        api.MapPost("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/summary", async (
                Guid accountId, string folderId, string messageId, SummaryInput input,
                ClaimsPrincipal user, AccountService accounts, AssistantService assistant, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await assistant.SummarizeAsync(userId, a, folderId, messageId, input, ct))
                    : MailboxEndpoints.NotFound();
            })
            .AddEndpointFilter<AssistantErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        api.MapPost("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/translation", async (
                Guid accountId, string folderId, string messageId, MessageTranslationInput input,
                ClaimsPrincipal user, AccountService accounts, AssistantService assistant, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await assistant.TranslateMessageAsync(userId, a, folderId, messageId, input, ct))
                    : MailboxEndpoints.NotFound();
            })
            .AddEndpointFilter<AssistantErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        api.MapPost("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/scam-check", async (
                Guid accountId, string folderId, string messageId, ScamCheckInput input,
                ClaimsPrincipal user, AccountService accounts, AssistantService assistant, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(await assistant.CheckScamAsync(userId, a, folderId, messageId, input, ct))
                    : MailboxEndpoints.NotFound();
            })
            .AddEndpointFilter<AssistantErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        api.MapPost("/assistant/write", async (WriteInput input, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
                Results.Ok(await assistant.WriteAsync(user.GetNextcloudUserId(), input, ct)))
            .AddEndpointFilter<AssistantErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        api.MapPost("/assistant/rewrite", async (RewriteInput input, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
                Results.Ok(await assistant.RewriteAsync(user.GetNextcloudUserId(), input, ct)))
            .AddEndpointFilter<AssistantErrorFilter>();

        api.MapGet("/assistant/tasks/{taskId:long}", async (long taskId, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
                Results.Ok(await assistant.GetTaskAsync(user.GetNextcloudUserId(), taskId, ct)))
            .AddEndpointFilter<AssistantErrorFilter>();

        api.MapPost("/assistant/tasks/{taskId:long}/cancel", async (long taskId, ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
                Results.Ok(await assistant.CancelAsync(user.GetNextcloudUserId(), taskId, ct)))
            .AddEndpointFilter<AssistantErrorFilter>();
    }

    /// <summary>
    /// Nextcloud's call when one of our tasks ends ({ task: { … } }, through AppAPI with the task's
    /// user). Outside /api and not in info.xml's routes, so browsers cannot reach it. It only nudges
    /// that user's tabs, which then ask for the task as the user.
    /// </summary>
    public static void MapAssistantWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost(AssistantService.WebhookPath, async (JsonElement body, ClaimsPrincipal user, AssistantService assistant, MailPush push) =>
            {
                var task = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("task", out var t) ? t : default;
                if (assistant.IsOwnTask(task)
                    && task.TryGetProperty("userId", out var owner) && owner.GetString() == user.GetNextcloudUserId())
                {
                    await push.AssistantTaskChangedAsync(user.GetNextcloudUserId(), task.GetProperty("id").GetInt64());
                }

                return Results.Ok(new { });
            })
            .RequireAuthorization(AppApiAuthentication.UserPolicy);
    }

    internal sealed class AssistantErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (AssistantException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.StatusCode);
            }
        }
    }
}
