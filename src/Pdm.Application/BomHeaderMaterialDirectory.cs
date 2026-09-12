using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record BomHeaderMaterialLink(Guid MaterialId, Guid ProjectId, ProjectBomHeaderKind Kind);

public sealed record BomHeaderMaterialDirectoryItem(
    Guid MaterialId, Guid ProjectId, string ProjectCode, string SubprojectCode, string ProjectName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind Kind,
    string MaterialCode, string MaterialName, string AutomaticStatus, string AutomaticMessage, bool IsArchived);

public sealed partial class BomHeaderService
{
    // 只读目录：不调用会为历史草稿补建申请的 ListAsync，也不入队或重试。
    public async Task<IReadOnlyList<BomHeaderMaterialDirectoryItem>> ListMaterialDirectoryAsync(
        string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialView, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有料品查看权限。");
        var projects = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken)).ToDictionary(p => p.Id);
        var links = await materials.ListBomHeaderMaterialLinksAsync(cancellationToken);
        var result = new List<BomHeaderMaterialDirectoryItem>();
        foreach (var group in links.Where(link => projects.ContainsKey(link.ProjectId)).GroupBy(link => link.ProjectId))
        {
            var project = projects[group.Key];
            var applications = await materials.ListMaterialCodeApplicationsAsync(project.Id, null, cancellationToken);
            var audits = applications.Any(a => a.Status == MaterialCodeApplicationStatus.Pending && string.IsNullOrWhiteSpace(a.WorkflowMessage))
                ? await repository.ListProjectAuditAsync(project.Id, 500, cancellationToken) : [];
            foreach (var link in group.Distinct())
            {
                var material = await materials.FindMaterialAsync(link.MaterialId, cancellationToken);
                if (material is null) continue;
                var application = applications.Where(a => a.MaterialId == material.Id && a.BomItemId is null && a.BomHeaderKind == link.Kind)
                    .OrderByDescending(a => a.RequestedAt).FirstOrDefault();
                var legacyFailure = audits.FirstOrDefault(a => a.Action == "bom.header.application.auto-trigger-failed"
                    && application is not null && a.OccurredAt >= application.RequestedAt
                    && (link.Kind == ProjectBomHeaderKind.Master || a.Detail.StartsWith($"{link.Kind}BOM", StringComparison.Ordinal)))?.Detail;
                var automatic = material.U9SyncConfirmed ? (Status: "Completed", Message: "U9C正式料号已回查确认", Retry: false)
                    : application is null ? (Status: "NotRequested", Message: "尚未进入自动流程，请到项目BOM多级总览核对发布及表头状态；无需人工审批。", Retry: false)
                    : AutomaticState(material, application, legacyFailure);
                var rootCode = projects.TryGetValue(project.RootProjectId ?? project.Id, out var root) ? root.Code : "—";
                result.Add(new(material.Id, project.Id, rootCode, project.ParentProjectId is null ? "—" : project.Code,
                    project.Name, link.Kind, material.MaterialCode, material.Name, automatic.Status, automatic.Message, material.IsArchived));
            }
        }
        return result.OrderBy(r => r.ProjectCode).ThenBy(r => r.SubprojectCode).ThenBy(r => r.Kind).ThenBy(r => r.MaterialCode).ToArray();
    }
}
