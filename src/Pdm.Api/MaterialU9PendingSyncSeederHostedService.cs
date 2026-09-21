using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>
/// 启动自检：把“料品已批准、但同步请求只生成过预览、从未执行”的任务自动排队到U9C同步批次，
/// 使普通料品不再依赖人工点击同步（历史遗留的待同步记录也会被自动处理）。
/// </summary>
public sealed class MaterialU9PendingSyncSeederHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MaterialU9PendingSyncSeederHostedService> logger) : BackgroundService
{
    private const string SystemActor = "system:u9-material-sync";
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        try
        {
            await SeedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "U9C material sync start-up seeding failed; manual sync stays available.");
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var batches = scope.ServiceProvider.GetRequiredService<MaterialSyncBatchService>();

        var candidates = new List<Guid>();
        foreach (var task in await materials.ListSyncTasksAsync(cancellationToken))
        {
            if (task.Status != MaterialSyncStatus.PreviewReady) continue;
            var material = await materials.FindMaterialAsync(task.MaterialId, cancellationToken);
            if (material is null || material.IsArchived || material.U9SyncConfirmed) continue;
            if (material.ApprovalStatus != MaterialApprovalStatus.Approved) continue;
            candidates.Add(task.Id);
        }

        if (candidates.Count == 0) return;
        var queued = 0;
        foreach (var chunk in candidates.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await batches.CreateAutomaticAsync(chunk, SystemActor, cancellationToken);
                queued += chunk.Length;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Queueing one U9C material sync batch at start-up failed.");
            }
        }
        if (queued > 0)
            logger.LogInformation("Queued {Count} pending U9C material sync task(s) at start-up.", queued);
    }
}
