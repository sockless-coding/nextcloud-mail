using System.Net.Http.Headers;
using System.Security.Claims;
using MailKit;
using MailKit.Net.Imap;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Api;

/// <summary>
/// Folders, messages and message parts of one account. Every route first looks the account up
/// for the verified user, so another user's account id answers 404 before any IMAP traffic.
/// </summary>
public static class MailboxEndpoints
{
    /// <summary>Message parts and proxied images may be anything; never let a browser run them on our origin.</summary>
    private const string ContentSandbox = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    private static readonly HashSet<string> InlineImageTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    public static void MapMailboxEndpoints(this RouteGroupBuilder api)
    {
        var account = api.MapGroup("/accounts/{accountId:guid}").AddEndpointFilter<MailErrorFilter>();

        account.MapGet("/folders", async (Guid accountId, ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                ? Results.Ok(await mailbox.GetFoldersAsync(a, ct))
                : NotFound());

        account.MapGet("/folders/{folderId}/messages", async (
                Guid accountId, string folderId, uint? before, int? limit,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                ? Results.Ok(await mailbox.GetMessagesAsync(a, folderId, before,
                    Math.Clamp(limit ?? MailboxService.DefaultPageSize, 1, MailboxService.MaxPageSize), ct))
                : NotFound());

        account.MapGet("/folders/{folderId}/messages/{messageId}", async (
                Guid accountId, string folderId, string messageId, string? remote,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } a
                ? Results.Ok(await mailbox.GetMessageAsync(a, folderId, messageId, remote == "allow", ct))
                : NotFound());

        account.MapGet("/folders/{folderId}/messages/{messageId}/parts/{partId}", async (
                Guid accountId, string folderId, string messageId, string partId, bool? download,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, HttpContext http, CancellationToken ct) =>
            {
                if (await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is not { } a)
                {
                    return NotFound();
                }

                var part = await mailbox.GetPartAsync(a, folderId, messageId, partId, ct);
                // Inline only for raster images (cid: references in the message view); everything
                // else, HTML and SVG included, is a download.
                var inline = download != true && InlineImageTypes.Contains(part.ContentType);
                var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment") { FileNameStar = part.FileName };
                http.Response.Headers.ContentDisposition = disposition.ToString();
                http.Response.Headers.ContentSecurityPolicy = ContentSandbox;
                http.Response.ContentType = part.ContentType;
                await part.WriteToAsync(http.Response.Body, ct);
                return Results.Empty;
            });

        api.MapGet("/image-proxy", async (string token, ClaimsPrincipal user, ImageProxyTokens tokens, ImageProxy proxy, HttpContext http, CancellationToken ct) =>
        {
            if (tokens.Resolve(user.GetNextcloudUserId(), token) is not { } url)
            {
                return NotFound();
            }

            if (await proxy.FetchAsync(url, ct) is not { } image)
            {
                return Results.Json(new { error = "image_unavailable" }, statusCode: StatusCodes.Status502BadGateway);
            }

            http.Response.Headers.ContentSecurityPolicy = ContentSandbox;
            http.Response.Headers.ContentDisposition = "inline";
            return Results.Bytes(image.Content, image.ContentType);
        });
    }

    private static IResult NotFound() => Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>Maps mail failures to responses the UI can explain. Message texts never contain credentials.</summary>
    private sealed class MailErrorFilter(ILogger<MailErrorFilter> logger) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (MailboxNotFoundException)
            {
                return NotFound();
            }
            catch (CredentialUnavailableException)
            {
                return Results.Json(new { error = "credential_unavailable", message = "The stored password cannot be decrypted. Enter it again in the account settings." },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (MailConnectionException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception e) when (e is ImapCommandException or FolderNotFoundException or MessageNotFoundException)
            {
                logger.LogInformation("IMAP command failed: {ExceptionType}", e.GetType().Name);
                return e is ImapCommandException
                    ? Results.Json(new { error = "server_error", message = "The mail server refused the request." }, statusCode: StatusCodes.Status502BadGateway)
                    : NotFound();
            }
        }
    }
}
