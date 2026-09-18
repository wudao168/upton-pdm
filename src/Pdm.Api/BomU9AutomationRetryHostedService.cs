using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>
/// 后台重试"依赖尚未满足"的 U9C BOM 自动同步：启动自检一次，之后按退避间隔重试，
/// 使 BOM料号/发布/子件料号等前置条件就绪后不再需要人工触发。
/// </summary>
public sealed class BomU9AutomationRetryHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<BomU9AutomationRetryHostedService> logger) : BackgroundService
{
    private const string SystemActor = "system:u9-bom-automation";
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        var seeded = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                if (!seeded)
                {
                    await SeedPendingAsync(scope, stoppingToken);
                    seeded = true;
                }
                await RetryDueAsync(scope, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "U9C BOM automatic retry pass failed; the next cycle will continue.");
            }
            try { await Task.Delay(IdleDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SeedPendingAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var applications = (await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, cancellationToken))
            .Where(application => application.BomHeaderKind is not null && application.MaterialId is not null)
            .ToArray();
        var scheduled = 0;
        foreach (var group in applications.GroupBy(application => (application.ProjectId, application.BomHeaderKind!.Value)))
        {
            var material = await materials.FindMaterialAsync(group.First().MaterialId!.Value, cancellationToken);
            if (material?.U9SyncConfirmed != true) continue;
            BomU9AutomationRetryQueue.Schedule(group.Key.ProjectId, group.Key.Item2, "启动自检：重新评估U9C BOM自动创建",
                timeProvider.GetUtcNow().AddSeconds(scheduled * 5), immediate: true);
            scheduled++;
        }
        if (scheduled > 0) logger.LogInformation("U9C BOM automatic retry queue seeded with {Count} header(s).", scheduled);
    }

    private async Task RetryDueAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var due = BomU9AutomationRetryQueue.TakeDue(timeProvider.GetUtcNow(), 3);
        if (due.Count == 0) return;
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var automation = scope.ServiceProvider.GetRequiredService<ApprovalU9AutomationService>();
        foreach (var retry in due)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                var result = await automation.ContinueAfterMaterialSyncAsync(retry.ProjectId, SystemActor, cancellationToken);
                await repository.AppendAuditAsync(new AuditEntry(
                    Guid.NewGuid(), timeProvider.GetUtcNow(), SystemActor, "u9.bom-auto-retry",
                    nameof(ProjectBomHeaderBinding), $"{retry.ProjectId}:{retry.Kind}",
                    $"第{retry.Attempt}次自动重试：{result.Stage}；{result.Message}"), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "U9C BOM automatic retry failed for project {ProjectId} / {Kind}.", retry.ProjectId, retry.Kind);
            }
        }
    }
}
