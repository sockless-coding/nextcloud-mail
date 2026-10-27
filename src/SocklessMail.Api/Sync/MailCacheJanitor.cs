namespace SocklessMail.Api.Sync;

/// <summary>Prunes old removal records from the header cache every hour.</summary>
public sealed class MailCacheJanitor(IServiceScopeFactory scopes, ILogger<MailCacheJanitor> logger) : BackgroundService
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
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Pruning the mail cache failed");
            }
        }
    }
}
