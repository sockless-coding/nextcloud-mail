using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Files;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Api;

/// <summary>Nextcloud Files: attach from Files, share links, save attachments, and the Files app's "Send by email".</summary>
public static class FilesEndpoints
{
    /// <summary>The Files app posts here (through AppAPI's PHP proxy) when "Send by email" is chosen; see <see cref="UiRegistration"/>.</summary>
    public const string SendActionPath = "files-action/send";

    public static void MapFilesEndpoints(this RouteGroupBuilder api)
    {
        var files = api.MapGroup("/files").AddEndpointFilter<FilesErrorFilter>().AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        files.MapPost("/attachments", async (AttachFromFilesInput input, ClaimsPrincipal user, FilesService service, CancellationToken ct) =>
            Results.Ok(new { files = await service.AttachAsync(user.GetNextcloudUserId(), input, ct) }));

        files.MapPost("/share-links", async (ShareLinkInput input, ClaimsPrincipal user, FilesService service, CancellationToken ct) =>
            Results.Ok(await service.CreateShareLinkAsync(user.GetNextcloudUserId(), input, ct)));

        api.MapGroup("/accounts/{accountId:guid}")
            .AddEndpointFilter<FilesErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>()
            .MapPost("/folders/{folderId}/messages/{messageId}/save-to-files", async (
                Guid accountId, string folderId, string messageId, SaveToFilesInput input,
                ClaimsPrincipal user, AccountService accounts, FilesService service, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                return await accounts.GetAsync(userId, accountId, ct) is { } a
                    ? Results.Ok(new { saved = await service.SaveAttachmentsAsync(userId, a, folderId, messageId, input, ct) })
                    : MailboxEndpoints.NotFound();
            });
    }

    /// <summary>
    /// The Files app's action handler. Outside /api because the Files app cannot send our CSRF
    /// header; it has no effect beyond telling the Files app where to go. AppAPI then opens our
    /// page with ?fileIds=…, and the page attaches them through /api/files/attachments.
    /// </summary>
    public static void MapFilesAction(this IEndpointRouteBuilder app) =>
        app.MapPost("/" + SendActionPath, () => Results.Json(new { redirect_handler = UiRegistration.MenuName }))
            .RequireAuthorization(AppApiAuthentication.UserPolicy);

    private sealed class FilesErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (NextcloudFilesException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.StatusCode);
            }
        }
    }
}
