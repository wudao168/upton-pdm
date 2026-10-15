using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class ArchiveBatchRecoveryService(IServiceScopeFactory scopeFactory, ILogger<ArchiveBatchRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
                var workflow = scope.ServiceProvider.GetRequiredService<PdmWorkflowService>();
                foreach (var work in await repository.ListArchiveBatchFollowupsAsync(10, stoppingToken))
                {
                    try { await workflow.CompleteArchiveBatchFollowupAsync(work, stoppingToken); }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning(error, "整批存档已成功，后续BOM或通知将在后台重试。批次 {BatchId}", work.Receipt.BatchId); }
                }
                await scope.ServiceProvider.GetRequiredService<IFileStorage>().CleanupExpiredArchiveUploadsAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning(error, "存档后续任务暂时无法读取，将自动重试。"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
