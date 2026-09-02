using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class U9MaterialFullSyncCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<U9MaterialFullSyncCoordinator> logger,
    IHostApplicationLifetime applicationLifetime)
{
    private static readonly TimeSpan MaximumRunDuration = TimeSpan.FromHours(2);
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
            var synchronization = scope.ServiceProvider.GetRequiredService<U9MaterialFullSyncService>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            timeout.CancelAfter(MaximumRunDuration);
            var run = await synchronization.SynchronizeAsync(actor, triggerKind, timeout.Token);
            logger.LogInformation(
                "U9C material full synchronization completed: {Status}; categories {Completed}/{Total}; created {Created}; refreshed {Refreshed}; skipped {Skipped}.",
                run.Status, run.CompletedCategoryCount, run.CategoryCount, run.CreatedCount, run.RefreshedCount, run.SkippedCount);
        }
        catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.LogInformation("U9C material full synchronization stopped with the application.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("U9C material full synchronization exceeded the two-hour timeout and was stopped.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "U9C material full synchronization failed.");
        }
    }
}
