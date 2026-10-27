using MailKit;
using MailKit.Net.Imap;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mail;

/// <summary>
/// Pooled, authenticated IMAP connections per account. An <see cref="ImapClient"/> is not
/// thread-safe, so each operation gets one exclusively for its duration. The pool is keyed by
/// account id; changing or deleting an account must call <see cref="InvalidateAsync"/>.
/// </summary>
public sealed class ImapConnections : IAsyncDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConnectionPool<ImapClient> _pool;
    private readonly MailServerConnector _connector;
    private readonly ILogger<ImapConnections> _logger;

    public ImapConnections(MailServerConnector connector, TimeProvider clock, ILogger<ImapConnections> logger)
        : this(new ConnectionPoolOptions(), connector, clock, logger)
    {
    }

    public ImapConnections(ConnectionPoolOptions options, MailServerConnector connector, TimeProvider clock, ILogger<ImapConnections> logger)
    {
        _connector = connector;
        _logger = logger;
        _pool = new ConnectionPool<ImapClient>(options, ValidateAsync, CloseAsync, clock);
    }

    public int OpenCount => _pool.OpenCount;

    /// <summary>
    /// Runs <paramref name="action"/> with a connected client of the account. If the connection
    /// turns out to be dead (server timeout, network change) and <paramref name="retry"/> is set,
    /// the action runs once more on a fresh connection; only pass it for operations that are
    /// safe to repeat.
    /// </summary>
    public async Task<T> RunAsync<T>(
        MailAccount account, MailCredential credential, Func<ImapClient, CancellationToken, Task<T>> action,
        CancellationToken ct, bool retry = true)
    {
        var endpoint = new MailEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _pool.AcquireAsync(account.Id.ToString("N"),
                token => _connector.ConnectImapAsync(endpoint, account.Username, credential, token), ct);
            try
            {
                return await action(lease.Connection, ct);
            }
            catch (Exception e) when (IsConnectionFailure(e))
            {
                lease.MarkBroken();
                if (!retry || attempt > 0 || ct.IsCancellationRequested)
                {
                    throw new MailConnectionException("connection_lost",
                        $"The connection to {endpoint.Host} was lost. Try again.", e);
                }

                _logger.LogInformation("IMAP connection of account {AccountId} was lost ({ExceptionType}); reconnecting",
                    account.Id, e.GetType().Name);
            }
            catch (Exception e) when (e is ImapCommandException or FolderNotFoundException or MessageNotFoundException)
            {
                // The server answered; the connection is fine.
                throw;
            }
            catch
            {
                // Unknown state (e.g. cancelled mid-command): do not hand this connection out again.
                lease.MarkBroken();
                throw;
            }
        }
    }

    public Task InvalidateAsync(Guid accountId) => _pool.InvalidateAsync(accountId.ToString("N"));

    public Task EvictIdleAsync() => _pool.EvictIdleAsync();

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    private static bool IsConnectionFailure(Exception e) =>
        e is ServiceNotConnectedException or ServiceNotAuthenticatedException or ImapProtocolException or IOException
            or System.Net.Sockets.SocketException;

    private static async Task<bool> ValidateAsync(ImapClient client, CancellationToken ct)
    {
        if (!client.IsConnected || !client.IsAuthenticated)
        {
            return false;
        }

        await client.NoOpAsync(ct);
        return true;
    }

    private static async Task CloseAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await client.DisconnectAsync(true, timeout.Token);
            }
        }
        finally
        {
            client.Dispose();
        }
    }
}

/// <summary>Closes IMAP connections that have been idle longer than the pool's idle timeout.</summary>
public sealed class ImapConnectionJanitor(ImapConnections connections, ILogger<ImapConnectionJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await connections.EvictIdleAsync();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Evicting idle IMAP connections failed");
            }
        }
    }
}
