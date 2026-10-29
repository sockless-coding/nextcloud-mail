using SocklessMail.Api.Bimi;

namespace SocklessMail.Api.Api;

/// <summary>
/// Sender logos (BIMI). The message lists say which domain's logo a message may show; the browser
/// asks for it here by domain, and keeps the answer for a day, so a list of a hundred messages
/// from a few senders costs a few requests, and the server asks DNS once per domain and day.
/// </summary>
public static class BimiEndpoints
{
    public const string RateLimit = "bimi";

    public static void MapBimiEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/bimi/{domain}", async (string domain, string? s, BimiResolver resolver, HttpContext http, CancellationToken ct) =>
            {
                var selector = s?.ToLowerInvariant() ?? BimiNames.DefaultSelector;
                if (BimiNames.NormalizeDomain(domain) is not { } name || name != domain.ToLowerInvariant() || !BimiNames.IsSelector(selector))
                {
                    return MailboxEndpoints.NotFound();
                }

                var logo = await resolver.GetAsync(name, selector, ct);
                // Per user (the request is authenticated), never shared by the PHP proxy or a cache in between.
                http.Response.Headers.CacheControl = logo is null ? "private, max-age=3600" : "private, max-age=86400";
                if (logo is null)
                {
                    return MailboxEndpoints.NotFound();
                }

                http.Response.Headers.ContentSecurityPolicy = MailboxEndpoints.ContentSandbox;
                http.Response.Headers.ContentDisposition = "inline";
                return Results.Bytes(logo.Svg, "image/svg+xml", entityTag: new($"\"{logo.Hash}\""));
            })
            .RequireRateLimiting(RateLimit);
    }
}
