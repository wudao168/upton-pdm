using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Upton.Pdm.Infrastructure;

internal readonly record struct ReferenceCountResult((int All, int Model, int Drawing) Counts, bool Cacheable = true);

// 只缓存由完整输入指纹确定的三个数值；不缓存用户权限、项目响应或引用树。
internal sealed class ProjectReferenceCountsCache : IDisposable
{
    private readonly MemoryCache completed = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly ConcurrentDictionary<string, Lazy<Task<(int All, int Model, int Drawing)>>> pending = new();

    internal Task<(int All, int Model, int Drawing)> GetAsync(string key, Func<CancellationToken, Task<ReferenceCountResult>> load, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (completed.TryGetValue(key, out (int All, int Model, int Drawing) value)) return Task.FromResult(value);
        var shared = pending.GetOrAdd(key, _ => new Lazy<Task<(int All, int Model, int Drawing)>>(() => ReadAsync(key, load)));
        return shared.Value.WaitAsync(ct);
    }

    private async Task<(int All, int Model, int Drawing)> ReadAsync(string key, Func<CancellationToken, Task<ReferenceCountResult>> load)
    {
        try
        {
            // 某个客户端离开页面不能取消其他客户端正在共用的读取。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await load(timeout.Token);
            if (result.Cacheable) completed.Set(key, result.Counts, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
            return result.Counts;
        }
        finally { pending.TryRemove(key, out _); }
    }

    public void Dispose() => completed.Dispose();
}
