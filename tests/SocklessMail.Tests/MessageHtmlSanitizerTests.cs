using AngleSharp.Html.Parser;
using SocklessMail.Api.Mail;

namespace SocklessMail.Tests;

public sealed class MessageHtmlSanitizerTests
{
    private static SanitizedHtml Clean(string html, bool allowRemote = false) =>
        MessageHtmlSanitizer.Sanitize(html,
            cid => cid == "logo@example" ? "parts/2" : null,
            allowRemote ? url => $"image-proxy?url={Uri.EscapeDataString(url)}" : null);

    private static AngleSharp.Html.Dom.IHtmlDocument Dom(string html) => new HtmlParser().ParseDocument(html);

    [Theory]
    [InlineData("<script>alert(1)</script><p>hi</p>")]
    [InlineData("<p onclick=\"alert(1)\">hi</p>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">hi</a>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=\"x.swf\"></object><embed src=\"x.swf\">")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\">")]
    [InlineData("<link rel=stylesheet href=\"https://evil.example/x.css\">")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    public void Active_content_is_removed(string html)
    {
        var result = Clean(html).Html;

        Assert.DoesNotContain("alert", result);
        Assert.DoesNotContain("evil.example", result);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forms_are_removed_so_mail_cannot_phish_inside_nextcloud()
    {
        var result = Clean("<form action=\"https://evil.example\"><input name=password type=password><button>Log in</button></form>").Html;
        Assert.DoesNotContain("<form", result);
        Assert.DoesNotContain("<input", result);
        Assert.DoesNotContain("evil.example", result);
    }

    [Fact]
    public void Remote_images_are_blocked_and_reported()
    {
        var result = Clean("<p>Hi</p><img src=\"https://tracker.example/pixel.gif\" alt=\"pixel\">");

        Assert.True(result.HasRemoteContent);
        Assert.DoesNotContain("tracker.example", result.Html);
        Assert.Contains("alt=\"pixel\"", result.Html);
    }

    [Theory]
    [InlineData("<table background=\"https://tracker.example/bg.png\"><tr><td>x</td></tr></table>")]
    [InlineData("<div style=\"background-image: url('https://tracker.example/bg.png')\">x</div>")]
    [InlineData("<style>body { background: url(https://tracker.example/bg.png) }</style><p>x</p>")]
    [InlineData("<img src=\"//tracker.example/pixel.gif\">")]
    [InlineData("<img srcset=\"https://tracker.example/a.png 2x\">")]
    public void Remote_resources_outside_img_src_are_removed(string html)
    {
        var result = Clean(html);
        Assert.DoesNotContain("tracker.example", result.Html);
    }

    [Fact]
    public void Allowed_remote_images_go_through_the_proxy()
    {
        var result = Clean("<img src=\"https://images.example/a.png\">", allowRemote: true);

        Assert.True(result.HasRemoteContent);
        var img = Dom(result.Html).QuerySelector("img")!;
        Assert.Null(img.GetAttribute("src"));
        Assert.Equal("image-proxy?url=https%3A%2F%2Fimages.example%2Fa.png", img.GetAttribute(MessageHtmlSanitizer.SourceAttribute));
    }

    [Fact]
    public void Inline_parts_are_resolved_by_content_id()
    {
        var result = Clean("<img src=\"cid:logo@example\"><img src=\"cid:unknown@example\">");

        var images = Dom(result.Html).QuerySelectorAll("img");
        Assert.Equal("parts/2", images[0].GetAttribute(MessageHtmlSanitizer.SourceAttribute));
        Assert.Null(images[1].GetAttribute(MessageHtmlSanitizer.SourceAttribute));
        Assert.Null(images[1].GetAttribute("src"));
        Assert.False(result.HasRemoteContent);
    }

    [Fact]
    public void Embedded_data_images_are_kept_but_not_other_data_urls()
    {
        var result = Clean("<img src=\"data:image/png;base64,iVBORw0KGgo=\"><img src=\"data:image/svg+xml;base64,PHN2Zz4=\"><a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>").Html;

        var dom = Dom(result);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", dom.QuerySelectorAll("img")[0].GetAttribute("src"));
        Assert.Null(dom.QuerySelectorAll("img")[1].GetAttribute("src"));
        Assert.Null(dom.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void Links_open_in_a_new_tab_without_opener()
    {
        var a = Dom(Clean("<a href=\"https://example.com/\">x</a>").Html).QuerySelector("a")!;
        Assert.Equal("https://example.com/", a.GetAttribute("href"));
        Assert.Equal("_blank", a.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", a.GetAttribute("rel"));
    }

    [Fact]
    public void Mailto_links_are_kept()
    {
        var a = Dom(Clean("<a href=\"mailto:bob@example.com\">x</a>").Html).QuerySelector("a")!;
        Assert.Equal("mailto:bob@example.com", a.GetAttribute("href"));
    }

    [Fact]
    public void Newsletter_layout_survives()
    {
        const string html = """
            <html><head><style>@media (max-width: 600px) { .col { width: 100% } } .title { color: #c00 }</style></head>
            <body><table width="600" class="layout"><tr><td class="col title" style="padding: 8px; color: #333">Hello</td></tr></table></body></html>
            """;

        var result = Clean(html).Html;

        Assert.Contains("@media", result);
        Assert.Contains(".title", result);
        Assert.Contains("class=\"col title\"", result);
        Assert.Contains("padding: 8px", result);
        Assert.Contains("width=\"600\"", result);
        Assert.False(Clean(html).HasRemoteContent);
    }
}
