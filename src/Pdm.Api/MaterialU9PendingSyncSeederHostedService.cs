using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>
/// 持续补排队：料品批准与批次创建之间中断，或非标发布仅生成预览时，自动恢复首次同步。
/// 已有批次、执行失败及属性修改任务仍走各自的重试或人工确认流程。
/// </summary>
public sealed class MaterialU9PendingSyncSeederHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MaterialU9PendingSyncSeederHostedService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private const string SystemActor = "system:u9-material-sync";
    private const int BatchSize = 200;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScanInterval, timeProvider, stoppingToken);
                await SeedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "U9C material sync recovery failed; the next scan will continue.");
            }
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
            if (task.Operation != MaterialSyncOperation.Create
                || task.Status != MaterialSyncStatus.PreviewReady || task.AttemptCount != 0) continue;
            var material = await materials.FindMaterialAsync(task.MaterialId, cancellationToken);
            if (material is null || material.IsArchived || material.U9SyncConfirmed) continue;
            if (material.ApprovalStatus != MaterialApprovalStatus.Approved) continue;
            if ((await materials.ListSyncBatchItemsForTaskAsync(task.Id, cancellationToken)).Count > 0) continue;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Queueing one recovered U9C material sync batch failed; the next scan will retry unscheduled tasks.");
            }
        }
        if (queued > 0)
            logger.LogInformation("Queued {Count} previously unscheduled U9C material sync task(s).", queued);
    }
}
