using System.Collections.Concurrent;

namespace Upton.Pdm.Application;

public static class ReleasePublishingProgress
{
    private static readonly ConcurrentDictionary<Guid, (string Phase, DateTimeOffset Since)> Progress = new();
    public static void Set(Guid id, string phase) => Progress[id] = (phase, DateTimeOffset.UtcNow);
    public static string Describe(Guid id) => Progress.TryGetValue(id, out var progress)
        ? $"{progress.Phase}（本环节 {(int)(DateTimeOffset.UtcNow - progress.Since).TotalSeconds} 秒）" : "等待后台续跑";
}
