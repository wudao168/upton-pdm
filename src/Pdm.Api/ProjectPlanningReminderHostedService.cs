using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class ProjectPlanningReminderHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectPlanningReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
                var plans = await scope.ServiceProvider.GetRequiredService<IProjectPlanningRepository>().ListPlansAsync(null, stoppingToken);
                foreach (var plan in plans.Where(plan => plan.ApprovalStatus == Upton.Pdm.Domain.ProjectPlanApprovalStatus.Approved))
                {
                    try { await service.SyncReleasedBomProgressAsync(plan.ProjectId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { logger.LogError(exception, "BOM发布进度同步失败：{ProjectId}", plan.ProjectId); }
                }
                await service.SendDueRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "项目计划到期提醒任务执行失败。");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
