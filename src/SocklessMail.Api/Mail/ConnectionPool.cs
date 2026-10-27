namespace SocklessMail.Api.Mail;

public sealed record ConnectionPoolOptions
{
    /// <summary>Connections one key (account) may hold at once; further callers wait.</summary>
    public int MaxPerKey { get; init; } = 3;

    /// <summary>Open connections across all keys; idle ones of other keys are closed to make room.</summary>
    public int MaxTotal { get; init; } = 100;

    /// <summary>Idle connections older than this are closed by <see cref="ConnectionPool{TConnection}.EvictIdleAsync"/>.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An idle connection older than this is validated (e.g. IMAP NOOP) before reuse.</summary>
    public TimeSpan ValidateAfterIdle { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Keyed pool of connections that must not be used concurrently (MailKit clients are not
/// thread-safe). A caller leases a connection exclusively and returns it by disposing the lease.
/// Protocol details are injected: how to create, validate and close a connection.
/// </summary>
public sealed class ConnectionPool<TConnection>(
    ConnectionPoolOptions options,
    Func<TConnection, CancellationToken, Task<bool>> validate,
    Func<TConnection, Task> close,
    TimeProvider clock) : IAsyncDisposable
    where TConnection : class
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly SemaphoreSlim _open = new(options.MaxTotal, options.MaxTotal);
    private int _openWaiters;
    private bool _disposed;

    internal sealed class Slot(int maxPerKey)
    {
        public readonly SemaphoreSlim Leases = new(maxPerKey, maxPerKey);
        public readonly Stack<(TConnection Connection, DateTimeOffset IdleSince)> Idle = new();
        public int Generation;
    }

    /// <summary>Open connections (idle and leased), for tests and diagnostics.</summary>
    public int OpenCount => options.MaxTotal - _open.CurrentCount;

    public int IdleCount
    {
        get
        {
            lock (_lock)
            {
                return _slots.Values.Sum(s => s.Idle.Count);
            }
        }
    }

    public async Task<Lease> AcquireAsync(string key, Func<CancellationToken, Task<TConnection>> create, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot slot;
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out slot!))
            {
                slot = _slots[key] = new Slot(options.MaxPerKey);
            }
        }

