using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class StockedMaterialSyncHostedService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<StockedMaterialSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var services = scopes.CreateAsyncScope();
                var store = services.ServiceProvider.GetRequiredService<IStockedMaterialStore>();
                var service = services.ServiceProvider.GetRequiredService<StockedMaterialService>();
                var config = await services.ServiceProvider.GetRequiredService<IMaterialRepository>().GetIntegrationConfigurationAsync(stoppingToken);
                foreach (var scope in await store.ListScopesAsync(stoppingToken))
                {
                    if (scope.EnterpriseCode != config.EnterpriseCode || scope.OrganizationCode != config.OrganizationCode) continue;
                    var catalog = await store.ReadAsync(scope, stoppingToken);
                    // 旧配置首次启用全源发现时立即补齐，后续（含失败重试）按配置间隔执行。
                    if (catalog.SourceDiscoveryAttempted && catalog.LastAttemptAt.HasValue
                        && clock.GetUtcNow() - catalog.LastAttemptAt < TimeSpan.FromMinutes(catalog.Settings.SyncIntervalMinutes)) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(15));
                    try { await service.SynchronizeAsync(scope, "system:stocked-material-sync", timeout.Token, scheduled: true); }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning(error, "Stocked material synchronization failed for {CompanyId}; previous snapshots retained", scope.CompanyId); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Stocked material scheduled check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
