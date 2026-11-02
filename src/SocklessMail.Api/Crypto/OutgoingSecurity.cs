using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace SocklessMail.Api.Crypto;

/// <summary>
/// How to protect a message: encrypt it, sign it, or both, with OpenPGP ("openpgp") or S/MIME
/// ("smime"); <see cref="AttachKey"/> adds the sender's OpenPGP public key as an attachment.
/// </summary>
public sealed record SecurityInput(bool Encrypt, bool Sign, string? Kind, bool AttachKey = false)
{
    public bool Any => Encrypt || Sign || AttachKey;
}

/// <summary>
/// A message cannot be protected as asked. <see cref="Code"/>: "missing_keys" (no key for
/// <see cref="Addresses"/>), "key_locked" (unlock <see cref="Keys"/> first), "no_own_key" (no key
/// of the sender's to sign with or to encrypt the sender's copy to), "encryption_required"
/// (decrypted content of an encrypted message would go out unencrypted).
/// </summary>
public sealed class MessageProtectionException(string code, string message, IReadOnlyList<string>? addresses = null, IReadOnlyList<KeyRefDto>? keys = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Addresses { get; } = addresses ?? [];

    public IReadOnlyList<KeyRefDto> Keys { get; } = keys ?? [];
}

/// <summary>
/// Signs and encrypts outgoing messages (PGP/MIME, RFC 3156; S/MIME, RFC 8551) with the user's
/// keys. Encrypted mail is encrypted to the sender too, so the copy in Sent stays readable; a
/// draft is encrypted to the sender only. The headers (subject included) stay readable, as with
/// most mail programs.
/// </summary>
public sealed class OutgoingSecurity(CryptoKeyStore store, TimeProvider clock)
{
    /// <summary>The draft header that remembers what the composer had chosen.</summary>
    public const string DraftHeader = "X-Sockless-Security";

    public static CryptoKind KindOf(string? kind) => kind?.ToLowerInvariant() switch
    {
        "smime" => CryptoKind.Smime,
        "openpgp" or null or "" => CryptoKind.OpenPgp,
        _ => throw new Mailbox.MailboxValidationException("security", "Choose OpenPGP or S/MIME."),
    };

    /// <summary>Replaces the message's body with its signed and/or encrypted form.</summary>
    public async Task ProtectAsync(string userId, MimeMessage message, SecurityInput security, bool draft, CancellationToken ct)
    {
        var kind = KindOf(security.Kind);
        var from = message.From.Mailboxes.First();
        var material = await store.LoadAsync(userId, ct);
        var now = clock.GetUtcNow();

        if (draft)
        {
            message.Headers.Replace(DraftHeader, $"encrypt={(security.Encrypt ? 1 : 0)}; sign={(security.Sign ? 1 : 0)}; kind={CryptoKeyStore.KindName(kind)}; key={(security.AttachKey ? 1 : 0)}");
            if (!security.Encrypt)
            {
                return;
            }
        }

        if (security.AttachKey && !draft)
        {
            AttachPublicKey(message, material, from, now);
        }

        var encrypt = security.Encrypt;
        var sign = security.Sign && !draft;
        if (!encrypt && !sign)
        {
            return;
        }

        var recipients = draft
            ? [from]
            : message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Append(from)
                .DistinctBy(m => m.Address, StringComparer.OrdinalIgnoreCase).ToList();
        if (encrypt)
        {
            var missing = recipients.Where(r => KeyUsability.For(material.Keys, kind, r.Address, now) is null).Select(r => r.Address).ToList();
            if (missing.Contains(from.Address, StringComparer.OrdinalIgnoreCase))
            {
                throw new MessageProtectionException("no_own_key",
                    $"You have no {Name(kind)} key for {from.Address}, so you could not read this message yourself. Create or import one under Settings → Encryption.");
            }

            if (missing.Count > 0)
            {
                throw new MessageProtectionException("missing_keys", $"There is no {Name(kind)} key for {string.Join(", ", missing)}.", missing);
            }
        }

        var content = message.Body ?? throw new InvalidOperationException("The message has no body to protect.");
        var signer = sign ? SigningKey(material, kind, from, now) : null;
        if (kind == CryptoKind.OpenPgp)
        {
            using var pgp = new UserPgpContext(material);
            var keys = encrypt
                ? recipients.Select(r => KeyUsability.EncryptionKey(new PgpPublicKeyRing(KeyUsability.For(material.Keys, kind, r.Address, now)!.PublicData), now)!).ToList()
                : [];
            PgpSecretKey? secretKey = signer is null ? null : pgp.GetSigningKey(from, ct);
            message.Body = (encrypt, secretKey) switch
            {
                (true, { } key) => await MultipartEncrypted.SignAndEncryptAsync(pgp, key, DigestAlgorithm.Sha256, keys, content, ct),
                (true, null) => await MultipartEncrypted.EncryptAsync(pgp, keys, content, ct),
                (false, { } key) => await MultipartSigned.CreateAsync(pgp, key, DigestAlgorithm.Sha256, content, ct),
                _ => content,
            };
            return;
        }

        using var smime = new UserSmimeContext(material);
        var body = content;
        if (signer is not null)
        {
            var cms = new CmsSigner(new MemoryStream(material.Secrets[signer.Id]), material.Passphrases[signer.Id]) { DigestAlgorithm = DigestAlgorithm.Sha256 };
            body = await MultipartSigned.CreateAsync(smime, cms, body, ct);
        }

        if (encrypt)
        {
            var cmsRecipients = new CmsRecipientCollection();
            foreach (var recipient in recipients)
            {
                var key = KeyUsability.For(material.Keys, kind, recipient.Address, now)!;
                cmsRecipients.Add(new CmsRecipient(SmimeCertificates.Read(key.PublicData)));
            }

            body = await ApplicationPkcs7Mime.EncryptAsync(smime, cmsRecipients, body, ct);
        }

        message.Body = body;
    }

