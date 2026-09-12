using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class ControlledDocumentRecycleCleanupService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ControlledDocumentRecycleCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
                var now = timeProvider.GetUtcNow();
                var count = await repository.PurgeExpiredDeletedDocumentsAsync(now.AddDays(-30), now, stoppingToken);
                if (count > 0) logger.LogInformation("Closed the restore window for {DocumentCount} controlled documents after 30 days; immutable version files remain retained.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Controlled-document recycle cleanup failed; it will retry later."); }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }
}
