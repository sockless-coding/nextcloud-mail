using System.Text.RegularExpressions;

namespace SocklessMail.Api.Senders;

/// <summary>
/// A link whose text shows one domain and whose target is on another: <see cref="Shown"/> is the
/// registered domain the text names, <see cref="Host"/> the host the link leads to; both as they read
/// (Unicode).
/// </summary>
public sealed record LinkMismatch(string Shown, string Host);

/// <summary>
/// Compares a link's visible text with its target, the classic phishing link ("https://paypal.com"
/// leading to another site). Only text that is itself an address counts: a web address, a domain name
/// or an e-mail address, not "Log in here" or a sentence that mentions a domain. A domain without
/// "http(s)://" or "www." counts only with a usual ending (<see cref="SenderChecker.NameEndings"/> or a
/// country), so "readme.txt" or "version 2.1" are not domains. Subdomains of the shown domain are fine
/// (a newsletter's "shop.com" leading to links.shop.com); redirect services are not exempt.
/// </summary>
public static partial class LinkText
{
    public const int MaxTextLength = 300;

    private static readonly char[] Brackets = ['<', '>', '(', ')', '[', ']', '"', '\'', '“', '”'];

    /// <returns>The mismatch, or null when the text names no domain or the link stays on it.</returns>
    public static LinkMismatch? Compare(string text, string href)
    {
        if (ShownDomain(text) is not { } shown || Target(href) is not { } target)
        {
            return null;
        }

        if (SenderDomain.Parse(target.Host) is { } actual)
        {
            return actual.Registered == shown.Registered ? null : new LinkMismatch(SenderDomain.ToUnicode(shown.Registered), actual.Unicode);
        }

        // An IP address (or a host no registry would hand out) behind a domain name.
        return target.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 or UriHostNameType.Dns
            ? new LinkMismatch(SenderDomain.ToUnicode(shown.Registered), target.Host)
            : null;
    }

    /// <summary>The domain <paramref name="text"/> shows when the whole text is an address.</summary>
    public static SenderDomain? ShownDomain(string text)
    {
        if (text.Length > MaxTextLength)
        {
            return null;
        }

        var trimmed = Whitespace().Replace(text, " ").Trim().TrimStart(Brackets).TrimEnd([.. Brackets, '.', ',', ';', ':', '!', '?']);
        if (Address().Match(trimmed) is not { Success: true } match || SenderDomain.Parse(match.Groups["host"].Value) is not { } domain)
        {
            return null;
        }

        var explicitWeb = match.Groups["scheme"].Success || match.Groups["user"].Success
            || match.Groups["host"].Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        var ending = domain.Suffix.Split('.')[^1];
        return explicitWeb || ending.Length == 2 || SenderChecker.NameEndings.Contains(ending) ? domain : null;
    }

    private static Uri? Target(string href)
    {
        var url = href.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0
            ? uri
            : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "https://host/path", "www.host", "host", "user@host" ("mailto:" included); no spaces anywhere.
    [GeneratedRegex(@"^(?:(?<scheme>https?)://|(?:mailto:)?(?<user>[\p{L}\p{N}._%+-]+)@)?(?<host>[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?(?:\.[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?)+)\.?(?::\d{1,5})?(?<rest>[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