    /// <summary>What a draft saved with <see cref="DraftHeader"/> had chosen; null when it has no such header.</summary>
    public static SecurityInput? FromDraft(MimeMessage? message) => FromHeader(message?.Headers[DraftHeader]);

    public static SecurityInput? FromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var values = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p[1], StringComparer.Ordinal);
        return new SecurityInput(values.GetValueOrDefault("encrypt") == "1", values.GetValueOrDefault("sign") == "1",
            values.GetValueOrDefault("kind") == "smime" ? "smime" : "openpgp", values.GetValueOrDefault("key") == "1");
    }

    /// <summary>The sender's own key to sign with, unlocked.</summary>
    private static CryptoKey SigningKey(KeyMaterial material, CryptoKind kind, MailboxAddress from, DateTimeOffset now)
    {
        var own = material.Keys
            .Where(k => k.Kind == kind && k.IsOwn && k.AddressList.Contains(from.Address, StringComparer.OrdinalIgnoreCase) && (k.ExpiresAt is null || k.ExpiresAt > now))
            .OrderByDescending(k => k.CreatedAt)
            .ToList();
        if (own.FirstOrDefault(k => material.Secrets.ContainsKey(k.Id)) is { } usable)
        {
            return usable;
        }

        if (own.Count > 0)
        {
            throw new MessageProtectionException("key_locked", "Unlock your key to sign this message.", keys: own.Select(k => new KeyRefDto(k.Id, k.Fingerprint, k.AddressList)).ToList());
        }

        throw new MessageProtectionException("no_own_key",
            $"You have no {Name(kind)} key for {from.Address} to sign with. Create or import one under Settings → Encryption.");
    }

    /// <summary>The sender's OpenPGP public key as an .asc attachment, so recipients can answer encrypted.</summary>
    private static void AttachPublicKey(MimeMessage message, KeyMaterial material, MailboxAddress from, DateTimeOffset now)
    {
        var key = material.Keys
            .Where(k => k.Kind == CryptoKind.OpenPgp && k.IsOwn && k.AddressList.Contains(from.Address, StringComparer.OrdinalIgnoreCase) && KeyUsability.CanEncrypt(k, now))
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefault()
            ?? throw new MessageProtectionException("no_own_key", $"You have no OpenPGP key for {from.Address} to attach.");
        var (_, _, text) = CryptoKeyStore.PublicExport(key);
        var part = new MimePart("application", "pgp-keys")
        {
            Content = new MimeContent(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = $"OpenPGP_0x{key.Fingerprint[^16..]}.asc" },
            ContentTransferEncoding = ContentEncoding.SevenBit,
        };
        if (message.Body is Multipart { ContentType.MediaSubtype: "mixed" } mixed)
        {
            mixed.Add(part);
        }
        else
        {
            message.Body = new Multipart("mixed") { message.Body!, part };
        }
    }

    private static string Name(CryptoKind kind) => kind == CryptoKind.OpenPgp ? "OpenPGP" : "S/MIME";
}
