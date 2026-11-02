using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Crypto;
using SocklessMail.Api.Data;
using static SocklessMail.Tests.CryptoTestKeys;

namespace SocklessMail.Tests;

/// <summary>Opening encrypted and signed messages, OpenPGP and S/MIME, with the keys a user imported.</summary>
public sealed class MessageSecurityTests : IDisposable
{
    private const string User = "alice";
    private readonly TestApp _app = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private async Task<T> WithUser<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(User);
        return await action(scope.ServiceProvider);
    }

    private Task<ImportKeyResult> ImportAsync(ImportKeyInput input) =>
        WithUser(s => s.GetRequiredService<CryptoKeyStore>().ImportAsync(User, input, Ct));

    private Task<OpenedMessage> OpenAsync(MimeMessage message) =>
        WithUser(s => s.GetRequiredService<MessageSecurity>().OpenAsync(User, message, Ct));

    private Task<bool> UnlockAsync(Guid id, string passphrase) =>
        WithUser(async s =>
        {
            var store = s.GetRequiredService<CryptoKeyStore>();
            return store.Unlock(User, (await store.FindAsync(User, id, Ct))!, passphrase);
        });

    private static MimeMessage Message(MimeEntity body, string from = "bob@example.org")
    {
        var message = new MimeMessage { Subject = "Secret", Body = body };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("alice@example.com"));
        return message;
    }

    private static MimeEntity Content()
    {
        var builder = new BodyBuilder { TextBody = "The launch code is 1234.", HtmlBody = "<p>The launch code is <b>1234</b>.</p>" };
        builder.Attachments.Add("plan.txt", "step one"u8.ToArray(), new ContentType("text", "plain"));
        return builder.ToMessageBody();
    }

    /// <summary>A message parsed back from its bytes, as it arrives from IMAP.</summary>
    private static MimeMessage Reparse(MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer, TestContext.Current.CancellationToken);
        buffer.Position = 0;
        return MimeMessage.Load(buffer, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OpenPgp_message_is_locked_until_the_key_is_unlocked()
    {
        var imported = await ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, Passphrase));
        var keyId = Assert.Single(imported.Added).Id;
        using var sender = PgpSender([], [AlicePgp]);
        var encrypted = Reparse(Message(await MultipartEncrypted.EncryptAsync(sender, sender.GetPublicKeys([MailboxAddress.Parse("alice@example.com")], Ct), Content(), Ct)));

        var locked = await OpenAsync(encrypted);
        Assert.Null(locked.Body);
        Assert.Equal("locked", locked.Security.Encryption!.Status);
        Assert.Equal(keyId, Assert.Single(locked.Security.Encryption.Keys).Id);

        Assert.False(await UnlockAsync(keyId, "wrong"));
        Assert.True(await UnlockAsync(keyId, Passphrase));
        var opened = await OpenAsync(encrypted);
        Assert.Equal("decrypted", opened.Security.Encryption!.Status);
        Assert.Contains("1234", Body(opened));
        Assert.Contains(Leaves(opened.Body!).OfType<MimePart>(), p => p.FileName == "plan.txt");
    }

    [Fact]
    public async Task OpenPgp_message_for_someone_else_has_no_key()
    {
        await ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, Passphrase));
        using var sender = PgpSender([], [BobPgp]);
        var encrypted = Reparse(Message(await MultipartEncrypted.EncryptAsync(sender, sender.GetPublicKeys([MailboxAddress.Parse("bob@example.org")], Ct), Content(), Ct)));

        var opened = await OpenAsync(encrypted);
        Assert.Equal("no_key", opened.Security.Encryption!.Status);
        Assert.Empty(opened.Security.Encryption.Keys);
    }

    [Fact]
    public async Task OpenPgp_signature_is_checked_with_the_senders_key()
    {
        using var sender = PgpSender([BobPgp], []);
        var signed = Reparse(Message(await MultipartSigned.CreateAsync(sender, sender.GetSigningKey(MailboxAddress.Parse("bob@example.org"), Ct), DigestAlgorithm.Sha256, Content(), Ct)));

        var unknown = Assert.Single((await OpenAsync(signed)).Security.Signatures);
        Assert.Equal("unknown_key", unknown.Status);
        Assert.Equal(16, unknown.KeyId!.Length);

        await ImportAsync(new ImportKeyInput(ArmoredPublic(BobPgp), null, null));
        var opened = await OpenAsync(signed);
        var valid = Assert.Single(opened.Security.Signatures);
        Assert.Equal("valid", valid.Status);
        Assert.True(valid.SenderMatches);
        Assert.Equal(["bob@example.org"], valid.SignerAddresses);
        Assert.Null(opened.Security.Encryption);
        Assert.Contains("1234", Body(opened));

        // Claimed to come from someone else: intact, but not the sender's key.
        signed.From.Clear();
        signed.From.Add(MailboxAddress.Parse("ceo@example.org"));
        Assert.False(Assert.Single((await OpenAsync(signed)).Security.Signatures).SenderMatches);
    }

    [Fact]
    public async Task Changed_OpenPgp_message_has_a_bad_signature()
    {
        await ImportAsync(new ImportKeyInput(ArmoredPublic(BobPgp), null, null));
        using var sender = PgpSender([BobPgp], []);
        var signed = Message(await MultipartSigned.CreateAsync(sender, sender.GetSigningKey(MailboxAddress.Parse("bob@example.org"), Ct), DigestAlgorithm.Sha256, new TextPart("plain") { Text = "Pay 10 EUR" }, Ct));
        using var buffer = new MemoryStream();
        signed.WriteTo(buffer, Ct);
        var tampered = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("Pay 10 EUR", "Pay 99 EUR"))), Ct);

        Assert.Equal("bad", Assert.Single((await OpenAsync(tampered)).Security.Signatures).Status);
    }

    [Fact]
    public async Task Signed_and_encrypted_OpenPgp_message_reports_both()
    {
        await ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, Passphrase));
        await ImportAsync(new ImportKeyInput(ArmoredPublic(BobPgp), null, null));
        var keys = await WithUser(s => s.GetRequiredService<CryptoKeyStore>().ListAsync(User, Ct));
        await UnlockAsync(keys.Single(k => k.Own).Id, Passphrase);
        using var sender = PgpSender([BobPgp], [AlicePgp]);
        var message = Reparse(Message(await MultipartEncrypted.SignAndEncryptAsync(
            sender, sender.GetSigningKey(MailboxAddress.Parse("bob@example.org"), Ct), DigestAlgorithm.Sha256,
            sender.GetPublicKeys([MailboxAddress.Parse("alice@example.com")], Ct), Content(), Ct)));

        var opened = await OpenAsync(message);
        Assert.Equal("decrypted", opened.Security.Encryption!.Status);
        Assert.Equal("valid", Assert.Single(opened.Security.Signatures).Status);
        Assert.Contains("1234", Body(opened));
    }

    [Fact]
    public async Task Smime_message_is_decrypted_after_unlocking_the_certificate()
    {
        var imported = await ImportAsync(new ImportKeyInput(null, Convert.ToBase64String(AliceSmime), Passphrase));
        var key = Assert.Single(imported.Added);
        Assert.Equal("smime", key.Kind);
        Assert.Equal(["alice@example.com"], key.Addresses);
        Assert.True(key.HasPassphrase);

        using var sender = new TemporarySecureMimeContext();
        var recipients = new CmsRecipientCollection { new CmsRecipient(X509CertificateLoader.LoadCertificate(PublicCertificate(AliceSmime, Passphrase))) };
        var encrypted = Reparse(Message(await ApplicationPkcs7Mime.EncryptAsync(sender, recipients, Content(), Ct)));

        Assert.Equal("locked", (await OpenAsync(encrypted)).Security.Encryption!.Status);
        Assert.True(await UnlockAsync(key.Id, Passphrase));
        var opened = await OpenAsync(encrypted);
        Assert.Equal("decrypted", opened.Security.Encryption!.Status);
        Assert.Equal("smime", opened.Security.Encryption.Kind);
        Assert.Contains("1234", Body(opened));
    }

    [Fact]
    public async Task Smime_signature_by_an_unknown_authority_is_intact_but_untrusted()
    {
        using var sender = new TemporarySecureMimeContext();
        var signer = new CmsSigner(new MemoryStream(BobSmime), "") { DigestAlgorithm = DigestAlgorithm.Sha256 };
        var signed = Reparse(Message(await MultipartSigned.CreateAsync(sender, signer, Content(), Ct)));

        var opened = await OpenAsync(signed);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Equal("smime", signature.Kind);
        Assert.Equal("untrusted", signature.Status);
        Assert.True(signature.SenderMatches);
        Assert.Contains("1234", Body(opened));
    }

    [Fact]
    public async Task Inline_pgp_is_decrypted_in_the_text()
    {
        var imported = await ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, Passphrase));
        await UnlockAsync(imported.Added[0].Id, Passphrase);
        using var sender = PgpSender([], [AlicePgp]);
        using var armored = new MemoryStream();
        var part = await sender.EncryptAsync(sender.GetPublicKeys([MailboxAddress.Parse("alice@example.com")], Ct), new MemoryStream("Meet at noon."u8.ToArray()), Ct);
        await part.Content!.DecodeToAsync(armored, Ct);
        var text = "Hi,\n\n" + Encoding.ASCII.GetString(armored.ToArray()) + "\nBye";

        var (opened, security) = await WithUser(s => s.GetRequiredService<MessageSecurity>().OpenInlineAsync(User, text, ["bob@example.org"], Ct));
        Assert.Equal("decrypted", security!.Encryption!.Status);
        Assert.StartsWith("Hi,\n\nMeet at noon.", opened);
        Assert.EndsWith("Bye", opened);
    }

    [Fact]
    public async Task Clear_signed_text_is_verified()
    {
        await ImportAsync(new ImportKeyInput(ArmoredPublic(BobPgp), null, null));
        var block = ClearSign("Dear Alice,\n- not a list\nTrailing space   \nBob");

        var (opened, security) = await WithUser(s => s.GetRequiredService<MessageSecurity>().OpenInlineAsync(User, block, ["bob@example.org"], Ct));
        Assert.Equal("Dear Alice,\n- not a list\nTrailing space   \nBob", opened.TrimEnd('\n'));
        Assert.Equal("valid", Assert.Single(security!.Signatures).Status);

        var (_, tampered) = await WithUser(s => s.GetRequiredService<MessageSecurity>().OpenInlineAsync(User, block.Replace("Dear", "Dearest"), ["bob@example.org"], Ct));
        Assert.Equal("bad", Assert.Single(tampered!.Signatures).Status);
    }

    [Fact]
    public async Task Imports_that_need_a_passphrase_say_so()
    {
        var error = await Assert.ThrowsAsync<SocklessMail.Api.Mailbox.MailboxValidationException>(() => ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, "nope")));
        Assert.Equal("passphrase", error.Field);
        var p12 = await Assert.ThrowsAsync<SocklessMail.Api.Mailbox.MailboxValidationException>(() => ImportAsync(new ImportKeyInput(null, Convert.ToBase64String(AliceSmime), null)));
        Assert.Equal("passphrase", p12.Field);
        var junk = await Assert.ThrowsAsync<SocklessMail.Api.Mailbox.MailboxValidationException>(() => ImportAsync(new ImportKeyInput("hello", null, null)));
        Assert.Equal("data", junk.Field);
    }

    [Fact]
    public async Task Importing_the_secret_key_later_upgrades_the_public_one()
    {
        var first = await ImportAsync(new ImportKeyInput(ArmoredPublic(AlicePgp), null, null));
        Assert.False(first.Added[0].Own);
        var second = await ImportAsync(new ImportKeyInput(ArmoredSecret(AlicePgp), null, Passphrase));
        Assert.Empty(second.Added);
        var updated = Assert.Single(second.Updated);
        Assert.Equal(first.Added[0].Id, updated.Id);
        Assert.True(updated.Own);
    }

    private static IEnumerable<MimeEntity> Leaves(MimeEntity entity) =>
        entity is Multipart multipart ? multipart.SelectMany(Leaves) : [entity];

    private static string Body(OpenedMessage opened) =>
        Leaves(opened.Body!).OfType<TextPart>().First(p => !p.IsAttachment).Text;

    /// <summary>A clear-signed block as GnuPG writes it (RFC 4880 §7), signed with Bob's key.</summary>
    private static string ClearSign(string text)
    {
        var secretKey = BobPgp.GetSecretKey();
        var privateKey = secretKey.ExtractPrivateKey(Passphrase.ToCharArray());
        var generator = new PgpSignatureGenerator(secretKey.PublicKey.Algorithm, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.CanonicalTextDocument, privateKey);
        var lines = text.Split('\n');
        generator.Update(Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Select(l => l.TrimEnd(' ', '\t')))));

        using var signature = new MemoryStream();
        using (var armored = new ArmoredOutputStream(signature))
        {
            generator.Generate().Encode(armored);
        }

        var escaped = lines.Select(l => l.StartsWith('-') ? "- " + l : l);
        return "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n" + string.Join('\n', escaped) + "\n"
               + Encoding.ASCII.GetString(signature.ToArray()).Replace("\r\n", "\n");
    }
}
