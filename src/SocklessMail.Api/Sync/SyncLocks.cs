namespace SocklessMail.Api.Sync;

/// <summary>
/// One lock per cached folder, so a sync and a local change (or two syncs) never interleave their
/// reads and writes of the same rows. Lock order: take this lock first, then a pooled IMAP
/// connection, never the other way round, or callers holding all of an account's connections
/// could wait on a sync that waits for a connection.
/// </summary>
public sealed class SyncLocks
{
    private readonly Dictionary<(Guid, string), Entry> _entries = new();
    private readonly Lock _lock = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    public async Task<IDisposable> AcquireAsync(Guid accountId, string fullName, CancellationToken ct)
    {
        var key = (accountId, fullName);
        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = _entries[key] = new Entry();
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Holder(this, key, entry);
    }

    private void Release((Guid, string) key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_lock)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Holder(SyncLocks owner, (Guid, string) key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(key, entry, held: true);
            }
        }
    }
}
