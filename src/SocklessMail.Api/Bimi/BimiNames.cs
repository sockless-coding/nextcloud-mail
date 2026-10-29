using System.Globalization;
using System.Text.RegularExpressions;

namespace SocklessMail.Api.Bimi;

/// <summary>Validation of the domain names and selectors that end up in DNS queries and URLs.</summary>
public static partial class BimiNames
{
    public const string DefaultSelector = "default";

    private static readonly IdnMapping Idn = new();

    /// <summary>The domain of an address in lower-case ASCII (punycode); null when it is not a plausible public host name.</summary>
    public static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? null : NormalizeDomain(address[(at + 1)..]);
    }

    public static string? NormalizeDomain(string domain)
    {
        try
        {
            var ascii = Idn.GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant();
            return ascii.Length <= 253 && Domain().IsMatch(ascii) ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static bool IsSelector(string selector) => selector.Length <= 63 && Selector().IsMatch(selector);

    // At least two labels; an IP address never matches, since a top-level domain starts with a letter.
    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Domain();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9_-]{0,61}[a-z0-9])?$")]
    private static partial Regex Selector();
}
