using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using SocklessMail.Api.Rules;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class RuleMatcherTests
{
    private static RuleMessage Message(
        string from = "Alice Example <alice@example.com>", string to = "me@example.org", string? cc = null,
        string subject = "Quarterly report", long size = 10_000, bool attachments = false,
        Dictionary<string, string[]>? headers = null) => new(
        [MailboxAddress.Parse(from)],
        [MailboxAddress.Parse(to)],
        cc is null ? [] : [MailboxAddress.Parse(cc)],
        subject,
        name => headers?.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? [],
        size,
        attachments);

    private static CompiledRule Rule(bool matchAll, params RuleCondition[] conditions) => new(
        new MailRule { OwnerUserId = "alice", Name = "r", MatchAll = matchAll, Conditions = "", Actions = "" },
        conditions,
        [new RuleAction(RuleActionTypes.Flag, null)]);

    private static bool Matches(RuleCondition condition, RuleMessage message) => Rule(true, condition).Matches(message);

    [Theory]
    [InlineData(RuleOps.Contains, "example.com", true)]
    [InlineData(RuleOps.Contains, "ALICE", true)]
    [InlineData(RuleOps.Is, "alice@example.com", true)]
    [InlineData(RuleOps.Is, "Alice Example", true)]
    [InlineData(RuleOps.Is, "example.com", false)]
    [InlineData(RuleOps.IsNot, "bob@example.com", true)]
    [InlineData(RuleOps.IsNot, "alice@EXAMPLE.com", false)]
    [InlineData(RuleOps.StartsWith, "alice@", true)]
    [InlineData(RuleOps.EndsWith, "@example.com", true)]
    [InlineData(RuleOps.EndsWith, ".org", false)]
    [InlineData(RuleOps.NotContains, "example.com", false)]
    [InlineData(RuleOps.NotContains, "newsletter", true)]
    [InlineData(RuleOps.Matches, "^alice@.*\\.com$", true)]
    [InlineData(RuleOps.Matches, "^bob", false)]
    public void Address_conditions(string op, string value, bool expected) =>
        Assert.Equal(expected, Matches(new RuleCondition(RuleFields.From, null, op, value), Message()));

    [Fact]
    public void Recipient_covers_to_and_cc()
    {
        var condition = new RuleCondition(RuleFields.Recipient, null, RuleOps.Is, "team@example.org");
        Assert.True(Matches(condition, Message(cc: "team@example.org")));
        Assert.False(Matches(condition with { Field = RuleFields.To }, Message(cc: "team@example.org")));
    }

    [Fact]
    public void Negations_hold_for_a_missing_field()
    {
        Assert.True(Matches(new RuleCondition(RuleFields.Cc, null, RuleOps.NotContains, "x"), Message()));
        Assert.True(Matches(new RuleCondition(RuleFields.Header, "List-Id", RuleOps.IsNot, "x"), Message()));
        Assert.False(Matches(new RuleCondition(RuleFields.Header, "List-Id", RuleOps.Contains, "x"), Message()));
    }

    [Fact]
    public void Headers_match_any_of_their_values_by_name_in_any_case()
    {
        var message = Message(headers: new() { ["List-Id"] = ["Other <other.example>", "News <news.example.com>"] });
        Assert.True(Matches(new RuleCondition(RuleFields.Header, "list-id", RuleOps.Contains, "news.example.com"), message));
    }

    [Fact]
    public void Size_and_attachments()
    {
        Assert.True(Matches(new RuleCondition(RuleFields.Size, null, RuleOps.GreaterThan, "5000"), Message(size: 6000)));
        Assert.False(Matches(new RuleCondition(RuleFields.Size, null, RuleOps.LessThan, "5000"), Message(size: 6000)));
        Assert.True(Matches(new RuleCondition(RuleFields.Attachment, null, RuleOps.Has, null), Message(attachments: true)));
        Assert.True(Matches(new RuleCondition(RuleFields.Attachment, null, RuleOps.HasNot, null), Message()));
    }

    [Fact]
    public void All_or_any()
    {
        var subject = new RuleCondition(RuleFields.Subject, null, RuleOps.Contains, "report");
        var from = new RuleCondition(RuleFields.From, null, RuleOps.Contains, "bob");
        Assert.False(Rule(true, subject, from).Matches(Message()));
        Assert.True(Rule(false, subject, from).Matches(Message()));
    }

    [Fact]
    public void A_runaway_regex_does_not_match()
    {
        var condition = new RuleCondition(RuleFields.Subject, null, RuleOps.Matches, "^(a+)+$");
        Assert.False(Matches(condition, Message(subject: new string('a', 5000) + "!")));
    }
}

public sealed class RuleEndpointTests : IDisposable
{
    private static readonly string Archive = "QXJjaGl2ZQ";

    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public RuleEndpointTests() => _alice = _app.ClientAs("alice");

    public void Dispose() => _app.Dispose();

    private static object Rule(string name = "Newsletters", bool enabled = true, Guid? id = null, object? condition = null, object[]? actions = null) => new
    {
        id,
        name,
        enabled,
        matchAll = true,
        stop = false,
        conditions = new[] { condition ?? new { field = "from", op = "contains", value = "news@" } },
        actions = actions ?? [new { type = "move", folderId = Archive }],
    };

