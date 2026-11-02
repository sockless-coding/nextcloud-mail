using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;

namespace SocklessMail.Api.Crypto;

/// <summary>
/// MimeKit's OpenPGP operations over one user's keys, in memory: the public rings of all their
/// OpenPGP keys, and the secret rings of the own keys that can be used now (see
/// <see cref="CryptoKeyStore.LoadAsync"/>). Keys are never fetched from key servers here.
/// </summary>
public sealed class UserPgpContext : OpenPgpContext
{
    private PgpPublicKeyRingBundle _public = new(Array.Empty<byte>());
    private PgpSecretKeyRingBundle _secret = new(Array.Empty<byte>());
    private readonly Dictionary<long, string> _passphrases = [];

    public UserPgpContext(KeyMaterial material)
    {
        AutoKeyRetrieve = false;
        foreach (var key in material.Of(CryptoKind.OpenPgp))
        {
            AddPublic(new PgpPublicKeyRing(key.PublicData));
            if (material.Secrets.TryGetValue(key.Id, out var secret) && OpenPgpKeys.Parse(secret).FirstOrDefault()?.Secret is { } ring)
            {
                AddSecret(ring);
                foreach (var secretKey in ring.GetSecretKeys())
                {
                    _passphrases[secretKey.KeyId] = material.Passphrases[key.Id];
                }
            }
        }
    }

    /// <summary>The key with this id (primary or subkey), from any of the user's OpenPGP keys.</summary>
    public PgpPublicKey? FindPublicKey(long keyId) => _public.GetPublicKey(keyId);

    public bool HasSecretKey(long keyId) => _secret.GetSecretKey(keyId) is not null;

    protected override string GetPasswordForKey(PgpSecretKey key) => _passphrases.GetValueOrDefault(key.KeyId, "");

    protected override PgpPublicKeyRing? GetPublicKeyRing(long keyId, CancellationToken cancellationToken) =>
        _public.GetPublicKeyRing(keyId);

    protected override Task<PgpPublicKeyRing?> GetPublicKeyRingAsync(long keyId, CancellationToken cancellationToken) =>
        Task.FromResult<PgpPublicKeyRing?>(_public.GetPublicKeyRing(keyId));

    protected override PgpSecretKey GetSecretKey(long keyId, CancellationToken cancellationToken) =>
        _secret.GetSecretKey(keyId) ?? throw new PrivateKeyNotFoundException(keyId, "None of your private keys opens this message.");

    /// <summary>For each mailbox, the newest usable encryption key of a key whose user ids name it.</summary>
    public override IList<PgpPublicKey> GetPublicKeys(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default)
    {
        var keys = new List<PgpPublicKey>();
        foreach (var mailbox in mailboxes)
        {
            var key = _public.GetKeyRings()
                .Where(ring => Names(ring.GetPublicKey(), mailbox) && !ring.GetPublicKey().HasRevocation() && !IsExpired(ring.GetPublicKey()))
                .SelectMany(ring => ring.GetPublicKeys())
                .Where(k => k.IsEncryptionKey && !k.HasRevocation() && !IsExpired(k))
                .MaxBy(k => k.CreationTime);
            keys.Add(key ?? throw new PublicKeyNotFoundException(mailbox, "No public key was found for " + mailbox.Address + "."));
        }

        return keys;
    }

    public override PgpSecretKey GetSigningKey(MailboxAddress mailbox, CancellationToken cancellationToken = default) =>
        _secret.GetKeyRings()
            .Where(ring => Names(ring.GetPublicKey(), mailbox) && !ring.GetPublicKey().HasRevocation() && !IsExpired(ring.GetPublicKey()))
            .SelectMany(ring => ring.GetSecretKeys())
            .Where(k => k.IsSigningKey && !k.IsPrivateKeyEmpty && !k.PublicKey.HasRevocation() && !IsExpired(k.PublicKey))
            .MaxBy(k => k.PublicKey.CreationTime)
        ?? throw new PrivateKeyNotFoundException(mailbox, "No private key to sign with was found for " + mailbox.Address + ".");

