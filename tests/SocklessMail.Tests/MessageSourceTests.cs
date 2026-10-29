using System.Text;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Tests;

public sealed class MessageSourceTests
{
    [Theory]
    [InlineData(null, "message.eml")]
    [InlineData("   ", "message.eml")]
    [InlineData("Re: Q3 numbers?", "Re_ Q3 numbers_.eml")]
    [InlineData("a/b\\c<d>|e*\"f\"", "a_b_c_d__e__f_.eml")]
    [InlineData("Line\r\nbreak", "Line__break.eml")]
    [InlineData("Ends with dots...", "Ends with dots.eml")]
    [InlineData("Räksmörgås 🦐", "Räksmörgås 🦐.eml")]
    public void Subject_becomes_a_safe_file_name(string? subject, string expected) =>
        Assert.Equal(expected, MailboxService.EmlFileName(subject));

    [Fact]
    public void Long_subjects_are_cut_without_splitting_a_surrogate_pair()
    {
        var name = MailboxService.EmlFileName(new string('x', 79) + "🦐🦐");
        Assert.Equal(new string('x', 79) + ".eml", name);
        Assert.True(MailboxService.EmlFileName(new string('y', 300)).Length <= 84);
    }

    [Fact]
    public void Utf8_is_decoded_and_other_bytes_fall_back_to_latin1()
    {
        Assert.Equal("Grüße", MailboxService.DecodeRaw(Encoding.UTF8.GetBytes("Grüße"), truncated: false));
        Assert.Equal("Grüße", MailboxService.DecodeRaw(Encoding.Latin1.GetBytes("Grüße"), truncated: false));
    }

    [Theory]
    [InlineData("é", 1)]    // cut after the lead byte of a 2-byte sequence
    [InlineData("€", 1)]    // 3-byte sequence, 1 of 3 bytes left
    [InlineData("€", 2)]    // 3-byte sequence, 2 of 3 bytes left
    [InlineData("🦐", 3)]   // 4-byte sequence, 3 of 4 bytes left
    public void A_truncated_source_drops_the_partial_character(string last, int keep)
    {
        var bytes = Encoding.UTF8.GetBytes("abc" + last)[..(3 + keep)];
        Assert.Equal("abc", MailboxService.DecodeRaw(bytes, truncated: true));
    }

    [Theory]
    [InlineData("abcé")]
    [InlineData("abc€")]
    [InlineData("abc🦐")]
    [InlineData("abc")]
    public void A_truncated_source_keeps_a_complete_last_character(string text) =>
        Assert.Equal(text, MailboxService.DecodeRaw(Encoding.UTF8.GetBytes(text), truncated: true));
}
