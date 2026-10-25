using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class WorkHoursReminderHostedService(IServiceScopeFactory scopes, ILogger<WorkHoursReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WorkHoursService>().SendDailyRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "工时完整性提醒暂未完成，将自动重试。"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