    public override bool CanSign(MailboxAddress signer, CancellationToken cancellationToken = default)
    {
        try
        {
            GetSigningKey(signer, cancellationToken);
            return true;
        }
        catch (PrivateKeyNotFoundException)
        {
            return false;
        }
    }

    public override bool CanEncrypt(MailboxAddress mailbox, CancellationToken cancellationToken = default)
    {
        try
        {
            GetPublicKeys([mailbox], cancellationToken);
            return true;
        }
        catch (PublicKeyNotFoundException)
        {
            return false;
        }
    }

    public override MimePart Export(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) =>
        Export(GetPublicKeys(mailboxes, cancellationToken), cancellationToken);

    public override Task<MimePart> ExportAsync(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) =>
        Task.FromResult(Export(mailboxes, cancellationToken));

    public override void Export(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) =>
        Export(GetPublicKeys(mailboxes, cancellationToken), stream, armor, cancellationToken);

    public override Task ExportAsync(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default)
    {
        Export(mailboxes, stream, armor, cancellationToken);
        return Task.CompletedTask;
    }

    private static bool Names(PgpPublicKey primary, MailboxAddress mailbox) =>
        OpenPgpKeys.Identities(primary).Addresses.Contains(mailbox.Address, StringComparer.OrdinalIgnoreCase);

    public override void Import(PgpPublicKeyRing keyring, CancellationToken cancellationToken = default) => AddPublic(keyring);

    public override void Import(PgpPublicKeyRingBundle bundle, CancellationToken cancellationToken = default)
    {
        foreach (var ring in bundle.GetKeyRings())
        {
            AddPublic(ring);
        }
    }

    public override void Import(PgpSecretKeyRing keyring, CancellationToken cancellationToken = default) => AddSecret(keyring);

    public override void Import(PgpSecretKeyRingBundle bundle, CancellationToken cancellationToken = default)
    {
        foreach (var ring in bundle.GetKeyRings())
        {
            AddSecret(ring);
        }
    }

    private void AddPublic(PgpPublicKeyRing ring)
    {
        if (!_public.Contains(ring.GetPublicKey().KeyId))
        {
            _public = PgpPublicKeyRingBundle.AddPublicKeyRing(_public, ring);
        }
    }

    private void AddSecret(PgpSecretKeyRing ring)
    {
        if (!_secret.Contains(ring.GetSecretKey().KeyId))
        {
            _secret = PgpSecretKeyRingBundle.AddSecretKeyRing(_secret, ring);
        }
    }
}

/// <summary>
/// MimeKit's S/MIME operations over one user's certificates, in memory: every certificate they
/// keep, the private keys of the own ones that can be used now, and the system's root
/// certificates as trust anchors. Revocation is not checked (that would fetch CRLs from addresses
/// the message names).
/// </summary>
public sealed class UserSmimeContext : TemporarySecureMimeContext
{
    private static readonly Lazy<IReadOnlyList<TrustAnchor>> SystemAnchors = new(LoadSystemAnchors);

    public UserSmimeContext(KeyMaterial material)
    {
        CheckCertificateRevocation = false;
        foreach (var key in material.Of(CryptoKind.Smime))
        {
            if (material.Secrets.TryGetValue(key.Id, out var pkcs12))
            {
                Import(new MemoryStream(pkcs12), material.Passphrases[key.Id]);
            }
            else
            {
                Import(SmimeCertificates.Read(key.PublicData));
            }
        }
    }

    protected override ISet<TrustAnchor> GetTrustedAnchors()
    {
        var anchors = base.GetTrustedAnchors();
        anchors.UnionWith(SystemAnchors.Value);
        return anchors;
    }

    private static IReadOnlyList<TrustAnchor> LoadSystemAnchors()
    {
        var anchors = new List<TrustAnchor>();
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var parser = new X509CertificateParser();
            foreach (var certificate in store.Certificates)
            {
                try
                {
                    anchors.Add(new TrustAnchor(parser.ReadCertificate(certificate.RawData), null));
                }
                catch (Exception e) when (e is Org.BouncyCastle.Security.Certificates.CertificateException or ArgumentException)
                {
                    // One unreadable root does not spoil the others.
                }
            }
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            // No system store: only certificates the user imported are trusted.
        }

        return anchors;
    }
}
