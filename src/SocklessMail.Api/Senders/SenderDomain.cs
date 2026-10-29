using System.Globalization;
using SocklessMail.Api.Bimi;

namespace SocklessMail.Api.Senders;

/// <summary>
/// A domain split into what its owner registered and what is below it: for "mail.paypal.co.uk",
/// <see cref="Label"/> "paypal", <see cref="Suffix"/> "co.uk", <see cref="Subdomain"/> "mail". Without
/// the public suffix list (see the BIMI notes), a two-letter country domain whose second label is one
/// of the usual registry categories (co, com, org, ...) counts as a suffix of two labels; that covers the
/// look-alikes this is for. Names are in lower-case ASCII (punycode); <see cref="Unicode"/> is how they read.
/// </summary>
public sealed record SenderDomain(string Name, string Label, string Suffix, string Subdomain)
{
    private static readonly IdnMapping Idn = new();

    private static readonly HashSet<string> SecondLevelSuffixes =
    [
        "ac", "co", "com", "edu", "gob", "gov", "govt", "gouv", "gv", "ltd", "me", "mil", "ne", "net", "nhs", "nom", "or", "org", "plc", "sch",
    ];

    /// <summary>The registered domain, such as "paypal.co.uk".</summary>
    public string Registered => $"{Label}.{Suffix}";

    public string Unicode => ToUnicode(Name);

    public string UnicodeLabel => ToUnicode(Label);

    public bool IsInternational => Name.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal));

    public static SenderDomain? OfAddress(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? null : Parse(address[(at + 1)..]);
    }

    public static SenderDomain? Parse(string domain)
    {
        if (BimiNames.NormalizeDomain(domain) is not { } name)
        {
            return null;
        }

        var labels = name.Split('.');
        var suffixLabels = labels.Length >= 3 && labels[^1].Length == 2 && SecondLevelSuffixes.Contains(labels[^2]) ? 2 : 1;
        var label = labels.Length - suffixLabels - 1;
        return new SenderDomain(
            name,
            labels[label],
            string.Join('.', labels[(label + 1)..]),
            string.Join('.', labels[..label]));
    }

    public static string ToUnicode(string ascii)
    {
        try
        {
            return Idn.GetUnicode(ascii);
        }
        catch (ArgumentException)
        {
            return ascii;
        }
    }
}
