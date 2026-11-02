using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Crypto;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>Encrypted and signed mail read from a real IMAP server: locked, unlocked, decrypted, its parts downloaded.</summary>
public sealed class SecureMailTests(GreenMailFixture mail) : IAsyncLifetime
{
    private const string Address = "secure@example.test";
    private const string Passphrase = "integration passphrase";
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly PgpSecretKeyRing _key = OpenPgpKeys.Generate(new MailboxAddress("Secure", Address), Passphrase, DateTimeOffset.UtcNow.AddMinutes(-1), null);
    private readonly MailApp _app = new();
    private HttpClient _client = null!;
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.ClientAs("secure-user");
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None, Ct);
            await imap.AuthenticateAsync(Address, GreenMailFixture.ActionsPassword, Ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            if (imap.Inbox.Count > 0)
            {
                await imap.Inbox.AddFlagsAsync(Enumerable.Range(0, imap.Inbox.Count).ToList(), MessageFlags.Deleted, true, Ct);
                await imap.Inbox.ExpungeAsync(Ct);
            }

            await imap.Inbox.AppendAsync(await EncryptedAsync(), cancellationToken: Ct);
            await imap.Inbox.AppendAsync(await SmimeSignedAsync(), cancellationToken: Ct);
        }

        _accountId = await AddAccountAsync(_client, mail, Address, GreenMailFixture.ActionsPassword);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private string Inbox => $"/api/accounts/{_accountId}/folders/SU5CT1g";

    private async Task<string> MessageIdAsync(string subject)
    {
        var page = await Json(await _client.GetAsync($"{Inbox}/messages", Ct));
        return page.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("subject").GetString() == subject).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Encrypted_message_opens_after_unlocking_and_its_parts_download()
    {
        var import = await Json(await _client.PostAsJsonAsync("/api/crypto/keys/import", new { text = Armored(_key), passphrase = Passphrase }, Ct));
        var keyId = import.GetProperty("added")[0].GetProperty("id").GetGuid();
        var id = await MessageIdAsync("...");

        var locked = await Json(await _client.GetAsync($"{Inbox}/messages/{id}", Ct));
        var encryption = locked.GetProperty("security").GetProperty("encryption");
        Assert.Equal("locked", encryption.GetProperty("status").GetString());
        Assert.Equal(keyId, encryption.GetProperty("keys")[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain("Revenue", locked.GetProperty("html").GetString());

        await Json(await _client.PostAsJsonAsync($"/api/crypto/keys/{keyId}/unlock", new { passphrase = Passphrase }, Ct));
        var opened = await Json(await _client.GetAsync($"{Inbox}/messages/{id}", Ct));
        Assert.Equal("decrypted", opened.GetProperty("security").GetProperty("encryption").GetProperty("status").GetString());
        // The protected subject replaces the placeholder.
        Assert.Equal("Quarterly numbers", opened.GetProperty("subject").GetString());
        var html = opened.GetProperty("html").GetString()!;
        Assert.Contains("Revenue is up", html);
        Assert.Matches("parts/c[0-9.]+", html);

        var attachment = opened.GetProperty("attachments").EnumerateArray().Single();
        Assert.Equal("numbers.csv", attachment.GetProperty("fileName").GetString());
        var partId = attachment.GetProperty("partId").GetString()!;
        Assert.StartsWith("c", partId);
        var download = await _client.GetAsync($"{Inbox}/messages/{id}/parts/{partId}?download=true", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        // A text part travels with CRLF line ends, as MIME has it.
        Assert.Equal("q1,q2\n10,12\n", (await download.Content.ReadAsStringAsync(Ct)).Replace("\r\n", "\n"));

        // Locking forgets the decrypted copy: parts need the key again.
        await _client.PostAsync("/api/crypto/keys/lock", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Inbox}/messages/{id}/parts/{partId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task List_marks_encrypted_and_signed_mail_without_paperclips()
    {
        var page = await Json(await _client.GetAsync($"{Inbox}/messages", Ct));
        var messages = page.GetProperty("messages").EnumerateArray().ToList();
        var encrypted = messages.Single(m => m.GetProperty("subject").GetString() == "...");
        Assert.Equal("encrypted", encrypted.GetProperty("security").GetString());
        // What it holds is not known until it is opened.
        Assert.False(encrypted.GetProperty("hasAttachments").GetBoolean());
        var signed = messages.Single(m => m.GetProperty("subject").GetString() == "Signed report");
        Assert.Equal("signed", signed.GetProperty("security").GetString());
        // The signature part is no attachment.
        Assert.False(signed.GetProperty("hasAttachments").GetBoolean());
    }

    [Fact]
    public async Task Smime_signed_message_shows_its_signature()
    {
        var id = await MessageIdAsync("Signed report");
        var message = await Json(await _client.GetAsync($"{Inbox}/messages/{id}", Ct));
        var signature = message.GetProperty("security").GetProperty("signatures")[0];
        Assert.Equal("smime", signature.GetProperty("kind").GetString());
        Assert.Equal("untrusted", signature.GetProperty("status").GetString());
        Assert.True(signature.GetProperty("senderMatches").GetBoolean());
        Assert.Contains("All figures attached", message.GetProperty("html").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("security").GetProperty("encryption").ValueKind);
    }

    private async Task<Guid> ImportOwnKeyAsync(bool unlock = true)
    {
        var import = await Json(await _client.PostAsJsonAsync("/api/crypto/keys/import", new { text = Armored(_key), passphrase = Passphrase }, Ct));
        var keyId = import.GetProperty("added")[0].GetProperty("id").GetGuid();
        if (unlock)
        {
            await Json(await _client.PostAsJsonAsync($"/api/crypto/keys/{keyId}/unlock", new { passphrase = Passphrase }, Ct));
        }

        return keyId;
    }

    [Fact]
    public async Task Sent_encrypted_and_signed_mail_arrives_and_opens()
    {
        await ImportOwnKeyAsync();
        var subject = $"Encrypted send {Guid.NewGuid():N}";
        var sent = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", new
        {
            to = Address,
            subject,
            text = "Only for my eyes.",
            security = new { encrypt = true, sign = true, kind = "openpgp" },
        }, Ct);
        await Json(sent);

        string? id = null;
        for (var attempt = 0; attempt < 40 && id is null; attempt++)
        {
            var page = await Json(await _client.GetAsync($"{Inbox}/messages", Ct));
            id = page.GetProperty("messages").EnumerateArray().FirstOrDefault(m => m.GetProperty("subject").GetString() == subject) is { ValueKind: JsonValueKind.Object } m
                ? m.GetProperty("id").GetString()
                : null;
            if (id is null)
            {
                await Task.Delay(250, Ct);
            }
        }

        Assert.NotNull(id);
        var message = await Json(await _client.GetAsync($"{Inbox}/messages/{id}", Ct));
        Assert.Equal("decrypted", message.GetProperty("security").GetProperty("encryption").GetProperty("status").GetString());
        Assert.Equal("valid", message.GetProperty("security").GetProperty("signatures")[0].GetProperty("status").GetString());
        Assert.Contains("Only for my eyes.", message.GetProperty("html").GetString());

        // What went over the wire is ciphertext.
        var source = await (await _client.GetAsync($"{Inbox}/messages/{id}/source", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Only for my eyes", source);
        Assert.Contains("application/pgp-encrypted", source);
    }

    [Fact]
    public async Task Missing_keys_and_locked_keys_are_answered_with_conflict()
    {
        var keyId = await ImportOwnKeyAsync(unlock: false);
        var missing = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", new
        {
            to = $"{Address}, stranger@example.test",
            subject = "x",
            text = "x",
            security = new { encrypt = true, sign = false, kind = "openpgp" },
        }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("missing_keys", body.GetProperty("error").GetString());
        Assert.Equal("stranger@example.test", body.GetProperty("addresses")[0].GetString());

        var locked = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", new
        {
            to = Address, subject = "x", text = "x", security = new { encrypt = false, sign = true, kind = "openpgp" },
        }, Ct);
        var lockedBody = JsonDocument.Parse(await locked.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("key_locked", lockedBody.GetProperty("error").GetString());
        Assert.Equal(keyId, lockedBody.GetProperty("keys")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Answering_encrypted_mail_quotes_it_and_keeps_its_parts_encrypted()
    {
        var keyId = await ImportOwnKeyAsync(unlock: false);
        var id = await MessageIdAsync("...");

        var locked = await _client.GetAsync($"{Inbox}/messages/{id}/draft?mode=reply", Ct);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        await Json(await _client.PostAsJsonAsync($"/api/crypto/keys/{keyId}/unlock", new { passphrase = Passphrase }, Ct));

        var reply = await Json(await _client.GetAsync($"{Inbox}/messages/{id}/draft?mode=reply", Ct));
        Assert.Equal("Re: Quarterly numbers", reply.GetProperty("subject").GetString());
        Assert.Contains("Revenue is up", reply.GetProperty("text").GetString());
        Assert.True(reply.GetProperty("security").GetProperty("encrypt").GetBoolean());

        var forward = await Json(await _client.GetAsync($"{Inbox}/messages/{id}/draft?mode=forward", Ct));
        var partId = forward.GetProperty("forwardedAttachments")[0].GetProperty("partId").GetString()!;
        Assert.StartsWith("c", partId);
        object Forward(bool encrypt) => new
        {
            to = Address,
            subject = "Fwd: numbers",
            text = "See attached.",
            source = new { folderId = "SU5CT1g", messageId = id, mode = "forward", forwardPartIds = new[] { partId } },
            security = new { encrypt, sign = false, kind = "openpgp" },
        };

        var plain = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", Forward(false), Ct);
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Contains("encryption_required", await plain.Content.ReadAsStringAsync(Ct));
        await Json(await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", Forward(true), Ct));
    }

    [Fact]
    public async Task Encrypted_draft_is_continued_decrypted_with_its_choices()
    {
        await ImportOwnKeyAsync();
        var saved = await Json(await _client.PutAsJsonAsync($"/api/accounts/{_accountId}/drafts", new
        {
            to = "nobody-with-a-key@example.test",
            subject = "Secret plan",
            text = "Step one: tell no one.",
            security = new { encrypt = true, sign = true, kind = "openpgp" },
        }, Ct));
        var folderId = saved.GetProperty("folderId").GetString();
        var draftId = saved.GetProperty("messageId").GetString();

        var source = await (await _client.GetAsync($"/api/accounts/{_accountId}/folders/{folderId}/messages/{draftId}/source", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("tell no one", source);

        var draft = await Json(await _client.GetAsync($"/api/accounts/{_accountId}/folders/{folderId}/messages/{draftId}/draft?mode=edit", Ct));
        Assert.Contains("tell no one", draft.GetProperty("text").GetString());
        var security = draft.GetProperty("security");
        Assert.True(security.GetProperty("encrypt").GetBoolean());
        Assert.True(security.GetProperty("sign").GetBoolean());
    }

    [Fact]
    public async Task Keys_arrive_by_Autocrypt_and_as_attachments()
    {
        var bob = OpenPgpKeys.Generate(new MailboxAddress("Bob", "bob@example.org"), "x", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        var carol = OpenPgpKeys.Generate(new MailboxAddress("Carol", "carol@example.org"), "x", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        var subject = $"Keys {Guid.NewGuid():N}";
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(mail.Host, mail.Imap, SecureSocketOptions.None, Ct);
            await imap.AuthenticateAsync(Address, GreenMailFixture.ActionsPassword, Ct);
            var builder = new BodyBuilder { TextBody = "Here is Carol's key." };
            builder.Attachments.Add("carol.asc", Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(OpenPgpKeys.PublicRingOf(carol))), new ContentType("application", "pgp-keys"));
            var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
            message.From.Add(MailboxAddress.Parse("bob@example.org"));
            message.To.Add(MailboxAddress.Parse(Address));
            message.Headers.Add("Autocrypt", $"addr=bob@example.org; keydata={Convert.ToBase64String(OpenPgpKeys.PublicRingOf(bob).GetEncoded())}");
            await imap.Inbox.AppendAsync(message, cancellationToken: Ct);
        }

        var id = await MessageIdAsync(subject);
        var opened = await Json(await _client.GetAsync($"{Inbox}/messages/{id}", Ct));
        var keys = await Json(await _client.GetAsync("/api/crypto/keys", Ct));
        var kept = keys.EnumerateArray().Single(k => k.GetProperty("addresses")[0].GetString() == "bob@example.org");
        Assert.Equal("autocrypt", kept.GetProperty("source").GetString());

        var partId = opened.GetProperty("attachments")[0].GetProperty("partId").GetString();
        var imported = await Json(await _client.PostAsync($"{Inbox}/messages/{id}/parts/{partId}/import-key", null, Ct));
        Assert.Equal("carol@example.org", imported.GetProperty("added")[0].GetProperty("addresses")[0].GetString());
    }

    [Fact]
    public async Task Sent_mail_carries_the_senders_key_in_an_Autocrypt_header()
    {
        await ImportOwnKeyAsync(unlock: false);
        var subject = $"Autocrypt {Guid.NewGuid():N}";
        await Json(await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/send", new { to = Address, subject, text = "Plain." }, Ct));
        string? id = null;
        for (var attempt = 0; attempt < 40 && id is null; attempt++)
        {
            var page = await Json(await _client.GetAsync($"{Inbox}/messages", Ct));
            id = page.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("subject").GetString() == subject).Select(m => m.GetProperty("id").GetString()).FirstOrDefault();
            if (id is null)
            {
                await Task.Delay(250, Ct);
            }
        }

        var headers = await Json(await _client.GetAsync($"{Inbox}/messages/{id}/headers", Ct));
        Assert.Contains($"Autocrypt: addr={Address}; keydata=", headers.GetProperty("headers").GetString());
    }

    private async Task<MimeMessage> EncryptedAsync()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>Revenue is up.</p><img src=\"cid:chart@test\">" };
        builder.LinkedResources.Add("chart.png", PngBytes, new ContentType("image", "png")).ContentId = "chart@test";
        builder.Attachments.Add("numbers.csv", "q1,q2\n10,12\n"u8.ToArray(), new ContentType("text", "csv"));
        var inner = builder.ToMessageBody();
        inner.ContentType.Parameters["protected-headers"] = "v1";
        inner.Headers.Add(HeaderId.Subject, "Quarterly numbers");

        var keys = new KeyMaterial([new CryptoKey
        {
            Id = Guid.NewGuid(), OwnerUserId = "sender", Kind = CryptoKind.OpenPgp, Fingerprint = "x", Addresses = Address,
            PublicData = OpenPgpKeys.PublicRingOf(_key).GetEncoded(),
        }], new Dictionary<Guid, byte[]>(), new Dictionary<Guid, string>());
        using var sender = new UserPgpContext(keys);
        var message = new MimeMessage
        {
            Subject = "...",
            Body = await MultipartEncrypted.EncryptAsync(sender, sender.GetPublicKeys([MailboxAddress.Parse(Address)], Ct), inner, Ct),
        };
        message.From.Add(MailboxAddress.Parse("finance@example.test"));
        message.To.Add(MailboxAddress.Parse(Address));
        return message;
    }

    private async Task<MimeMessage> SmimeSignedAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reports, E=reports@example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12, "p");

        using var context = new TemporarySecureMimeContext();
        var signer = new CmsSigner(new MemoryStream(pkcs12), "p") { DigestAlgorithm = DigestAlgorithm.Sha256 };
        var message = new MimeMessage
        {
            Subject = "Signed report",
            Body = await MultipartSigned.CreateAsync(context, signer, new TextPart("plain") { Text = "All figures attached." }, Ct),
        };
        message.From.Add(MailboxAddress.Parse("reports@example.test"));
        message.To.Add(MailboxAddress.Parse(Address));
        return message;
    }

    private static string Armored(PgpSecretKeyRing ring)
    {
        using var buffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(buffer))
        {
            ring.Encode(armored);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
