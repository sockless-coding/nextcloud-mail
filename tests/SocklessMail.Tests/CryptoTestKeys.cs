using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Crypto;

namespace SocklessMail.Tests;

/// <summary>Keys and certificates for the encryption tests, made once per test run (RSA keys take a moment).</summary>
internal static class CryptoTestKeys
{
    public const string Passphrase = "correct horse battery";

    private static readonly Lazy<PgpSecretKeyRing> AliceRing = new(() => OpenPgpKeys.Generate(new MailboxAddress("Alice", "alice@example.com"), Passphrase, DateTimeOffset.UtcNow.AddMinutes(-1), null));
    private static readonly Lazy<PgpSecretKeyRing> BobRing = new(() => OpenPgpKeys.Generate(new MailboxAddress("Bob", "bob@example.org"), Passphrase, DateTimeOffset.UtcNow.AddMinutes(-1), null));
    private static readonly Lazy<byte[]> AliceP12 = new(() => SelfSigned("Alice", "alice@example.com", Passphrase));
    private static readonly Lazy<byte[]> BobP12 = new(() => SelfSigned("Bob", "bob@example.org", ""));

    public static PgpSecretKeyRing AlicePgp => AliceRing.Value;

    public static PgpSecretKeyRing BobPgp => BobRing.Value;

    public static byte[] AliceSmime => AliceP12.Value;

    public static byte[] BobSmime => BobP12.Value;

    public static string ArmoredPublic(PgpSecretKeyRing ring) => OpenPgpKeys.Armor(OpenPgpKeys.PublicRingOf(ring));

    public static string ArmoredSecret(PgpSecretKeyRing ring)
    {
        using var buffer = new MemoryStream();
        using (var armored = new Org.BouncyCastle.Bcpg.ArmoredOutputStream(buffer))
        {
            ring.Encode(armored);
        }

        return System.Text.Encoding.ASCII.GetString(buffer.ToArray());
    }

    public static byte[] PublicCertificate(byte[] pkcs12, string password) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, password).Export(X509ContentType.Cert);

    /// <summary>A sender's OpenPGP context holding these secret rings (unlocked) and public rings.</summary>
    public static UserPgpContext PgpSender(IEnumerable<PgpSecretKeyRing> secrets, IEnumerable<PgpSecretKeyRing> publics)
    {
        var keys = new List<CryptoKey>();
        var opened = new Dictionary<Guid, byte[]>();
        var passphrases = new Dictionary<Guid, string>();
        foreach (var ring in secrets)
        {
            var key = Key(CryptoKind.OpenPgp, OpenPgpKeys.PublicRingOf(ring).GetEncoded());
            keys.Add(key);
            opened[key.Id] = ring.GetEncoded();
            passphrases[key.Id] = Passphrase;
        }

        keys.AddRange(publics.Select(ring => Key(CryptoKind.OpenPgp, OpenPgpKeys.PublicRingOf(ring).GetEncoded())));
        return new UserPgpContext(new KeyMaterial(keys, opened, passphrases));
    }

    private static CryptoKey Key(CryptoKind kind, byte[] publicData) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = "sender",
        Kind = kind,
        Fingerprint = Guid.NewGuid().ToString("N"),
        Addresses = "",
        PublicData = publicData,
        ProtectedSecret = "x",
    };

    private static byte[] SelfSigned(string name, string address, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, E={address}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.4")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pkcs12, password);
    }
}
