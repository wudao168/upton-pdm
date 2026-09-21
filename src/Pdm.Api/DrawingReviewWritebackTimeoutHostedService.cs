using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

/// <summary>
/// 写入审核标记的兜底任务：审核单长时间停在“正在写入审核标记”时自动放弃属性回写并完成审核，
/// 避免 2D 工程图被无限期锁定、设计者拿不到编辑权限。
/// </summary>
public sealed class DrawingReviewWritebackTimeoutHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DrawingReviewWritebackTimeoutHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<PdmWorkflowService>();
                var completed = await workflow.CompleteTimedOutDrawingReviewWritebacksAsync(Timeout, stoppingToken);
                if (completed > 0) logger.LogInformation("超时未写入审核标记的图纸审核单已自动完成 {Count} 单。", completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "图纸审核标记写回超时兜底任务执行失败。");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
