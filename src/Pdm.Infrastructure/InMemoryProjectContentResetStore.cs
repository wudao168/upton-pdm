using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectContentResetStore : IProjectContentResetStore
{
    private readonly ConcurrentDictionary<Guid, ProjectContentResetSnapshotSummary> snapshots = new();

    public Task<ProjectContentResetInspection> InspectAsync(IReadOnlyList<Guid> projectIds, CancellationToken cancellationToken) =>
        Task.FromResult(new ProjectContentResetInspection(new Dictionary<string, int>(), [], false));

    public Task<ProjectContentResetSnapshotSummary> ResetAsync(Guid projectId, string projectCode, IReadOnlyList<Guid> projectIds, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = new ProjectContentResetSnapshotSummary(Guid.NewGuid(), projectId, projectCode, projectIds, reason, new Dictionary<string, int>(), actor, now, now.AddDays(30), null, null, null);
        snapshots[snapshot.Id] = snapshot;
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<ProjectContentResetSnapshotSummary>> ListSnapshotsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProjectContentResetSnapshotSummary>>(snapshots.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAt).ToArray());

    public Task<ProjectContentResetSnapshotSummary?> FindSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult(snapshots.GetValueOrDefault(snapshotId));

    public Task<ProjectContentResetSnapshotSummary> RestoreAsync(Guid snapshotId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!snapshots.TryGetValue(snapshotId, out var snapshot)) throw new PdmNotFoundException("项目重置快照不存在。");
        var restored = snapshot with { RestoredBy = actor, RestoredAt = now };
        snapshots[snapshotId] = restored;
        return Task.FromResult(restored);
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var purged = 0;
        foreach (var item in snapshots.Values.Where(item => item.PurgedAt is null && item.ExpiresAt <= now).ToArray())
        {
            snapshots[item.Id] = item with { PurgedAt = now };
            purged++;
        }
        return Task.FromResult(purged);
    }
}
