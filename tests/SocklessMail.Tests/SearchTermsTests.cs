using SocklessMail.Api.Mailbox;

namespace SocklessMail.Tests;

public sealed class SearchTermsTests
{
    [Theory]
    [InlineData("invoice", new[] { "invoice" })]
    [InlineData("  invoice   march ", new[] { "invoice", "march" })]
    [InlineData("\"quarterly report\" alice", new[] { "quarterly report", "alice" })]
    [InlineData("Alice alice ALICE", new[] { "Alice" })]
    [InlineData("\"unclosed phrase", new[] { "\"unclosed", "phrase" })]
    [InlineData("tab\there", new[] { "tabhere" })]
    public void Words_and_phrases_are_terms(string query, string[] expected) =>
        Assert.Equal(expected, SearchTerms.Parse(query));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\" \"  \"")]
    public void Nothing_to_search_for_is_refused(string? query) =>
        Assert.Equal("q", Assert.Throws<MailboxValidationException>(() => SearchTerms.Parse(query)).Field);

    [Fact]
    public void Too_many_terms_or_characters_are_refused()
    {
        Assert.Throws<MailboxValidationException>(() => SearchTerms.Parse(string.Join(' ', Enumerable.Range(1, SearchTerms.MaxTerms + 1))));
        Assert.Throws<MailboxValidationException>(() => SearchTerms.Parse(new string('x', SearchTerms.MaxLength + 1)));
        Assert.Equal(SearchTerms.MaxTerms, SearchTerms.Parse(string.Join(' ', Enumerable.Range(1, SearchTerms.MaxTerms))).Count);
    }
}
