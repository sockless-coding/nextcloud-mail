using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using MimeKit;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Compose;

/// <summary>
/// HTML the app sends: what the editor produced, and the quoted original of a reply or forward.
/// Both go through one allowlist, so nothing active (scripts, event handlers, forms, frames)
/// reaches a recipient, and nothing remote, not even as a CSS background: the editor would load
/// it in the user's browser. The editor's own images are inline parts of the message, referred to
/// by Content-ID (<see cref="SanitizeWithImages"/>); a quoted original keeps only images that
/// are its own parts (sent along), never remote ones.
/// </summary>
public static partial class OutgoingHtml
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer(images: false);
    private static readonly HtmlSanitizer ImageSanitizer = CreateSanitizer(images: true);

    /// <summary>
    /// What the editor marks an image with: the Content-ID of its part (also while the image is
    /// still an upload, see <see cref="UploadContentId"/>). Its src is only for showing it there.
    /// </summary>
    public const string ContentIdAttribute = "data-cid";

    private const int MaxImageSize = 4000;

    /// <summary>What may be shown inside a message; other files are attachments.</summary>
    public static readonly IReadOnlySet<string> InlineImageTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    /// <summary>A Content-ID as an addr-spec without angle brackets, as the editor and MIME parts carry it.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~.\-]{1,200}@[A-Za-z0-9.\-]{1,100}$")]
    private static partial Regex ContentIdFormat();

    [GeneratedRegex(@"^([0-9a-f]{32})@inline\.sockless-mail$")]
    private static partial Regex UploadContentIdFormat();

    /// <summary>The Content-ID an inserted image has while it is an upload; it keeps it as a part.</summary>
    public static string UploadContentId(string uploadId) => $"{uploadId}@inline.sockless-mail";

    /// <summary>The upload a Content-ID made by <see cref="UploadContentId"/> refers to.</summary>
    public static string? UploadIdOf(string contentId) =>
        UploadContentIdFormat().Match(contentId) is { Success: true } match ? match.Groups[1].Value : null;

    private static HtmlSanitizer CreateSanitizer(bool images)
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "img", "form", "input", "button", "select", "textarea", "option", "label", "fieldset", "legend" })
        {
            if (tag != "img" || !images)
            {
                sanitizer.AllowedTags.Remove(tag);
            }
        }

        if (images)
        {
            sanitizer.AllowedSchemes.Add("cid");
            sanitizer.AllowedAttributes.Add(ContentIdAttribute);
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

    /// <summary>A fragment safe to send and to load into the editor, without images (a quoted original).</summary>
    public static string Sanitize(string html) => Sanitizer.Sanitize(html).Trim();

    /// <summary>
    /// Like <see cref="Sanitize"/>, but images that refer to a part by Content-ID stay, as
    /// <c>&lt;img src="cid:…"&gt;</c> with only alt, width and height; every other image goes.
    /// </summary>
    public static string SanitizeWithImages(string html)
    {
        var document = new HtmlParser().ParseDocument($"<body>{ImageSanitizer.Sanitize(html)}</body>");
        foreach (var img in document.Body!.QuerySelectorAll("img").ToList())
        {
            CanonicalImage(img);
        }

        return document.Body.InnerHtml.Trim();
    }

    /// <summary>An image of the editor as it is sent, or removed when it refers to no part.</summary>
    private static void CanonicalImage(IElement img)
    {
        var src = img.GetAttribute("src")?.Trim() ?? "";
        var contentId = img.GetAttribute(ContentIdAttribute)?.Trim()
                        ?? (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(src[4..]).Trim('<', '>') : null);
        if (contentId is null || !ContentIdFormat().IsMatch(contentId))
        {
            img.Remove();
            return;
        }

        foreach (var name in img.Attributes.Select(a => a.Name).ToList())
        {
            if (name is not ("alt" or "width" or "height"))
            {
                img.RemoveAttribute(name);
            }
        }

        foreach (var dimension in new[] { "width", "height" })
        {
            if (img.GetAttribute(dimension) is { } value
                && !(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pixels) && pixels is > 0 and <= MaxImageSize))
            {
                img.RemoveAttribute(dimension);
            }
        }

        img.SetAttribute("src", "cid:" + contentId);
    }

    /// <summary>The Content-IDs a fragment from <see cref="SanitizeWithImages"/> refers to, each once, in order.</summary>
    public static IReadOnlyList<string> ContentIds(string fragment) =>
        new HtmlParser().ParseDocument($"<body>{fragment}</body>").Body!.QuerySelectorAll("img")
            .Select(img => img.GetAttribute("src") ?? "")
            .Where(src => src.StartsWith("cid:", StringComparison.Ordinal))
            .Select(src => src[4..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The fragment without the images whose Content-ID is not in <paramref name="available"/>.</summary>
    public static string KeepImages(string fragment, IReadOnlySet<string> available)
    {
        var document = new HtmlParser().ParseDocument($"<body>{fragment}</body>");
        foreach (var img in document.Body!.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            if (!src.StartsWith("cid:", StringComparison.Ordinal) || !available.Contains(src[4..]))
            {
                img.Remove();
            }
        }

        return document.Body.InnerHtml;
    }

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

        // Never wider than the recipient's window, and still in proportion when narrowed.
        foreach (var img in document.QuerySelectorAll("img"))
        {
            var style = img.GetAttribute("style");
            img.SetAttribute("style", string.IsNullOrEmpty(style) ? "max-width:100%;height:auto" : $"max-width:100%;height:auto;{style}");
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
    /// a reply, after a header block for a forward. Of the original's images, those with a
    /// Content-ID in <paramref name="images"/> (its own parts, sent along) stay.
    /// </summary>
    public static string Quote(
        ReplyMode mode, DateTimeOffset date, InternetAddressList? from, InternetAddressList? to, InternetAddressList? cc, string? subject, string originalHtml,
        IReadOnlySet<string>? images = null)
    {
        string Encode(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
        var when = date.ToString("f", CultureInfo.InvariantCulture);
        var original = images is { Count: > 0 } ? KeepImages(SanitizeWithImages(originalHtml), images) : Sanitize(originalHtml);
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
