using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Cms;

namespace SocklessMail.Api.Crypto;

/// <summary>How a message is protected, as its structure (BODYSTRUCTURE or MIME tree) shows.</summary>
public enum SecureShape
{
    None,
    Encrypted,
    Signed,
}

/// <summary>
/// Decrypts messages and checks their signatures, OpenPGP (PGP/MIME, RFC 3156, and inline PGP)
/// and S/MIME (RFC 8551), with the user's own keys. Nothing is looked up elsewhere: a signature
/// by a key the user does not keep is reported as "unknown_key".
/// </summary>
public sealed partial class MessageSecurity(CryptoKeyStore store)
{
    /// <summary>Messages larger than this are not fetched whole to decrypt or check.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>Layers of signing, encryption and compression undone at most.</summary>
    private const int MaxLayers = 4;

    public static SecureShape ShapeOf(string mediaType, string mediaSubtype, string? smimeType)
    {
        if (mediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            return mediaSubtype.ToLowerInvariant() switch
            {
                "encrypted" => SecureShape.Encrypted,
                "signed" => SecureShape.Signed,
                _ => SecureShape.None,
            };
        }

        if (mediaType.Equals("application", StringComparison.OrdinalIgnoreCase)
            && mediaSubtype.ToLowerInvariant() is "pkcs7-mime" or "x-pkcs7-mime")
        {
            return smimeType?.ToLowerInvariant() switch
            {
                "signed-data" => SecureShape.Signed,
                "compressed-data" => SecureShape.None,
                // Enveloped data, or a sender that left smime-type out (it is mostly encryption then).
                _ => SecureShape.Encrypted,
            };
        }

        return SecureShape.None;
    }

    /// <summary>Decrypts and checks <paramref name="message"/>; From is what signatures are compared with.</summary>
    public async Task<OpenedMessage> OpenAsync(string userId, MimeMessage message, CancellationToken ct)
    {
        var material = await store.LoadAsync(userId, ct);
        using var pgp = new UserPgpContext(material);
        using var smime = new UserSmimeContext(material);
        var from = message.From.Mailboxes.Select(m => m.Address.ToLowerInvariant()).ToHashSet();
        var signatures = new List<SignatureDto>();
        EncryptionDto? encryption = null;
        var signerCertificates = new List<(byte[] Der, IReadOnlyList<string> Addresses)>();

        // S/MIME signatures bring the signer's certificate: kept to encrypt answers, but only
        // from a certificate a trusted authority issued for the sender's own address, so a
        // self-made certificate in someone else's name cannot slip in.
        IEnumerable<SignatureDto> Collect(IEnumerable<IDigitalSignature> checkedSignatures)
        {
            foreach (var signature in checkedSignatures)
            {
                var described = Describe(signature, pgp, material, from);
                if (described is { Kind: "smime", Status: "valid", SenderMatches: true }
                    && (signature.SignerCertificate as SecureMimeDigitalCertificate)?.Certificate is { } certificate)
                {
                    signerCertificates.Add((certificate.GetEncoded(), described.SignerAddresses));
                }

                yield return described;
            }
        }

        var entity = message.Body;
        for (var layer = 0; layer < MaxLayers && entity is not null; layer++)
        {
            switch (entity)
            {
                case MultipartEncrypted encrypted:
                    var pgpKeys = PgpRecipients(encrypted, pgp, material);
                    if (pgpKeys.Status != "decrypted")
                    {
                        return new OpenedMessage(null, new MessageSecurityDto(pgpKeys, signatures));
                    }

                    try
                    {
                        entity = encrypted.Decrypt(pgp, out var signed, ct);
                        encryption = pgpKeys;
                        signatures.AddRange(((IEnumerable<IDigitalSignature>?)signed ?? []).Select(s => Describe(s, pgp, material, from)));
                    }
                    catch (Exception e) when (IsCryptoFailure(e))
                    {
                        return new OpenedMessage(null, new MessageSecurityDto(Failed("openpgp"), signatures));
                    }

                    continue;

                case ApplicationPkcs7Mime pkcs7 when pkcs7.SecureMimeType is SecureMimeType.EnvelopedData or SecureMimeType.AuthEnvelopedData or SecureMimeType.Unknown:
                    var smimeKeys = SmimeRecipients(pkcs7, material);
                    if (smimeKeys.Status != "decrypted")
                    {
                        return new OpenedMessage(null, new MessageSecurityDto(smimeKeys, signatures));
                    }

                    try
                    {
                        entity = pkcs7.Decrypt(smime, ct);
                        encryption = smimeKeys;
                    }
                    catch (Exception e) when (IsCryptoFailure(e))
                    {
                        return new OpenedMessage(null, new MessageSecurityDto(Failed("smime"), signatures));
                    }

                    continue;

                case ApplicationPkcs7Mime pkcs7 when pkcs7.SecureMimeType == SecureMimeType.SignedData:
                    try
                    {
                        var checkedSignatures = pkcs7.Verify(smime, out var content, ct);
                        signatures.AddRange(Collect(checkedSignatures));
                        entity = content;
                    }
                    catch (Exception e) when (IsCryptoFailure(e))
                    {
                        signatures.Add(Unreadable("smime"));
                        return new OpenedMessage(null, new MessageSecurityDto(encryption, signatures));
                    }

                    continue;

                case ApplicationPkcs7Mime pkcs7 when pkcs7.SecureMimeType == SecureMimeType.CompressedData:
                    try
                    {
                        entity = pkcs7.Decompress(smime, ct);
                    }
                    catch (Exception e) when (IsCryptoFailure(e))
                    {
                        return new OpenedMessage(null, new MessageSecurityDto(encryption ?? Failed("smime"), signatures));
                    }

                    continue;

                case MultipartSigned signed when signed.Count >= 2:
                    var protocol = signed.ContentType.Parameters["protocol"]?.Trim().ToLowerInvariant() ?? "";
                    CryptographyContext context = protocol.Contains("pgp") ? pgp : smime;
                    var kind = protocol.Contains("pgp") ? "openpgp" : "smime";
                    try
                    {
                        signatures.AddRange(Collect(signed.Verify(context, ct)));
                    }
                    catch (Exception e) when (IsCryptoFailure(e))
                    {
                        signatures.Add(Unreadable(kind));
                    }

                    entity = signed[0];
                    continue;
            }

            break;
        }

        foreach (var (der, addresses) in signerCertificates)
        {
            await store.RememberAsync(userId, CryptoKind.Smime, der, KeySources.Message, addresses.Where(from.Contains).ToList(), ct);
        }

        return new OpenedMessage(entity, new MessageSecurityDto(encryption, signatures));
    }

