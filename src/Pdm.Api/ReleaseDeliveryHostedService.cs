using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class ReleaseDeliveryHostedService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ReleaseDeliveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IReleaseDeliveryStore>();
                var service = scope.ServiceProvider.GetRequiredService<ReleaseDeliveryService>();
                foreach (var id in await store.ListUntrackedPublishedAsync(10, stoppingToken))
                {
                    try { await service.PublishedAsync(id, stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Release delivery recovery pending for {PackageId}", id); }
                }
                foreach (var claim in await store.ClaimDueAsync(clock.GetUtcNow(), 5, stoppingToken))
                {
                    try { await service.ProcessAsync(claim, stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Release delivery processing pending for {PackageId}", claim.ReleasePackageId); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Release delivery background pass failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
