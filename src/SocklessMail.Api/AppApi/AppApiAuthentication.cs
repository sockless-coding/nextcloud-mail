using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace SocklessMail.Api.AppApi;

public static class AppApiAuthentication
{
    public const string Scheme = "AppApi";

    /// <summary>Any caller holding the app secret, including system calls without a user (lifecycle).</summary>
    public const string SystemPolicy = "AppApiSystem";

    /// <summary>A request on behalf of a signed-in Nextcloud user. The default for every endpoint.</summary>
    public const string UserPolicy = "NextcloudUser";

    public static IServiceCollection AddAppApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, AppApiAuthenticationHandler>(Scheme, null);

        var userPolicy = new AuthorizationPolicyBuilder(Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !string.IsNullOrEmpty(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)))
            .Build();

        services.AddAuthorizationBuilder()
            .AddPolicy(SystemPolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser())
            .AddPolicy(UserPolicy, userPolicy)
            .SetDefaultPolicy(userPolicy)
            // Endpoints must opt out explicitly (heartbeat, static assets), never opt in.
            .SetFallbackPolicy(userPolicy);

        return services;
    }

    /// <summary>
    /// The verified Nextcloud user id. Every account, folder and message lookup must be scoped by
    /// this value and by nothing the client sends.
    /// </summary>
    public static string GetNextcloudUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? id
            : throw new InvalidOperationException("No Nextcloud user on this request; the endpoint is missing the user policy.");
}

internal sealed class AppApiAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppApiOptions appApi)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;
        if (!headers.ContainsKey(AppApiHeaders.Authorization))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AppApiHeaders.TryValidate(
                headers[AppApiHeaders.ExAppId], headers[AppApiHeaders.ExAppVersion], headers[AppApiHeaders.Authorization],
                appApi, out var userId))
        {
            Logger.LogWarning("Rejected request with invalid AppAPI credentials for {Path}", Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid AppAPI credentials."));
        }

        var claims = userId.Length > 0 ? [new Claim(ClaimTypes.NameIdentifier, userId)] : Array.Empty<Claim>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteError(StatusCodes.Status401Unauthorized, "unauthorized");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteError(StatusCodes.Status403Forbidden, "forbidden");

    private Task WriteError(int status, string error)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(new { error });
    }
}
