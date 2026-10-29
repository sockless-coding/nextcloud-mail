using System.Text;

namespace SocklessMail.Api.Contacts;

/// <summary>
/// The few vCard (3.0/4.0) properties mail needs, read from and written as text. Changes to an
/// existing card insert lines into the original text, so everything this class does not know
/// (photos, addresses, custom properties) stays exactly as it was.
/// </summary>
public sealed record VCard(string? Uid, string Name, IReadOnlyList<string> Emails, IReadOnlyList<string> Groups, string? Organization)
{
    public static VCard Parse(string text)
    {
        string? uid = null, fn = null, n = null, org = null;
        var emails = new List<string>();
        var groups = new List<string>();
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
        return new VCard(uid, display, emails, groups, string.IsNullOrWhiteSpace(org) ? null : org.Trim());
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

    /// <summary>The card's text with one more EMAIL line, just before END:VCARD.</summary>
    public static string AddEmail(string text, string email)
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

        return before + "EMAIL;TYPE=INTERNET:" + Escape(email) + newline + text[end..];
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
