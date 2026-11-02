using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Contacts;
using SocklessMail.Api.Crypto;
using SocklessMail.Api.Data;
using static SocklessMail.Tests.CryptoTestKeys;

namespace SocklessMail.Tests;

/// <summary>Autocrypt headers both ways, keys saved into contacts, and publishing on keys.openpgp.org.</summary>
public sealed class KeySharingTests : IDisposable
{
    private const string User = "alice";
    private readonly FakeKeyServer _keyServer = new();
    private readonly FakeCardDav _carddav = new();
    private readonly TestApp _app;
    private readonly HttpClient _client;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public KeySharingTests()
    {
        _app = new TestApp(services: services =>
            services.AddHttpClient(PublicKeyFinder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _keyServer));
        _app.Nextcloud.Respond = _carddav.Respond;
        _carddav.AddBook(User, "contacts", "Contacts");
        _client = _app.ClientAs(User);
    }

    public void Dispose() => _app.Dispose();

    private async Task<T> WithUser<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(User);
        return await action(scope.ServiceProvider);
    }

    private Task<ImportKeyResult> ImportAsync(string text, string? passphrase = null) =>
        WithUser(s => s.GetRequiredService<CryptoKeyStore>().ImportAsync(User, new ImportKeyInput(text, null, passphrase), Ct));

    private Task<IReadOnlyList<CryptoKeyDto>> KeysAsync() => WithUser(s => s.GetRequiredService<CryptoKeyStore>().ListAsync(User, Ct));

    private static string Autocrypt(string address, byte[] key) => $"addr={address}; keydata={Convert.ToBase64String(key)}";

    private Task<CryptoKey?> TakeAsync(string header, string from = "bob@example.org", bool dmarcFailed = false) =>
        WithUser(s => s.GetRequiredService<KeySharing>().TakeAutocryptAsync(User, header, InternetAddressList.Parse(from), dmarcFailed, Ct));

    [Fact]
    public async Task Outgoing_mail_carries_the_senders_key_in_an_Autocrypt_header()
    {
        await ImportAsync(ArmoredSecret(AlicePgp), Passphrase);
        var message = new MimeMessage { Subject = "Hi", Body = new TextPart("plain") { Text = "Hello" } };
        message.From.Add(MailboxAddress.Parse("alice@example.com"));
        await WithUser(async s =>
        {
            await s.GetRequiredService<KeySharing>().AddAutocryptAsync(User, message, Ct);
            return 0;
        });

        // Written and read back, as a recipient's program sees it.
        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, Ct);
        var raw = Encoding.ASCII.GetString(buffer.ToArray());
        Assert.All(raw.Split("\r\n").TakeWhile(l => l.Length > 0), line => Assert.True(line.Length <= 998));
        buffer.Position = 0;
        var parsed = KeySharing.ParseAutocrypt((await MimeMessage.LoadAsync(buffer, Ct)).Headers[KeySharing.AutocryptHeader]!)!.Value;
        Assert.Equal("alice@example.com", parsed.Address);
        Assert.Equal(Convert.ToHexString(AlicePgp.GetPublicKey().GetFingerprint()), OpenPgpKeys.Parse(parsed.KeyData)[0].Fingerprint);
    }

    [Fact]
    public async Task No_Autocrypt_header_without_an_own_key()
    {
        await ImportAsync(ArmoredPublic(BobPgp));
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "Hello" } };
        message.From.Add(MailboxAddress.Parse("alice@example.com"));
        await WithUser(async s =>
        {
            await s.GetRequiredService<KeySharing>().AddAutocryptAsync(User, message, Ct);
            return 0;
        });
        Assert.Null(message.Headers[KeySharing.AutocryptHeader]);
    }

    [Fact]
    public void Autocrypt_headers_with_unknown_critical_attributes_are_ignored()
    {
        var key = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        Assert.NotNull(KeySharing.ParseAutocrypt(Autocrypt("bob@example.org", key) + "; prefer-encrypt=mutual; _extra=1"));
        Assert.Null(KeySharing.ParseAutocrypt(Autocrypt("bob@example.org", key) + "; critical=1"));
        Assert.Null(KeySharing.ParseAutocrypt("addr=bob@example.org"));
    }

    [Fact]
    public async Task Incoming_Autocrypt_key_is_kept_once_and_never_replaces_one()
    {
        var bob = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        Assert.Null(await TakeAsync(Autocrypt("bob@example.org", bob), dmarcFailed: true));
        Assert.Null(await TakeAsync(Autocrypt("bob@example.org", bob), from: "mallory@example.net"));
        // A key that does not name the address is not taken either.
        Assert.Null(await TakeAsync(Autocrypt("bob@example.org", OpenPgpKeys.PublicRingOf(AlicePgp).GetEncoded())));

        var kept = await TakeAsync(Autocrypt("bob@example.org", bob));
        Assert.Equal(KeySources.Autocrypt, kept!.Source);

        // Someone else's key in a header claiming to be Bob: Bob's stays.
        var other = OpenPgpKeys.Generate(new MailboxAddress("Fake", "bob@example.org"), "x", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        Assert.Null(await TakeAsync(Autocrypt("bob@example.org", OpenPgpKeys.PublicRingOf(other).GetEncoded())));
        Assert.Single(await KeysAsync());
    }

    [Fact]
    public void Vcard_keys_are_written_as_the_card_version_has_them()
    {
        var key = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        var v3 = VCard.AddKey("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Bob\r\nEMAIL:bob@example.org\r\nEND:VCARD\r\n", key, certificate: false)!;
        Assert.Contains("KEY;TYPE=PGP;ENCODING=b:", v3);
        Assert.All(v3.Split("\r\n"), line => Assert.True(line.Length <= 75));
        Assert.Equal(key, Assert.Single(VCard.Parse(v3).Keys!));
        Assert.Null(VCard.AddKey(v3, key, certificate: false));

        var v4 = VCard.AddKey("BEGIN:VCARD\nVERSION:4.0\nFN:Bob\nEND:VCARD\n", key, certificate: false)!;
        Assert.Contains("KEY:data:application/pgp-keys;base64,", v4);
        Assert.DoesNotContain("\r\n", v4);
        Assert.Equal(key, Assert.Single(VCard.Parse(v4).Keys!));
    }

    [Fact]
    public async Task Saving_to_a_contact_adds_the_key_or_makes_the_contact()
    {
        _carddav.AddCard(User, "contacts", "bob.vcf", "BEGIN:VCARD\nVERSION:3.0\nUID:bob\nFN:Bob Builder\nEMAIL:bob@example.org\nEND:VCARD");
        var bob = (await ImportAsync(ArmoredPublic(BobPgp))).Added[0];
        var saved = await JsonAsync(await _client.PostAsync($"/api/crypto/keys/{bob.Id}/contact", null, Ct));
        Assert.False(saved.GetProperty("created").GetBoolean());
        Assert.Equal("Bob Builder", saved.GetProperty("name").GetString());
        Assert.Equal(OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded(), Assert.Single(VCard.Parse(_carddav.Card(User, "contacts", "bob.vcf")!).Keys!));

        var alice = (await ImportAsync(ArmoredPublic(AlicePgp))).Added[0];
        var created = await JsonAsync(await _client.PostAsync($"/api/crypto/keys/{alice.Id}/contact", null, Ct));
        Assert.True(created.GetProperty("created").GetBoolean());
        var card = _carddav.Cards(User, "contacts").Single(c => c != "bob.vcf");
        var parsed = VCard.Parse(_carddav.Card(User, "contacts", card)!);
        Assert.Equal(["alice@example.com"], parsed.Emails);
        Assert.Single(parsed.Keys!);
    }

    [Fact]
    public async Task Publishing_uploads_and_asks_to_confirm_the_unpublished_addresses()
    {
        var own = (await ImportAsync(ArmoredSecret(AlicePgp), Passphrase)).Added[0];
        var result = await JsonAsync(await _client.PostAsync($"/api/crypto/keys/{own.Id}/publish", null, Ct));
        Assert.Equal("pending", result.GetProperty("addresses").GetProperty("alice@example.com").GetString());

        var upload = _keyServer.Bodies["/vks/v1/upload"];
        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", upload.GetProperty("keytext").GetString());
        Assert.DoesNotContain("PRIVATE", upload.GetProperty("keytext").GetString());
        var verify = _keyServer.Bodies["/vks/v1/request-verify"];
        Assert.Equal("token-1", verify.GetProperty("token").GetString());
        Assert.Equal("alice@example.com", verify.GetProperty("addresses")[0].GetString());

        // Someone else's key is not the user's to publish.
        var bob = (await ImportAsync(ArmoredPublic(BobPgp))).Added[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/crypto/keys/{bob.Id}/publish", null, Ct)).StatusCode);

        _keyServer.Fail = true;
        var failed = await _client.PostAsync($"/api/crypto/keys/{own.Id}/publish", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("key_server", await failed.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>keys.openpgp.org's VKS API: upload answers with a token and every address unpublished, request-verify with pending.</summary>
    private sealed class FakeKeyServer : HttpMessageHandler
    {
        public ConcurrentDictionary<string, JsonElement> Bodies { get; } = new();

        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "Too many requests" }) };
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
            Bodies[path] = body;
            return path switch
            {
                "/vks/v1/upload" => Answer(new { key_fpr = "X", token = "token-1", status = new Dictionary<string, string> { ["alice@example.com"] = "unpublished" } }),
                "/vks/v1/request-verify" => Answer(new { key_fpr = "X", token = "token-1", status = new Dictionary<string, string> { ["alice@example.com"] = "pending" } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Answer(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
