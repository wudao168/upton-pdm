using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class MaterialU9PendingSyncSeederTests
{
    [Fact]
    public async Task QueuesNonStandardTasksCreatedAfterTheStartupScan()
    {
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        await using var provider = Provider(materials);
        var clock = new FastScanClock();
        using var worker = Worker(provider, clock);
        await worker.StartAsync(default);
        try
        {
            await clock.FirstScanCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var task = await AddTaskAsync(materials);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await materials.ListSyncBatchItemsForTaskAsync(task.Id, default)).Count == 0)
                await Task.Delay(10, deadline.Token);
            Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(task.Id, default));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task ExistingBatchDoesNotPreventAnotherTaskFromBeingQueuedOrGetDuplicated()
    {
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        await using var provider = Provider(materials);
        var first = await AddTaskAsync(materials);
        var batches = provider.GetRequiredService<MaterialSyncBatchService>();
        await batches.CreateAutomaticAsync([first.Id], "admin", default);
        var second = await AddTaskAsync(materials);
        using var worker = Worker(provider, TimeProvider.System);

        await ScanAsync(worker);
        await ScanAsync(worker);

        Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(first.Id, default));
        Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(second.Id, default));
    }

    [Theory]
    [InlineData(MaterialSyncOperation.Update, MaterialSyncStatus.PreviewReady, 0, MaterialApprovalStatus.Approved, false, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.Failed, 1, MaterialApprovalStatus.Approved, false, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.NeedsReview, 1, MaterialApprovalStatus.Approved, false, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.PreviewReady, 1, MaterialApprovalStatus.Approved, false, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.PreviewReady, 0, MaterialApprovalStatus.Draft, false, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.PreviewReady, 0, MaterialApprovalStatus.Approved, true, false)]
    [InlineData(MaterialSyncOperation.Create, MaterialSyncStatus.PreviewReady, 0, MaterialApprovalStatus.Approved, false, true)]
    public async Task OnlyUnexecutedApprovedCreationsAreRecovered(MaterialSyncOperation operation,
        MaterialSyncStatus status, int attempts, MaterialApprovalStatus approval, bool archived, bool confirmed)
    {
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        await using var provider = Provider(materials);
        var excluded = await AddTaskAsync(materials, operation, status, attempts);
        var material = (await materials.FindMaterialAsync(excluded.MaterialId, default))!;
        await materials.UpdateMaterialAsync(material with
        {
            ApprovalStatus = approval, IsArchived = archived, U9SyncConfirmed = confirmed
        }, material.RowVersion, default);
        var eligible = await AddTaskAsync(materials);
        using var worker = Worker(provider, TimeProvider.System);

        await ScanAsync(worker);

        Assert.Empty(await materials.ListSyncBatchItemsForTaskAsync(excluded.Id, default));
        Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(eligible.Id, default));
    }

    [Fact]
    public async Task FailureBeforeTheFirstU9AttemptDoesNotCreateAnAutomaticRetryLoop()
    {
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        await using var provider = Provider(materials);
        var task = await AddTaskAsync(materials);
        var batch = await provider.GetRequiredService<MaterialSyncBatchService>()
            .CreateAutomaticAsync([task.Id], "admin", default);
        await materials.ClaimNextSyncBatchItemAsync(DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(15), default);
        await materials.CompleteSyncBatchItemAsync(batch.Id, batch.Items[0].Id,
            MaterialSyncBatchItemStatus.Failed, "U9C连接失败", DateTimeOffset.UtcNow, default);
        using var worker = Worker(provider, TimeProvider.System);

        await ScanAsync(worker);

        Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(task.Id, default));
    }

    private static ServiceProvider Provider(InMemoryMaterialRepository materials) => new ServiceCollection()
        .AddSingleton<IMaterialRepository>(materials)
        .AddSingleton<IPdmRepository>(new InMemoryPdmRepository(TimeProvider.System))
        .AddSingleton(TimeProvider.System)
        .AddScoped<MaterialSyncBatchService>()
        .BuildServiceProvider();

    private static MaterialU9PendingSyncSeederHostedService Worker(ServiceProvider provider, TimeProvider clock) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MaterialU9PendingSyncSeederHostedService>.Instance, clock);

    private static Task ScanAsync(MaterialU9PendingSyncSeederHostedService worker) =>
        (Task)typeof(MaterialU9PendingSyncSeederHostedService).GetMethod("SeedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [CancellationToken.None])!;

    private static async Task<MaterialSyncTask> AddTaskAsync(InMemoryMaterialRepository materials,
        MaterialSyncOperation operation = MaterialSyncOperation.Create,
        MaterialSyncStatus status = MaterialSyncStatus.PreviewReady, int attempts = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var material = new PdmMaterial(id, $"0204-{id:N}", "非标安装板", MaterialKind.NonStandard,
            MaterialSupplyMode.Purchase, "001", "200x100", "6061", null, null, null, null, null,
            null, MaterialApprovalStatus.Draft, null, null, null, null, null, MaterialSyncStatus.NotQueued,
            "admin", now, "admin", now, 1, CategoryCode: "0204");
        await materials.CreateMaterialAsync(material, (await materials.FindCategoryAsync("0204", default))!, default);
        var task = new MaterialSyncTask(Guid.NewGuid(), id, operation, status, $"test-{id:N}", "[]", "hash",
            attempts, null, null, null, null, null, now, now);
        await materials.ApproveAndEnqueueAsync(material, 1, "0204", task,
            new AuditEntry(Guid.NewGuid(), now, "admin", "material.approve", nameof(PdmMaterial), id.ToString(), "测试"), default);
        return task;
    }

    private sealed class FastScanClock : TimeProvider
    {
        private int delays;
        public TaskCompletionSource FirstScanCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Increment(ref delays) == 2) FirstScanCompleted.TrySetResult();
            return base.CreateTimer(callback, state, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        }
    }
}
