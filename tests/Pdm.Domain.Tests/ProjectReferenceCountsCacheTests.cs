using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectReferenceCountsCacheTests
{
    [Fact]
    public async Task ConcurrentReadersShareOneCalculationAndCompletedValues()
    {
        using var cache = new ProjectReferenceCountsCache();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ReferenceCountResult> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls); await gate.Task.WaitAsync(ct);
            return new((12, 8, 4));
        }
        var readers = Enumerable.Range(0, 50).Select(_ => cache.GetAsync("company/project/snapshot/documents", Load, default)).ToArray();
        Assert.Equal(1, calls);
        gate.SetResult();
        Assert.All(await Task.WhenAll(readers), value => Assert.Equal((12, 8, 4), value));
        Assert.Equal((12, 8, 4), await cache.GetAsync("company/project/snapshot/documents", Load, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SnapshotOrDocumentFingerprintChangesCannotReuseAnotherResult()
    {
        using var cache = new ProjectReferenceCountsCache();
        Assert.Equal((1, 1, 0), await cache.GetAsync("company/project/snapshot-1/documents-1", _ => Task.FromResult(new ReferenceCountResult((1, 1, 0))), default));
        Assert.Equal((2, 1, 1), await cache.GetAsync("company/project/snapshot-2/documents-1", _ => Task.FromResult(new ReferenceCountResult((2, 1, 1))), default));
        Assert.Equal((3, 1, 2), await cache.GetAsync("company/project/snapshot-2/documents-2", _ => Task.FromResult(new ReferenceCountResult((3, 1, 2))), default));
    }

    [Fact]
    public async Task OneCancelledClientDoesNotCancelOtherReaders()
    {
        using var cache = new ProjectReferenceCountsCache();
        using var cancelled = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ReferenceCountResult> Load(CancellationToken ct) { await gate.Task.WaitAsync(ct); return new((5, 3, 2)); }
        var first = cache.GetAsync("shared", Load, cancelled.Token);
        var second = cache.GetAsync("shared", Load, default);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult();
        Assert.Equal((5, 3, 2), await second);
    }

    [Fact]
    public async Task FailedOrChangedSnapshotReadsDoNotPoisonTheCache()
    {
        using var cache = new ProjectReferenceCountsCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("failure", _ => Task.FromException<ReferenceCountResult>(new InvalidOperationException("读取失败")), default));
        Assert.Equal((2, 2, 0), await cache.GetAsync("failure", _ => Task.FromResult(new ReferenceCountResult((2, 2, 0))), default));
        Assert.Equal((1, 1, 0), await cache.GetAsync("changed", _ => Task.FromResult(new ReferenceCountResult((1, 1, 0), false)), default));
        Assert.Equal((2, 1, 1), await cache.GetAsync("changed", _ => Task.FromResult(new ReferenceCountResult((2, 1, 1))), default));
    }
}
