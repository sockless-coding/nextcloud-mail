using System.Text;
using SocklessMail.Api.Senders;

namespace SocklessMail.Api.Tags;

/// <summary>
/// IMAP keywords as tags. Keywords compare case-insensitively (servers differ in what they keep).
/// A message's keywords are stored as one string, " k1 k2 ", so "has keyword k" is a LIKE on
/// "% k %" (keywords contain no spaces); "" when it has none.
/// </summary>
public static class Keywords
{
    public const int MaxKeywordLength = 64;
    public const int MaxStoredLength = 1000;

    /// <summary>Keywords mail programs set for their own bookkeeping; never offered as tags.</summary>
    private static readonly HashSet<string> Housekeeping = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Forwarded", "$MDNSent", "$Junk", "$NotJunk", "Junk", "NonJunk", "NotJunk", "$Phishing", "$NotPhishing",
        "$SubmitPending", "$Submitted", "$HasAttachment", "$HasNoAttachment", "$IsTrusted", "$AutoSent",
        "$Muted", "$Followed", "$Notify", "$Unsubscribed", "$CanUnsubscribe", "$Memo", "$HasMemo", "$Seen",
        "$Answered", "Old", "$Draft", "$Deleted", "$Recent",
    };

    /// <summary>Thunderbird's (and Nextcloud Mail's) five default tags, offered with their names and colors.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Color)> WellKnown =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["$label1"] = ("Important", "#ff0000"),
            ["$label2"] = ("Work", "#ff9900"),
            ["$label3"] = ("Personal", "#009900"),
            ["$label4"] = ("To Do", "#3333ff"),
            ["$label5"] = ("Later", "#993399"),
        };

    /// <summary>Whether a keyword may be a tag: a valid IMAP atom that is not bookkeeping.</summary>
    public static bool IsTagKeyword(string keyword) =>
        IsValid(keyword) && !Housekeeping.Contains(keyword)
        && !keyword.StartsWith("$MailFlagBit", StringComparison.OrdinalIgnoreCase)
        && !keyword.StartsWith("$X-", StringComparison.OrdinalIgnoreCase);

    /// <summary>An IMAP flag-keyword (RFC 3501 atom): printable ASCII without atom-specials.</summary>
    public static bool IsValid(string keyword) =>
        keyword.Length is > 0 and <= MaxKeywordLength
        && keyword[0] != '\\'
        && keyword.All(c => c is > ' ' and < '\x7f' && c is not ('(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']'));

    public static string Format(IEnumerable<string>? keywords)
    {
        var list = keywords?.Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var text = new StringBuilder(" ");
        foreach (var keyword in list)
        {
            if (text.Length + keyword.Length + 1 > MaxStoredLength)
            {
                break;
            }

            text.Append(keyword).Append(' ');
        }

        return text.Length == 1 ? "" : text.ToString();
    }

    public static string[] Parse(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The keywords that may be tags, for clients; bookkeeping ones ($HasNoAttachment, $Forwarded, …) are left out.</summary>
    public static string[] ForClient(string? stored) => Parse(stored).Where(IsTagKeyword).ToArray();

    public static string[] ForClient(IEnumerable<string>? keywords) => ForClient(Format(keywords));

    public static bool Has(string? stored, string keyword) =>
        Parse(stored).Contains(keyword, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any of the keywords may be a tag, so the message belongs in the tag index.</summary>
    public static bool HasTagKeyword(string? stored) => Parse(stored).Any(IsTagKeyword);

    /// <summary><paramref name="stored"/> with <paramref name="add"/> added and <paramref name="remove"/> removed.</summary>
    public static string Change(string? stored, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var removed = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Format(Parse(stored).Where(k => !removed.Contains(k)).Concat(add));
    }

    /// <summary>
    /// The keyword for a new tag: "$" and the name in lower case, with letters stripped of accents
    /// and anything else but digits, "-" and "_" turned into "_"; a number is added when taken.
    /// </summary>
    public static string ForName(string name, IEnumerable<string> taken)
    {
        var slug = new StringBuilder();
        // The app runs with invariant globalization, where string.Normalize does not decompose.
        foreach (var c in Skeletons.WithoutMarks(name).ToLowerInvariant())
        {
            var keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (keep)
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '_')
            {
                slug.Append('_');
            }
        }

        var stem = "$" + slug.ToString().Trim('_');
        if (stem.Length == 1)
        {
            stem = "$tag";
        }

        stem = stem.Length > MaxKeywordLength - 4 ? stem[..(MaxKeywordLength - 4)] : stem;
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Never a bookkeeping keyword, or one of the five Thunderbird defaults under another name.
        var keyword = stem;
        for (var n = 2; used.Contains(keyword) || !IsTagKeyword(keyword) || WellKnown.ContainsKey(keyword); n++)
        {
            keyword = $"{stem}{n}";
        }

        return keyword;
    }
}
