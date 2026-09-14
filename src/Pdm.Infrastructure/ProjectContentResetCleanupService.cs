using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class ProjectContentResetCleanupService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ProjectContentResetCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IProjectContentResetStore>();
                var count = await store.PurgeExpiredAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (count > 0) logger.LogInformation("Closed {SnapshotCount} expired project-content reset snapshots after 30 days.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Project-content reset snapshot cleanup failed; it will retry later."); }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }
}
