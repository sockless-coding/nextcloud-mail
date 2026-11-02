using System.Text;

namespace SocklessMail.Api.Contacts;

/// <summary>
/// The few vCard (3.0/4.0) properties mail needs, read from and written as text. Changes to an
/// existing card insert lines into the original text, so everything this class does not know
/// (photos, addresses, custom properties) stays exactly as it was.
/// </summary>
/// <param name="Keys">Public keys or certificates in KEY properties (vCard 4 data: URIs, vCard 3 inline base64), as bytes; linked ones are not fetched.</param>
public sealed record VCard(string? Uid, string Name, IReadOnlyList<string> Emails, IReadOnlyList<string> Groups, string? Organization, IReadOnlyList<byte[]>? Keys = null)
{
    public static VCard Parse(string text)
    {
        string? uid = null, fn = null, n = null, org = null;
        var emails = new List<string>();
        var groups = new List<string>();
        var keys = new List<byte[]>();
        foreach (var (name, value) in Properties(text))
        {
            switch (name)
            {
                case "UID":
                    uid ??= Unescape(value);
                    break;
                case "FN":
                    fn ??= Unescape(value);
                    break;
                case "N":
                    n ??= FormatN(value);
                    break;
                case "ORG":
                    org ??= Unescape(SplitEscaped(value, ';').FirstOrDefault() ?? "");
                    break;
                case "EMAIL":
                    var email = Unescape(value).Trim();
                    if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        email = email[7..];
                    }

                    if (email.Contains('@') && !emails.Contains(email, StringComparer.OrdinalIgnoreCase))
                    {
                        emails.Add(email);
                    }

                    break;
                case "KEY" when KeyData(value) is { } key:
                    keys.Add(key);
                    break;
                case "CATEGORIES":
                    foreach (var group in SplitEscaped(value, ',').Select(Unescape).Select(g => g.Trim()))
                    {
                        if (group.Length > 0 && !groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                        {
                            groups.Add(group);
                        }
                    }

                    break;
            }
        }

        var display = !string.IsNullOrWhiteSpace(fn) ? fn.Trim() : !string.IsNullOrWhiteSpace(n) ? n : org ?? "";
        return new VCard(uid, display, emails, groups, string.IsNullOrWhiteSpace(org) ? null : org.Trim(), keys);
    }

    /// <summary>A KEY value's bytes: "data:…;base64,…" (vCard 4) or bare base64 (vCard 3, ENCODING=b); null for links and anything else.</summary>
    private static byte[]? KeyData(string value)
    {
        var data = value.Trim();
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            data = data[(comma + 1)..];
        }
        else if (data.Contains(':'))
        {
            // A URL (http:, https:, ldap:): not fetched.
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(data);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A new vCard 3.0 (what Nextcloud's Contacts app writes) with a name and one address.</summary>
    public static string Create(string uid, string name, string email)
    {
        var builder = new StringBuilder();
        builder.Append("BEGIN:VCARD\r\nVERSION:3.0\r\nPRODID:-//Sockless Mail//EN\r\n");
        AppendLine(builder, "UID:" + Escape(uid));
        AppendLine(builder, "FN:" + Escape(name));
        var (given, family) = SplitName(name);
        AppendLine(builder, $"N:{Escape(family)};{Escape(given)};;;");
        AppendLine(builder, "EMAIL;TYPE=INTERNET:" + Escape(email));
        builder.Append("END:VCARD\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// The card's text with one more KEY line (folded at 75 characters), as vCard 4 writes it
    /// (a data: URI) or vCard 3 (inline base64, TYPE=PGP or X509). Null when the card already has
    /// that key.
    /// </summary>
    public static string? AddKey(string text, byte[] key, bool certificate)
    {
        var card = Parse(text);
        if (card.Keys?.Any(k => k.AsSpan().SequenceEqual(key)) == true)
        {
            return null;
        }

        var version4 = Properties(text).Any(p => p.Name == "VERSION" && p.Value.Trim() == "4.0");
        var data = Convert.ToBase64String(key);
        var line = version4
            ? $"KEY:data:{(certificate ? "application/pkix-cert" : "application/pgp-keys")};base64,{data}"
            : $"KEY;TYPE={(certificate ? "X509" : "PGP")};ENCODING=b:{data}";
        return Insert(text, Fold(line));
    }

    /// <summary>A content line folded as RFC 6350 asks: at most 75 characters, continued after a space.</summary>
    private static string Fold(string line)
    {
        if (line.Length <= 75)
        {
            return line;
        }

        var folded = new StringBuilder(line[..75]);
        for (var at = 75; at < line.Length; at += 74)
        {
            folded.Append("\n ").Append(line.AsSpan(at, Math.Min(74, line.Length - at)));
        }

        return folded.ToString();
    }

    /// <summary>The card's text with one more EMAIL line, just before END:VCARD.</summary>
    public static string AddEmail(string text, string email) => Insert(text, "EMAIL;TYPE=INTERNET:" + Escape(email));

    /// <summary>The card's text with <paramref name="line"/> ("\n" between folded parts) just before END:VCARD, in the card's line ends.</summary>
    private static string Insert(string text, string line)
    {
        var end = text.LastIndexOf("END:VCARD", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            throw new FormatException("Not a vCard.");
        }

        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var before = text[..end];
        if (before.Length > 0 && !before.EndsWith('\n'))
        {
            before += newline;
        }

        return before + line.Replace("\n", newline) + newline + text[end..];
    }

    /// <summary>(name, value) of each content line, unfolded; parameters and "item1." groups dropped.</summary>
    private static IEnumerable<(string Name, string Value)> Properties(string text)
    {
        var unfolded = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        foreach (var line in unfolded.Split('\n'))
        {
            var colon = ValueStart(line);
            if (colon <= 0)
            {
                continue;
            }

            var head = line[..colon];
            var name = head.Split(';')[0];
            var dot = name.LastIndexOf('.');
            yield return ((dot >= 0 ? name[(dot + 1)..] : name).Trim().ToUpperInvariant(), line[(colon + 1)..]);
        }
    }

    /// <summary>The colon that starts the value: the first one outside quoted parameter values.</summary>
    private static int ValueStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>"Family;Given;Middle;Prefix;Suffix" → "Prefix Given Middle Family Suffix".</summary>
    private static string? FormatN(string value)
    {
        var parts = SplitEscaped(value, ';').Select(Unescape).Select(p => p.Trim()).ToArray();
        string Part(int i) => i < parts.Length ? parts[i] : "";
        var name = string.Join(' ', new[] { Part(3), Part(1), Part(2), Part(0), Part(4) }.Where(p => p.Length > 0));
        return name.Length > 0 ? name : null;
    }

    private static (string Given, string Family) SplitName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        return space > 0 ? (trimmed[..space], trimmed[(space + 1)..]) : (trimmed, "");
    }

    private static IEnumerable<string> SplitEscaped(string value, char separator)
    {
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[++i]);
            }
            else if (value[i] == separator)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        yield return current.ToString();
    }

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>Folds lines longer than 75 octets, as vCard requires.</summary>
    private static void AppendLine(StringBuilder builder, string line)
    {
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                builder.Append("\r\n ");
                bytes = 1;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        builder.Append("\r\n");
    }
}
