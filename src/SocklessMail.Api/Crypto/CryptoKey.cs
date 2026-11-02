namespace SocklessMail.Api.Crypto;

/// <summary>Where a key came from.</summary>
public static class KeySources
{
    /// <summary>Imported by the user.</summary>
    public const string Import = "import";

    /// <summary>Made in the app.</summary>
    public const string Generated = "generated";

    /// <summary>A KEY in one of the user's contacts.</summary>
    public const string Contacts = "contacts";

    /// <summary>The recipient's domain (Web Key Directory).</summary>
    public const string Wkd = "wkd";

    /// <summary>keys.openpgp.org, which publishes only addresses their owner confirmed.</summary>
    public const string KeyServer = "keyserver";

    /// <summary>The Autocrypt header of a message the user received.</summary>
    public const string Autocrypt = "autocrypt";

    /// <summary>The certificate of a signed message the user received.</summary>
    public const string Message = "message";
}

/// <summary>Which standard a key belongs to.</summary>
public enum CryptoKind
{
    OpenPgp,
    Smime,
}

/// <summary>
/// An OpenPGP key or an S/MIME certificate of a user: one of their own (with the private key) or
/// a correspondent's (public only). Keys are the user's, not an account's: a key serves every
/// account whose address it names.
/// </summary>
public sealed class CryptoKey
{
    public Guid Id { get; init; }

    public required string OwnerUserId { get; init; }

    public CryptoKind Kind { get; init; }

    /// <summary>
    /// Upper-case hex: the OpenPGP v4 fingerprint of the primary key, or the SHA-256 of an S/MIME
    /// certificate. Unique per user and kind.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>The e-mail addresses the key names, lower case, newline-separated.</summary>
    public required string Addresses { get; set; }

    /// <summary>The name of its first user id, or the certificate subject's common name.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The public part, not secret: the binary OpenPGP public key ring, or the DER certificate.
    /// </summary>
    public required byte[] PublicData { get; set; }

    /// <summary>
    /// For the user's own keys: the binary OpenPGP secret key ring, or the PKCS#12 file, as
    /// imported (so still protected by the key's own passphrase), encrypted with the app's key
    /// ring (<see cref="KeySecrets"/>). Null for a correspondent's key.
    /// </summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>The private key needs a passphrase (an OpenPGP key without one, or a PKCS#12 file with an empty password, does not).</summary>
    public bool HasPassphrase { get; set; }

    /// <summary>Where the key came from: <see cref="KeySources"/>.</summary>
    public string Source { get; set; } = KeySources.Import;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public bool IsOwn => ProtectedSecret is not null;

    public IReadOnlyList<string> AddressList => Addresses.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
