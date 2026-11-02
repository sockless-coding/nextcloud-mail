using MimeKit;

namespace SocklessMail.Api.Crypto;

/// <summary>
/// Messages just opened with <see cref="MessageSecurity"/>, kept in memory for
/// <see cref="Lifetime"/> so the inline images and attachments the reader asks for next do not
/// fetch and decrypt the whole message again each. Never written anywhere; forgotten when the user
/// locks their keys.
/// </summary>
public sealed class DecryptedMessages(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Kept at once, across users; the oldest go first.</summary>
    public const int MaxEntries = 32;

    /// <summary>Messages larger than this are not kept.</summary>
    public const long MaxKeptBytes = 10 * 1024 * 1024;

    private sealed record Entry(string UserId, OpenedMessage Message, DateTimeOffset Until);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public static string Key(Guid accountId, string folderId, string messageId) => $"{accountId:N}/{folderId}/{messageId}";

    public OpenedMessage? Find(string userId, string key)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.UserId == userId && entry.Until > clock.GetUtcNow())
            {
                return entry.Message;
            }

            _entries.Remove(key);
            return null;
        }
    }

    public void Keep(string userId, string key, OpenedMessage message, long size)
    {
        if (size > MaxKeptBytes)
        {
            return;
        }

        lock (_lock)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _entries.Where(e => e.Value.Until <= now).Select(e => e.Key).ToList())
            {
                _entries.Remove(expired);
            }

            while (_entries.Count >= MaxEntries)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.Until).Key);
            }

            _entries[key] = new Entry(userId, message, now + Lifetime);
        }
    }

    public void Forget(string userId)
    {
        lock (_lock)
        {
            foreach (var key in _entries.Where(e => e.Value.UserId == userId).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }
}

/// <summary>
/// A message after decryption and signature checks: <see cref="Body"/> is what to show (the
/// decrypted or signed content), null when it could not be opened.
/// </summary>
public sealed record OpenedMessage(MimeEntity? Body, MessageSecurityDto Security);