    /// <summary>A text that contains inline PGP: an encrypted block, or a clear-signed one.</summary>
    public static bool HasInlinePgp(string text) =>
        text.Contains("-----BEGIN PGP MESSAGE-----", StringComparison.Ordinal)
        || text.Contains("-----BEGIN PGP SIGNED MESSAGE-----", StringComparison.Ordinal);

    /// <summary>
    /// Decrypts the first inline PGP block of a plain text, or checks the first clear-signed one;
    /// the text comes back with that block replaced by what it says. Null security: nothing to do.
    /// </summary>
    public async Task<(string Text, MessageSecurityDto? Security)> OpenInlineAsync(string userId, string text, IEnumerable<string> fromAddresses, CancellationToken ct)
    {
        var from = fromAddresses.Select(a => a.ToLowerInvariant()).ToHashSet();
        if (EncryptedBlock().Match(text) is { Success: true } encrypted)
        {
            var material = await store.LoadAsync(userId, ct);
            using var pgp = new UserPgpContext(material);
            var keys = PgpStatus(RecipientKeyIds(Encoding.ASCII.GetBytes(encrypted.Value)), pgp, material);
            if (keys.Status != "decrypted")
            {
                return (text, new MessageSecurityDto(keys, []));
            }

            try
            {
                using var output = new MemoryStream();
                var signed = pgp.DecryptTo(new MemoryStream(Encoding.ASCII.GetBytes(encrypted.Value)), output, ct);
                var plain = Encoding.UTF8.GetString(output.ToArray());
                // A signed and encrypted inline block may itself be clear-signed text.
                var signatures = ((IEnumerable<IDigitalSignature>?)signed ?? []).Select(s => Describe(s, pgp, material, from)).ToList();
                if (ClearSignedBlock().Match(plain) is { Success: true } inner)
                {
                    var (innerText, innerSignatures) = VerifyClearSigned(inner.Value, pgp, material, from);
                    plain = plain[..inner.Index] + innerText + plain[(inner.Index + inner.Length)..];
                    signatures.AddRange(innerSignatures);
                }

                return (text[..encrypted.Index] + plain + text[(encrypted.Index + encrypted.Length)..], new MessageSecurityDto(keys, signatures));
            }
            catch (Exception e) when (IsCryptoFailure(e))
            {
                return (text, new MessageSecurityDto(Failed("openpgp"), []));
            }
        }

        if (ClearSignedBlock().Match(text) is { Success: true } clear)
        {
            var material = await store.LoadAsync(userId, ct);
            using var pgp = new UserPgpContext(material);
            var (cleartext, signatures) = VerifyClearSigned(clear.Value, pgp, material, from);
            return (text[..clear.Index] + cleartext + text[(clear.Index + clear.Length)..], new MessageSecurityDto(null, signatures));
        }

        return (text, null);
    }

