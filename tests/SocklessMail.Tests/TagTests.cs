using System.Net;
using System.Net.Http.Json;
using SocklessMail.Api.Tags;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class KeywordTests
{
    [Theory]
    [InlineData("Invoices", "$invoices")]
    [InlineData("Räkningar 2026", "$rakningar_2026")]
    [InlineData("To do / later!", "$to_do_later")]
    [InlineData("日本", "$tag")]
    [InlineData("label1", "$label12")]
    [InlineData("Forwarded", "$forwarded2")]
    public void A_new_tag_gets_a_keyword_from_its_name(string name, string keyword) =>
        Assert.Equal(keyword, Keywords.ForName(name, []));

    [Fact]
    public void A_taken_keyword_gets_a_number() =>
        Assert.Equal("$work3", Keywords.ForName("Work", ["$work", "$WORK2"]));

    [Theory]
    [InlineData("$label1", true)]
    [InlineData("projectx", true)]
    [InlineData("$Forwarded", false)]
    [InlineData("$HasNoAttachment", false)]
    [InlineData("$MailFlagBit0", false)]
    [InlineData("\\Seen", false)]
    [InlineData("a b", false)]
    [InlineData("a(b", false)]
    [InlineData("", false)]
    public void Only_atoms_that_are_not_bookkeeping_may_be_tags(string keyword, bool tag) =>
        Assert.Equal(tag, Keywords.IsTagKeyword(keyword));

    [Fact]
    public void Keywords_are_stored_padded_and_compared_without_case()
    {
        var stored = Keywords.Format(["$a", "$B", "$b", "bad word"]);
        Assert.Equal(" $a $B ", stored);
        Assert.True(Keywords.Has(stored, "$b"));
        Assert.Equal(" $B $c ", Keywords.Change(stored, ["$c"], ["$A"]));
        Assert.Equal("", Keywords.Format([]));
        Assert.Equal(["$a"], Keywords.ForClient(" $a $HasAttachment "));
    }
}

/// <summary>Tag definitions over the API: validation, order, and one user's tags hidden from another.</summary>
public sealed class TagStoreTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public TagStoreTests() => _alice = _app.ClientAs("alice");

    public void Dispose() => _app.Dispose();

    private async Task<Guid> CreateAsync(HttpClient client, string name, string? color = null) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/tags", new { name, color }, Ct))).GetProperty("id").GetGuid();

    [Fact]
    public async Task Tags_are_created_listed_and_reordered()
    {
        var work = await CreateAsync(_alice, " Work ", "#ABCDEF");
        var home = await CreateAsync(_alice, "Home");

        var list = await JsonAsync(await _alice.GetAsync("/api/tags", Ct));
        Assert.Equal(["Work", "Home"], list.EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("#abcdef", list[0].GetProperty("color").GetString());
        Assert.Equal(TagStore.DefaultColor, list[1].GetProperty("color").GetString());

        var reordered = await JsonAsync(await _alice.PutAsJsonAsync("/api/tags/order", new { ids = new[] { home, work } }, Ct));
        Assert.Equal(["Home", "Work"], reordered.EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("", "#000000", "name")]
    [InlineData("Red", "red", "color")]
    [InlineData("Red", "#12345", "color")]
    [InlineData("Existing", "#000000", "name")]
    [InlineData("existing", "#000000", "name")]
    public async Task Invalid_tags_are_refused(string name, string color, string field)
    {
        await CreateAsync(_alice, "Existing");

        var response = await _alice.PostAsJsonAsync("/api/tags", new { name, color }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Bookkeeping_keywords_cannot_be_adopted()
    {
        var response = await _alice.PostAsJsonAsync("/api/tags", new { name = "Fwd", keyword = "$Forwarded" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_tags_are_invisible()
    {
        var alices = await CreateAsync(_alice, "Private");
        var bob = _app.ClientAs("bob");

        Assert.Empty((await JsonAsync(await bob.GetAsync("/api/tags", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/tags/{alices}", new { name = "Mine" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/tags/{alices}", Ct)).StatusCode);
        // Bob may use the same name; it is his own tag.
        await CreateAsync(bob, "Private");
        Assert.Equal("Private", (await JsonAsync(await _alice.GetAsync("/api/tags", Ct)))[0].GetProperty("name").GetString());
    }
}
