using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class U9InventorySyncCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<U9InventorySyncCoordinator> logger,
    IHostApplicationLifetime applicationLifetime)
{
    private static readonly TimeSpan MaximumRunDuration = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private Task? activeRun;

    public bool TryStart(string actor, string triggerKind)
    {
        lock (gate)
        {
            if (activeRun is { IsCompleted: false }) return false;
            activeRun = RunAsync(actor, triggerKind);
            return true;
        }
    }

    private async Task RunAsync(string actor, string triggerKind)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<U9InventoryService>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            timeout.CancelAfter(MaximumRunDuration);
            var run = await service.SynchronizeAsync(actor, triggerKind, timeout.Token);
            logger.LogInformation(
                "U9C inventory synchronization completed: {Status}; source rows {SourceRows}; stored rows {StoredRows}; materials {Materials}.",
                run.Status, run.SourceRowCount, run.StoredRowCount, run.MaterialCount);
        }
        catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.LogInformation("U9C inventory synchronization stopped with the application.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("U9C inventory synchronization exceeded the fifteen-minute timeout and was stopped.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "U9C inventory synchronization failed; the previous complete snapshot remains active.");
        }
    }
}
