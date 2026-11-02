using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.Data;
using static SocklessMail.Tests.AccountTestData;
using static SocklessMail.Tests.CryptoTestKeys;

namespace SocklessMail.Tests;

/// <summary>The key API: import, make, unlock, export, delete; and one user never reaching another's keys.</summary>
public sealed class CryptoKeyEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private readonly HttpClient _bob;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public CryptoKeyEndpointTests()
    {
        _alice = _app.ClientAs("alice");
        _bob = _app.ClientAs("bob");
    }

    public void Dispose() => _app.Dispose();

    private async Task<JsonElement> ImportAsync(HttpClient client, object input)
    {
        var response = await client.PostAsJsonAsync("/api/crypto/keys/import", input, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    [Fact]
    public async Task Imported_public_key_is_listed_and_exported()
    {
        var result = await ImportAsync(_alice, new { text = ArmoredPublic(BobPgp) });
        var key = Assert.Single(result.GetProperty("added").EnumerateArray());
        Assert.Equal("openpgp", key.GetProperty("kind").GetString());
        Assert.False(key.GetProperty("own").GetBoolean());
        Assert.Equal("bob@example.org", key.GetProperty("addresses")[0].GetString());
        Assert.Equal("Bob", key.GetProperty("name").GetString());
        Assert.Equal(40, key.GetProperty("fingerprint").GetString()!.Length);

        var list = await JsonAsync(await _alice.GetAsync("/api/crypto/keys", Ct));
        Assert.Single(list.EnumerateArray());

        var export = await _alice.GetAsync($"/api/crypto/keys/{key.GetProperty("id").GetGuid()}/public", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/pgp-keys", export.Content.Headers.ContentType?.MediaType);
        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", await export.Content.ReadAsStringAsync(Ct));
        Assert.Contains("bob@example.org.asc", export.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task Generated_key_is_for_an_own_address_and_starts_unlocked()
    {
        var notMine = await _alice.PostAsJsonAsync("/api/crypto/keys/generate", new { name = "Alice", address = "ceo@example.com", passphrase = Passphrase }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);

        await CreateAsync(_alice);
        var weak = await _alice.PostAsJsonAsync("/api/crypto/keys/generate", new { name = "Alice", address = "alice@example.com", passphrase = "short" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var response = await _alice.PostAsJsonAsync("/api/crypto/keys/generate", new { name = "Alice", address = "Alice@Example.com", passphrase = Passphrase, validYears = 2 }, Ct);
        var key = await JsonAsync(response);
        Assert.True(key.GetProperty("own").GetBoolean());
        Assert.True(key.GetProperty("hasPassphrase").GetBoolean());
        Assert.Equal(JsonValueKind.String, key.GetProperty("unlockedUntil").ValueKind);
        Assert.Equal(JsonValueKind.String, key.GetProperty("expiresAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.PostAsync("/api/crypto/keys/lock", null, Ct)).StatusCode);
        var locked = (await JsonAsync(await _alice.GetAsync("/api/crypto/keys", Ct)))[0];
        Assert.Equal(JsonValueKind.Null, locked.GetProperty("unlockedUntil").ValueKind);
    }

    [Fact]
    public async Task Unlocking_checks_the_passphrase()
    {
        var key = (await ImportAsync(_alice, new { text = ArmoredSecret(AlicePgp), passphrase = Passphrase })).GetProperty("added")[0];
        var id = key.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, key.GetProperty("unlockedUntil").ValueKind);

        var wrong = await _alice.PostAsJsonAsync($"/api/crypto/keys/{id}/unlock", new { passphrase = "nope" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var right = await JsonAsync(await _alice.PostAsJsonAsync($"/api/crypto/keys/{id}/unlock", new { passphrase = Passphrase }, Ct));
        Assert.Equal(JsonValueKind.String, right.GetProperty("unlockedUntil").ValueKind);
    }

    [Fact]
    public async Task Pkcs12_import_and_certificate_export()
    {
        var key = (await ImportAsync(_alice, new { data = Convert.ToBase64String(AliceSmime), passphrase = Passphrase })).GetProperty("added")[0];
        Assert.Equal("smime", key.GetProperty("kind").GetString());
        Assert.True(key.GetProperty("own").GetBoolean());

        var export = await _alice.GetAsync($"/api/crypto/keys/{key.GetProperty("id").GetGuid()}/public", Ct);
        var pem = await export.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.DoesNotContain("PRIVATE", pem);
    }

    [Fact]
    public async Task Other_users_keys_answer_like_missing_ones()
    {
        var id = (await ImportAsync(_alice, new { text = ArmoredSecret(AlicePgp), passphrase = Passphrase })).GetProperty("added")[0].GetProperty("id").GetGuid();

        Assert.Empty((await JsonAsync(await _bob.GetAsync("/api/crypto/keys", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.GetAsync($"/api/crypto/keys/{id}/public", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.PostAsJsonAsync($"/api/crypto/keys/{id}/unlock", new { passphrase = Passphrase }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.DeleteAsync($"/api/crypto/keys/{id}", Ct)).StatusCode);

        // Bob may keep the same key himself: keys are per user.
        await ImportAsync(_bob, new { text = ArmoredPublic(AlicePgp) });
        Assert.Single((await JsonAsync(await _alice.GetAsync("/api/crypto/keys", Ct))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/crypto/keys/{id}", Ct)).StatusCode);
        Assert.Empty((await JsonAsync(await _alice.GetAsync("/api/crypto/keys", Ct))).EnumerateArray());
        Assert.Single((await JsonAsync(await _bob.GetAsync("/api/crypto/keys", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Private_keys_are_stored_encrypted()
    {
        var id = (await ImportAsync(_alice, new { text = ArmoredSecret(AlicePgp), passphrase = Passphrase })).GetProperty("added")[0].GetProperty("id").GetGuid();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var row = await db.CryptoKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == id, Ct);
        Assert.NotNull(row.ProtectedSecret);
        Assert.DoesNotContain(Convert.ToBase64String(AlicePgp.GetEncoded())[..40], row.ProtectedSecret);
    }
}
