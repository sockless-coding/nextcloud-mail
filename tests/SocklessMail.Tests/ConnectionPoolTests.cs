using Microsoft.Extensions.Time.Testing;
using SocklessMail.Api.Mail;

namespace SocklessMail.Tests;

public sealed class ConnectionPoolTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<FakeConnection> _created = [];
    private readonly ConnectionPool<FakeConnection> _pool;
    private bool _healthy = true;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ConnectionPoolTests() => _pool = CreatePool(new ConnectionPoolOptions { MaxPerKey = 2, MaxTotal = 3 });

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    public sealed class FakeConnection(int id)
    {
        public int Id { get; } = id;
        public bool Closed { get; set; }
        public int Validations { get; set; }
    }

    private ConnectionPool<FakeConnection> CreatePool(ConnectionPoolOptions options) => new(
        options,
        validate: (c, _) => { c.Validations++; return Task.FromResult(_healthy); },
        close: c => { c.Closed = true; return Task.CompletedTask; },
        _clock);

    private Task<FakeConnection> Create(CancellationToken _)
    {
        var connection = new FakeConnection(_created.Count + 1);
        _created.Add(connection);
        return Task.FromResult(connection);
    }

    [Fact]
    public async Task Returned_connection_is_reused()
    {
        using (var lease = await _pool.AcquireAsync("a", Create, Ct))
        {
            Assert.Equal(1, lease.Connection.Id);
        }

        using var again = await _pool.AcquireAsync("a", Create, Ct);
        Assert.Equal(1, again.Connection.Id);
        Assert.Single(_created);
    }

    [Fact]
    public async Task Concurrent_leases_of_one_key_get_different_connections()
    {
        using var first = await _pool.AcquireAsync("a", Create, Ct);
        using var second = await _pool.AcquireAsync("a", Create, Ct);
        Assert.NotSame(first.Connection, second.Connection);
    }

    [Fact]
    public async Task Per_key_limit_makes_callers_wait()
    {
        var first = await _pool.AcquireAsync("a", Create, Ct);
        using var second = await _pool.AcquireAsync("a", Create, Ct);

        var third = _pool.AcquireAsync("a", Create, Ct);
        await Task.Delay(50, Ct);
        Assert.False(third.IsCompleted);

        first.Dispose();
        using var lease = await third;
        Assert.Same(first.Connection, lease.Connection);
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        using var first = await _pool.AcquireAsync("a", Create, Ct);
        using var second = await _pool.AcquireAsync("a", Create, Ct);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _pool.AcquireAsync("a", Create, cts.Token));

        // The cancelled waiter must not have consumed a lease slot.
        first.Dispose();
        using var lease = await _pool.AcquireAsync("a", Create, Ct);
    }

    [Fact]
    public async Task Total_limit_closes_an_idle_connection_of_another_key()
    {
        (await _pool.AcquireAsync("a", Create, Ct)).Dispose();
        using var b1 = await _pool.AcquireAsync("b", Create, Ct);
        using var b2 = await _pool.AcquireAsync("b", Create, Ct);
        Assert.Equal(3, _pool.OpenCount);

        using var c = await _pool.AcquireAsync("c", Create, Ct);

        Assert.True(_created[0].Closed);
        Assert.Equal(3, _pool.OpenCount);
    }

    [Fact]
    public async Task Total_limit_waits_when_everything_is_leased()
    {
        var a = await _pool.AcquireAsync("a", Create, Ct);
        using var b1 = await _pool.AcquireAsync("b", Create, Ct);
        using var b2 = await _pool.AcquireAsync("b", Create, Ct);

        var c = _pool.AcquireAsync("c", Create, Ct);
        await Task.Delay(50, Ct);
        Assert.False(c.IsCompleted);

        a.MarkBroken();
        a.Dispose();
        using var lease = await c;
        Assert.Equal(3, _pool.OpenCount);
    }

    [Fact]
    public async Task Broken_connection_is_closed_not_reused()
    {
        var lease = await _pool.AcquireAsync("a", Create, Ct);
        lease.MarkBroken();
        lease.Dispose();

        Assert.True(_created[0].Closed);
        using var next = await _pool.AcquireAsync("a", Create, Ct);
        Assert.Equal(2, next.Connection.Id);
        Assert.Equal(1, _pool.OpenCount);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_returns_it_once()
    {
        var lease = await _pool.AcquireAsync("a", Create, Ct);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, _pool.IdleCount);
    }

    [Fact]
    public async Task Connection_idle_for_a_while_is_validated_before_reuse()
    {
        (await _pool.AcquireAsync("a", Create, Ct)).Dispose();

        using (await _pool.AcquireAsync("a", Create, Ct))
        {
            Assert.Equal(0, _created[0].Validations);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var lease = await _pool.AcquireAsync("a", Create, Ct);
        Assert.Equal(1, _created[0].Validations);
        Assert.Same(_created[0], lease.Connection);
    }

    [Fact]
    public async Task Failed_validation_replaces_the_connection()
    {
        (await _pool.AcquireAsync("a", Create, Ct)).Dispose();
        _clock.Advance(TimeSpan.FromMinutes(1));
        _healthy = false;

        using var lease = await _pool.AcquireAsync("a", Create, Ct);

        Assert.True(_created[0].Closed);
        Assert.Equal(2, lease.Connection.Id);
        Assert.Equal(1, _pool.OpenCount);
    }

    [Fact]
    public async Task Idle_connections_are_evicted_after_the_timeout()
    {
        (await _pool.AcquireAsync("a", Create, Ct)).Dispose();
        _clock.Advance(TimeSpan.FromMinutes(2));
        (await _pool.AcquireAsync("b", Create, Ct)).Dispose();
        _clock.Advance(TimeSpan.FromMinutes(4));

        await _pool.EvictIdleAsync();

        Assert.True(_created[0].Closed);
        Assert.False(_created[1].Closed);
        Assert.Equal(1, _pool.OpenCount);
    }

    [Fact]
    public async Task Invalidate_closes_idle_and_retires_leased_connections()
    {
        var leased = await _pool.AcquireAsync("a", Create, Ct);
        (await _pool.AcquireAsync("a", Create, Ct)).Dispose(); // second connection, now idle

        await _pool.InvalidateAsync("a");
        Assert.True(_created[1].Closed);
        Assert.False(_created[0].Closed);

        leased.Dispose();
        Assert.True(_created[0].Closed);
        Assert.Equal(0, _pool.OpenCount);
    }

    [Fact]
    public async Task Failed_create_releases_its_slots()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            _pool.AcquireAsync("a", _ => Task.FromException<FakeConnection>(new IOException()), Ct));

        Assert.Equal(0, _pool.OpenCount);
        using var a = await _pool.AcquireAsync("a", Create, Ct);
        using var b = await _pool.AcquireAsync("a", Create, Ct);
    }

    [Fact]
    public async Task Many_parallel_users_never_share_a_connection()
    {
        var pool = CreatePool(new ConnectionPoolOptions { MaxPerKey = 3, MaxTotal = 5 });
        var inUse = new HashSet<FakeConnection>();
        var violations = 0;

        await Parallel.ForAsync(0, 200, Ct, async (i, ct) =>
        {
            using var lease = await pool.AcquireAsync($"k{i % 4}", Create, ct);
            lock (inUse)
            {
                if (!inUse.Add(lease.Connection))
                {
                    violations++;
                }
            }

            await Task.Yield();
            lock (inUse)
            {
                inUse.Remove(lease.Connection);
            }
        });

        Assert.Equal(0, violations);
        Assert.True(pool.OpenCount <= 5);
    }
}