    [GeneratedRegex(@"-----BEGIN PGP MESSAGE-----.*?-----END PGP MESSAGE-----", RegexOptions.Singleline)]
    private static partial Regex EncryptedBlock();

    [GeneratedRegex(@"-----BEGIN PGP SIGNED MESSAGE-----\r?\n.*?-----END PGP SIGNATURE-----", RegexOptions.Singleline)]
    private static partial Regex ClearSignedBlock();

    /// <summary>
    /// A clear-signed block (RFC 4880 §7): the text, dash-escaping undone, and its signatures. The
    /// signed data is the text with trailing blanks removed from each line and CRLF between lines,
    /// without the line break before the signature.
    /// </summary>
    internal static (string Text, IReadOnlyList<SignatureDto> Signatures) VerifyClearSigned(
        string block, UserPgpContext pgp, KeyMaterial material, IReadOnlySet<string> from)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        var at = 1;
        // Armour headers (Hash: …) up to the empty line.
        while (at < lines.Length && lines[at].Length > 0)
        {
            at++;
        }

        at++;
        var text = new List<string>();
        while (at < lines.Length && lines[at] != "-----BEGIN PGP SIGNATURE-----")
        {
            text.Add(lines[at].StartsWith("- ", StringComparison.Ordinal) ? lines[at][2..] : lines[at]);
            at++;
        }

        var cleartext = string.Join('\n', text);
        if (at >= lines.Length)
        {
            return (cleartext, [Unreadable("openpgp")]);
        }

        var signed = Encoding.UTF8.GetBytes(string.Join("\r\n", text.Select(l => l.TrimEnd(' ', '\t'))));
        var armored = Encoding.ASCII.GetBytes(string.Join('\n', lines[at..]));
        try
        {
            var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(new MemoryStream(armored)));
            var results = new List<SignatureDto>();
            while (factory.NextPgpObject() is { } item)
            {
                if (item is not PgpSignatureList list)
                {
                    continue;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    var signature = list[i];
                    var key = pgp.FindPublicKey(signature.KeyId);
                    var signedAt = new DateTimeOffset(DateTime.SpecifyKind(signature.CreationTime, DateTimeKind.Utc));
                    if (key is null)
                    {
                        results.Add(new SignatureDto("openpgp", "unknown_key", null, [], null, KeyIdHex(signature.KeyId), signedAt, false, null));
                        continue;
                    }

                    signature.InitVerify(key);
                    signature.Update(signed);
                    var owner = OwnerOf(signature.KeyId, pgp, material);
                    var addresses = owner?.AddressList ?? [];
                    results.Add(new SignatureDto(
                        "openpgp", signature.Verify() ? "valid" : "bad", owner?.Name, addresses, owner?.Fingerprint, KeyIdHex(signature.KeyId),
                        signedAt, addresses.Any(from.Contains), null));
                }
            }

