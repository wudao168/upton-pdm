using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectContentResetInspection(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<string> Blockers,
    bool HasContent);

public sealed record ProjectContentResetSnapshotSummary(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    IReadOnlyList<Guid> IncludedProjectIds,
    string Reason,
    IReadOnlyDictionary<string, int> Counts,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? RestoredBy,
    DateTimeOffset? RestoredAt,
    DateTimeOffset? PurgedAt);

public sealed record ProjectContentResetReadiness(
    Project Project,
    bool IncludeChildren,
    IReadOnlyList<Project> IncludedProjects,
    bool CanReset,
    IReadOnlyList<string> Blockers,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<ProjectContentResetSnapshotSummary> RestorableSnapshots);

public interface IProjectContentResetStore
{
    Task<ProjectContentResetInspection> InspectAsync(IReadOnlyList<Guid> projectIds, CancellationToken cancellationToken);
    Task<ProjectContentResetSnapshotSummary> ResetAsync(Guid projectId, string projectCode, IReadOnlyList<Guid> projectIds, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectContentResetSnapshotSummary>> ListSnapshotsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ProjectContentResetSnapshotSummary?> FindSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);
    Task<ProjectContentResetSnapshotSummary> RestoreAsync(Guid snapshotId, string actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class ProjectContentResetService(IPdmRepository repository, IProjectContentResetStore store, TimeProvider timeProvider)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<ProjectContentResetReadiness> GetReadinessAsync(Guid projectId, bool includeChildren, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, cancellationToken);
        var projects = await repository.ListProjectsAsync(cancellationToken);
        var project = projects.FirstOrDefault(item => item.Id == projectId)
            ?? throw new PdmNotFoundException("项目不存在。");
        var included = CollectProjects(project, projects, includeChildren);
        var inspection = await store.InspectAsync(included.Select(item => item.Id).ToArray(), cancellationToken);
        var snapshots = await store.ListSnapshotsAsync(projectId, cancellationToken);
        return new(project, includeChildren, included, inspection.Blockers.Count == 0, inspection.Blockers, inspection.Counts,
            snapshots.Where(item => item.RestoredAt is null && item.PurgedAt is null && item.ExpiresAt > timeProvider.GetUtcNow()).ToArray());
    }

    public async Task<ProjectContentResetSnapshotSummary> ResetAsync(
        Guid projectId,
        bool includeChildren,
        string? reason,
        string? confirmation,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        reason = reason?.Trim();
        confirmation = confirmation?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) throw new PdmRuleException("重置原因不能为空。");
        if (reason.Length > 500) throw new PdmRuleException("重置原因不能超过500个字符。");
        var readiness = await GetReadinessAsync(projectId, includeChildren, actor, role, cancellationToken);
        if (!readiness.CanReset) throw new PdmRuleException(string.Join("；", readiness.Blockers));
        if (!string.Equals(confirmation, readiness.Project.Code, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("确认文字必须与当前项目号完全一致。");
        if (!readiness.Counts.Values.Any(value => value > 0)) throw new PdmRuleException("当前项目没有可重置的内容。");

        var now = timeProvider.GetUtcNow();
        var snapshot = await store.ResetAsync(projectId, readiness.Project.Code, readiness.IncludedProjects.Select(item => item.Id).ToArray(), reason, actor, now, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "project.content.reset", nameof(Project), projectId.ToString(),
            $"{readiness.Project.Code} · 范围{readiness.IncludedProjects.Count}个项目号 · 原因：{reason} · {Retention.TotalDays:0}天内可整项恢复"), cancellationToken);
        return snapshot;
    }

    public async Task<ProjectContentResetSnapshotSummary> RestoreAsync(Guid projectId, Guid snapshotId, string? confirmation, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, cancellationToken);
        var snapshot = await store.FindSnapshotAsync(snapshotId, cancellationToken)
            ?? throw new PdmNotFoundException("项目重置快照不存在。");
        if (snapshot.ProjectId != projectId) throw new PdmNotFoundException("重置快照不属于当前项目。");
        if (snapshot.RestoredAt is not null) throw new PdmRuleException("该重置快照已经恢复。");
        if (snapshot.PurgedAt is not null || snapshot.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmRuleException("该重置快照已超过30天恢复期限。");
        if (!string.Equals(confirmation?.Trim(), snapshot.ProjectCode, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("确认文字必须与当前项目号完全一致。");
        var current = await store.InspectAsync(snapshot.IncludedProjectIds, cancellationToken);
        if (current.HasContent) throw new PdmRuleException("项目重置后已经产生新内容，不能直接恢复旧快照；请先清空新内容或联系开发者处理。");

        var now = timeProvider.GetUtcNow();
        var restored = await store.RestoreAsync(snapshotId, actor, now, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "project.content.reset.restore", nameof(Project), projectId.ToString(),
            $"{snapshot.ProjectCode} · 恢复重置快照 {snapshot.Id}"), cancellationToken);
        return restored;
    }

    private async Task RequireAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role != UserRole.Administrator
            || !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectContentReset, cancellationToken))
            throw new UnauthorizedAccessException("仅系统管理员可重置项目内容。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目内容的访问权限。");
    }

    private static Project[] CollectProjects(Project project, IReadOnlyList<Project> projects, bool includeChildren)
    {
        if (!includeChildren) return [project];
        var result = new List<Project> { project };
        for (var index = 0; index < result.Count; index++)
            result.AddRange(projects.Where(item => item.ParentProjectId == result[index].Id && result.All(existing => existing.Id != item.Id)));
        return result.ToArray();
    }
}
