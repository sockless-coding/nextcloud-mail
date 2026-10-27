namespace SocklessMail.Api.AppApi;

/// <summary>
/// The top-menu page. AppAPI renders it at /index.php/apps/app_api/embedded/sockless_mail/sockless_mail
/// as an empty #content element, injects the registered script and style (both fetched through
/// the PHP proxy) and provides the initial state under the app_api namespace.
/// </summary>
public sealed class UiRegistration(NextcloudClient nextcloud, AppApiOptions options, ILogger<UiRegistration> logger)
{
    public const string MenuName = "sockless_mail";
    public const string DisplayName = "Sockless Mail";
    public const string InitialStateKey = "sockless_mail_config";

    // AppAPI appends .js / .css itself.
    private const string ScriptPath = "js/sockless_mail-main";
    private const string StylePath = "css/sockless_mail-main";
    private const string Ui = "apps/app_api/api/v1/ui";

    public async Task RegisterAsync(CancellationToken ct)
    {
        await nextcloud.OcsAsync(HttpMethod.Post, $"{Ui}/top-menu",
            new { name = MenuName, displayName = DisplayName, icon = "img/app.svg", adminRequired = 0 }, ct: ct);
        await nextcloud.OcsAsync(HttpMethod.Post, $"{Ui}/initial-state",
            new { type = "top_menu", name = MenuName, key = InitialStateKey, value = new { apiMode = ApiMode } }, ct: ct);
        await nextcloud.OcsAsync(HttpMethod.Post, $"{Ui}/script",
            new { type = "top_menu", name = MenuName, path = ScriptPath, afterAppId = "" }, ct: ct);
        await nextcloud.OcsAsync(HttpMethod.Post, $"{Ui}/style",
            new { type = "top_menu", name = MenuName, path = StylePath }, ct: ct);
        logger.LogInformation("Registered top menu entry (API mode {ApiMode})", ApiMode);
    }

    /// <summary>
    /// Best effort: AppAPI removes all UI registrations when the app is unregistered anyway, and
    /// a failure here must not block an admin from disabling the app.
    /// </summary>
    public async Task UnregisterAsync(CancellationToken ct)
    {
        (HttpMethod, string, object)[] calls =
        [
            (HttpMethod.Delete, $"{Ui}/style", new { type = "top_menu", name = MenuName, path = StylePath }),
            (HttpMethod.Delete, $"{Ui}/script", new { type = "top_menu", name = MenuName, path = ScriptPath }),
            (HttpMethod.Delete, $"{Ui}/initial-state", new { type = "top_menu", name = MenuName, key = InitialStateKey }),
            (HttpMethod.Delete, $"{Ui}/top-menu", new { name = MenuName }),
        ];
        foreach (var (method, path, body) in calls)
        {
            try
            {
                await nextcloud.OcsAsync(method, path, body, ct: ct);
            }
            catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Could not remove UI registration {Path}", path);
            }
        }
    }

    /// <summary>
    /// How the browser reaches our API. HaRP serves /exapps/&lt;appid&gt;/ directly; everything else
    /// goes through AppAPI's PHP proxy, which works with every daemon type, so it is the fallback
    /// (Kubernetes HaRP runs in direct mode without HP_SHARED_KEY and lands here too).
    /// </summary>
    private string ApiMode => options.IsHarp ? "harp" : "proxy";
}
