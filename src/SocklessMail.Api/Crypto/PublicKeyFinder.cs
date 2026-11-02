using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Crypto;

/// <summary>
/// Finds someone's OpenPGP key on the public internet: their domain's Web Key Directory (WKD,
/// draft-koch-openpgp-webkey-service: the advanced method, then the direct one), then
/// keys.openpgp.org, which publishes an address only after its owner confirmed it. Requests go
/// through the address policy like remote images (<see cref="PolicyHttp"/>), so a recipient's
/// domain cannot point the app at the local network. Addresses nothing was found for are not
/// asked about again for <see cref="MissLifetime"/>.
/// </summary>
public sealed class PublicKeyFinder(IHttpClientFactory http, TimeProvider clock, ILogger<PublicKeyFinder> logger)
{
    public const string HttpClientName = "SocklessMail.PublicKeys";
    public static readonly TimeSpan MissLifetime = TimeSpan.FromHours(1);
    public const int MaxKeyBytes = 256 * 1024;
    public static readonly Uri KeyServer = new("https://keys.openpgp.org/");

    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses = new(StringComparer.OrdinalIgnoreCase);

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<PublicKeyFinder>();
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SocklessMail-KeyLookup");
            })
            .ConfigurePrimaryHttpMessageHandler(PolicyHttp.CreateHandler);
    }

    /// <summary>The key data found for <paramref name="address"/> and where, or null.</summary>
    public async Task<(byte[] Data, string Source)?> FindAsync(string address, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_misses.TryGetValue(address, out var until) && until > now)
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return null;
        }

        var local = address[..at];
        var domain = address[(at + 1)..].ToLowerInvariant();
        var hash = ZBase32(SHA1.HashData(Encoding.UTF8.GetBytes(local.ToLowerInvariant())));
        var query = "?l=" + Uri.EscapeDataString(local);
        var candidates = new (string Url, string Source)[]
        {
            ($"https://openpgpkey.{domain}/.well-known/openpgpkey/{domain}/hu/{hash}{query}", KeySources.Wkd),
            ($"https://{domain}/.well-known/openpgpkey/hu/{hash}{query}", KeySources.Wkd),
            (new Uri(KeyServer, "vks/v1/by-email/" + Uri.EscapeDataString(address)).ToString(), KeySources.KeyServer),
        };

        foreach (var (url, source) in candidates)
        {
            if (await FetchAsync(url, ct) is { } data)
            {
                return (data, source);
            }
        }

        _misses[address] = now + MissLifetime;
        foreach (var (key, expired) in _misses)
        {
            if (expired <= now)
            {
                _misses.TryRemove(key, out _);
            }
        }

        return null;
    }

    /// <summary>Forgets that nothing was found for the address, e.g. once the user imported a key and removed it again.</summary>
    public void Forget(string address) => _misses.TryRemove(address, out _);

    private async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            using var response = await http.CreateClient(HttpClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxKeyBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxKeyBytes)
                {
                    return null;
                }
            }

            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // No such host (most domains have no WKD), refused by the address policy, or a timeout.
            logger.LogDebug("Key lookup at {Host} failed ({ExceptionType})", uri.Host, e.GetType().Name);
            return null;
        }
    }

    /// <summary>z-base-32 (as WKD hashes local parts), most significant bits first.</summary>
    internal static string ZBase32(byte[] data)
    {
        const string alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }
}
