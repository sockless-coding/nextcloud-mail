using System.Text.RegularExpressions;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Ganss.Xss;

namespace SocklessMail.Api.Mail;

public sealed record SanitizedHtml(string Html, bool HasRemoteContent);

/// <summary>
/// Makes message HTML safe to display. First line of defence; the browser shows the result in
/// an iframe sandboxed without scripts, as the second.
///
/// After HtmlSanitizer has removed scripts, event handlers, forms, frames and unsafe URLs:
/// <list type="bullet">
/// <item>Remote resources (http/https in img src, background attributes, CSS url()) never stay
/// as they are, so opening a message does not tell the sender anything. With remote content
/// allowed, images are routed through the app's image proxy; otherwise they are removed and
/// <see cref="SanitizedHtml.HasRemoteContent"/> is set so the UI can offer to load them.</item>
/// <item>cid: references to inline parts become links to the message part.</item>
/// <item>Links open in a new tab without access to the opener.</item>
/// </list>
/// Rewritten image sources are API-relative paths in <c>data-sockless-src</c>; the frontend turns
/// them into URLs, since only it knows whether the API is reached through HaRP or the PHP proxy.
/// </summary>
public static partial class MessageHtmlSanitizer
{
    public const string SourceAttribute = "data-sockless-src";

    private static readonly string[] RemovedTags =
        ["form", "input", "button", "select", "textarea", "option", "optgroup", "fieldset", "legend", "label", "datalist", "output"];

    private static readonly string[] SafeDataImageTypes =
        ["data:image/png;", "data:image/jpeg;", "data:image/jpg;", "data:image/gif;", "data:image/webp;"];

    [GeneratedRegex(@"url\(\s*(['""]?)(.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrl();

    /// <param name="html">The message's HTML (or plain text already converted to HTML).</param>
    /// <param name="resolveContentId">Maps a Content-ID to the API-relative path of that part, or null.</param>
    /// <param name="proxyRemote">Maps a remote image URL to an API-relative proxy path; null blocks remote content.</param>
    public static SanitizedHtml Sanitize(string html, Func<string, string?> resolveContentId, Func<string, string>? proxyRemote)
    {
        var hasRemote = false;
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in RemovedTags)
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        // Newsletters depend on <style> blocks and media queries; HtmlSanitizer sanitizes their CSS.
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedAtRules.Add(CssRuleType.Media);
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowDataAttributes = false;

        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var element in e.Document.All.ToList())
            {
                RewriteElement(element, resolveContentId, proxyRemote, ref hasRemote);
            }
        };

        var result = sanitizer.SanitizeDocument(html);
        return new SanitizedHtml(result, hasRemote);
    }

    private static void RewriteElement(IElement element, Func<string, string?> resolveContentId, Func<string, string>? proxyRemote, ref bool hasRemote)
    {
        switch (element.LocalName)
        {
            case "img":
                element.RemoveAttribute("srcset");
                RewriteImageSource(element, resolveContentId, proxyRemote, ref hasRemote);
                break;
            case "a":
                RewriteLink(element);
                break;
            case "style":
                element.TextContent = RewriteCssUrls(element.TextContent, ref hasRemote);
                break;
        }

        if (element.GetAttribute("background") is { } background)
        {
            hasRemote |= IsRemote(background);
            element.RemoveAttribute("background");
        }

        if (element.GetAttribute("style") is { } style && style.Contains("url(", StringComparison.OrdinalIgnoreCase))
        {
            element.SetAttribute("style", RewriteCssUrls(style, ref hasRemote));
        }
    }

    private static void RewriteImageSource(IElement img, Func<string, string?> resolveContentId, Func<string, string>? proxyRemote, ref bool hasRemote)
    {
        var src = img.GetAttribute("src")?.Trim();
        img.RemoveAttribute("src");
        if (string.IsNullOrEmpty(src))
        {
            return;
        }

        if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            if (resolveContentId(Uri.UnescapeDataString(src[4..]).Trim('<', '>')) is { } partPath)
            {
                img.SetAttribute(SourceAttribute, partPath);
            }
        }
        else if (SafeDataImageTypes.Any(t => src.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
        {
            img.SetAttribute("src", src);
        }
        else if (IsRemote(src))
        {
            hasRemote = true;
            if (proxyRemote is not null)
            {
                img.SetAttribute(SourceAttribute, proxyRemote(src));
            }
        }
    }

    private static void RewriteLink(IElement a)
    {
        var href = a.GetAttribute("href")?.Trim();
        if (href is not null && !(IsRemote(href) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
        {
            a.RemoveAttribute("href");
        }

        a.SetAttribute("target", "_blank");
        a.SetAttribute("rel", "noopener noreferrer");
    }

    /// <summary>CSS cannot go through the proxy without rewriting stylesheets; remote url() is dropped.</summary>
    private static string RewriteCssUrls(string css, ref bool hasRemote)
    {
        var remote = false;
        var rewritten = CssUrl().Replace(css, match =>
        {
            var url = match.Groups[2].Value.Trim();
            if (SafeDataImageTypes.Any(t => url.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            {
                return match.Value;
            }

            remote |= IsRemote(url);
            return "none";
        });
        hasRemote |= remote;
        return rewritten;
    }

    private static bool IsRemote(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("//", StringComparison.Ordinal);
}
