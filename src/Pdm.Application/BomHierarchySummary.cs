using Upton.Pdm.Domain;
using System.Text.Json.Serialization;

namespace Upton.Pdm.Application;

public sealed record BomHierarchyMetric(Guid ProjectId, [property: JsonConverter(typeof(JsonStringEnumConverter))] BomKind Kind, int ItemCount, int EffectiveCount,
    int UnresolvedCount, string? Version, DateTimeOffset? ReleasedAt, DateTimeOffset? LongLeadPublishedAt, bool LongLeadPublished = false)
{
    public static BomHierarchyMetric Count(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, BomValidationRules rules)
    {
        var grouped = BomSummaryProjection.Group(items.Select(item => item with { IsComplete = BomValidationFieldCatalog.IsComplete(item, kind, rules) }).ToArray(), includeFingerprint: false);
        return new(projectId, kind, grouped.Count(item => !item.IsManuallyExcluded && !item.IsPendingClassification),
            grouped.Count(item => !item.IsManuallyExcluded && !item.IsPendingRemoval),
            grouped.Count(item => !item.IsManuallyExcluded && (item.IsPendingRemoval || item.IsPendingClassification || item.IsManualUnmatched || !item.IsComplete)), null, null, null);
    }
}
public sealed record BomHierarchyProject(Guid ProjectId, IReadOnlyList<BomHierarchyMetric> Categories,
    IReadOnlyList<ProjectBomHeader> Headers, string? Error = null)
{
    [JsonIgnore] public Exception? DiagnosticError { get; init; }
}

public sealed partial class BomHeaderService
{
    public async Task<IReadOnlyList<BomHierarchyProject>> ReadHierarchyAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var visible = await repository.ListProjectNavigationForUserAsync(actor, role, ct);
        var selected = visible.FirstOrDefault(project => project.Id == projectId && project.CanReadContent)
            ?? throw new UnauthorizedAccessException("当前用户没有项目内容查看权限。");
        var projects = visible.Where(project => project.CanReadContent && (selected.ParentProjectId is not null
            ? project.Id == selected.Id : (project.RootProjectId ?? project.Id) == selected.Id)).ToArray();
        var metrics = await repository.ReadBomHierarchyMetricsAsync(projects.Select(project => project.Id).ToArray(), ct);
        var result = new List<BomHierarchyProject>();
        foreach (var project in projects)
        {
            try
            {
                var headers = await ListForProjectAsync(project, project.EffectiveProjectPermissions.Contains(PermissionCodes.BomEdit), actor, ct);
                result.Add(new(project.Id, metrics.Where(item => item.ProjectId == project.Id).ToArray(), headers));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { result.Add(new(project.Id, [], [], "BOM层级状态暂时无法读取，请刷新后重试。") { DiagnosticError = error }); }
        }
        return result;
    }
}
