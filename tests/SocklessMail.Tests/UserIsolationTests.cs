using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Sync;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

/// <summary>
/// A user must never see or touch another user's accounts. Every account endpoint is tried with
/// bob against alice's account; each must look exactly like a missing account and leave it unchanged.
/// </summary>
public sealed class UserIsolationTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private readonly HttpClient _bob;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public UserIsolationTests()
    {
        _alice = _app.ClientAs("alice");
        _bob = _app.ClientAs("bob");
    }

    public void Dispose() => _app.Dispose();

    private async Task<string> AliceAccountJson(Guid id) =>
        await (await _alice.GetAsync($"/api/accounts/{id}", Ct)).Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task Other_users_accounts_are_not_listed()
    {
        await CreateAsync(_alice);
        var bobsId = await CreateAsync(_bob, "Bob's");

        var list = await JsonAsync(await _bob.GetAsync("/api/accounts", Ct));
        Assert.Equal([bobsId], list.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
    }

    public static TheoryData<string> Requests() =>
    [
        "GET /api/accounts/{id}",
        "PUT /api/accounts/{id}",
        "DELETE /api/accounts/{id}",
        "POST /api/accounts/{id}/test",
        "POST /api/accounts/test (accountId)",
    ];

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Other_users_account_answers_like_a_missing_one(string request)
    {
        var aliceId = await CreateAsync(_alice);
        var before = await AliceAccountJson(aliceId);

        async Task<HttpResponseMessage> Send(HttpClient client, Guid id) => request switch
        {
            "GET /api/accounts/{id}" => await client.GetAsync($"/api/accounts/{id}", Ct),
            "PUT /api/accounts/{id}" => await client.PutAsJsonAsync($"/api/accounts/{id}", Input("Hijacked", "bobs password"), Ct),
            "DELETE /api/accounts/{id}" => await client.DeleteAsync($"/api/accounts/{id}", Ct),
            "POST /api/accounts/{id}/test" => await client.PostAsync($"/api/accounts/{id}/test", null, Ct),
            _ => await client.PostAsJsonAsync("/api/accounts/test", Input(password: null, accountId: id), Ct),
        };

        var foreign = await Send(_bob, aliceId);
        var missing = await Send(_bob, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await foreign.Content.ReadAsStringAsync(Ct));
        Assert.Equal(before, await AliceAccountJson(aliceId));
    }

    [Theory]
    [InlineData("folders")]
    [InlineData("folders/SU5CT1g/messages")]
    [InlineData("folders/SU5CT1g/changes?since=0")]
    [InlineData("folders/SU5CT1g/search?q=secret")]
    [InlineData("folders/SU5CT1g/messages/1-1")]
    [InlineData("folders/SU5CT1g/messages/1-1/parts/2")]
    public async Task Other_users_mailbox_answers_like_a_missing_one(string path)
    {
        var aliceId = await CreateAsync(_alice);

        var foreign = await _bob.GetAsync($"/api/accounts/{aliceId}/{path}", Ct);
        var missing = await _bob.GetAsync($"/api/accounts/{Guid.NewGuid()}/{path}", Ct);

        // 404 before any IMAP connection is attempted (the account points at an unroutable address).
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await foreign.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("PUT", "folders/SU5CT1g/messages/flags", """{"messageIds":["1-1"],"seen":true}""")]
    [InlineData("POST", "folders/SU5CT1g/messages/move", """{"messageIds":["1-1"],"targetFolderId":"VHJhc2g"}""")]
    [InlineData("POST", "folders/SU5CT1g/messages/delete", """{"messageIds":["1-1"],"permanent":true}""")]
    [InlineData("POST", "folders", """{"name":"Planted"}""")]
    [InlineData("PUT", "folders/VHJhc2g", """{"name":"Renamed"}""")]
    public async Task Other_users_mailbox_cannot_be_changed(string method, string path, string body)
    {
        var aliceId = await CreateAsync(_alice);

        HttpRequestMessage Request(Guid id) =>
            new(new HttpMethod(method), $"/api/accounts/{id}/{path}") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        var foreign = await _bob.SendAsync(Request(aliceId), Ct);
        var missing = await _bob.SendAsync(Request(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await foreign.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Unified_inbox_holds_only_the_users_own_accounts()
    {
        var aliceId = await CreateAsync(_alice);

        // Bob has no accounts: an empty list, without trying alice's (unroutable) server.
        var page = await JsonAsync(await _bob.GetAsync("/api/unified/messages", Ct));
        Assert.Empty(page.GetProperty("messages").EnumerateArray());
        Assert.Empty(page.GetProperty("errors").EnumerateArray());
        Assert.Equal("", page.GetProperty("token").GetString());

        // Alice's token is not bob's to poll: his inboxes do not match it, so it only resets.
        var changes = await JsonAsync(await _bob.GetAsync($"/api/unified/changes?since={aliceId:N}.1", Ct));
        Assert.True(changes.GetProperty("reset").GetBoolean());
        Assert.Empty(changes.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Reorder_cannot_include_another_users_account()
    {
        var aliceId = await CreateAsync(_alice);
        var bobsId = await CreateAsync(_bob);

        var response = await _bob.PutAsJsonAsync("/api/accounts/order", new { accountIds = new[] { aliceId, bobsId } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_be_set_through_the_request_body()
    {
        var response = await _bob.PostAsJsonAsync("/api/accounts", new
        {
            ownerUserId = "alice",
            displayName = "Planted",
            emailAddress = "bob@example.com",
            username = "bob",
            password = "x",
            imap = new { host = "imap.example.com", port = 993, security = "sslTls" },
            smtp = new { host = "smtp.example.com", port = 465, security = "sslTls" },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Empty((await JsonAsync(await _alice.GetAsync("/api/accounts", Ct))).EnumerateArray());
        Assert.Single((await JsonAsync(await _bob.GetAsync("/api/accounts", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Database_filter_hides_other_users_rows_even_for_unscoped_queries()
    {
        await CreateAsync(_alice);
        await CreateAsync(_bob);

        await using (var asBob = _app.CreateDbContext("bob"))
        {
            // Deliberately no owner condition: the query filter must still apply.
            Assert.All(await asBob.Accounts.ToListAsync(Ct), a => Assert.Equal("bob", a.OwnerUserId));
        }

        await using (var noUser = _app.CreateDbContext(null))
        {
            Assert.Empty(await noUser.Accounts.ToListAsync(Ct));
            Assert.Equal(2, await noUser.Accounts.IgnoreQueryFilters().CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Cached_mail_is_filtered_by_owner()
    {
        var aliceId = await CreateAsync(_alice);
        var folderId = Guid.NewGuid();
        await using (var asAlice = _app.CreateDbContext("alice"))
        {
            asAlice.Folders.Add(new CachedFolder { Id = folderId, AccountId = aliceId, OwnerUserId = "alice", FullName = "INBOX" });
            asAlice.Messages.Add(new CachedMessage { FolderId = folderId, Uid = 1, OwnerUserId = "alice", Subject = "Secret", From = [], To = [], SearchText = "secret" });
            asAlice.RemovedMessages.Add(new RemovedMessage { FolderId = folderId, Uid = 2, OwnerUserId = "alice" });
            await asAlice.SaveChangesAsync(Ct);
        }

        // Bob's search across "all" folders covers only his own.
        var bobResponse = await _bob.GetAsync("/api/search?q=secret", Ct);
        Assert.True(bobResponse.IsSuccessStatusCode, await bobResponse.Content.ReadAsStringAsync(Ct));
        var search = await JsonAsync(bobResponse);
        Assert.Empty(search.GetProperty("hits").EnumerateArray());
        var own = await JsonAsync(await _alice.GetAsync("/api/search?q=secret", Ct));
        Assert.Single(own.GetProperty("hits").EnumerateArray());

        foreach (var user in new[] { "bob", null })
        {
            await using var db = _app.CreateDbContext(user);
            Assert.Empty(await db.Folders.ToListAsync(Ct));
            Assert.Empty(await db.Messages.ToListAsync(Ct));
            Assert.Empty(await db.RemovedMessages.ToListAsync(Ct));
        }

        // Deleting the account takes its cache with it.
        await _alice.DeleteAsync($"/api/accounts/{aliceId}", Ct);
        await using (var noUser = _app.CreateDbContext(null))
        {
            Assert.Equal(0, await noUser.Messages.IgnoreQueryFilters().CountAsync(Ct));
            Assert.Equal(0, await noUser.RemovedMessages.IgnoreQueryFilters().CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Credential_ciphertext_is_bound_to_its_account()
    {
        var aliceId = await CreateAsync(_alice);
        var bobsId = await CreateAsync(_bob);

        // Simulate a row-level tampering: copy alice's encrypted credential into bob's account.
        await using (var db = _app.CreateDbContext(null))
        {
            var alice = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == aliceId, Ct);
            var bob = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == bobsId, Ct);
            bob.ProtectedCredential = alice.ProtectedCredential;
            await db.SaveChangesAsync(Ct);
        }

        var result = await JsonAsync(await _bob.PostAsync($"/api/accounts/{bobsId}/test", null, Ct));
        Assert.Equal("credential_unavailable", result.GetProperty("imap").GetProperty("errorCode").GetString());
    }
}
