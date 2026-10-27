namespace SocklessMail.Api.Hosting;

/// <summary>
/// CSRF protection and response hardening for /api. Neither HaRP nor AppAPI's PHP proxy checks
/// Nextcloud's request token, so a state-changing request must carry a custom header, which a
/// cross-site form cannot send and a cross-site fetch cannot send without a CORS preflight that
/// neither proxy answers.
/// </summary>
public sealed class ApiRequestGuard(RequestDelegate next)
{
    public const string RequiredHeader = "X-Sockless-Request";

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            return next(context);
        }

        if (string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)
            || (!IsSafe(context.Request.Method) && context.Request.Headers[RequiredHeader] != "1"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = "csrf_check_failed" });
        }

        // The PHP proxy adds a one-hour public cache lifetime to any non-JSON response without
        // its own Cache-Control; mail content and attachments must never be shared that way.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return next(context);
    }

    private static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
}
