using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class MaterialU9SyncBatchHostedService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<MaterialU9SyncBatchHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ItemLease = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "U9C material sync batch worker failed; polling will continue.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var synchronizer = scope.ServiceProvider.GetRequiredService<MaterialCodeSynchronizationService>();
        var processedAny = false;
        for (var index = 0; index < 50; index++)
        {
            var now = timeProvider.GetUtcNow();
            var claim = await repository.ClaimNextSyncBatchItemAsync(now, now.Add(ItemLease), cancellationToken);
            if (claim is null) break;
            processedAny = true;
            try
            {
                var result = await synchronizer.SynchronizeTaskAsync(
                    claim.Item.TaskId,
                    claim.Batch.RequestedBy,
                    claim.Batch.RequestedRole,
                    cancellationToken);
                await repository.CompleteSyncBatchItemAsync(
                    claim.Batch.Id,
                    claim.Item.Id,
                    result.Completed ? MaterialSyncBatchItemStatus.Succeeded : MaterialSyncBatchItemStatus.Waiting,
                    result.Message,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "U9C material sync batch item {BatchItemId} failed.", claim.Item.Id);
                await repository.CompleteSyncBatchItemAsync(
                    claim.Batch.Id,
                    claim.Item.Id,
                    MaterialSyncBatchItemStatus.Failed,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None);
            }
        }
        return processedAny;
    }
}
