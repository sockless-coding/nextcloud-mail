using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Crypto;

/// <summary>A user's keys as the crypto contexts need them; private parts only of keys that can be used now.</summary>
/// <param name="Locked">Own keys with a passphrase that are not unlocked.</param>
public sealed record KeyMaterial(IReadOnlyList<CryptoKey> Keys, IReadOnlyDictionary<Guid, byte[]> Secrets, IReadOnlyDictionary<Guid, string> Passphrases)
{
    public IEnumerable<CryptoKey> Of(CryptoKind kind) => Keys.Where(k => k.Kind == kind);
}

/// <summary>A user's OpenPGP keys and S/MIME certificates: importing, making, listing, unlocking.</summary>
public sealed class CryptoKeyStore(MailDbContext db, KeySecrets secrets, KeyUnlocks unlocks, DecryptedMessages decrypted, TimeProvider clock)
{
    public const int MaxKeysPerUser = 500;
    public const int MaxImportBytes = 1024 * 1024;
    public const int MinPassphraseLength = 8;
    public const int MaxPassphraseLength = 1000;

    public async Task<IReadOnlyList<CryptoKeyDto>> ListAsync(string userId, CancellationToken ct)
    {
        var keys = await db.CryptoKeys.Where(k => k.OwnerUserId == userId).ToListAsync(ct);
        var unlocked = unlocks.Unlocked(userId);
        return keys
            .OrderByDescending(k => k.IsOwn)
            .ThenBy(k => k.AddressList.FirstOrDefault() ?? "", StringComparer.Ordinal)
            .ThenBy(k => k.Kind)
            .Select(k => ToDto(k, unlocked))
            .ToList();
    }

    public async Task<CryptoKey?> FindAsync(string userId, Guid id, CancellationToken ct) =>
        await db.CryptoKeys.FirstOrDefaultAsync(k => k.OwnerUserId == userId && k.Id == id, ct);

    public async Task<ImportKeyResult> ImportAsync(string userId, ImportKeyInput input, CancellationToken ct)
    {
        var data = ReadInput(input);
        var passphrase = input.Passphrase ?? "";
        var found = new List<CryptoKey>();
        if (SmimeCertificates.LooksLikePkcs12(data))
        {
            IReadOnlyList<ParsedCertificate> certificates;
            try
            {
                certificates = SmimeCertificates.ParsePkcs12(data, passphrase);
            }
            catch (CryptographicException)
            {
                throw new MailboxValidationException("passphrase", input.Passphrase is null or "" ? "Enter the file's password." : "The password is wrong.");
            }
            catch (FormatException e)
            {
                throw new MailboxValidationException("data", e.Message);
            }

            found.AddRange(certificates.Select(c => FromCertificate(userId, c, passphrase.Length > 0)));
        }
        else if (TryParsePgp(data) is { } pgp)
        {
            foreach (var key in pgp)
            {
                if (key.Secret is { } secret && !OpenPgpKeys.Opens(secret, passphrase))
                {
                    throw new MailboxValidationException("passphrase", input.Passphrase is null or "" ? "Enter the key's passphrase." : "The passphrase is wrong.");
                }

                found.Add(FromPgp(userId, key));
            }
        }
        else
        {
            try
            {
                found.AddRange(SmimeCertificates.ParseCertificates(data).Select(c => FromCertificate(userId, c, false)));
            }
            catch (FormatException)
            {
                throw new MailboxValidationException("data", "This is not an OpenPGP key, a certificate or a PKCS#12 file.");
            }
        }

        var added = new List<CryptoKey>();
        var updated = new List<CryptoKey>();
        foreach (var key in found)
        {
            var known = await db.CryptoKeys.FirstOrDefaultAsync(k => k.OwnerUserId == userId && k.Kind == key.Kind && k.Fingerprint == key.Fingerprint, ct);
            if (known is null)
            {
                db.CryptoKeys.Add(key);
                added.Add(key);
                continue;
            }

            known.Addresses = key.Addresses;
            known.Name = key.Name;
            known.PublicData = key.PublicData;
            if (key.ProtectedSecret is not null)
            {
                // Re-protected under the existing row's id.
                known.ProtectedSecret = secrets.Protect(known.Id, secrets.Unprotect(key.Id, key.ProtectedSecret));
                known.HasPassphrase = key.HasPassphrase;
                unlocks.Lock(userId, known.Id);
            }

            updated.Add(known);
        }

        if (await db.CryptoKeys.CountAsync(k => k.OwnerUserId == userId, ct) + added.Count > MaxKeysPerUser)
        {
            throw new MailboxValidationException("data", $"You can keep at most {MaxKeysPerUser} keys.");
        }

        await db.SaveChangesAsync(ct);
        var unlocked = unlocks.Unlocked(userId);
        return new ImportKeyResult(added.Select(k => ToDto(k, unlocked)).ToList(), updated.Select(k => ToDto(k, unlocked)).ToList());
    }

