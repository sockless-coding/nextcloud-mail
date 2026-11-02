namespace SocklessMail.Api.Crypto;

/// <param name="Kind">"openpgp" or "smime".</param>
/// <param name="Own">The user's own key, with its private key; otherwise a correspondent's public key.</param>
/// <param name="UnlockedUntil">When an unlocked private key locks again unless used; null when locked or not protected.</param>
public sealed record CryptoKeyDto(
    Guid Id,
    string Kind,
    string Fingerprint,
    IReadOnlyList<string> Addresses,
    string? Name,
    bool Own,
    bool HasPassphrase,
    DateTimeOffset? UnlockedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset AddedAt,
    string Source);

/// <summary>
/// A key file to import: <see cref="Text"/> for pasted ASCII (an armoured OpenPGP key or a PEM
/// certificate), or <see cref="Data"/> as base64 for a file (also .p12/.pfx, which need
/// <see cref="Passphrase"/>).
/// </summary>
public sealed record ImportKeyInput(string? Text, string? Data, string? Passphrase);

/// <param name="Added">Keys that were new.</param>
/// <param name="Updated">Keys already known, now with what the file added (a private key, new user ids).</param>
public sealed record ImportKeyResult(IReadOnlyList<CryptoKeyDto> Added, IReadOnlyList<CryptoKeyDto> Updated);

/// <param name="ValidYears">0 for a key that does not expire.</param>
public sealed record GenerateKeyInput(string? Name, string? Address, string? Passphrase, int? ValidYears);

public sealed record UnlockKeyInput(string? Passphrase);

/// <summary>What a message's encryption and signatures came to (null on a message with neither).</summary>
public sealed record MessageSecurityDto(EncryptionDto? Encryption, IReadOnlyList<SignatureDto> Signatures);

/// <param name="Kind">"openpgp" or "smime".</param>
/// <param name="Status">
/// "decrypted"; "locked" (a private key that opens it is locked: <see cref="Keys"/>); "no_key"
/// (none of the user's keys opens it); "too_large"; "failed".
/// </param>
public sealed record EncryptionDto(string Kind, string Status, IReadOnlyList<KeyRefDto> Keys);

public sealed record KeyRefDto(Guid Id, string Fingerprint, IReadOnlyList<string> Addresses);

/// <param name="Kind">"openpgp" or "smime".</param>
/// <param name="Status">
/// "valid" (intact, by a key the user has, or a certificate a trusted authority issued); "untrusted"
/// (intact, but the certificate chain is not trusted or the certificate had expired); "unknown_key"
/// (intact or not cannot be told without the sender's public key); "bad" (the message was changed
/// after signing, or the signature is broken); "not_checked" (too large to check).
/// </param>
/// <param name="SenderMatches">The signer's key or certificate names the From address.</param>
/// <param name="KeyId">For "unknown_key": the 16-digit OpenPGP key id to look for.</param>
/// <param name="Detail">Why a certificate is not trusted, when known.</param>
public sealed record SignatureDto(
    string Kind,
    string Status,
    string? SignerName,
    IReadOnlyList<string> SignerAddresses,
    string? Fingerprint,
    string? KeyId,
    DateTimeOffset? SignedAt,
    bool SenderMatches,
    string? Detail);
