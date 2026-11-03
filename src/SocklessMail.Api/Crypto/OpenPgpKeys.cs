using System.Text;
using MimeKit;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace SocklessMail.Api.Crypto;

/// <summary>An OpenPGP key read from a file: its public ring, and its secret ring when the file had one.</summary>
public sealed record ParsedPgpKey(PgpPublicKeyRing Public, PgpSecretKeyRing? Secret)
{
    public PgpPublicKey Primary => Public.GetPublicKey();

    public string Fingerprint => Convert.ToHexString(Primary.GetFingerprint());

    /// <summary>The private key needs a passphrase: one of its secret keys is encrypted.</summary>
    public bool HasPassphrase => Secret?.GetSecretKeys().Any(k => !k.IsPrivateKeyEmpty && k.KeyEncryptionAlgorithm != SymmetricKeyAlgorithmTag.Null) ?? false;
}

/// <summary>Reading, writing and making OpenPGP keys (BouncyCastle).</summary>
public static class OpenPgpKeys
{
    /// <summary>
    /// Every key in <paramref name="data"/>, ASCII-armoured or binary; one file may hold several
    /// keys and armoured blocks. A secret ring and the public ring of the same key become one entry.
    /// Secret rings whose private keys are all stubs (on a smartcard, say) count as public only.
    /// </summary>
    /// <exception cref="FormatException">Nothing in the data is an OpenPGP key.</exception>
    public static IReadOnlyList<ParsedPgpKey> Parse(byte[] data)
    {
        var keys = new Dictionary<string, ParsedPgpKey>(StringComparer.Ordinal);
        var input = new MemoryStream(data);
        try
        {
            while (input.Position < input.Length)
            {
                var start = input.Position;
                var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(input));
                var found = false;
                while (factory.NextPgpObject() is { } item)
                {
                    found = true;
                    switch (item)
                    {
                        case PgpSecretKeyRing secret:
                            var parsed = new ParsedPgpKey(PublicRingOf(secret), secret.GetSecretKeys().All(k => k.IsPrivateKeyEmpty) ? null : secret);
                            keys[parsed.Fingerprint] = parsed with { Public = keys.TryGetValue(parsed.Fingerprint, out var known) ? known.Public : parsed.Public };
                            break;
                        case PgpPublicKeyRing ring:
                            var fingerprint = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());
                            keys[fingerprint] = keys.TryGetValue(fingerprint, out var withSecret)
                                ? withSecret with { Public = ring }
                                : new ParsedPgpKey(ring, null);
                            break;
                    }
                }

                // Text after the last block (or nothing read at all) ends the loop.
                if (!found || input.Position == start)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or PgpException or ArgumentException or InvalidOperationException or InvalidCastException)
        {
            if (keys.Count == 0)
            {
                throw new FormatException(L.T("This is not an OpenPGP key."), e);
            }
        }

        return keys.Count > 0 ? keys.Values.ToList() : throw new FormatException(L.T("This is not an OpenPGP key."));
    }

    /// <summary>The public keys of a secret ring, as a public ring.</summary>
    public static PgpPublicKeyRing PublicRingOf(PgpSecretKeyRing secret)
    {
        using var buffer = new MemoryStream();
        foreach (var key in secret.GetPublicKeys())
        {
            key.Encode(buffer);
        }

        // Extra public keys (subkeys without a secret part) travel alongside.
        foreach (var key in secret.GetExtraPublicKeys())
        {
            key.Encode(buffer);
        }

        buffer.Position = 0;
        return new PgpPublicKeyRing(buffer);
    }

    /// <summary>The e-mail addresses of the key's user ids, lower case, and the first user id's name.</summary>
    public static (IReadOnlyList<string> Addresses, string? Name) Identities(PgpPublicKey primary)
    {
        var addresses = new List<string>();
        string? name = null;
        foreach (var userId in primary.GetUserIds())
        {
            if (!MailboxAddress.TryParse(userId, out var mailbox) || !mailbox.Address.Contains('@'))
            {
                name ??= string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
                continue;
            }

            name ??= string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name.Trim();
            var address = mailbox.Address.ToLowerInvariant();
            if (!addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }

        return (addresses, name);
    }

    public static DateTimeOffset? Expires(PgpPublicKey primary)
    {
        var seconds = primary.GetValidSeconds();
        return seconds > 0 ? new DateTimeOffset(DateTime.SpecifyKind(primary.CreationTime, DateTimeKind.Utc)).AddSeconds(seconds) : null;
    }

    public static string Armor(PgpPublicKeyRing ring)
    {
        using var buffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(buffer))
        {
            ring.Encode(armored);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>Whether <paramref name="passphrase"/> opens the key's private keys.</summary>
    public static bool Opens(PgpSecretKeyRing secret, string passphrase)
    {
        try
        {
            foreach (var key in secret.GetSecretKeys().Where(k => !k.IsPrivateKeyEmpty))
            {
                key.ExtractPrivateKey(passphrase.ToCharArray());
            }

            return true;
        }
        catch (PgpException)
        {
            return false;
        }
    }

    /// <summary>
    /// A new key for <paramref name="mailbox"/>: an RSA-3072 primary key for certifying and
    /// signing with an RSA-3072 subkey for encryption, the private keys protected by
    /// <paramref name="passphrase"/> (AES-256, iterated and salted S2K). RSA rather than the
    /// newer curves, because every OpenPGP implementation reads it.
    /// </summary>
    public static PgpSecretKeyRing Generate(MailboxAddress mailbox, string passphrase, DateTimeOffset now, TimeSpan? validFor)
    {
        var random = new SecureRandom();
        var rsa = new RsaKeyPairGenerator();
        rsa.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), random, 3072, 100));
        var created = now.UtcDateTime;
        var primary = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, rsa.GenerateKeyPair(), created);
        var encryption = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, rsa.GenerateKeyPair(), created);

        var primaryPackets = new PgpSignatureSubpacketGenerator();
        primaryPackets.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        primaryPackets.SetPreferredSymmetricAlgorithms(false, [(int)SymmetricKeyAlgorithmTag.Aes256, (int)SymmetricKeyAlgorithmTag.Aes192, (int)SymmetricKeyAlgorithmTag.Aes128]);
        primaryPackets.SetPreferredHashAlgorithms(false, [(int)HashAlgorithmTag.Sha512, (int)HashAlgorithmTag.Sha384, (int)HashAlgorithmTag.Sha256]);
        primaryPackets.SetPreferredCompressionAlgorithms(false, [(int)CompressionAlgorithmTag.Zip, (int)CompressionAlgorithmTag.ZLib, (int)CompressionAlgorithmTag.Uncompressed]);
        primaryPackets.SetFeature(false, Org.BouncyCastle.Bcpg.Sig.Features.FEATURE_MODIFICATION_DETECTION);
        var subkeyPackets = new PgpSignatureSubpacketGenerator();
        subkeyPackets.SetKeyFlags(false, PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage);
        if (validFor is { } valid)
        {
            primaryPackets.SetKeyExpirationTime(false, (long)valid.TotalSeconds);
            subkeyPackets.SetKeyExpirationTime(false, (long)valid.TotalSeconds);
        }

        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification, primary, mailbox.ToString(true), SymmetricKeyAlgorithmTag.Aes256,
            passphrase.ToCharArray(), true, primaryPackets.Generate(), null, random);
        generator.AddSubKey(encryption, subkeyPackets.Generate(), null);
        return generator.GenerateSecretKeyRing();
    }
}