        await slot.Leases.WaitAsync(ct);
        try
        {
            while (TakeIdle(slot) is var (connection, idleSince, generation) && connection is not null)
            {
                if (clock.GetUtcNow() - idleSince < options.ValidateAfterIdle || await SafeValidateAsync(connection, ct))
                {
                    return new Lease(this, key, slot, connection, generation);
                }

                await CloseAsync(connection);
            }

            await ReserveOpenSlotAsync(ct);
            try
            {
                int generationAtCreate;
                lock (_lock)
                {
                    generationAtCreate = slot.Generation;
                }

                return new Lease(this, key, slot, await create(ct), generationAtCreate);
            }
            catch
            {
                _open.Release();
                throw;
            }
        }
        catch
        {
            slot.Leases.Release();
            throw;
        }
    }

    /// <summary>Closes idle connections of <paramref name="key"/>; leased ones are closed when returned.</summary>
    public async Task InvalidateAsync(string key)
    {
        List<TConnection> toClose;
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                return;
            }

            slot.Generation++;
            toClose = slot.Idle.Select(i => i.Connection).ToList();
            slot.Idle.Clear();
        }

        foreach (var connection in toClose)
        {
            await CloseAsync(connection);
        }
    }

    public async Task EvictIdleAsync()
    {
        var cutoff = clock.GetUtcNow() - options.IdleTimeout;
        List<TConnection> toClose = [];
        lock (_lock)
        {
            foreach (var slot in _slots.Values)
            {
                var keep = slot.Idle.Where(i => i.IdleSince > cutoff).Reverse().ToList();
                toClose.AddRange(slot.Idle.Where(i => i.IdleSince <= cutoff).Select(i => i.Connection));
                slot.Idle.Clear();
                foreach (var item in keep)
                {
                    slot.Idle.Push(item);
                }
            }
        }

        foreach (var connection in toClose)
        {
            await CloseAsync(connection);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<TConnection> toClose;
        lock (_lock)
        {
            _disposed = true;
            toClose = _slots.Values.SelectMany(s => s.Idle.Select(i => i.Connection)).ToList();
            foreach (var slot in _slots.Values)
            {
                slot.Idle.Clear();
            }
        }

        foreach (var connection in toClose)
        {
            await CloseAsync(connection);
        }
    }

    private (TConnection? Connection, DateTimeOffset IdleSince, int Generation) TakeIdle(Slot slot)
    {
        lock (_lock)
        {
            return slot.Idle.TryPop(out var item)
                ? (item.Connection, item.IdleSince, slot.Generation)
                : (null, default, 0);
        }
    }

    /// <summary>
    /// Waits for room under <see cref="ConnectionPoolOptions.MaxTotal"/>, closing an idle connection
    /// of another key if that makes room. While anyone waits here, returned connections are closed
    /// instead of kept idle (see <see cref="Return"/>), so a waiter cannot starve behind idle
    /// connections that appeared after it started waiting.
    /// </summary>
    private async Task ReserveOpenSlotAsync(CancellationToken ct)
    {
        if (_open.Wait(0))
        {
            return;
        }

        TConnection? oldest = null;
        lock (_lock)
        {
            _openWaiters++;
            var candidate = _slots.Values
                .Where(s => s.Idle.Count > 0)
                .Select(s => (Slot: s, Item: s.Idle.Last()))
                .OrderBy(x => x.Item.IdleSince)
                .FirstOrDefault();
            if (candidate.Slot is not null)
            {
                var rest = candidate.Slot.Idle.Reverse().Skip(1).ToList();
                candidate.Slot.Idle.Clear();
                foreach (var item in rest)
                {
                    candidate.Slot.Idle.Push(item);
                }

                oldest = candidate.Item.Connection;
            }
        }

        try
        {
            if (oldest is not null)
            {
                await CloseAsync(oldest); // releases its permit
            }

            await _open.WaitAsync(ct);
        }
        finally
        {
            lock (_lock)
            {
                _openWaiters--;
            }
        }
    }

    private void Return(Lease lease, bool broken)
    {
        var close = true;
        lock (_lock)
        {
            if (!broken && !_disposed && lease.Generation == lease.Slot.Generation && _openWaiters == 0)
            {
                lease.Slot.Idle.Push((lease.Connection, clock.GetUtcNow()));
                close = false;
            }
        }

        lease.Slot.Leases.Release();
        if (close)
        {
            // Not awaited by the caller: closing (e.g. IMAP LOGOUT) must not delay the response.
            _ = CloseAsync(lease.Connection);
        }
    }

    private async Task<bool> SafeValidateAsync(TConnection connection, CancellationToken ct)
    {
        try
        {
            return await validate(connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task CloseAsync(TConnection connection)
    {
        try
        {
            await close(connection);
        }
        catch
        {
            // Closing a broken connection fails routinely; the permit is what matters.
        }
        finally
        {
            _open.Release();
        }
    }

    /// <summary>Exclusive use of one connection. Dispose to return it; <see cref="MarkBroken"/> first if its state is unknown.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly ConnectionPool<TConnection> _pool;
        private bool _broken;
        private int _returned;

        internal Lease(ConnectionPool<TConnection> pool, string key, Slot slot, TConnection connection, int generation)
        {
            _pool = pool;
            Key = key;
            Slot = slot;
            Connection = connection;
            Generation = generation;
        }

        public string Key { get; }
        public TConnection Connection { get; }
        internal int Generation { get; }
        internal Slot Slot { get; }

        public void MarkBroken() => _broken = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                _pool.Return(this, _broken);
            }
        }
    }
}
