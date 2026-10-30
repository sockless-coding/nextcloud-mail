using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class ViewStateTests : IDisposable
{
    private const string Inbox = "SU5CT1g";
    private const string Archive = "QXJjaGl2ZQ";

    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ViewStateTests() => _alice = _app.ClientAs("alice");

    public void Dispose() => _app.Dispose();

    private async Task<JsonElement> GetAsync(HttpClient client) => await JsonAsync(await client.GetAsync("/api/view-state", Ct));

    private Task<HttpResponseMessage> PutAsync(HttpClient client, object body) => client.PutAsJsonAsync("/api/view-state", body, Ct);

    private static object State(Guid accountId, params string[] expanded) => new
    {
        lastView = new { accountId, folderId = Archive },
        accounts = new Dictionary<string, object> { [accountId.ToString()] = new { expanded, lastFolderId = Archive } },
    };

    [Fact]
    public async Task Starts_on_the_server_without_state()
    {
        var state = await GetAsync(_alice);
        Assert.Equal("server", state.GetProperty("storage").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("state").ValueKind);
    }

    [Fact]
    public async Task Saved_state_is_read_back()
    {
        var id = await CreateAsync(_alice);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(_alice, new { storage = "server", state = State(id, Archive, Inbox) })).StatusCode);

        var state = (await GetAsync(_alice)).GetProperty("state");
        Assert.Equal(id, state.GetProperty("lastView").GetProperty("accountId").GetGuid());
        Assert.Equal(Archive, state.GetProperty("lastView").GetProperty("folderId").GetString());
        var account = state.GetProperty("accounts").GetProperty(id.ToString());
        Assert.Equal([Archive, Inbox], account.GetProperty("expanded").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Archive, account.GetProperty("lastFolderId").GetString());
    }

    [Fact]
    public async Task A_unified_view_is_remembered()
    {
        await PutAsync(_alice, new { storage = "server", state = new { lastView = new { unified = "flagged" } } });
        Assert.Equal("flagged", (await GetAsync(_alice)).GetProperty("state").GetProperty("lastView").GetProperty("unified").GetString());
    }

    [Fact]
    public async Task Browser_storage_forgets_the_state_but_keeps_the_choice()
    {
        var id = await CreateAsync(_alice);
        await PutAsync(_alice, new { storage = "server", state = State(id, Archive) });

        await PutAsync(_alice, new { storage = "browser", state = State(id, Archive) });

        var state = await GetAsync(_alice);
        Assert.Equal("browser", state.GetProperty("storage").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("state").ValueKind);
        using var db = _app.CreateDbContext(null);
        Assert.Empty(db.AccountViewStates.IgnoreQueryFilters());
        Assert.Null(db.ViewSettings.IgnoreQueryFilters().Single().LastView);
    }

    [Fact]
    public async Task Deleting_the_account_deletes_its_state()
    {
        var id = await CreateAsync(_alice);
        await PutAsync(_alice, new { storage = "server", state = State(id, Archive) });

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/accounts/{id}", Ct)).StatusCode);

        using var db = _app.CreateDbContext(null);
        Assert.Empty(db.AccountViewStates.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Other_users_accounts_are_skipped_and_state_is_per_user()
    {
        var bob = _app.ClientAs("bob");
        var bobs = await CreateAsync(bob);
        await PutAsync(bob, new { storage = "server", state = State(bobs, Inbox) });

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(_alice, new { storage = "server", state = State(bobs, Archive) })).StatusCode);

        Assert.Empty((await GetAsync(_alice)).GetProperty("state").GetProperty("accounts").EnumerateObject());
        var bobsAccount = (await GetAsync(bob)).GetProperty("state").GetProperty("accounts").GetProperty(bobs.ToString());
        Assert.Equal([Inbox], bobsAccount.GetProperty("expanded").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        var id = await CreateAsync(_alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(_alice, new { storage = "cloud" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(_alice, new { storage = "server", state = State(id, "not/a/folder") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(_alice, new { storage = "server", state = new { lastView = new { accountId = id } } })).StatusCode);
        var tooMany = Enumerable.Range(0, 2001).Select(i => $"F{i}").ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(_alice, new { storage = "server", state = State(id, tooMany) })).StatusCode);
    }
}
