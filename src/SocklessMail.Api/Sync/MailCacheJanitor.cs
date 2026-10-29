using SocklessMail.Api.Bimi;

namespace SocklessMail.Api.Sync;

/// <summary>Prunes old removal records from the header cache, and expired BIMI lookups, every hour.</summary>
public sealed class MailCacheJanitor(IServiceScopeFactory scopes, BimiResolver bimi, ILogger<MailCacheJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FolderCache>().PruneRemovedAsync(stoppingToken);
                await bimi.PruneAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Pruning the mail cache failed");
            }
        }
    }
}
