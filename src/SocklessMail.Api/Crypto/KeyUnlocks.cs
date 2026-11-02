using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace SocklessMail.Api.Crypto;

/// <summary>
/// Encrypts the private part of a user's key with the app's key ring, on top of the key's own
/// passphrase. The purpose is bound to the key id, so a ciphertext copied into another row does
/// not decrypt.
/// </summary>
public sealed class KeySecrets(IDataProtectionProvider provider)
{
    private IDataProtector For(Guid keyId) => provider.CreateProtector("SocklessMail.CryptoKeys.v1", keyId.ToString("N"));

    public string Protect(Guid keyId, byte[] secret) => Convert.ToBase64String(For(keyId).Protect(secret));

    /// <exception cref="KeyUnavailableException">The app's key ring no longer opens it.</exception>
    public byte[] Unprotect(Guid keyId, string protectedSecret)
    {
        try
        {
            return For(keyId).Unprotect(Convert.FromBase64String(protectedSecret));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new KeyUnavailableException(e);
        }
    }
}

/// <summary>Thrown when a stored private key cannot be decrypted (key ring lost or changed); it has to be imported again.</summary>
public sealed class KeyUnavailableException(Exception inner) : Exception("The stored private key cannot be decrypted; import it again.", inner);

/// <summary>
/// The passphrases of the private keys a user unlocked, held in memory only, each until it was
/// not used for <see cref="IdleTimeout"/>, the user locks it, or the app restarts.
/// </summary>
public sealed class KeyUnlocks(TimeProvider clock)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private sealed class Entry(string passphrase, DateTimeOffset until)
    {
        public string Passphrase { get; } = passphrase;

        public DateTimeOffset Until { get; set; } = until;
    }

    private readonly ConcurrentDictionary<(string UserId, Guid KeyId), Entry> _unlocked = new();

    public void Unlock(string userId, Guid keyId, string passphrase)
    {
        Sweep();
        _unlocked[(userId, keyId)] = new Entry(passphrase, clock.GetUtcNow() + IdleTimeout);
    }

    /// <summary>The passphrase of an unlocked key (and it stays unlocked a while longer); null when locked.</summary>
    public string? Passphrase(string userId, Guid keyId)
    {
        if (!_unlocked.TryGetValue((userId, keyId), out var entry))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (entry.Until <= now)
        {
            _unlocked.TryRemove((userId, keyId), out _);
            return null;
        }

        entry.Until = now + IdleTimeout;
        return entry.Passphrase;
    }

    /// <summary>When each of the user's unlocked keys locks again unless used.</summary>
    public IReadOnlyDictionary<Guid, DateTimeOffset> Unlocked(string userId)
    {
        var now = clock.GetUtcNow();
        return _unlocked.Where(e => e.Key.UserId == userId && e.Value.Until > now).ToDictionary(e => e.Key.KeyId, e => e.Value.Until);
    }

    public void Lock(string userId, Guid keyId) => _unlocked.TryRemove((userId, keyId), out _);

    public void LockAll(string userId)
    {
        foreach (var key in _unlocked.Keys.Where(k => k.UserId == userId))
        {
            _unlocked.TryRemove(key, out _);
        }
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, entry) in _unlocked)
        {
            if (entry.Until <= now)
            {
                _unlocked.TryRemove(key, out _);
            }
        }
    }
}
