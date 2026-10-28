using MimeKit;
using SocklessMail.Api.Compose;

namespace SocklessMail.Tests;

public sealed class OutgoingHtmlTests
{
    [Fact]
    public void Nothing_active_or_remote_survives()
    {
        var html = OutgoingHtml.Sanitize(
            "<p onclick=\"x()\" class=\"a\">Hi <a href=\"javascript:x()\">bad</a> <a href=\"mailto:a@example.test\">mail</a></p>"
            + "<script>x()</script><iframe src=\"https://example.test\"></iframe><img src=\"https://example.test/t.gif\"><form><input></form>");
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("class=", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<form", html);
        Assert.Contains("href=\"mailto:a@example.test\"", html);
    }

    [Theory]
    [InlineData("<div style=\"background:url(https://tracker.example/a.png)\">x</div>")]
    [InlineData("<div style=\"background:u\\72l(https://tracker.example/a.png)\">x</div>")]
    [InlineData("<div style=\"background-image:image-set('https://tracker.example/a.png' 1x)\">x</div>")]
    [InlineData("<ul style=\"list-style-image:url('https://tracker.example/a.png')\"><li>x</li></ul>")]
    [InlineData("<style>p { background: url(https://tracker.example/a.png) }</style><p>x</p>")]
    public void Quoted_css_cannot_load_remote_resources(string html)
    {
        var result = OutgoingHtml.Sanitize(html);
        Assert.DoesNotContain("tracker.example", result);
        Assert.Contains("x", result);
    }

    [Theory]
    [InlineData("<svg><a href=\"javascript:x()\"><text>x</text></a></svg>")]
    [InlineData("<map name=m><area href=\"javascript:x()\"></map>")]
    [InlineData("<a href=\"java&#x09;script:x()\">x</a>")]
    [InlineData("<a href=\"data:text/html,<script>x()</script>\">x</a>")]
    [InlineData("<details open ontoggle=x()>x</details>")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=x()>\"></noscript>")]
    public void Active_content_from_a_quoted_original_is_removed(string html)
    {
        var result = OutgoingHtml.Sanitize(html);
        Assert.DoesNotContain("x()", result);
        Assert.DoesNotContain("<img", result);
    }

    [Fact]
    public void Formatting_from_the_editor_is_kept()
    {
        var html = OutgoingHtml.Sanitize(
            "<h2>Title</h2><p style=\"text-align: center\"><strong>b</strong> <em>i</em> <u>u</u> <s>s</s> "
            + "<span style=\"color: #c13a3a; font-size: 18px\">big</span> <mark>hi</mark></p><ul><li><p>one</p></li></ul><blockquote><p>q</p></blockquote>");
        foreach (var expected in new[] { "<h2>", "text-align: center", "<strong>", "<em>", "<u>", "<s>", "font-size: 18px", "<mark>", "<li>", "<blockquote>" })
        {
            Assert.Contains(expected, html);
        }
    }

    [Fact]
    public void Paragraphs_lose_their_margins_and_empty_ones_keep_a_line()
    {
        var document = OutgoingHtml.Document("<p>one</p><p></p><p style=\"text-align: right\">two</p>");
        Assert.Contains("<p style=\"margin:0\">one</p>", document);
        Assert.Contains("<p style=\"margin:0\"><br></p>", document);
        Assert.Contains("<p style=\"margin:0;text-align: right\">two</p>", document);
        Assert.StartsWith("<!DOCTYPE html>", document);
        // Blocks are separated, so a preview of the HTML does not run them together.
        Assert.Contains("one</p>\n<p", document);
        Assert.Equal("one two", new MimeKit.Text.HtmlTextPreviewer().GetPreviewText(OutgoingHtml.Document("<p>one</p><p>two</p>")));
    }

    [Fact]
    public void Text_becomes_paragraphs_with_line_breaks()
    {
        Assert.Equal("<p>a &lt;b&gt;<br>c</p><p>d</p>", OutgoingHtml.FromText("a <b>\nc\n\nd"));
    }

    [Fact]
    public void Quotes_escape_the_header_and_drop_images()
    {
        var from = InternetAddressList.Parse("\"A <B>\" <a@example.test>");
        var reply = OutgoingHtml.Quote(ReplyMode.Reply, DateTimeOffset.UnixEpoch, from, null, null, "S", "<p>x<img src=\"cid:1\"></p>");
        Assert.StartsWith("<p></p>", reply);
        Assert.Contains("&quot;A &lt;B&gt;&quot;", reply);
        Assert.Contains("<blockquote><p>x</p></blockquote>", reply);

        var forward = OutgoingHtml.Quote(ReplyMode.Forward, DateTimeOffset.UnixEpoch, from, from, null, "S & T", "<p>x</p>");
        Assert.Contains("---------- Forwarded message ----------", forward);
        Assert.Contains("Subject: S &amp; T", forward);
        Assert.DoesNotContain("<blockquote>", forward);
    }
}
