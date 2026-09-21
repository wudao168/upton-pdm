using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>转图（发布预览生成）的单飞协调器：后台定时触发与页面手动重试共用同一次运行。</summary>
public sealed class ReleasePreviewCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<ReleasePreviewCoordinator> logger,
    IHostApplicationLifetime applicationLifetime)
{
    /// <summary>整批运行的兜底时长：必须大于单次转图超时，否则单张图纸还没转完就被整批截断。</summary>
    private static readonly TimeSpan ExtraRunDuration = TimeSpan.FromMinutes(5);
    private const int DefaultTimeoutMinutes = 30;
    private readonly object gate = new();
    private Task? activeRun;

    public bool TryStart(string triggerKind)
    {
        lock (gate)
        {
            if (activeRun is { IsCompleted: false }) return false;
            activeRun = RunAsync(triggerKind);
            return true;
        }
    }

    private async Task RunAsync(string triggerKind)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ReleasePreviewService>();
            var settings = await scope.ServiceProvider.GetRequiredService<IPdmRepository>().GetSystemSettingsAsync(CancellationToken.None);
            var timeoutMinutes = settings.PreviewConversion?.TimeoutMinutes ?? DefaultTimeoutMinutes;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes) + ExtraRunDuration);
            var processed = await service.ProcessPendingAsync(timeout.Token);
            if (processed > 0) logger.LogInformation("发布转图任务处理完成 {Count} 项（{Trigger}）。", processed, triggerKind);
        }
        catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.LogInformation("发布转图任务随应用停止而中断。");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("发布转图任务达到单次运行上限被终止，剩余任务会在下一轮重试。");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "发布转图任务执行失败，剩余任务会在下一轮重试。");
        }
    }
}

/// <summary>
/// 启动时恢复被打断的发布：服务重启/崩溃会让发布包停在"发布中"、BOM 一直锁定，
/// 这里统一标记为发布失败，让项目经理可以重新提交发布，避免整单卡死。
/// </summary>
public sealed class PublishRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublishRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<Upton.Pdm.Application.IPdmRepository>();
            // 转图与发布解耦后同样需要恢复：重启会打断正在转换的任务，必须重新排队，否则发布面板会一直停在"转换中"。
            var recoveredPreviews = await repository.RecoverInterruptedPreviewRunsAsync("转图处理被服务重启打断，已重新排队。", cancellationToken);
            if (recoveredPreviews > 0) logger.LogWarning("服务重启后已重新排队被中断的转图任务 {Count} 项。", recoveredPreviews);
            // 发布成品归档补登记：老发布包（归档功能上线前发布的）在这里自动补进"机械发布/电气发布"目录，已登记过的会被跳过。
            try
            {
                var deliveryArchive = scope.ServiceProvider.GetRequiredService<ReleaseDeliveryArchiveService>();
                var archivedPackages = 0;
                foreach (var packageId in await repository.ListRecentPublishedReleasePackageIdsAsync(20, cancellationToken))
                {
                    if (await deliveryArchive.ArchiveAsync(packageId, "system", cancellationToken) > 0) archivedPackages++;
                }
                if (archivedPackages > 0) logger.LogInformation("服务启动时补登记发布成品归档 {Count} 个发布包。", archivedPackages);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 控制台日志在 Windows 服务方式下看不到，这里同时留审计痕迹，便于排查归档失败。
                await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow(),
                    "system", "release-package.delivery-archive-backfill-failed", nameof(ReleasePackage), Guid.Empty.ToString(),
                    $"补登记发布成品归档失败：{exception.Message}"), cancellationToken);
                logger.LogWarning(exception, "补登记发布成品归档失败。");
            }
            // 发布动作可重入（发布目录幂等、正式版本为单个事务），重启后直接续跑，不再要求人工重新提交。
            var workflow = scope.ServiceProvider.GetRequiredService<Upton.Pdm.Application.PdmWorkflowService>();
            foreach (var packageId in await repository.ListPublishingReleasePackageIdsAsync(20, cancellationToken))
            {
                try
                {
                    await workflow.ResumePublishAsync(packageId, cancellationToken);
                    logger.LogWarning("服务重启后已续跑被中断的发布 {PackageId}。", packageId);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "续跑发布 {PackageId} 失败；该发布包已标记为发布失败，可人工重新提交。", packageId);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "恢复被中断的发布包失败，下次启动会再试。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>后台定时重试转图：发布不受影响，转图成功或最终失败后由服务本身发消息反馈。</summary>
public sealed class ReleasePreviewRetryHostedService(
    ReleasePreviewCoordinator coordinator,
    ILogger<ReleasePreviewRetryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                coordinator.TryStart("Scheduled");
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "发布转图定时重试循环异常，下一轮继续。");
            }
        }
    }
}
