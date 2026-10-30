using System.Net;
using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Tags;

namespace SocklessMail.Api.Api;

public sealed record OAuthFlowInput(string? Provider, Guid? AccountId);

public sealed record OAuthFlowStarted(string FlowId, string AuthorizeUrl);

public sealed record OAuthFlowState(OAuthFlowStatus Status, string? Email, string? Error);

public sealed record OAuthAccountInput(string? FlowId, string? DisplayName, string? SenderName);

public sealed record OAuthGrantInput(string? FlowId);

public sealed record OAuthProviderDto(string Id, string Name);

/// <summary>
/// Signing in with OAuth2 (authorization code + PKCE):
/// <list type="number">
/// <item>The browser starts a flow (POST /api/oauth/flows) and opens the returned provider URL in a popup.</item>
/// <item>The provider redirects to /oauth/callback, a PUBLIC route: the redirect is a cross-site
/// navigation that may come without a Nextcloud session, so the flow is found by its random state
/// alone. The code is exchanged there; the grant waits in the flow.</item>
/// <item>The browser, polling GET /api/oauth/flows/{id}, sees it done and creates the account
/// (POST /api/accounts/oauth) or replaces an account's grant (POST /api/accounts/{id}/oauth).
/// A grant is handed out once, and only to the user who started the flow.</item>
/// </list>
/// Admins register the app with each provider under /api/admin (access level ADMIN in info.xml).
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/oauth/providers", async (OAuthClientStore clients, CancellationToken ct) =>
            (await clients.ListAsync(ct)).Where(c => c.Configured).Select(c => new OAuthProviderDto(c.Provider, c.Name)));

        // For an existing account (signing in again) the provider may be left out: it is the one of the stored grant.
        api.MapPost("/oauth/flows", async (
            OAuthFlowInput input, ClaimsPrincipal user, OAuthClientStore clients, OAuthFlows flows, AccountService accounts,
            CredentialProtector credentials, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            var providerId = input.Provider;
            if (input.AccountId is { } accountId)
            {
                if (await accounts.GetAsync(userId, accountId, ct) is not { } account)
                {
                    return MailboxEndpoints.NotFound();
                }

                if (providerId is null && account.AuthMethod == MailAuthMethod.OAuth2)
                {
                    try
                    {
                        providerId = (credentials.Unprotect(account.Id, account.ProtectedCredential) as OAuthCredential)?.Provider;
                    }
                    catch (CredentialUnavailableException)
                    {
                    }
                }
            }

            if (await clients.GetAsync(providerId ?? "", ct) is not { } client)
            {
                return Results.Json(new { error = "oauth_not_configured", message = "Signing in with this provider is not set up. Ask your administrator." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (flows.Start(userId, client.Provider.Id, client.RedirectUri, input.AccountId) is not { } flow)
            {
                return Results.Json(new { error = "too_many_sign_ins", message = "Too many sign-ins in progress. Wait a few minutes and try again." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Ok(new OAuthFlowStarted(flow.Id, OAuthTokens.AuthorizeUrl(client, flow)));
        });

        api.MapGet("/oauth/flows/{flowId}", (string flowId, ClaimsPrincipal user, OAuthFlows flows) =>
            flows.Get(user.GetNextcloudUserId(), flowId) is { } flow
                ? Results.Ok(new OAuthFlowState(flow.Status, flow.Email, flow.Error))
                : MailboxEndpoints.NotFound());

        api.MapPost("/accounts/oauth", async (
            OAuthAccountInput input, ClaimsPrincipal user, OAuthFlows flows, OAuthProviderCatalog catalog, AccountService accounts, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (input.DisplayName is { Length: > 200 })
            {
                errors["displayName"] = ["Use at most 200 characters."];
            }

            if (input.SenderName is { Length: > 200 })
            {
                errors["senderName"] = ["Use at most 200 characters."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var userId = user.GetNextcloudUserId();
            if (flows.Get(userId, input.FlowId ?? "") is not { Status: OAuthFlowStatus.Done, AccountId: null }
                || flows.Take(userId, input.FlowId!) is not { } flow
                || catalog.Find(flow.ProviderId) is not { } provider)
            {
                return NoSignIn();
            }

            try
            {
                var account = await accounts.CreateOAuthAsync(userId, provider, new OAuthIdentity(flow.Email!, flow.Name), flow.RefreshToken!,
                    input.DisplayName, input.SenderName, ct);
                return Results.Created($"accounts/{account.Id}", AccountDto.From(account));
            }
            catch (AccountLimitException e)
            {
                return Results.Json(new { error = "account_limit", message = e.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapPost("/accounts/{id:guid}/oauth", async (
            Guid id, OAuthGrantInput input, ClaimsPrincipal user, OAuthFlows flows, AccountService accounts, TagScanner scanner, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await accounts.GetAsync(userId, id, ct) is not { } account)
            {
                return MailboxEndpoints.NotFound();
            }

            if (flows.Get(userId, input.FlowId ?? "") is not { Status: OAuthFlowStatus.Done } done || done.AccountId != id)
            {
                return NoSignIn();
            }

            // Signing in as someone else would quietly turn this account into another mailbox.
            if (!string.Equals(done.Email, account.Username, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new
                {
                    error = "different_account",
                    message = $"You signed in as {done.Email}, but this account is {account.Username}. Sign in with that address, or add a new account.",
                }, statusCode: StatusCodes.Status409Conflict);
            }

            if (flows.Take(userId, done.Id) is not { } flow)
            {
                return NoSignIn();
            }

            var updated = await accounts.ReplaceGrantAsync(userId, id, new OAuthCredential(flow.ProviderId, flow.RefreshToken!), ct);
            // A new sign-in may bring the Graph consent: the scan moves app-kept tags to Outlook.
            scanner.Request(userId);
            return updated is null ? MailboxEndpoints.NotFound() : Results.Ok(AccountDto.From(updated));
        });

        var admin = api.MapGroup("/admin").AddEndpointFilter<AdminOnlyFilter>();

        admin.MapGet("/oauth", (OAuthClientStore clients, CancellationToken ct) => clients.ListAsync(ct));

        admin.MapPut("/oauth/{provider}", async (string provider, OAuthClientInput input, OAuthProviderCatalog catalog, OAuthClientStore clients, CancellationToken ct) =>
        {
            if (catalog.Find(provider) is not { } found)
            {
                return MailboxEndpoints.NotFound();
            }

            try
            {
                await clients.SaveAsync(found, input, ct);
            }
            catch (MailboxValidationException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
            }

            return Results.Ok(await clients.ListAsync(ct));
        });
    }

    /// <summary>
    /// Where the provider sends the browser back. Requires AppAPI's signature (it comes through
    /// HaRP or the PHP proxy) but no user. Answers a small page; the app's window, which polls the
    /// flow, closes the popup.
    /// </summary>
    public static void MapOAuthCallback(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/callback", async (
                string? state, string? code, string? error, ClaimsPrincipal user, HttpContext http,
                OAuthFlows flows, OAuthClientStore clients, OAuthTokens tokens, ILogger<OAuthFlow> logger, CancellationToken ct) =>
            {
                if (flows.FindByState(state) is not { } flow)
                {
                    return Page(http, StatusCodes.Status400BadRequest, "This sign-in has expired. Start it again in Sockless Mail.");
                }

                // A signed-in Nextcloud user must be the one who started the flow.
                if (user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId && userId != flow.UserId)
                {
                    return Page(http, StatusCodes.Status403Forbidden, "This sign-in was started by another user.");
                }

                if (Interlocked.Exchange(ref flow.Claimed, 1) == 1)
                {
                    return Page(http, StatusCodes.Status409Conflict, "This sign-in has already been handled. You can close this window.");
                }

                var message = await CompleteAsync(flow, code, error, clients, tokens, logger, ct);
                return flow.Status == OAuthFlowStatus.Done
                    ? Page(http, StatusCodes.Status200OK, $"Signed in as {flow.Email}. You can close this window.")
                    : Page(http, StatusCodes.Status400BadRequest, message);
            })
            .RequireAuthorization(AppApiAuthentication.SystemPolicy);
    }

    /// <summary>Exchanges the code and records the outcome on the flow; returns the message for the user.</summary>
    private static async Task<string> CompleteAsync(
        OAuthFlow flow, string? code, string? error, OAuthClientStore clients, OAuthTokens tokens, ILogger logger, CancellationToken ct)
    {
        string Fail(string reason)
        {
            flow.Error = reason;
            flow.Status = OAuthFlowStatus.Failed;
            return reason;
        }

        var client = await clients.GetAsync(flow.ProviderId, ct);
        if (client is null)
        {
            return Fail("Signing in with this provider is no longer set up. Ask your administrator.");
        }

        if (error is not null || string.IsNullOrEmpty(code))
        {
            return Fail(error == "access_denied"
                ? "The sign-in was cancelled."
                : $"{client.Provider.Name} did not complete the sign-in ({error ?? "no code"}).");
        }

        OAuthTokenResponse response;
        try
        {
            response = await tokens.ExchangeCodeAsync(client with { RedirectUri = flow.RedirectUri }, code, flow.CodeVerifier, ct);
        }
        catch (Exception e) when (e is OAuthGrantRevokedException or OAuthUnavailableException)
        {
            logger.LogInformation("Exchanging an OAuth code with {Provider} failed: {Error}", client.Provider.Id, e.GetType().Name);
            return Fail(e is OAuthUnavailableException ? e.Message : "The sign-in code was not accepted. Try again.");
        }

        if (OAuthTokens.ReadIdentity(response.IdToken) is not { } identity)
        {
            return Fail($"{client.Provider.Name} did not say which address signed in.");
        }

        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            return Fail($"{client.Provider.Name} did not grant lasting access. Remove Sockless Mail's access in your {client.Provider.Name} account settings and sign in again.");
        }

        flow.Email = identity.Email;
        flow.Name = identity.Name;
        flow.RefreshToken = response.RefreshToken;
        flow.Status = OAuthFlowStatus.Done;
        return "";
    }

    private static IResult NoSignIn() =>
        Results.Json(new { error = "no_sign_in", message = "The sign-in has expired or was not completed. Sign in again." },
            statusCode: StatusCodes.Status409Conflict);

    private static IResult Page(HttpContext http, int status, string message)
    {
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        var text = WebUtility.HtmlEncode(message);
        return Results.Content(
            $$"""
              <!doctype html>
              <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sockless Mail</title>
              <style>body{font-family:system-ui,sans-serif;margin:3em auto;max-width:32em;padding:0 1em;line-height:1.5}</style></head>
              <body><h1>Sockless Mail</h1><p>{{text}}</p></body></html>
              """,
            "text/html; charset=utf-8", statusCode: status);
    }
}
