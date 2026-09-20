using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

/// <summary>转图（发布预览生成）的单飞协调器：后台定时触发与页面手动重试共用同一次运行。</summary>
public sealed class ReleasePreviewCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<ReleasePreviewCoordinator> logger,
    IHostApplicationLifetime applicationLifetime)
{
    private static readonly TimeSpan MaximumRunDuration = TimeSpan.FromMinutes(30);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            timeout.CancelAfter(MaximumRunDuration);
            var processed = await service.ProcessPendingAsync(timeout.Token);
            if (processed > 0) logger.LogInformation("发布转图任务处理完成 {Count} 项（{Trigger}）。", processed, triggerKind);
        }
        catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.LogInformation("发布转图任务随应用停止而中断。");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("发布转图任务超过三十分钟被终止，剩余任务会在下一轮重试。");
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
