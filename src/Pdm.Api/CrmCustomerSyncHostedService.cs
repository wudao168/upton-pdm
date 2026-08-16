using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class CrmCustomerSyncHostedService(
    IServiceProvider serviceProvider,
    ILogger<CrmCustomerSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<CrmCustomerIntegrationService>();
                var result = await service.TrySyncAutomaticallyAsync(stoppingToken);
                if (result is not null)
                {
                    logger.LogInformation(
                        "CRM automatic customer sync completed: {CustomerCount} customers, {SkippedCount} skipped.",
                        result.CustomerCount,
                        result.SkippedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "CRM automatic customer sync failed; the configured interval will be observed before retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