            return (cleartext, results.Count > 0 ? results : [Unreadable("openpgp")]);
        }
        catch (Exception e) when (IsCryptoFailure(e))
        {
            return (cleartext, [Unreadable("openpgp")]);
        }
    }

    /// <summary>Which of the user's OpenPGP keys would open a PGP/MIME message, and whether one can now.</summary>
    private static EncryptionDto PgpRecipients(MultipartEncrypted encrypted, UserPgpContext pgp, KeyMaterial material)
    {
        if (encrypted.Count < 2 || encrypted[1] is not MimePart { Content: { } content })
        {
            return Failed("openpgp");
        }

        using var data = new MemoryStream();
        content.DecodeTo(data);
        return PgpStatus(RecipientKeyIds(data.ToArray()), pgp, material);
    }

    private static EncryptionDto PgpStatus(IReadOnlyList<long>? recipients, UserPgpContext pgp, KeyMaterial material)
    {
        if (recipients is null)
        {
            return Failed("openpgp");
        }

        // Key id 0 hides the recipient (--throw-keyids): any own key may be the one.
        var hidden = recipients.Contains(0);
        if (recipients.Any(pgp.HasSecretKey) || (hidden && material.Secrets.Keys.Any(id => material.Keys.Any(k => k.Id == id && k.Kind == CryptoKind.OpenPgp))))
        {
            return new EncryptionDto("openpgp", "decrypted", []);
        }

        var locked = material.Of(CryptoKind.OpenPgp)
            .Where(k => k.IsOwn && !material.Secrets.ContainsKey(k.Id))
            .Where(k => hidden || recipients.Any(id => new PgpPublicKeyRing(k.PublicData).GetPublicKey(id) is not null))
            .Select(ToRef)
            .ToList();
        return new EncryptionDto("openpgp", locked.Count > 0 ? "locked" : "no_key", locked);
    }

    /// <summary>The key ids an OpenPGP message is encrypted to (armoured or binary); null when it is not one.</summary>
    internal static IReadOnlyList<long>? RecipientKeyIds(byte[] data)
    {
        try
        {
            var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(new MemoryStream(data)));
            while (factory.NextPgpObject() is { } item)
            {
                if (item is PgpEncryptedDataList list)
                {
                    return list.GetEncryptedDataObjects().OfType<PgpPublicKeyEncryptedData>().Select(d => d.KeyId).ToList();
                }
            }
        }
        catch (Exception e) when (IsCryptoFailure(e))
        {
            // Not OpenPGP data.
        }

        return null;
    }

    /// <summary>Which of the user's certificates an S/MIME message is encrypted to, and whether one can open it now.</summary>
    private static EncryptionDto SmimeRecipients(ApplicationPkcs7Mime pkcs7, KeyMaterial material)
    {
        if (pkcs7.Content is null)
        {
            return Failed("smime");
        }

        List<RecipientID> recipients;
        try
        {
            using var data = new MemoryStream();
            pkcs7.Content.DecodeTo(data);
            recipients = new CmsEnvelopedData(data.ToArray()).GetRecipientInfos().GetRecipients().Select(r => r.RecipientID).ToList();
        }
        catch (Exception e) when (IsCryptoFailure(e))
        {
            return Failed("smime");
        }

        var matching = material.Of(CryptoKind.Smime)
            .Where(k => k.IsOwn)
            .Where(k =>
            {
                var certificate = SmimeCertificates.Read(k.PublicData);
                return recipients.Any(r => r.Match(certificate));
            })
            .ToList();
        if (matching.Any(k => material.Secrets.ContainsKey(k.Id)))
        {
            return new EncryptionDto("smime", "decrypted", []);
        }

        var locked = matching.Select(ToRef).ToList();
        return new EncryptionDto("smime", locked.Count > 0 ? "locked" : "no_key", locked);
    }

    private static SignatureDto Describe(IDigitalSignature signature, UserPgpContext pgp, KeyMaterial material, IReadOnlySet<string> from)
    {
        var signedAt = signature.CreationDate == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(signature.CreationDate, DateTimeKind.Utc));
        switch (signature)
        {
            case OpenPgpDigitalSignature openPgp:
                var keyId = (openPgp.SignerCertificate as OpenPgpDigitalCertificate)?.PublicKey.KeyId ?? SignatureKeyId(openPgp);
                if (openPgp.SignerCertificate is null || pgp.FindPublicKey(keyId) is null)
                {
                    return new SignatureDto("openpgp", "unknown_key", null, [], null, KeyIdHex(keyId), signedAt, false, null);
                }

                var owner = OwnerOf(keyId, pgp, material);
                var addresses = owner?.AddressList ?? [];
                string status;
                try
                {
                    status = openPgp.Verify() ? "valid" : "bad";
                }
                catch (DigitalSignatureVerifyException)
                {
                    status = "bad";
                }

                return new SignatureDto("openpgp", status, owner?.Name, addresses, owner?.Fingerprint, KeyIdHex(keyId), signedAt, addresses.Any(from.Contains), null);

            case SecureMimeDigitalSignature smime:
                var certificate = (smime.SignerCertificate as SecureMimeDigitalCertificate)?.Certificate;
                var (certAddresses, name) = certificate is null ? ([], null) : SmimeCertificates.Identities(certificate);
                var fingerprint = certificate is null ? null : SmimeCertificates.Fingerprint(certificate);
                bool intact;
                try
                {
                    intact = smime.Verify(true);
                }
                catch (DigitalSignatureVerifyException)
                {
                    intact = false;
                }

                string? detail = null;
                var smimeStatus = "bad";
                if (intact && certificate is null)
                {
                    smimeStatus = "unknown_key";
                }
                else if (intact)
                {
                    try
                    {
                        smimeStatus = smime.Verify(false) ? "valid" : "untrusted";
                    }
                    catch (DigitalSignatureVerifyException e)
                    {
                        smimeStatus = "untrusted";
                        detail = (e.InnerException ?? e).Message;
                    }
                }

                return new SignatureDto("smime", smimeStatus, name, certAddresses, fingerprint, null, signedAt, certAddresses.Any(from.Contains), detail);

            default:
                return Unreadable("openpgp");
        }
    }

    /// <summary>
    /// The key id a signature names when its key is unknown. MimeKit keeps the BouncyCastle
    /// signature internal, so it is read by reflection; 0 if that ever fails.
    /// </summary>
    private static long SignatureKeyId(OpenPgpDigitalSignature signature)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var type = typeof(OpenPgpDigitalSignature);
        return type.GetProperty("Signature", flags)?.GetValue(signature) is PgpSignature full ? full.KeyId
            : type.GetProperty("OnePassSignature", flags)?.GetValue(signature) is PgpOnePassSignature onePass ? onePass.KeyId
            : 0;
    }

    /// <summary>The user's key that holds the key with this id (a subkey's primary key).</summary>
    private static CryptoKey? OwnerOf(long keyId, UserPgpContext pgp, KeyMaterial material) =>
        material.Of(CryptoKind.OpenPgp).FirstOrDefault(k => new PgpPublicKeyRing(k.PublicData).GetPublicKey(keyId) is not null);

    private static string KeyIdHex(long keyId) => keyId.ToString("X16");

    private static KeyRefDto ToRef(CryptoKey key) => new(key.Id, key.Fingerprint, key.AddressList);

    private static EncryptionDto Failed(string kind) => new(kind, "failed", []);

    private static SignatureDto Unreadable(string kind) => new(kind, "bad", null, [], null, null, null, false, L.T("The signature could not be read."));

    /// <summary>What BouncyCastle and MimeKit throw on data they cannot use.</summary>
    private static bool IsCryptoFailure(Exception e) =>
        e is PgpException or CmsException or IOException or FormatException or ArgumentException or InvalidCastException
            or InvalidOperationException or NotSupportedException or Org.BouncyCastle.Crypto.CryptoException
            or Org.BouncyCastle.Security.SecurityUtilityException or PrivateKeyNotFoundException or CertificateNotFoundException
            or PublicKeyNotFoundException or DigitalSignatureVerifyException or System.Security.Cryptography.CryptographicException;
}

