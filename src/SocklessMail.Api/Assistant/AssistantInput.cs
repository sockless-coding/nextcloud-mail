using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NodaTime;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Assistant;

/// <summary>
/// The text an Assistant task gets for messages: a short header per message, then its text. Mail
/// content goes to whatever provider the Nextcloud admin set up, so only what the task needs goes
/// in, and never more than <see cref="MaxInputLength"/> characters.
/// </summary>
public static partial class AssistantInput
{
    /// <summary>Roughly 8 000 tokens: within what every common model accepts, and plenty for mail.</summary>
    public const int MaxInputLength = 32_000;

    /// <summary>One message's share of a conversation at most; a single message may use all of <see cref="MaxInputLength"/>.</summary>
    public const int MaxMessageLength = 8_000;

    /// <summary>
    /// The messages, oldest first. In a conversation the quoted history is left out of each
    /// message (the earlier messages are there themselves); a single message keeps it, since it is
    /// all the context there is. The oldest messages are dropped first when it gets too long.
    /// </summary>
    public static string Format(IReadOnlyList<MessageText> messages, DateTimeZone zone)
    {
        if (messages.Count == 1)
        {
            return Shorten(Block(messages[0], Clean(messages[0].Text), zone), MaxInputLength);
        }

        var blocks = messages.Select(m => Shorten(Block(m, WithoutQuotes(m.Text), zone), MaxMessageLength)).ToList();
        var length = 0;
        var kept = new List<string>();
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            length += blocks[i].Length + Separator.Length;
            if (length > MaxInputLength && kept.Count > 0)
            {
                break;
            }

            kept.Insert(0, blocks[i]);
        }

        return string.Join(Separator, kept);
    }

    private const string Separator = "\n\n-----\n\n";

    private static string Block(MessageText message, string text, DateTimeZone zone)
    {
        var block = new StringBuilder();
        block.Append("Subject: ").Append(OneLine(message.Subject)).Append('\n');
        if (message.From is { } from)
        {
            block.Append("From: ").Append(OneLine(string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>")).Append('\n');
        }

        var local = Instant.FromDateTimeOffset(message.Date).InZone(zone).LocalDateTime;
        block.Append("Date: ").Append(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");
        block.Append(text.Length > 0 ? text : "(no text)");
        return block.ToString();
    }

    /// <summary>
    /// The message's own words: quoted lines ("> …"), the "On … wrote:" line before them, and
    /// everything from an Outlook-style reply header or "Original Message" separator on are left out.
    /// </summary>
    public static string WithoutQuotes(string text)
    {
        var lines = Clean(text).Split('\n');
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (OriginalMessage().IsMatch(line) || (OutlookRule().IsMatch(line) && NextNonBlank(lines, i) is { } next && next.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
                || (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase) && HeaderBlockFollows(lines, i) && !AfterForwardMarker(kept)))
            {
                break;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                // "On Monday, Alice wrote:" (possibly wrapped over two lines) introduces the quote.
                while (kept.Count > 0 && kept[^1].Trim().Length == 0)
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                if (kept.Count > 0 && Wrote().IsMatch(kept[^1]))
                {
                    var wrote = kept[^1];
                    kept.RemoveAt(kept.Count - 1);
                    if (!wrote.StartsWith("On ", StringComparison.Ordinal) && kept.Count > 0 && kept[^1].StartsWith("On ", StringComparison.Ordinal))
                    {
                        kept.RemoveAt(kept.Count - 1);
                    }
                }

                continue;
            }

            kept.Add(line);
        }

        return ExtraBlankLines().Replace(string.Join('\n', kept), "\n\n").Trim();
    }

    /// <summary>"From: …" followed within two lines by "Sent:", "Date:" or "To:": Outlook's quoted header.</summary>
    private static bool HeaderBlockFollows(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length && i <= index + 2; i++)
        {
            if (Regex.IsMatch(lines[i], "^(Sent|Date|To|Subject):", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A forwarded message's header block is content, not history.</summary>
    private static bool AfterForwardMarker(List<string> kept) =>
        kept.LastOrDefault(l => l.Trim().Length > 0) is { } previous && previous.Contains("Forwarded message", StringComparison.OrdinalIgnoreCase);

    private static string? NextNonBlank(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                return lines[i].Trim();
            }
        }

        return null;
    }

    /// <summary>Line breaks normalized, control characters out, no trailing spaces, no runs of blank lines.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var lines = builder.ToString().Split('\n').Select(l => l.TrimEnd());
        return ExtraBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"^\s*-{2,}\s*(Original Message|Ursprüngliche Nachricht|Message d'origine|Originalmeddelande)\s*-{2,}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^\s*_{10,}\s*$")]
    private static partial Regex OutlookRule();

    [GeneratedRegex(@"\bwrote:\s*$|\bschrieb:\s*$|\ba écrit\s*:\s*$|\bskrev:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Wrote();
}