    public async Task<CryptoKeyDto> GenerateAsync(string userId, GenerateKeyInput input, IReadOnlyCollection<string> ownAddresses, CancellationToken ct)
    {
        var address = input.Address?.Trim() ?? "";
        if (!ownAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            throw new MailboxValidationException("address", "Choose the address of one of your accounts.");
        }

        var passphrase = input.Passphrase ?? "";
        if (passphrase.Length is < MinPassphraseLength or > MaxPassphraseLength)
        {
            throw new MailboxValidationException("passphrase", $"Use a passphrase of at least {MinPassphraseLength} characters.");
        }

        var years = input.ValidYears ?? 3;
        if (years is < 0 or > 10)
        {
            throw new MailboxValidationException("validYears", "Choose between 1 and 10 years, or no expiry.");
        }

        var name = input.Name?.Trim();
        if (name?.Length > 200)
        {
            throw new MailboxValidationException("name", "The name is too long.");
        }

        var mailbox = new MailboxAddress(string.IsNullOrEmpty(name) ? null : name, address);
        var now = clock.GetUtcNow();
        var validFor = years == 0 ? (TimeSpan?)null : now.AddYears(years) - now;
        // Two RSA-3072 keys take a few seconds; off the request thread's way.
        var secret = await Task.Run(() => OpenPgpKeys.Generate(mailbox, passphrase, now, validFor), ct);
        var key = FromPgp(userId, new ParsedPgpKey(OpenPgpKeys.PublicRingOf(secret), secret));
        key.Source = KeySources.Generated;
        db.CryptoKeys.Add(key);
        await db.SaveChangesAsync(ct);
        unlocks.Unlock(userId, key.Id, passphrase);
        return ToDto(key, unlocks.Unlocked(userId));
    }

