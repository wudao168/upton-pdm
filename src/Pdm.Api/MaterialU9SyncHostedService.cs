using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class MaterialU9SyncHostedService(
    IServiceProvider serviceProvider,
    U9MaterialFullSyncCoordinator coordinator,
    ILogger<MaterialU9SyncHostedService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FullSyncStartTime = TimeSpan.FromHours(2);
    private const string SystemActor = "system:u9-material-full-sync";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(MaintenanceInterval);
        do
        {
            try
            {
                await RunScheduledFullSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled U9C material full synchronization failed; the next cycle will continue.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunScheduledFullSyncAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var synchronization = scope.ServiceProvider.GetRequiredService<U9MaterialFullSyncService>();
        var latest = await synchronization.GetLatestRunAsync(cancellationToken);
        var now = timeProvider.GetLocalNow();
        var latestLocal = latest?.StartedAt.ToLocalTime();
        var alreadySucceededToday = latest?.Status == U9MaterialFullSyncStatus.Succeeded
            && latestLocal?.Date == now.Date;
        var recentRunning = latest?.Status == U9MaterialFullSyncStatus.Running
            && latestLocal.HasValue
            && now - latestLocal.Value < TimeSpan.FromHours(3);
        if (now.TimeOfDay < FullSyncStartTime || alreadySucceededToday || recentRunning) return;

        if (!coordinator.TryStart(SystemActor, "Scheduled"))
            logger.LogInformation("Scheduled U9C material full synchronization was skipped because another run is active.");
    }
}
