using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class ProjectFileRecycleCleanupService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ProjectFileRecycleCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IProjectFileRepository>();
                var storage = scope.ServiceProvider.GetRequiredService<IProjectFileStorage>();
                var versions = await repository.PurgeDeletedBeforeAsync(timeProvider.GetUtcNow().AddDays(-30), stoppingToken);
                await storage.DeleteVersionsAsync(versions, stoppingToken);
                if (versions.Count > 0) logger.LogInformation("Purged {VersionCount} expired project file versions from the 30-day recycle bin.", versions.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Project file recycle-bin cleanup failed; it will retry later."); }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }
}