    public async Task<bool> DeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } key)
        {
            return false;
        }

        db.CryptoKeys.Remove(key);
        await db.SaveChangesAsync(ct);
        unlocks.Lock(userId, id);
        decrypted.Forget(userId);
        return true;
    }

    /// <summary>Unlocks an own key with its passphrase; false when the passphrase is wrong.</summary>
    /// <exception cref="KeyUnavailableException">The stored private key cannot be decrypted.</exception>
    public bool Unlock(string userId, CryptoKey key, string passphrase)
    {
        if (key.ProtectedSecret is null)
        {
            throw new MailboxValidationException("id", "This is not one of your own keys.");
        }

        var secret = secrets.Unprotect(key.Id, key.ProtectedSecret);
        var opens = key.Kind == CryptoKind.OpenPgp
            ? OpenPgpKeys.Parse(secret).FirstOrDefault()?.Secret is { } ring && OpenPgpKeys.Opens(ring, passphrase)
            : SmimeCertificates.Opens(secret, passphrase);
        if (opens)
        {
            unlocks.Unlock(userId, key.Id, passphrase);
        }

        return opens;
    }

    /// <summary>
    /// Keeps a correspondent's public key or certificate found elsewhere (<paramref name="source"/>,
    /// see <see cref="KeySources"/>), unless the user already has it. Returns the stored key, or
    /// null when it names none of <paramref name="addresses"/> (a key must be for the address it was
    /// found for).
    /// </summary>
    public async Task<CryptoKey?> RememberAsync(string userId, CryptoKind kind, byte[] data, string source, IReadOnlyCollection<string> addresses, CancellationToken ct)
    {
        CryptoKey key;
        try
        {
            key = kind == CryptoKind.OpenPgp
                ? FromPgp(userId, OpenPgpKeys.Parse(data)[0] with { Secret = null })
                : FromCertificate(userId, new ParsedCertificate(SmimeCertificates.Read(data), null), false);
        }
        catch (Exception e) when (e is FormatException or IOException or ArgumentException or Org.BouncyCastle.Security.Certificates.CertificateException or InvalidCastException)
        {
            return null;
        }

        if (!key.AddressList.Any(a => addresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (await db.CryptoKeys.FirstOrDefaultAsync(k => k.OwnerUserId == userId && k.Kind == kind && k.Fingerprint == key.Fingerprint, ct) is { } known)
        {
            return known;
        }

        if (await db.CryptoKeys.CountAsync(k => k.OwnerUserId == userId, ct) >= MaxKeysPerUser)
        {
            return null;
        }

        key.Source = source;
        db.CryptoKeys.Add(key);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Stored meanwhile by another request.
            db.Entry(key).State = EntityState.Detached;
            return await db.CryptoKeys.AsNoTracking().FirstOrDefaultAsync(k => k.OwnerUserId == userId && k.Kind == kind && k.Fingerprint == key.Fingerprint, ct);
        }

        return key;
    }

    public void LockAll(string userId)
    {
        unlocks.LockAll(userId);
        decrypted.Forget(userId);
    }

    /// <summary>The public key to hand out: an armoured OpenPGP key (.asc) or a PEM certificate.</summary>
    public static (string FileName, string ContentType, string Text) PublicExport(CryptoKey key)
    {
        var stem = string.Concat((key.AddressList.FirstOrDefault() ?? key.Fingerprint[^16..]).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@'));
        return key.Kind == CryptoKind.OpenPgp
            ? ($"{stem}.asc", "application/pgp-keys", OpenPgpKeys.Armor(new PgpPublicKeyRing(key.PublicData)))
            : ($"{stem}.pem", "application/x-pem-file", SmimeCertificates.Pem(SmimeCertificates.Read(key.PublicData)));
    }

    /// <summary>
    /// All of a user's keys, with the private parts of own keys that need no passphrase or are
    /// unlocked. Own keys the app's key ring can no longer open are left as public keys.
    /// </summary>
    public async Task<KeyMaterial> LoadAsync(string userId, CancellationToken ct)
    {
        var keys = await db.CryptoKeys.AsNoTracking().Where(k => k.OwnerUserId == userId).ToListAsync(ct);
        var opened = new Dictionary<Guid, byte[]>();
        var passphrases = new Dictionary<Guid, string>();
        foreach (var key in keys.Where(k => k.IsOwn))
        {
            var passphrase = key.HasPassphrase ? unlocks.Passphrase(userId, key.Id) : "";
            if (passphrase is null)
            {
                continue;
            }

            try
            {
                opened[key.Id] = secrets.Unprotect(key.Id, key.ProtectedSecret!);
                passphrases[key.Id] = passphrase;
            }
            catch (KeyUnavailableException)
            {
                // Treated as locked for good; the key list shows it as unusable.
            }
        }

        return new KeyMaterial(keys, opened, passphrases);
    }

    private static byte[] ReadInput(ImportKeyInput input)
    {
        byte[] data;
        if (!string.IsNullOrWhiteSpace(input.Data))
        {
            try
            {
                data = Convert.FromBase64String(input.Data);
            }
            catch (FormatException)
            {
                throw new MailboxValidationException("data", "The file could not be read.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.Text))
        {
            data = Encoding.UTF8.GetBytes(input.Text.Trim());
        }
        else
        {
            throw new MailboxValidationException("data", "Choose a key file or paste a key.");
        }

        return data.Length <= MaxImportBytes ? data : throw new MailboxValidationException("data", "The file is too large for a key.");
    }

    private static IReadOnlyList<ParsedPgpKey>? TryParsePgp(byte[] data)
    {
        try
        {
            return OpenPgpKeys.Parse(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private CryptoKey FromPgp(string userId, ParsedPgpKey parsed)
    {
        var (addresses, name) = OpenPgpKeys.Identities(parsed.Primary);
        var id = Guid.NewGuid();
        return new CryptoKey
        {
            Id = id,
            OwnerUserId = userId,
            Kind = CryptoKind.OpenPgp,
            Fingerprint = parsed.Fingerprint,
            Addresses = string.Join('\n', addresses),
            Name = name,
            PublicData = parsed.Public.GetEncoded(),
            ProtectedSecret = parsed.Secret is { } secret ? secrets.Protect(id, secret.GetEncoded()) : null,
            HasPassphrase = parsed.HasPassphrase,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.Primary.CreationTime, DateTimeKind.Utc)),
            ExpiresAt = OpenPgpKeys.Expires(parsed.Primary),
            AddedAt = clock.GetUtcNow(),
        };
    }

    private CryptoKey FromCertificate(string userId, ParsedCertificate parsed, bool hasPassphrase)
    {
        var (addresses, name) = SmimeCertificates.Identities(parsed.Certificate);
        var id = Guid.NewGuid();
        return new CryptoKey
        {
            Id = id,
            OwnerUserId = userId,
            Kind = CryptoKind.Smime,
            Fingerprint = parsed.Fingerprint,
            Addresses = string.Join('\n', addresses),
            Name = name,
            PublicData = parsed.Certificate.GetEncoded(),
            ProtectedSecret = parsed.Pkcs12 is { } pkcs12 ? secrets.Protect(id, pkcs12) : null,
            HasPassphrase = parsed.Pkcs12 is not null && hasPassphrase,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.Certificate.NotBefore, DateTimeKind.Utc)),
            ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.Certificate.NotAfter, DateTimeKind.Utc)),
            AddedAt = clock.GetUtcNow(),
        };
    }

    public static CryptoKeyDto ToDto(CryptoKey key, IReadOnlyDictionary<Guid, DateTimeOffset> unlocked) => new(
        key.Id,
        KindName(key.Kind),
        key.Fingerprint,
        key.AddressList,
        key.Name,
        key.IsOwn,
        key.HasPassphrase,
        key.IsOwn && key.HasPassphrase && unlocked.TryGetValue(key.Id, out var until) ? until : null,
        key.CreatedAt,
        key.ExpiresAt,
        key.AddedAt,
        key.Source);

    public static string KindName(CryptoKind kind) => kind == CryptoKind.OpenPgp ? "openpgp" : "smime";
}
