using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class WorkflowNotificationHostedService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<WorkflowNotificationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IWorkflowNotificationStore>();
                var service = scope.ServiceProvider.GetRequiredService<WorkflowNotificationService>();
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
