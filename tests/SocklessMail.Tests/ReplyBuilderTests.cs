using MimeKit;
using SocklessMail.Api.Compose;

namespace SocklessMail.Tests;

public sealed class ReplyBuilderTests
{
    private static InternetAddressList L(string value) => InternetAddressList.Parse(value);

    [Theory]
    [InlineData(ReplyMode.Reply, "Hello", "Re: Hello")]
    [InlineData(ReplyMode.Reply, "Re: Hello", "Re: Hello")]
    [InlineData(ReplyMode.ReplyAll, "AW: Hallo", "AW: Hallo")]
    [InlineData(ReplyMode.Reply, "", "Re: ")]
    [InlineData(ReplyMode.Forward, "Hello", "Fwd: Hello")]
    [InlineData(ReplyMode.Forward, "FW: Hello", "FW: Hello")]
    [InlineData(ReplyMode.Forward, "Re: Hello", "Fwd: Re: Hello")]
    public void Subjects_get_one_prefix(ReplyMode mode, string original, string expected)
    {
        Assert.Equal(expected, ReplyBuilder.Subject(mode, original));
    }

    [Fact]
    public void Reply_goes_to_reply_to_if_present()
    {
        var (to, cc) = ReplyBuilder.Recipients(ReplyMode.Reply, L("Ann <ann@example.com>"), L("list@example.com"), L("me@example.com"), null, "me@example.com");
        Assert.Equal("list@example.com", to.ToString());
        Assert.Empty(cc);
    }

    [Fact]
    public void Reply_all_copies_everyone_once_except_me()
    {
        var (to, cc) = ReplyBuilder.Recipients(ReplyMode.ReplyAll,
            from: L("Ann <ann@example.com>"),
            replyTo: null,
            to: L("Me <ME@example.com>, bob@example.com, ann@example.com"),
            cc: L("carol@example.com, Bob <bob@example.com>"),
            ownAddress: "me@example.com");

        Assert.Equal(["ann@example.com"], to.Mailboxes.Select(m => m.Address));
        Assert.Equal(["bob@example.com", "carol@example.com"], cc.Mailboxes.Select(m => m.Address));
    }

    [Fact]
    public void Replying_to_my_own_sent_message_goes_to_its_recipients()
    {
        var (to, _) = ReplyBuilder.Recipients(ReplyMode.Reply, L("me@example.com"), null, L("bob@example.com"), null, "me@example.com");
        Assert.Equal("bob@example.com", to.ToString());
    }

    [Fact]
    public void Reply_to_a_note_to_self_goes_to_me()
    {
        var (to, cc) = ReplyBuilder.Recipients(ReplyMode.ReplyAll, L("Me <me@example.com>"), null, L("me@example.com"), null, "me@example.com");
        Assert.Equal("me@example.com", to.ToString());
        Assert.Empty(cc);
    }

    [Fact]
    public void Forward_has_no_recipients()
    {
        var (to, cc) = ReplyBuilder.Recipients(ReplyMode.Forward, L("ann@example.com"), null, L("me@example.com"), L("x@example.com"), "me@example.com");
        Assert.Empty(to);
        Assert.Empty(cc);
    }

    [Fact]
    public void Reply_quotes_every_line()
    {
        var text = ReplyBuilder.QuotedText(ReplyMode.Reply, new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
            L("Ann <ann@example.com>"), null, null, "Hi", "first\r\n> earlier\nlast\n");

        Assert.StartsWith("\n\nOn ", text);
        Assert.Contains("\"Ann\" <ann@example.com> wrote:", text.Replace("Ann <", "\"Ann\" <"));
        Assert.Contains("\n> first\n>> earlier\n> last\n", text);
    }

    [Fact]
    public void Forward_includes_the_original_headers_and_body()
    {
        var text = ReplyBuilder.QuotedText(ReplyMode.Forward, DateTimeOffset.UnixEpoch, L("ann@example.com"), L("me@example.com"), L("bob@example.com"), "Plans", "The plan.");

        Assert.Contains("---------- Forwarded message ----------", text);
        Assert.Contains("Subject: Plans", text);
        Assert.Contains("Cc: bob@example.com", text);
        Assert.EndsWith("The plan.\n", text);
    }

    [Fact]
    public void Html_becomes_readable_text()
    {
        var text = ReplyBuilder.HtmlToText("""
            <html><head><style>p{color:red}</style><script>x()</script></head><body>
            <h1>Title</h1><p>First   paragraph<br>second line</p>
            <ul><li>one</li><li>two</li></ul>
            <p>See <a href="https://example.com/x">the page</a>&nbsp;now.</p>
            </body></html>
            """);

        Assert.Equal("Title\n\nFirst paragraph\nsecond line\n\n- one\n- two\n\nSee the page <https://example.com/x> now.", text);
    }
}
