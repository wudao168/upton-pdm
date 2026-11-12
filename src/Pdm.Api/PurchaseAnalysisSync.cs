using System.Collections.Concurrent;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class PurchaseAnalysisSyncCoordinator(IServiceScopeFactory scopes, ILogger<PurchaseAnalysisSyncCoordinator> logger,
    IHostApplicationLifetime lifetime, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public bool Running(Guid company) => active.ContainsKey(company);
    public bool TryStart(Guid company, string actor)
    {
        if (!active.TryAdd(company, 0)) return false;
        _ = Task.Run(async () => {
            TenantContext.Clear();
            await using var scope = scopes.CreateAsyncScope();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(TimeSpan.FromHours(2));
            try { await scope.ServiceProvider.GetRequiredService<PurchaseAnalysisService>().SynchronizeAsync(company, actor, timeout.Token); }
            catch (Exception ex) {
                logger.LogError(ex, "Purchase analysis synchronization failed for {Company}", company);
                try { await scope.ServiceProvider.GetRequiredService<IPurchaseAnalysisStore>().FailureAsync(company, clock.GetUtcNow(),
                    "采购采集未完成，已保留上次完整数据。请核对U9C连接、组织及接口字段后重新同步；详细原因已记入服务端日志。", CancellationToken.None); }
                catch (Exception statusError) { logger.LogError(statusError, "Could not save purchase synchronization status"); }
            }
            finally { active.TryRemove(company, out _); }
        });
        return true;
    }
}

public sealed class PurchaseAnalysisSyncHostedService(IServiceScopeFactory scopes, PurchaseAnalysisSyncCoordinator sync,
    ILogger<PurchaseAnalysisSyncHostedService> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct)) {
            try {
                await using var scope = scopes.CreateAsyncScope(); var store = scope.ServiceProvider.GetRequiredService<IPurchaseAnalysisStore>();
                foreach (var company in await store.CompaniesAsync(ct)) {
                    var settings = await store.SettingsAsync(company, ct);
                    if (!settings.AutoSync || settings.Organizations.Length == 0 || sync.Running(company)) continue;
                    var snapshot = await store.ReadAsync(company, ct);
                    if (snapshot.Status.LastAttempt is { } last && clock.GetUtcNow() - last < TimeSpan.FromMinutes(settings.IntervalMinutes)) continue;
                    sync.TryStart(company, "system:purchase-analysis");
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Purchase analysis schedule check failed"); }
        }
    }
}
