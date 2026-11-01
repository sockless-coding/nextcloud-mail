using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.Accounts;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class AccountEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public AccountEndpointTests() => _alice = _app.ClientAs("alice");

    public void Dispose() => _app.Dispose();

    private MailAccount StoredAccount(Guid id)
    {
        using var db = _app.CreateDbContext(null);
        return db.Accounts.IgnoreQueryFilters().AsNoTracking().Single(a => a.Id == id);
    }

    private string StoredPassword(Guid id)
    {
        var protector = _app.Services.GetRequiredService<CredentialProtector>();
        var account = StoredAccount(id);
        return Assert.IsType<PasswordCredential>(protector.Unprotect(id, account.ProtectedCredential)).Password;
    }

    [Fact]
    public async Task Create_returns_the_account_without_its_credential()
    {
        var response = await _alice.PostAsJsonAsync("/api/accounts", Input(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Password, raw);
        Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);

        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Work", body.GetProperty("displayName").GetString());
        Assert.Equal("sslTls", body.GetProperty("imap").GetProperty("security").GetString());
        Assert.Equal("password", body.GetProperty("authMethod").GetString());

        var list = await JsonAsync(await _alice.GetAsync("/api/accounts", Ct));
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Credential_is_encrypted_at_rest()
    {
        var id = await CreateAsync(_alice);

        var stored = StoredAccount(id);
        Assert.DoesNotContain(Password, stored.ProtectedCredential);
        Assert.Equal(Password, StoredPassword(id));

        // Neither the database file nor the key ring contains the password or an unwrapped key.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_app.StorageDirectory, "*", SearchOption.AllDirectories))
        {
            var content = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct));
            Assert.DoesNotContain(Password, content);
            Assert.DoesNotContain("<masterKey", content);
        }
    }

    [Fact]
    public async Task Validation_errors_name_the_fields()
    {
        var response = await _alice.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "",
            emailAddress = "not-an-address",
            username = "u",
            password = "",
            imap = new { host = "imaps://mail.example.com", port = 0, security = "sslTls" },
            smtp = new { host = "smtp.example.com", port = 587, security = "startTls" },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "displayName", "emailAddress", "password", "imap.host", "imap.port" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"missing error for {field}");
        }
        Assert.False(errors.TryGetProperty("smtp.host", out _));
    }

    [Fact]
    public async Task Unknown_security_value_is_rejected()
    {
        var response = await _alice.PostAsJsonAsync("/api/accounts", new
        {
            displayName = "x", emailAddress = "a@example.com", username = "a", password = "p",
            imap = new { host = "h", port = 993, security = "magic" },
            smtp = new { host = "h", port = 587, security = "startTls" },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_without_password_keeps_the_stored_credential()
    {
        var id = await CreateAsync(_alice);

        var response = await _alice.PutAsJsonAsync($"/api/accounts/{id}", Input("Renamed", password: null), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await JsonAsync(response)).GetProperty("displayName").GetString());
        Assert.Equal(Password, StoredPassword(id));
    }

    [Fact]
    public async Task Update_with_password_replaces_the_credential()
    {
        var id = await CreateAsync(_alice);
        await _alice.PutAsJsonAsync($"/api/accounts/{id}", Input(password: "new secret"), Ct);
        Assert.Equal("new secret", StoredPassword(id));
    }

    [Fact]
    public async Task Delete_removes_the_account()
    {
        var id = await CreateAsync(_alice);

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/accounts/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync($"/api/accounts/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.DeleteAsync($"/api/accounts/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Accounts_are_listed_in_creation_order_and_can_be_reordered()
    {
        var a = await CreateAsync(_alice, "A");
        var b = await CreateAsync(_alice, "B");
        var c = await CreateAsync(_alice, "C");

        async Task<string[]> Names() => (await JsonAsync(await _alice.GetAsync("/api/accounts", Ct)))
            .EnumerateArray().Select(x => x.GetProperty("displayName").GetString()!).ToArray();

        Assert.Equal(["A", "B", "C"], await Names());

        var response = await _alice.PutAsJsonAsync("/api/accounts/order", new { accountIds = new[] { c, a, b } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["C", "A", "B"], await Names());
    }

    [Fact]
    public async Task Appearance_is_saved_and_cleared_without_touching_the_settings()
    {
        var id = await CreateAsync(_alice);
        var before = StoredAccount(id);
        Assert.True(before.IncludeInUnified);

        var response = await _alice.PutAsJsonAsync($"/api/accounts/{id}/appearance",
            new { color = "#1A62D6", badgeText = " 💼 ", badgeIcon = (string?)null, includeInUnified = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("#1a62d6", json.GetProperty("color").GetString());
        Assert.Equal("💼", json.GetProperty("badgeText").GetString());
        Assert.False(json.GetProperty("includeInUnified").GetBoolean());
        Assert.Equal(before.UpdatedAt, StoredAccount(id).UpdatedAt);

        response = await _alice.PutAsJsonAsync($"/api/accounts/{id}/appearance",
            new { color = "", badgeText = "", badgeIcon = "briefcase", includeInUnified = true }, Ct);
        json = await JsonAsync(response);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("color").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("badgeText").ValueKind);
        Assert.Equal("briefcase", json.GetProperty("badgeIcon").GetString());
        Assert.True(json.GetProperty("includeInUnified").GetBoolean());
    }

    [Theory]
    [InlineData("color", "blue")]
    [InlineData("color", "#12345")]
    [InlineData("badgeText", "ABCD")]
    [InlineData("badgeIcon", "not-an-icon")]
    public async Task Invalid_appearance_is_refused(string field, string value)
    {
        var id = await CreateAsync(_alice);
        var body = new Dictionary<string, object?> { ["color"] = null, ["badgeText"] = null, ["badgeIcon"] = null, ["includeInUnified"] = true, [field] = value };

        var response = await _alice.PutAsJsonAsync($"/api/accounts/{id}/appearance", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Reorder_requires_exactly_the_users_accounts(string variant)
    {
        var a = await CreateAsync(_alice, "A");
        var b = await CreateAsync(_alice, "B");
        Guid[] ids = variant switch
        {
            "missing" => [a],
            "duplicate" => [a, a],
            _ => [a, Guid.NewGuid()],
        };

        var response = await _alice.PutAsJsonAsync("/api/accounts/order", new { accountIds = ids }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var order = (await JsonAsync(await _alice.GetAsync("/api/accounts", Ct))).EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid());
        Assert.Equal([a, b], order);
    }

    [Fact]
    public async Task Account_limit_is_enforced()
    {
        for (var i = 0; i < AccountService.MaxAccountsPerUser; i++)
        {
            await CreateAsync(_alice, $"Account {i}");
        }

        var response = await _alice.PostAsJsonAsync("/api/accounts", Input(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_limit", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Connection_test_refuses_local_addresses_by_default()
    {
        var response = await _alice.PostAsJsonAsync("/api/accounts/test", Input(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("address_not_allowed", body.GetProperty("imap").GetProperty("errorCode").GetString());
        Assert.Equal("address_not_allowed", body.GetProperty("smtp").GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Connection_test_of_a_stored_account_decrypts_its_credential()
    {
        var id = await CreateAsync(_alice);

        // Saved settings, no password in the request: the stored credential is used. It got past
        // decryption, because credential_unavailable would be reported first.
        var stored = await JsonAsync(await _alice.PostAsync($"/api/accounts/{id}/test", null, Ct));
        Assert.Equal("address_not_allowed", stored.GetProperty("imap").GetProperty("errorCode").GetString());

        var edited = await JsonAsync(await _alice.PostAsJsonAsync("/api/accounts/test", Input(password: null, accountId: id), Ct));
        Assert.Equal("address_not_allowed", edited.GetProperty("imap").GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Connection_test_without_password_or_account_is_invalid()
    {
        var response = await _alice.PostAsJsonAsync("/api/accounts/test", Input(password: null), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Connection_tests_are_rate_limited_per_user()
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _alice.PostAsJsonAsync("/api/accounts/test", Input(), Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _alice.PostAsJsonAsync("/api/accounts/test", Input(), Ct)).StatusCode);
        // Another user has their own budget.
        Assert.Equal(HttpStatusCode.OK, (await _app.ClientAs("bob").PostAsJsonAsync("/api/accounts/test", Input(), Ct)).StatusCode);
    }
}
