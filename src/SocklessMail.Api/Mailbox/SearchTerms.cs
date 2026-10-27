using System.Text.RegularExpressions;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// A search as typed: words and "quoted phrases", all of which must match. The same terms drive
/// the IMAP search of one folder and the search of cached headers.
/// </summary>
public static partial class SearchTerms
{
    public const int MaxTerms = 10;
    public const int MaxLength = 200;

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex Term();

    /// <exception cref="MailboxValidationException">Nothing to search for, or too much.</exception>
    public static IReadOnlyList<string> Parse(string? query)
    {
        var text = string.Concat((query ?? "").Where(c => !char.IsControl(c))).Trim();
        if (text.Length > MaxLength)
        {
            throw new MailboxValidationException("q", $"Use at most {MaxLength} characters.");
        }

        var terms = Term().Matches(text)
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0)
        {
            throw new MailboxValidationException("q", "Enter something to search for.");
        }

        if (terms.Count > MaxTerms)
        {
            throw new MailboxValidationException("q", $"Use at most {MaxTerms} search words.");
        }

        return terms;
    }

    /// <summary>The form cached search text and terms are compared in.</summary>
    public static string Normalize(string text) => text.ToLowerInvariant();
}
