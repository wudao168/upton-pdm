using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class BrowserCadWorkerResilienceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureRecordingErrorDoesNotStopWorkerAndReleasesJobGate(bool denied)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-worker-resilience-" + Guid.NewGuid().ToString("N"));
        var queue = new BrowserCadArchiveQueue(Options.Create(new PdmStorageOptions { BrowserCadArchiveRoot = root }));
        var job = new BrowserCadJob(Guid.NewGuid(), Guid.NewGuid(), "qa", new BrowserCadArchiveTarget(Guid.NewGuid()), "Queued", "等待处理", [], []);
        await queue.SaveAsync(job, default);
        var scopes = new FailingScopes(denied);
        using var worker = new BrowserCadArchiveHostedService(scopes, queue, NullLogger<BrowserCadArchiveHostedService>.Instance);
        try
        {
            await worker.StartAsync(default);
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (scopes.Calls < 4 && DateTime.UtcNow < deadline && !worker.ExecuteTask!.IsCompleted) await Task.Delay(20);
            Assert.True(scopes.Calls >= 4, "任务处理及失败记录异常后应继续下一轮轮询。");
            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.Equal("Queued", queue.Read(job.Id)!.State);
            Assert.Equal(1, queue.Gate(job.Id).CurrentCount);
        }
        finally
        {
            await worker.StopAsync(default);
            Directory.Delete(root, true);
        }
    }

    private sealed class FailingScopes(bool denied) : IServiceScopeFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref calls);
            throw denied ? new UnauthorizedAccessException("模拟队列写入权限失效") : new IOException("模拟队列存储暂时不可用");
        }
    }
}
