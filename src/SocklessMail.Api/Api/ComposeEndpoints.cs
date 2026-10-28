using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Api;

/// <summary>Attachment uploads, reply/forward drafts, saved drafts and sending.</summary>
public static class ComposeEndpoints
{
    public const string SendRateLimit = "send";

    public static void MapComposeEndpoints(this RouteGroupBuilder api)
    {
        // Our own CSRF guard (X-Sockless-Request) covers this; ASP.NET's antiforgery tokens do not
        // fit requests that arrive through AppAPI's proxies.
        api.MapPost("/uploads", async (IFormFile file, ClaimsPrincipal user, AttachmentUploads uploads, CancellationToken ct) =>
            {
                await using var content = file.OpenReadStream();
                return await uploads.SaveAsync(user.GetNextcloudUserId(), file.FileName, file.ContentType, content, ct) is { } upload
                    ? Results.Ok(upload)
                    : Results.Json(new { error = "too_large", message = $"Attachments may be at most {AttachmentUploads.MaxFileBytes / (1024 * 1024)} MB." },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(AttachmentUploads.MaxFileBytes + 1024 * 1024));

        api.MapDelete("/uploads/{id}", (string id, ClaimsPrincipal user, AttachmentUploads uploads) =>
        {
            uploads.Delete(user.GetNextcloudUserId(), id);
            return Results.NoContent();
        });

        var account = api.MapGroup("/accounts/{accountId:guid}").AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        account.MapGet("/folders/{folderId}/messages/{messageId}/draft", async (
                Guid accountId, string folderId, string messageId, string? mode,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ReplyMode>(mode, ignoreCase: true, out var replyMode))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["mode"] = ["Use reply, replyAll, forward or edit."] });
                }

                return await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                    ? Results.Ok(await mailbox.GetDraftAsync(a, folderId, messageId, replyMode, ct))
                    : MailboxEndpoints.NotFound();
            });

        account.MapPost("/send", async (
                Guid accountId, SendInput input,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
                await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                    ? Results.Ok(await mailbox.SendAsync(a, input, ct))
                    : MailboxEndpoints.NotFound())
            .RequireRateLimiting(SendRateLimit);

        // Saved while writing (the composer saves a few seconds after each change).
        account.MapPut("/drafts", async (
                Guid accountId, SaveDraftInput input,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
                await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                    ? Results.Ok(await mailbox.SaveDraftAsync(a, input, ct))
                    : MailboxEndpoints.NotFound());
    }
}