    private Task<HttpResponseMessage> PutAsync(Guid account, params object[] rules) =>
        _alice.PutAsJsonAsync($"/api/accounts/{account}/rules", new { rules }, Ct);

    private async Task<JsonElement> RulesAsync(Guid account) =>
        (await JsonAsync(await _alice.GetAsync($"/api/accounts/{account}/rules", Ct))).GetProperty("rules");

    [Fact]
    public async Task Saved_rules_are_read_back_in_order()
    {
        var account = await CreateAsync(_alice);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(account, Rule("First"), Rule("Second", enabled: false))).StatusCode);

        var rules = await RulesAsync(account);
        Assert.Equal(["First", "Second"], rules.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var first = rules[0];
        Assert.True(first.GetProperty("enabled").GetBoolean());
        Assert.Equal("news@", first.GetProperty("conditions")[0].GetProperty("value").GetString());
        Assert.Equal(Archive, first.GetProperty("actions")[0].GetProperty("folderId").GetString());
        Assert.False(rules[1].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Rules_keep_their_ids_when_reordered()
    {
        var account = await CreateAsync(_alice);
        await PutAsync(account, Rule("First"), Rule("Second"));
        var ids = (await RulesAsync(account)).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

        await PutAsync(account, Rule("Second", id: ids[1]), Rule("First", id: ids[0]));
        Assert.Equal([ids[1], ids[0]], (await RulesAsync(account)).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_cursor_exists_only_while_a_rule_is_enabled()
    {
        var account = await CreateAsync(_alice);
        await PutAsync(account, Rule());
        Assert.True(await HasCursorAsync(account));

        await PutAsync(account, Rule(enabled: false));
        Assert.False(await HasCursorAsync(account));
    }

    private async Task<bool> HasCursorAsync(Guid account)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MailDbContext>().RuleCursors.IgnoreQueryFilters()
            .AnyAsync(c => c.AccountId == account, Ct);
    }

    public static TheoryData<string, object> Invalid() => new()
    {
        { "rules[0].name", new { name = " ", enabled = true, conditions = new[] { new { field = "subject", op = "contains", value = "x" } }, actions = new[] { new { type = "flag" } } } },
        { "rules[0].conditions", new { name = "r", enabled = true, conditions = Array.Empty<object>(), actions = new[] { new { type = "flag" } } } },
        { "rules[0].actions", new { name = "r", enabled = true, conditions = new[] { new { field = "subject", op = "contains", value = "x" } }, actions = Array.Empty<object>() } },
        { "rules[0].conditions[0].field", Rule(condition: new { field = "body", op = "contains", value = "x" }) },
        { "rules[0].conditions[0].op", Rule(condition: new { field = "subject", op = "greaterThan", value = "x" }) },
        { "rules[0].conditions[0].value", Rule(condition: new { field = "subject", op = "contains", value = "  " }) },
        { "rules[0].conditions[0].value", Rule(condition: new { field = "subject", op = "matches", value = "(" }) },
        { "rules[0].conditions[0].value", Rule(condition: new { field = "size", op = "greaterThan", value = "big" }) },
        { "rules[0].conditions[0].header", Rule(condition: new { field = "header", header = "List Id:", op = "contains", value = "x" }) },
        { "rules[0].actions[0].folderId", Rule(actions: [new { type = "move", folderId = "SU5CT1g" }]) },
        { "rules[0].actions[0].folderId", Rule(actions: [new { type = "copy" }]) },
        { "rules[0].actions", Rule(actions: [new { type = "move", folderId = Archive }, new { type = "trash" }]) },
        { "rules[0].actions[1]", Rule(actions: [new { type = "flag" }, new { type = "flag" }]) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Invalid_rules_are_refused(string field, object rule)
    {
        var account = await CreateAsync(_alice);
        var response = await PutAsync(account, rule);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Another_users_rules_are_not_found()
    {
        var account = await CreateAsync(_alice);
        await PutAsync(account, Rule());
        var bob = _app.ClientAs("bob");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/accounts/{account}/rules", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/accounts/{account}/rules", new { rules = Array.Empty<object>() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/accounts/{account}/rules/test", new { rule = Rule() }, Ct)).StatusCode);
        var ruleId = (await RulesAsync(account))[0].GetProperty("id").GetGuid();
        foreach (var action in new[] { "preview", "run" })
        {
            var response = await bob.PostAsJsonAsync($"/api/accounts/{account}/rules/{ruleId}/{action}", new { folderId = "SU5CT1g", messageIds = new[] { "1-1" } }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Single((await RulesAsync(account)).EnumerateArray());
    }

    [Fact]
    public async Task Rules_go_with_the_account()
    {
        var account = await CreateAsync(_alice);
        await PutAsync(account, Rule());
        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/accounts/{account}", Ct)).StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        Assert.False(await db.Rules.IgnoreQueryFilters().AnyAsync(Ct));
        Assert.False(await db.RuleCursors.IgnoreQueryFilters().AnyAsync(Ct));
    }
}
