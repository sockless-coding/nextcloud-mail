using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using SocklessMail.Api.Contacts;
using SocklessMail.Api.Crypto;
using SocklessMail.Api.Data;
using static SocklessMail.Tests.CryptoTestKeys;

namespace SocklessMail.Tests;

/// <summary>Signing and encrypting outgoing mail, and finding the keys to encrypt to.</summary>
public sealed class OutgoingSecurityTests : IDisposable
{
    private const string User = "alice";
    private readonly FakeKeyWeb _web = new();
    private readonly TestApp _app;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public OutgoingSecurityTests()
    {
        _app = new TestApp(services: services =>
            services.AddHttpClient(PublicKeyFinder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _web));
    }

    public void Dispose() => _app.Dispose();

    private async Task<T> WithUser<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(User);
        return await action(scope.ServiceProvider);
    }

    private Task<ImportKeyResult> ImportAsync(string? text, string? data = null, string? passphrase = null) =>
        WithUser(s => s.GetRequiredService<CryptoKeyStore>().ImportAsync(User, new ImportKeyInput(text, data, passphrase), Ct));

    private Task UnlockAsync(Guid id) =>
        WithUser(async s =>
        {
            var store = s.GetRequiredService<CryptoKeyStore>();
            return store.Unlock(User, (await store.FindAsync(User, id, Ct))!, Passphrase);
        });

    private async Task<MimeMessage> ProtectAsync(MimeMessage message, SecurityInput security, bool draft = false)
    {
        await WithUser(async s =>
        {
            await s.GetRequiredService<OutgoingSecurity>().ProtectAsync(User, message, security, draft, Ct);
            return 0;
        });
        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, Ct);
        buffer.Position = 0;
        return await MimeMessage.LoadAsync(buffer, Ct);
    }

    private Task<OpenedMessage> OpenAsync(MimeMessage message) =>
        WithUser(s => s.GetRequiredService<MessageSecurity>().OpenAsync(User, message, Ct));

    private static MimeMessage Message(params string[] to)
    {
        var builder = new BodyBuilder { TextBody = "Meet at noon." };
        builder.Attachments.Add("map.txt", "north"u8.ToArray(), new ContentType("text", "plain"));
        var message = new MimeMessage { Subject = "Plans", Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        foreach (var address in to)
        {
            message.To.Add(MailboxAddress.Parse(address));
        }

        return message;
    }

    private static string Text(OpenedMessage opened) => Leaves(opened.Body!).OfType<TextPart>().First(p => !p.IsAttachment).Text;

    private static IEnumerable<MimeEntity> Leaves(MimeEntity entity) => entity is Multipart multipart ? multipart.SelectMany(Leaves) : [entity];

    [Fact]
    public async Task OpenPgp_signed_and_encrypted_mail_opens_for_the_sender()
    {
        var own = (await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase)).Added[0];
        await ImportAsync(ArmoredPublic(BobPgp));
        await UnlockAsync(own.Id);

        var sent = await ProtectAsync(Message("bob@example.org"), new SecurityInput(true, true, "openpgp"));
        Assert.IsType<MultipartEncrypted>(sent.Body);
        Assert.Equal("Plans", sent.Subject);

        var opened = await OpenAsync(sent);
        Assert.Equal("decrypted", opened.Security.Encryption!.Status);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Equal("valid", signature.Status);
        Assert.True(signature.SenderMatches);
        Assert.Equal("Meet at noon.", Text(opened));
        Assert.Contains(Leaves(opened.Body!).OfType<MimePart>(), p => p.FileName == "map.txt");
    }

    [Fact]
    public async Task OpenPgp_signature_only_stays_readable()
    {
        var own = (await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase)).Added[0];
        await UnlockAsync(own.Id);

        var sent = await ProtectAsync(Message("carol@example.net"), new SecurityInput(false, true, "openpgp"));
        Assert.IsType<MultipartSigned>(sent.Body);
        var opened = await OpenAsync(sent);
        Assert.Null(opened.Security.Encryption);
        Assert.Equal("valid", Assert.Single(opened.Security.Signatures).Status);
    }

    [Fact]
    public async Task Recipients_without_keys_are_named()
    {
        await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase);
        await ImportAsync(ArmoredPublic(BobPgp));

        var error = await Assert.ThrowsAsync<MessageProtectionException>(() => ProtectAsync(Message("bob@example.org", "carol@example.net"), new SecurityInput(true, false, "openpgp")));
        Assert.Equal("missing_keys", error.Code);
        Assert.Equal(["carol@example.net"], error.Addresses);
    }

    [Fact]
    public async Task Encrypting_needs_an_own_key_and_signing_an_unlocked_one()
    {
        await ImportAsync(ArmoredPublic(BobPgp));
        var noOwn = await Assert.ThrowsAsync<MessageProtectionException>(() => ProtectAsync(Message("bob@example.org"), new SecurityInput(true, false, "openpgp")));
        Assert.Equal("no_own_key", noOwn.Code);

        var own = (await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase)).Added[0];
        // Encrypting needs only public keys: a locked own key is fine.
        Assert.IsType<MultipartEncrypted>((await ProtectAsync(Message("bob@example.org"), new SecurityInput(true, false, "openpgp"))).Body);

        var locked = await Assert.ThrowsAsync<MessageProtectionException>(() => ProtectAsync(Message("bob@example.org"), new SecurityInput(true, true, "openpgp")));
        Assert.Equal("key_locked", locked.Code);
        Assert.Equal(own.Id, Assert.Single(locked.Keys).Id);
    }

    [Fact]
    public async Task Smime_signed_and_encrypted_mail_opens_for_the_sender()
    {
        var own = (await ImportAsync(null, Convert.ToBase64String(AliceSmime), Passphrase)).Added[0];
        await ImportAsync(null, Convert.ToBase64String(PublicCertificate(BobSmime, "")));
        await UnlockAsync(own.Id);

        var sent = await ProtectAsync(Message("bob@example.org"), new SecurityInput(true, true, "smime"));
        Assert.IsType<ApplicationPkcs7Mime>(sent.Body);

        var opened = await OpenAsync(sent);
        Assert.Equal("decrypted", opened.Security.Encryption!.Status);
        Assert.Equal("smime", opened.Security.Encryption.Kind);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Contains(signature.Status, new[] { "valid", "untrusted" });
        Assert.Equal("Meet at noon.", Text(opened));
    }

    [Fact]
    public async Task Draft_is_encrypted_to_the_sender_only_and_remembers_the_choice()
    {
        await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase);

        // No key for Carol is needed yet: only the sender reads a draft.
        var draft = await ProtectAsync(Message("carol@example.net"), new SecurityInput(true, true, "openpgp", AttachKey: true), draft: true);
        Assert.IsType<MultipartEncrypted>(draft.Body);
        var remembered = OutgoingSecurity.FromDraft(draft)!;
        Assert.True(remembered.Encrypt);
        Assert.True(remembered.Sign);
        Assert.True(remembered.AttachKey);
        Assert.Equal("openpgp", remembered.Kind);

        var plain = await ProtectAsync(Message("carol@example.net"), new SecurityInput(false, true, "smime"), draft: true);
        Assert.IsNotType<MultipartSigned>(plain.Body);
        Assert.Equal("smime", OutgoingSecurity.FromDraft(plain)!.Kind);
    }

    [Fact]
    public async Task Attached_public_key_travels_as_an_asc_file()
    {
        await ImportAsync(ArmoredSecret(AlicePgp), passphrase: Passphrase);

        var sent = await ProtectAsync(Message("carol@example.net"), new SecurityInput(false, false, "openpgp", AttachKey: true));
        var key = Assert.Single(sent.Attachments.OfType<MimePart>(), p => p.ContentType.IsMimeType("application", "pgp-keys"));
        Assert.EndsWith(".asc", key.FileName);
        using var text = new MemoryStream();
        await key.Content!.DecodeToAsync(text, Ct);
        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", Encoding.ASCII.GetString(text.ToArray()));
    }

    [Fact]
    public void Wkd_hashes_local_parts_as_the_draft_says() =>
        // The example of draft-koch-openpgp-webkey-service, section 3.1.
        Assert.Equal("iy9q119eutrkn8s1mk4r39qejnbu3n5q", PublicKeyFinder.ZBase32(System.Security.Cryptography.SHA1.HashData("joe.doe"u8.ToArray())));

    [Fact]
    public async Task Keys_are_found_in_the_recipients_domain_then_on_the_key_server()
    {
        _web.Keys["openpgpkey.example.org"] = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        var client = _app.ClientAs(User);

        var found = await (await client.PostAsJsonAsync("/api/crypto/recipients", new { addresses = new[] { "bob@example.org" }, search = true }, Ct))
            .Content.ReadFromJsonAsync<List<RecipientKeysDto>>(Ct);
        Assert.Equal(KeySources.Wkd, Assert.Single(found!).OpenPgp!.Source);
        Assert.Contains(_web.Requests, r => r.Contains("/.well-known/openpgpkey/example.org/hu/") && r.Contains("?l=bob"));

        // Kept: asking again needs no lookup.
        _web.Requests.Clear();
        var again = await (await client.PostAsJsonAsync("/api/crypto/recipients", new { addresses = new[] { "bob@example.org" }, search = true }, Ct))
            .Content.ReadFromJsonAsync<List<RecipientKeysDto>>(Ct);
        Assert.NotNull(again![0].OpenPgp);
        Assert.Empty(_web.Requests);

        // Bob's key does not name Carol: published for her address or not, it is not taken.
        _web.Keys["keys.openpgp.org"] = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        var carol = await (await client.PostAsJsonAsync("/api/crypto/recipients", new { addresses = new[] { "carol@example.net" }, search = true }, Ct))
            .Content.ReadFromJsonAsync<List<RecipientKeysDto>>(Ct);
        Assert.Null(carol![0].OpenPgp);
        Assert.Contains(_web.Requests, r => r.StartsWith("https://keys.openpgp.org/vks/v1/by-email/carol%40example.net"));
    }

    [Fact]
    public async Task Without_search_nothing_is_asked_on_the_internet()
    {
        _web.Keys["openpgpkey.example.org"] = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        var found = await (await _app.ClientAs(User).PostAsJsonAsync("/api/crypto/recipients", new { addresses = new[] { "bob@example.org" } }, Ct))
            .Content.ReadFromJsonAsync<List<RecipientKeysDto>>(Ct);
        Assert.Null(found![0].OpenPgp);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task Self_made_smime_certificates_of_signed_mail_are_not_kept()
    {
        using var context = new TemporarySecureMimeContext();
        var signer = new CmsSigner(new MemoryStream(BobSmime), "") { DigestAlgorithm = DigestAlgorithm.Sha256 };
        var message = Message("alice@example.com");
        message.From.Clear();
        message.From.Add(MailboxAddress.Parse("bob@example.org"));
        message.Body = await MultipartSigned.CreateAsync(context, signer, message.Body!, Ct);

        Assert.Equal("untrusted", Assert.Single((await OpenAsync(message)).Security.Signatures).Status);
        Assert.Empty(await WithUser(s => s.GetRequiredService<CryptoKeyStore>().ListAsync(User, Ct)));
    }

    [Fact]
    public void Vcard_keys_are_read_from_data_uris_and_inline_base64()
    {
        var key = OpenPgpKeys.PublicRingOf(BobPgp).GetEncoded();
        var v4 = VCard.Parse($"BEGIN:VCARD\nVERSION:4.0\nFN:Bob\nEMAIL:bob@example.org\nKEY:data:application/pgp-keys;base64,{Convert.ToBase64String(key)}\nKEY;MEDIATYPE=application/pgp-keys:https://example.org/bob.asc\nEND:VCARD");
        Assert.Equal(key, Assert.Single(v4.Keys!));
        var v3 = VCard.Parse($"BEGIN:VCARD\nVERSION:3.0\nFN:Bob\nKEY;TYPE=PGP;ENCODING=b:{Convert.ToBase64String(key)}\nEND:VCARD");
        Assert.Equal(key, Assert.Single(v3.Keys!));
    }

    /// <summary>WKD hosts and keys.openpgp.org: a key per host, 404 for everything else.</summary>
    private sealed class FakeKeyWeb : HttpMessageHandler
    {
        public ConcurrentDictionary<string, byte[]> Keys { get; } = new();

        public ConcurrentBag<string> Requests { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Keys.TryGetValue(request.RequestUri.Host, out var key)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(key) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

file static class BagExtensions
{
    public static void Clear(this ConcurrentBag<string> bag)
    {
        while (bag.TryTake(out _))
        {
        }
    }
}
