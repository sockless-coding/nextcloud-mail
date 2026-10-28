using SocklessMail.Api.Mailbox;

namespace SocklessMail.Tests;

public sealed class MessagePreviewTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \r\n\t ", "")]
    [InlineData("Hello  there,\r\n\r\nthe numbers\tare in.", "Hello there, the numbers are in.")]
    [InlineData("Invisible​‌͏ padding", "Invisible padding")]
    public void Text_is_collapsed_to_one_line(string? text, string expected) =>
        Assert.Equal(expected, MessagePreview.Normalize(text));

    [Fact]
    public void Long_text_is_cut()
    {
        var preview = MessagePreview.Normalize(string.Join(' ', Enumerable.Repeat("word", 100)));
        Assert.True(preview.Length <= MessagePreview.MaxLength);
        Assert.StartsWith("word word", preview);
        Assert.False(preview.EndsWith(' '));
    }
}

public sealed class MessageFiltersTests
{
    [Theory]
    [InlineData(null, MessageFilter.None)]
    [InlineData("", MessageFilter.None)]
    [InlineData("unread", MessageFilter.Unread)]
    [InlineData("flagged", MessageFilter.Flagged)]
    [InlineData("attachments", MessageFilter.Attachments)]
    public void Known_filters_are_accepted(string? value, MessageFilter expected) =>
        Assert.Equal(expected, MessageFilters.Parse(value));

    [Theory]
    [InlineData("Unread")]
    [InlineData("starred")]
    public void Other_values_are_refused(string value) =>
        Assert.Equal("filter", Assert.Throws<MailboxValidationException>(() => MessageFilters.Parse(value)).Field);
}
