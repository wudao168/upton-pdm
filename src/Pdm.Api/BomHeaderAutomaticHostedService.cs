using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class BomHeaderAutomaticHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BomHeaderAutomaticHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRecovery = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<BomHeaderService>();
                if (DateTimeOffset.UtcNow >= nextRecovery)
                {
                    nextRecovery = DateTimeOffset.UtcNow.AddMinutes(1);
                    await service.RecoverPublishedApplicationsAsync(stoppingToken);
                }
                await service.ProcessAutomaticQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "BOM header automatic queue interrupted; persisted work will be recovered.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
