using System.Net.Http.Json;
using System.Text.Json;

namespace SocklessMail.Api.AppApi;

/// <summary>
/// Calls Nextcloud's OCS API as this ExApp. With a user id the call acts as that user (Nextcloud
/// records it in exapp_impersonation.log); without one it runs in system context.
/// </summary>
public sealed class NextcloudClient(HttpClient http, AppApiOptions options)
{
    public static void Configure(IServiceProvider services, HttpClient http)
    {
        http.BaseAddress = services.GetRequiredService<AppApiOptions>().NextcloudUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Sends an OCS v2 request and returns <c>ocs.data</c>.</summary>
    /// <param name="path">Relative to <c>ocs/v2.php/</c>, e.g. <c>apps/app_api/api/v1/ui/top-menu</c>.</param>
    public async Task<JsonElement> OcsAsync(
        HttpMethod method, string path, object? body = null, string userId = "", CancellationToken ct = default)
    {
        var separator = path.Contains('?') ? '&' : '?';
        using var request = new HttpRequestMessage(method, $"ocs/v2.php/{path}{separator}format=json");
        request.Headers.Add(AppApiHeaders.ExAppId, options.AppId);
        request.Headers.Add(AppApiHeaders.ExAppVersion, options.AppVersion);
        request.Headers.Add(AppApiHeaders.Authorization, AppApiHeaders.BuildAuthorization(userId, options.AppSecret));
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new NextcloudApiException(method, path, (int)response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.TryGetProperty("ocs", out var ocs) && ocs.TryGetProperty("data", out var data)
            ? data.Clone()
            : default;
    }
}

/// <summary>Carries method, path and status only: response bodies may echo request data.</summary>
public sealed class NextcloudApiException(HttpMethod method, string path, int statusCode)
    : Exception($"Nextcloud OCS {method} {path} returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}
