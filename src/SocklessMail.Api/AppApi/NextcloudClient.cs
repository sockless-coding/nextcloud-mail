using System.Net.Http.Json;
using System.Text.Json;

namespace SocklessMail.Api.AppApi;

/// <summary>
/// Calls Nextcloud's OCS and WebDAV APIs as this ExApp. With a user id the call acts as that user
/// (Nextcloud records it in exapp_impersonation.log); without one it runs in system context.
/// AppAPI's DavPlugin accepts the same AUTHORIZATION-APP-API header on remote.php/dav.
/// </summary>
public sealed class NextcloudClient(HttpClient http, AppApiOptions options)
{
    private static readonly TimeSpan OcsTimeout = TimeSpan.FromSeconds(30);

    public static void Configure(IServiceProvider services, HttpClient http)
    {
        http.BaseAddress = services.GetRequiredService<AppApiOptions>().NextcloudUrl;
        // WebDAV transfers carry attachments; OCS calls apply their own, shorter limit.
        http.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Sends a WebDAV request as <paramref name="userId"/> and returns the response unread (the
    /// caller disposes it). Status codes are the caller's business: WebDAV uses them for results.
    /// </summary>
    /// <param name="request">Its URI is relative to the Nextcloud root, e.g. <c>remote.php/dav/files/alice/a.txt</c>.</param>
    public Task<HttpResponseMessage> DavAsync(HttpRequestMessage request, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        AddAuthentication(request, userId);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private void AddAuthentication(HttpRequestMessage request, string userId)
    {
        request.Headers.Add(AppApiHeaders.ExAppId, options.AppId);
        request.Headers.Add(AppApiHeaders.ExAppVersion, options.AppVersion);
        request.Headers.Add(AppApiHeaders.Authorization, AppApiHeaders.BuildAuthorization(userId, options.AppSecret));
    }

    /// <summary>Sends an OCS v2 request and returns <c>ocs.data</c>.</summary>
    /// <param name="path">Relative to <c>ocs/v2.php/</c>, e.g. <c>apps/app_api/api/v1/ui/top-menu</c>.</param>
    public async Task<JsonElement> OcsAsync(
        HttpMethod method, string path, object? body = null, string userId = "", CancellationToken ct = default)
    {
        var separator = path.Contains('?') ? '&' : '?';
        using var request = new HttpRequestMessage(method, $"ocs/v2.php/{path}{separator}format=json");
        AddAuthentication(request, userId);
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OcsTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new NextcloudApiException(method, path, (int)response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
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
