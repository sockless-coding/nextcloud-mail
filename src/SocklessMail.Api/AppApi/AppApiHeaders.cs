using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SocklessMail.Api.AppApi;

/// <summary>
/// The AppAPI authentication headers, used in both directions: Nextcloud (directly, through the
/// PHP proxy, or through HaRP) signs requests to us, and we sign our requests to Nextcloud's OCS API.
/// </summary>
public static class AppApiHeaders
{
    public const string ExAppId = "EX-APP-ID";
    public const string ExAppVersion = "EX-APP-VERSION";
    public const string Authorization = "AUTHORIZATION-APP-API";

    /// <summary>Header values that must never reach a log.</summary>
    public static readonly IReadOnlySet<string> Sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Authorization, "harp-shared-key", "Authorization", "Cookie", "Set-Cookie",
    };

    /// <summary>base64("&lt;userId&gt;:&lt;secret&gt;"); an empty user id means system context.</summary>
    public static string BuildAuthorization(string userId, string secret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userId}:{secret}"));

    /// <summary>
    /// Validates an incoming request's AppAPI headers. AA-VERSION is deliberately not checked:
    /// HaRP rewrites it to a fixed value regardless of the real AppAPI version.
    /// </summary>
    /// <param name="userId">The acting Nextcloud user; empty for system calls and anonymous visitors.</param>
    public static bool TryValidate(
        string? exAppId, string? exAppVersion, string? authorization, AppApiOptions options,
        [NotNullWhen(true)] out string? userId)
    {
        userId = null;
        if (string.IsNullOrEmpty(exAppId) || string.IsNullOrEmpty(exAppVersion) || string.IsNullOrEmpty(authorization))
        {
            return false;
        }

        if (!string.Equals(exAppId, options.AppId, StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(authorization));
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException)
        {
            return false;
        }

        // Same split as AppAPI itself: the user id ends at the first colon.
        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        if (!SecretEquals(decoded[(separator + 1)..], options.AppSecret))
        {
            return false;
        }

        userId = decoded[..separator];
        return true;
    }

    /// <summary>Constant-time comparison; hashing first also hides the secret's length.</summary>
    private static bool SecretEquals(string candidate, string secret) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
