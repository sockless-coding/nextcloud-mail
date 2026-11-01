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
    public const string ImageImportRateLimit = "image-import";

    public sealed record ImageImportInput(string? Url);

    /// <summary>The last segment of the URL's path, with an extension matching what was received.</summary>
    private static string ImageFileName(string url, string contentType)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath)) : "";
        var extension = contentType switch { "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".jpg" };
        return (string.IsNullOrWhiteSpace(name) ? "image" : name) + extension;
    }

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

        // An uploaded image, for the editor to show (one copied from Files or from the web).
        api.MapGet("/uploads/{id}/content", async (string id, ClaimsPrincipal user, AttachmentUploads uploads, HttpContext http, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await uploads.GetAsync(userId, id, ct) is not { } upload || !OutgoingHtml.InlineImageTypes.Contains(upload.ContentType))
            {
                return MailboxEndpoints.NotFound();
            }

            http.Response.Headers.ContentSecurityPolicy = MailboxEndpoints.ContentSandbox;
            http.Response.Headers.ContentDisposition = "inline";
            return Results.Stream(uploads.OpenRead(userId, upload), upload.ContentType);
        });

        // An image of pasted web content, fetched by the server (as the image proxy does: never
        // from internal addresses) so it can be sent as part of the message.
        api.MapPost("/uploads/import", async (ImageImportInput input, ClaimsPrincipal user, AttachmentUploads uploads, ImageProxy proxy, CancellationToken ct) =>
            {
                if (await proxy.FetchAsync(input.Url ?? "", ct) is not { } image || !OutgoingHtml.InlineImageTypes.Contains(image.ContentType))
                {
                    return Results.Json(new { error = "image_unavailable", message = "The image could not be fetched." }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                await using var content = new MemoryStream(image.Content);
                return Results.Ok(await uploads.SaveAsync(user.GetNextcloudUserId(), ImageFileName(input.Url!, image.ContentType), image.ContentType, content, ct));
            })
            .RequireRateLimiting(ImageImportRateLimit);

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
