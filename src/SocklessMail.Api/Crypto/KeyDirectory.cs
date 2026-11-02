using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Contacts;

namespace SocklessMail.Api.Crypto;

/// <param name="Id">The stored key.</param>
/// <param name="Source">Where it came from (<see cref="KeySources"/>).</param>
public sealed record RecipientKeyDto(Guid Id, string Fingerprint, string Source, DateTimeOffset? ExpiresAt);

/// <summary>Which keys a recipient can be encrypted to; null where there is none.</summary>
public sealed record RecipientKeysDto(string Address, RecipientKeyDto? OpenPgp, RecipientKeyDto? Smime);

/// <param name="Search">Also look in Contacts, the recipients' domains (WKD) and keys.openpgp.org; otherwise only keys already kept.</param>
public sealed record RecipientLookupInput(IReadOnlyList<string>? Addresses, bool Search = false);

/// <summary>Which keys and certificates are usable now: not expired or revoked, and able to encrypt.</summary>
public static class KeyUsability
{
    public static bool IsExpired(PgpPublicKey key, DateTimeOffset now)
    {
        var seconds = key.GetValidSeconds();
        return seconds > 0 && new DateTimeOffset(DateTime.SpecifyKind(key.CreationTime, DateTimeKind.Utc)).AddSeconds(seconds) <= now;
    }

    /// <summary>The newest usable encryption key of a ring (a subkey, normally), or null.</summary>
    public static PgpPublicKey? EncryptionKey(PgpPublicKeyRing ring, DateTimeOffset now)
    {
        var primary = ring.GetPublicKey();
        if (primary.HasRevocation() || IsExpired(primary, now))
        {
            return null;
        }

        return ring.GetPublicKeys()
            .Where(k => k.IsEncryptionKey && !k.HasRevocation() && !IsExpired(k, now))
            .MaxBy(k => k.CreationTime);
    }

    /// <summary>Whether a stored key or certificate can be encrypted to now.</summary>
    public static bool CanEncrypt(CryptoKey key, DateTimeOffset now)
    {
        try
        {
            if (key.Kind == CryptoKind.OpenPgp)
            {
                return EncryptionKey(new PgpPublicKeyRing(key.PublicData), now) is not null;
            }

            var certificate = SmimeCertificates.Read(key.PublicData);
            var usage = certificate.GetKeyUsage();
            // keyEncipherment (2) for RSA, keyAgreement (4) for elliptic curves; no extension allows both.
            return certificate.NotBefore <= now.UtcDateTime && certificate.NotAfter > now.UtcDateTime
                   && (usage is null || usage[2] || usage[4]);
        }
        catch (Exception e) when (e is IOException or PgpException or ArgumentException or Org.BouncyCastle.Security.Certificates.CertificateException)
        {
            return false;
        }
    }

    /// <summary>The key of <paramref name="kind"/> to encrypt to <paramref name="address"/>: the newest usable one naming it.</summary>
    public static CryptoKey? For(IEnumerable<CryptoKey> keys, CryptoKind kind, string address, DateTimeOffset now) =>
        keys.Where(k => k.Kind == kind && k.AddressList.Contains(address, StringComparer.OrdinalIgnoreCase) && CanEncrypt(k, now))
            .OrderByDescending(k => k.IsOwn)
            .ThenByDescending(k => k.CreatedAt)
            .FirstOrDefault();
}

/// <summary>
/// Finds the keys to encrypt to recipients: those the user keeps, then (when asked to search)
/// keys in their contacts, then OpenPGP keys published for the address (<see cref="PublicKeyFinder"/>).
/// What is found is kept as a correspondent's key, so it shows in the settings and can be removed.
/// </summary>
public sealed class KeyDirectory(
    CryptoKeyStore store, PublicKeyFinder finder, NextcloudContacts contacts, ContactDirectory directory, TimeProvider clock, ILogger<KeyDirectory> logger)
{
    public const int MaxAddresses = 100;

    /// <summary>Recipients looked up on the internet at once.</summary>
    private const int Parallel = 4;

    public async Task<IReadOnlyList<RecipientKeysDto>> LookupAsync(string userId, IReadOnlyList<string> addresses, bool search, CancellationToken ct)
    {
        var wanted = addresses.Select(a => a.Trim()).Where(a => a.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxAddresses).ToList();
        var now = clock.GetUtcNow();
        var keys = (await store.LoadAsync(userId, ct)).Keys.ToList();

        if (search)
        {
            var missing = wanted.Where(a => KeyUsability.For(keys, CryptoKind.OpenPgp, a, now) is null).ToList();
            if (missing.Count > 0)
            {
                foreach (var key in await FromContactsAsync(userId, missing, ct))
                {
                    keys.Add(key);
                }

                missing = missing.Where(a => KeyUsability.For(keys, CryptoKind.OpenPgp, a, now) is null).ToList();
                var found = new List<(string Address, byte[] Data, string Source)>();
                await System.Threading.Tasks.Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (address, token) =>
                {
                    if (await finder.FindAsync(address, token) is { } result)
                    {
                        lock (found)
                        {
                            found.Add((address, result.Data, result.Source));
                        }
                    }
                });

                foreach (var (address, data, source) in found)
                {
                    if (await store.RememberAsync(userId, CryptoKind.OpenPgp, data, source, [address], ct) is { } key)
                    {
                        keys.Add(key);
                    }
                }
            }
        }

        return wanted.Select(a => new RecipientKeysDto(a, Describe(KeyUsability.For(keys, CryptoKind.OpenPgp, a, now)), Describe(KeyUsability.For(keys, CryptoKind.Smime, a, now)))).ToList();
    }

    private static RecipientKeyDto? Describe(CryptoKey? key) => key is null ? null : new(key.Id, key.Fingerprint, key.Source, key.ExpiresAt);

    /// <summary>OpenPGP keys and certificates in the user's contacts for these addresses, kept.</summary>
    private async Task<List<CryptoKey>> FromContactsAsync(string userId, IReadOnlyList<string> addresses, CancellationToken ct)
    {
        var kept = new List<CryptoKey>();
        IReadOnlyList<(AddressBook Book, IReadOnlyList<Contact> Contacts)> books;
        try
        {
            books = await directory.GetAsync(userId, contacts, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Contacts are one more place to look, not a requirement (the app may be off, the answer unreadable).
            logger.LogDebug("Contacts could not be read for key lookup ({ExceptionType})", e.GetType().Name);
            return kept;
        }

        foreach (var contact in books.SelectMany(b => b.Contacts))
        {
            var emails = contact.Card.Emails.Where(e => addresses.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (emails.Count == 0)
            {
                continue;
            }

            foreach (var data in contact.Card.Keys ?? [])
            {
                var kind = LooksLikeCertificate(data) ? CryptoKind.Smime : CryptoKind.OpenPgp;
                if (await store.RememberAsync(userId, kind, data, KeySources.Contacts, emails, ct) is { } key)
                {
                    kept.Add(key);
                }
            }
        }

        return kept;
    }

    /// <summary>DER certificates are a SEQUENCE; OpenPGP packets start with a tag byte with the high bit set (0x99, 0xC6, …) or armour.</summary>
    private static bool LooksLikeCertificate(byte[] data) => data.Length > 2 && data[0] == 0x30;
}
