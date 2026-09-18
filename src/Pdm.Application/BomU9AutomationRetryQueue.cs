using System.Collections.Concurrent;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>
/// 记录"依赖尚未满足"的 U9C BOM 自动同步项，供后台按退避间隔重试。
/// 自动创建原先只在发布与料号同步两个时点触发，依赖晚于触发时点就绪时不会再次执行。
/// </summary>
public static class BomU9AutomationRetryQueue
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1)
    ];

    private sealed class Entry
    {
        public int Attempt { get; set; }
        public DateTimeOffset DueAt { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    private static readonly ConcurrentDictionary<(Guid ProjectId, ProjectBomHeaderKind Kind), Entry> Entries = new();

    public static int PendingCount => Entries.Count;

    public static void Schedule(Guid projectId, ProjectBomHeaderKind kind, string message, DateTimeOffset now, bool immediate = false)
    {
        var entry = Entries.GetOrAdd((projectId, kind), _ => new Entry { DueAt = immediate ? now : now + Backoff[0] });
        lock (entry)
        {
            entry.Message = message;
            if (immediate) entry.DueAt = now;
        }
    }

    public static void Clear(Guid projectId, ProjectBomHeaderKind kind) => Entries.TryRemove((projectId, kind), out _);

    public static IReadOnlyList<BomU9AutomationRetry> TakeDue(DateTimeOffset now, int maximum)
    {
        var due = new List<BomU9AutomationRetry>();
        foreach (var pair in Entries)
        {
            if (due.Count >= maximum) break;
            var entry = pair.Value;
            lock (entry)
            {
                if (entry.DueAt > now) continue;
                var attempt = entry.Attempt;
                entry.Attempt = Math.Min(attempt + 1, Backoff.Length);
                entry.DueAt = now + Backoff[Math.Min(entry.Attempt, Backoff.Length - 1)];
                due.Add(new BomU9AutomationRetry(pair.Key.ProjectId, pair.Key.Kind, attempt + 1, entry.Message));
            }
        }
        return due;
    }
}

public sealed record BomU9AutomationRetry(Guid ProjectId, ProjectBomHeaderKind Kind, int Attempt, string Message);
