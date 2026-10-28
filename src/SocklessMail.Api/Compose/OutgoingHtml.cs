using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using MimeKit;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Compose;

/// <summary>
/// HTML the app sends: what the editor produced, and the quoted original of a reply or forward.
/// Both go through one allowlist, so nothing active (scripts, event handlers, forms, frames)
/// reaches a recipient, and no image is quoted, not even as a CSS background: the editor would
/// load remote ones in the user's browser, and inline ones would need their parts attached.
/// </summary>
public static class OutgoingHtml
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "img", "form", "input", "button", "select", "textarea", "option", "label", "fieldset", "legend" })
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        sanitizer.AllowedTags.Add("mark");
        sanitizer.AllowedSchemes.Add("mailto");
        // Classes mean nothing without the sender's stylesheet.
        sanitizer.AllowedAttributes.Remove("class");
        // The editor never refers to a resource from CSS; a quoted original's url() would load in
        // the recipient's mail program. By now the CSS is reserialized, escapes and all.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement element && element.GetAttribute("style") is { } style
                && style.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                element.SetAttribute("style", MessageHtmlSanitizer.CssUrl().Replace(style, "none"));
            }
        };
        return sanitizer;
    }

    /// <summary>A fragment safe to send and to load into the editor.</summary>
    public static string Sanitize(string html) => Sanitizer.Sanitize(html).Trim();

    /// <summary>
    /// The HTML part of an outgoing message. Paragraphs get no margins, as in desktop mail
    /// programs (one Enter is one line), and empty ones keep their height.
    /// </summary>
    public static string Document(string fragment)
    {
        var document = new HtmlParser().ParseDocument($"<body>{fragment}</body>");
        foreach (var paragraph in document.QuerySelectorAll("p"))
        {
            var style = paragraph.GetAttribute("style");
            paragraph.SetAttribute("style", string.IsNullOrEmpty(style) ? "margin:0" : $"margin:0;{style}");
            if (paragraph.ChildNodes.Length == 0)
            {
                paragraph.AppendChild(document.CreateElement("br"));
            }
        }

        foreach (var quote in document.QuerySelectorAll("blockquote"))
        {
            quote.SetAttribute("style", "margin:0 0 0 0.8ex;padding-left:1ex;border-left:2px solid #ccc");
        }

        // A line break after each block: previews made from the HTML (MailKit's among them) would
        // otherwise run adjacent paragraphs together ("onetwo").
        foreach (var block in document.QuerySelectorAll("p, li, h1, h2, h3, h4, h5, h6, blockquote, ul, ol, div"))
        {
            block.After(document.CreateTextNode("\n"));
        }

        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>"
               + "<body style=\"font-family:Calibri,Arial,Helvetica,sans-serif;font-size:11pt\">"
               + document.Body!.InnerHtml
               + "</body></html>";
    }

    /// <summary>Plain text as HTML paragraphs, line breaks kept.</summary>
    public static string FromText(string text)
    {
        var html = new StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            html.Append("<p>")
                .Append(string.Join("<br>", paragraph.Split('\n').Select(WebUtility.HtmlEncode)))
                .Append("</p>");
        }

        return html.ToString();
    }

    /// <summary>
    /// The original below an empty first paragraph (where the cursor starts): in a blockquote for
    /// a reply, after a header block for a forward.
    /// </summary>
    public static string Quote(
        ReplyMode mode, DateTimeOffset date, InternetAddressList? from, InternetAddressList? to, InternetAddressList? cc, string? subject, string originalHtml)
    {
        string Encode(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
        var when = date.ToString("f", CultureInfo.InvariantCulture);
        var original = Sanitize(originalHtml);
        if (mode == ReplyMode.Forward)
        {
            var header = new StringBuilder("---------- Forwarded message ----------");
            header.Append($"<br>From: {Encode(from)}<br>Date: {Encode(when)}<br>Subject: {Encode(subject)}<br>To: {Encode(to)}");
            if (cc is { Count: > 0 })
            {
                header.Append($"<br>Cc: {Encode(cc)}");
            }

            return $"<p></p><p></p><p>{header}</p><p></p>{original}";
        }

        return $"<p></p><p></p><p>On {Encode(when)}, {Encode(from)} wrote:</p><blockquote>{original}</blockquote>";
    }
}
