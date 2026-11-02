using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class WorkflowNotificationHostedService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<WorkflowNotificationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var historyReconciled = false;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IWorkflowNotificationStore>();
                var service = scope.ServiceProvider.GetRequiredService<WorkflowNotificationService>();
                if (!historyReconciled)
                {
                    var inbox = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
                    var previousTenant = TenantContext.Current;
                    try
                    {
                        TenantContext.Clear();
                        var count = await inbox.ReconcileAsync(null, stoppingToken);
                        logger.LogInformation("已整理各角色历史消息{Count}条，原记录保留。", count);
                    }
                    finally { if (previousTenant is null) TenantContext.Clear(); else TenantContext.Set(previousTenant); }
                    historyReconciled = true;
                }
                foreach (var job in await store.ListDueAsync(clock.GetUtcNow(), 25, stoppingToken))
                    try { await service.ProcessAsync(job.Id, stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { logger.LogWarning(error, "Workflow notification {JobId} remains queued for retry", job.Id); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Workflow notification background pass failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
