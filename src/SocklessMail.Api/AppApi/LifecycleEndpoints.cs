namespace SocklessMail.Api.AppApi;

/// <summary>
/// The endpoints AppAPI calls on us. There is no /init: we have no long-running setup (database
/// migrations run before the server starts listening), and AppAPI treats a 404 as "no init needed".
/// Neither HaRP nor the PHP proxy forwards these paths from browsers.
/// </summary>
public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // Polled every second during install and enable. No auth and no dependencies.
        app.MapGet("/heartbeat", () => Results.Json(new { status = "ok" }))
            .AllowAnonymous();

        app.MapPut("/enabled", async (int enabled, UiRegistration ui, ILoggerFactory loggers, CancellationToken ct) =>
            {
                var logger = loggers.CreateLogger(typeof(LifecycleEndpoints));
                if (enabled != 1)
                {
                    await ui.UnregisterAsync(ct);
                    logger.LogInformation("App disabled");
                    return Results.Json(new { error = "" });
                }

                try
                {
                    await ui.RegisterAsync(ct);
                }
                catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException)
                {
                    // A non-empty error makes AppAPI abort enabling and report it to the admin.
                    logger.LogError(e, "Enabling failed while registering the UI");
                    return Results.Json(new { error = $"Sockless Mail could not register its UI in Nextcloud: {e.Message}" });
                }

                logger.LogInformation("App enabled");
                return Results.Json(new { error = "" });
            })
            .RequireAuthorization(AppApiAuthentication.SystemPolicy);
    }
}
