using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MimeKit;

namespace SocklessMail.Api.Compose;

/// <summary>Subjects, recipients and quoted text for replies and forwards. No I/O.</summary>
public static partial class ReplyBuilder
{
    [GeneratedRegex(@"^\s*(re|aw|sv|antw)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"^\s*(fwd?|wg|tr)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardPrefix();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    public static string Subject(ReplyMode mode, string? original)
    {
        var subject = (original ?? "").Trim();
        return mode == ReplyMode.Forward
            ? ForwardPrefix().IsMatch(subject) ? subject : $"Fwd: {subject}"
            : ReplyPrefix().IsMatch(subject) ? subject : $"Re: {subject}";
    }

    /// <summary>
    /// Reply: to Reply-To, else From. Reply all: additionally everyone in To and Cc as Cc. The
    /// account's own address is never a recipient, and nobody appears twice. Forward: empty.
    /// </summary>
    public static (InternetAddressList To, InternetAddressList Cc) Recipients(
        ReplyMode mode, InternetAddressList? from, InternetAddressList? replyTo, InternetAddressList? to, InternetAddressList? cc, string ownAddress)
    {
        var resultTo = new InternetAddressList();
        var resultCc = new InternetAddressList();
        if (mode == ReplyMode.Forward)
        {
            return (resultTo, resultCc);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownAddress };
        void AddTo(InternetAddressList target, InternetAddressList? source)
        {
            foreach (var mailbox in source?.Mailboxes ?? [])
            {
                if (seen.Add(mailbox.Address))
                {
                    target.Add(new MailboxAddress(mailbox.Name, mailbox.Address));
                }
            }
        }

        var primary = replyTo is { Count: > 0 } ? replyTo : from;
        // Replying to your own sent message goes back to its recipients.
        if (primary?.Mailboxes.All(m => m.Address.Equals(ownAddress, StringComparison.OrdinalIgnoreCase)) == true)
        {
            primary = to;
        }

        AddTo(resultTo, primary);
        if (mode == ReplyMode.ReplyAll)
        {
            AddTo(resultCc, to);
            AddTo(resultCc, cc);
        }

        // A note to self: everyone involved was me, so the reply goes back to me.
        if (resultTo.Count == 0 && resultCc.Count == 0)
        {
            resultTo.Add(MailboxAddress.Parse(ownAddress));
        }

        return (resultTo, resultCc);
    }

    /// <summary>The original quoted below an empty first line, so the cursor starts above it.</summary>
    public static string QuotedText(
        ReplyMode mode, DateTimeOffset date, InternetAddressList? from, InternetAddressList? to, InternetAddressList? cc, string? subject, string body)
    {
        var text = new StringBuilder("\n\n");
        var when = date.ToString("f", CultureInfo.InvariantCulture);
        if (mode == ReplyMode.Forward)
        {
            text.Append("---------- Forwarded message ----------\n");
            text.Append($"From: {from}\n");
            text.Append($"Date: {when}\n");
            text.Append($"Subject: {subject}\n");
            text.Append($"To: {to}\n");
            if (cc is { Count: > 0 })
            {
                text.Append($"Cc: {cc}\n");
            }

            text.Append('\n').Append(body.TrimEnd()).Append('\n');
            return text.ToString();
        }

        text.Append($"On {when}, {from} wrote:\n");
        foreach (var line in body.TrimEnd().Replace("\r\n", "\n").Split('\n'))
        {
            text.Append(line.StartsWith('>') ? ">" : "> ").Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Readable plain text from HTML: block elements become line breaks, links keep their target.</summary>
    public static string HtmlToText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder();
        Append(document.Body ?? (INode)document.DocumentElement, text);
        // Whitespace between elements leaves stray spaces at line edges.
        var lines = text.ToString().Replace(" ", " ").Split('\n').Select(line => line.Trim());
        return ExtraBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table", "ul", "ol", "hr", "section", "article", "header", "footer",
    };

    private static void Append(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText t:
                    text.Append(Regex.Replace(t.Data, @"\s+", " "));
                    break;
                case IElement { LocalName: "script" or "style" or "head" or "title" }:
                    break;
                case IElement { LocalName: "br" }:
                    text.Append('\n');
                    break;
                case IElement e:
                    var block = BlockElements.Contains(e.LocalName);
                    if (block && text.Length > 0 && text[^1] != '\n')
                    {
                        text.Append('\n');
                    }

                    if (e.LocalName == "li")
                    {
                        text.Append("- ");
                    }

                    Append(e, text);
                    if (e.LocalName == "a" && e.GetAttribute("href") is { } href
                        && href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !e.TextContent.Contains(href))
                    {
                        text.Append($" <{href}>");
                    }

                    if (block)
                    {
                        text.Append(e.LocalName is "p" or "blockquote" or "table" or "h1" or "h2" or "h3" ? "\n\n" : "\n");
                    }

                    break;
            }
        }
    }
}