/// <summary>Attachments as a list shows them: not the ciphertext of encrypted mail, nor signatures or keys of the protocol.</summary>
public static class SecurityParts
{
    public static bool HasAttachments(MailKit.IMessageSummary summary)
    {
        var type = summary.Body?.ContentType;
        if (type is not null && MessageSecurity.ShapeOf(type.MediaType, type.MediaSubtype, type.Parameters["smime-type"]) == SecureShape.Encrypted)
        {
            // What it holds is unknown until it is opened.
            return false;
        }

        return summary.Attachments.Any(a => !IsProtocolPart(a.ContentType));
    }

    /// <summary>
    /// "encrypted" or "signed" as the message's structure shows it at the top (a signature inside
    /// the encryption shows only once opened), "" for neither.
    /// </summary>
    public static string Kind(MailKit.IMessageSummary summary)
    {
        var type = summary.Body?.ContentType;
        return (type is null ? SecureShape.None : MessageSecurity.ShapeOf(type.MediaType, type.MediaSubtype, type.Parameters["smime-type"])) switch
        {
            SecureShape.Encrypted => "encrypted",
            SecureShape.Signed => "signed",
            _ => "",
        };
    }

    /// <summary>The list's value: null for neither.</summary>
    public static string? ForClient(string? kind) => string.IsNullOrEmpty(kind) ? null : kind;

    private static bool IsProtocolPart(ContentType type) =>
        type.IsMimeType("application", "pgp-signature") || type.IsMimeType("application", "pgp-encrypted")
        || type.IsMimeType("application", "pkcs7-signature") || type.IsMimeType("application", "x-pkcs7-signature");
}
