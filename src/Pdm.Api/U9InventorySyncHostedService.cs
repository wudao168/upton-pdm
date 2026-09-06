using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class U9InventorySyncHostedService(
    IServiceProvider serviceProvider,
    U9InventorySyncCoordinator coordinator,
    ILogger<U9InventorySyncHostedService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private const string SystemActor = "system:u9-inventory-sync";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled U9C inventory synchronization check failed; the next cycle will continue.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IU9InventoryRepository>();
        var settings = await repository.GetSettingsAsync(cancellationToken);
        if (!settings.AutoSyncEnabled) return;
        var latest = await repository.GetLatestRunAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var lastAttempt = latest?.CompletedAt ?? latest?.StartedAt;
        if (latest?.Status == U9InventorySyncStatus.Running && lastAttempt.HasValue && now - lastAttempt.Value < TimeSpan.FromMinutes(15)) return;
        if (lastAttempt.HasValue && now - lastAttempt.Value < TimeSpan.FromMinutes(settings.SyncIntervalMinutes)) return;
        if (!coordinator.TryStart(SystemActor, "Scheduled"))
            logger.LogInformation("Scheduled U9C inventory synchronization was skipped because another run is active.");
    }
}
