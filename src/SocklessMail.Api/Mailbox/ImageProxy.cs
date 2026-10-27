using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// Signed, expiring tokens for remote image URLs found in a message the user chose to show
/// images for. The proxy only fetches URLs it issued a token for, and a token is bound to the
/// user, so the endpoint cannot be used as a general-purpose fetcher.
/// </summary>
public sealed class ImageProxyTokens(IDataProtectionProvider provider, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private ITimeLimitedDataProtector Protector(string userId) =>
        provider.CreateProtector("SocklessMail.ImageProxy.v1", userId).ToTimeLimitedDataProtector();

    public string Create(string userId, string url) =>
        Protector(userId).Protect(url, clock.GetUtcNow() + Lifetime);

    public string? Resolve(string userId, string token)
    {
        try
        {
            return Protector(userId).Unprotect(token, out var expiration) is var url && expiration > clock.GetUtcNow() ? url : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

public sealed record ProxiedImage(byte[] Content, string ContentType);

/// <summary>
/// Fetches remote images on the user's behalf, so the sender sees the server, not the user's
/// browser, and Nextcloud's CSP (img-src 'self') is satisfied. Connections go through
/// <see cref="PolicySocketConnector"/>, so neither the URL nor a redirect can reach internal
/// addresses. Only raster images up to <see cref="MaxBytes"/> are passed on; SVG is refused,
/// because it is active content when opened directly on our origin.
/// </summary>
public sealed class ImageProxy(IHttpClientFactory httpClients, ILogger<ImageProxy> logger)
{
    public const string HttpClientName = "image-proxy";
    public const int MaxBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/x-icon", "image/vnd.microsoft.icon" };

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SocklessMail-ImageProxy)");
                client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,image/gif;q=0.9,*/*;q=0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
                UseCookies = false,
                // An HTTP proxy would resolve the host itself and bypass the address policy.
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectCallback = async (context, ct) =>
                {
                    var socket = await sp.GetRequiredService<PolicySocketConnector>()
                        .ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            });
    }

    /// <returns>The image, or null when it cannot or must not be delivered.</returns>
    public async Task<ProxiedImage?> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType is null || !AllowedTypes.Contains(contentType)
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return new ProxiedImage(buffer.ToArray(), contentType);
        }
        catch (Exception e) when (e is HttpRequestException or MailConnectionException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The URL itself is not logged: it identifies the sender's tracking and the user's mail.
            logger.LogInformation("Remote image could not be fetched ({ExceptionType})", e.GetType().Name);
            return null;
        }
    }
}
