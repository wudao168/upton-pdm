using System.ComponentModel;
using Microsoft.Extensions.AI;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class AiBusinessTools(IPdmRepository repository, MaterialService materials, PdmWorkflowService workflow, AiAccess access)
{
    public List<AiReference> Sources { get; } = [];
    private int calls;
    public IList<AITool> Create() => [AIFunctionFactory.Create(SearchProjects), AIFunctionFactory.Create(SearchMaterials), AIFunctionFactory.Create(ReadProjectBom), AIFunctionFactory.Create(ReadProjectStatus), AIFunctionFactory.Create(ReadBomPublication)];
    // A caller may narrow a turn to one project's status. No details or other project tools are exposed.
    public IList<AITool> CreateStatusScope(Guid projectId) => [
        AIFunctionFactory.Create(async (CancellationToken ct) => {
            var status = System.Text.Json.JsonSerializer.SerializeToElement(await ReadProjectStatus(projectId, ct));
            return new { project = status.GetProperty("Code").GetString(), businessStatus = status.GetProperty("businessStatus").GetString() };
        }, "ReadProjectStatus", "读取本轮已选定项目的当前业务状态，不包含子项目。"),
        AIFunctionFactory.Create((string kind, CancellationToken ct) => ReadBomPublication(projectId, kind, ct),
            "ReadBomPublication", "读取本轮已选定项目的BOM发布状态；kind只允许Standard、NonStandard、Electrical。不返回BOM明细。")
    ];
    private void Count() { if (++calls > 8) throw new PdmRuleException("本次业务查询次数已达上限，请缩小问题范围。"); }

    [Description("按项目号或名称查询当前用户获准查看的项目，最多返回20条；未找到不代表项目不存在。")]
    public async Task<object> SearchProjects([Description("项目号或名称关键词，最多100字")] string query, CancellationToken ct)
    {
        Count(); ValidateQuery(query); await access.RequireAsync(AiPermissions.Chat, null, ct);
        var user = await repository.FindUserAsync(access.Tenant.Username, ct) ?? throw new UnauthorizedAccessException("请重新登录。");
        var projects = await repository.ListProjectHeadersForUserAsync(user.Username, user.Role, ct);
        var rows = new List<object>();
        foreach (var project in projects.Where(p => p.OrganizationId == access.Tenant.CompanyId && (p.Code.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Code.Equals(query, StringComparison.OrdinalIgnoreCase) || p.Code.Equals("P" + query, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Code)) {
            if (!await access.AllowsAsync(AiPermissions.Business, project.Id, ct) || !await access.AllowsAsync(PermissionCodes.ProjectView, project.Id, ct)) continue;
            Sources.Add(new(project.Code + " " + project.Name, project.Id, [PermissionCodes.ProjectView], "project"));
            rows.Add(new { project.Id, project.Code, project.Name, project.ParentProjectId, project.DeliveryDate, project.Quantity, project.IsActive });
            if (rows.Count == 20) break;
        }
        return new { items = rows, limit = 20, note = "仅显示当前授权范围内匹配的项目；交期为计划日期。" };
    }

    [Description("读取指定项目当前业务状态、启用状态和计划交期。正常只表示当前没有待处理状态，不代表BOM已发布。查询是否发布BOM请使用ReadBomPublication。")]
    public async Task<object> ReadProjectStatus([Description("SearchProjects返回的项目ID")] Guid projectId, CancellationToken ct)
    {
        Count(); await access.RequireAsync(AiPermissions.Business, projectId, ct);
        string[] permissions = [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView];
        foreach (var permission in permissions) await access.RequireAsync(permission, projectId, ct);
        var user = await repository.FindUserAsync(access.Tenant.Username, ct) ?? throw new UnauthorizedAccessException("请重新登录。");
        // Use the same activity projection as the project details API; the raw entity has no computed status in MySQL.
        var project = await repository.FindProjectForUserAsync(projectId, user.Username, user.Role, ct) ?? throw new PdmNotFoundException("项目不存在或不可访问。");
        Sources.Add(new(project.Code + " 项目状态", projectId, permissions, "project"));
        return new { project.Code, project.Name, project.ParentProjectId, project.IsActive, project.DeliveryDate,
            businessStatus = project.BusinessStatus ?? "暂无状态", note = "只表示当前项目状态；正常不代表BOM已发布，也不代表整个项目已完成。" };
    }

    [Description("查询指定项目各类BOM的正式版本及长周期发布状态。kind只允许Standard、NonStandard、Electrical。已发布与草稿、审批中分开，结果仅针对当前项目，不含子项目。")]
    public async Task<object> ReadBomPublication([Description("SearchProjects返回的项目ID")] Guid projectId, string kind, CancellationToken ct)
    {
        Count(); var bomKind = ParseKind(kind);
        string[] permissions = [PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("bom"), bomKind == BomKind.Electrical ? "bom.electrical.row.view" : "bom.mechanical.row.view"];
        await access.RequireAsync(AiPermissions.Business, projectId, ct);
        foreach (var permission in permissions) await access.RequireAsync(permission, projectId, ct);
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在或不可访问。");
        var versions = await repository.ListBomVersionsAsync(projectId, bomKind, ct);
        var released = versions.Where(v => v.State == BomVersionState.Released).OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        var lastPublished = versions.Where(v => v.ReleasedAt is not null && v.State is BomVersionState.Released or BomVersionState.Obsolete).OrderByDescending(v => v.ReleasedAt).FirstOrDefault();
        var metric = (await repository.ReadBomHierarchyMetricsAsync([projectId], ct)).FirstOrDefault(m => m.Kind == bomKind);
        Sources.Add(new(project.Code + " " + (bomKind == BomKind.Standard ? "标准件" : bomKind == BomKind.NonStandard ? "非标件" : "电气件") + "BOM发布状态", projectId, permissions, "bom"));
        return new { project = project.Code, kind, currentPublishedVersion = released?.Label, publishedAt = released?.ReleasedAt,
            historicalPublishedVersion = lastPublished?.Label, historicalPublishedAt = lastPublished?.ReleasedAt,
            longLeadPublished = metric?.LongLeadPublished ?? false, longLeadPublishedAt = metric?.LongLeadPublishedAt,
            draftCount = versions.Count(v => v.State == BomVersionState.Draft), reviewingCount = versions.Count(v => v.State == BomVersionState.InReview),
            note = "正式版本、长周期发布分别判断；草稿和审批中不是已发布。无记录只表示当前项目该类别无对应发布记录，不代表子项目未发布。" };
    }

    private static BomKind ParseKind(string kind) => kind switch { "Standard" => BomKind.Standard, "NonStandard" => BomKind.NonStandard, "Electrical" => BomKind.Electrical,
        _ => throw new PdmRuleException("请选择标准件、非标件或电气件BOM。") };

    [Description("按料号、名称或规格查询料品主档，最多20条；不读取价格、附件或外部链接。")]
    public async Task<object> SearchMaterials([Description("料号或名称关键词，最多100字")] string query, CancellationToken ct)
    {
        Count(); ValidateQuery(query); await access.RequireAsync(AiPermissions.Business, null, ct); await access.RequireAsync(PermissionCodes.MaterialView, null, ct);
        var user = await repository.FindUserAsync(access.Tenant.Username, ct) ?? throw new UnauthorizedAccessException("请重新登录。");
        var rows = await materials.ListMaterialsAsync(query, null, false, 20, user.Username, user.Role, ct);
        Sources.Add(new("料品主档查询", null, [PermissionCodes.MaterialView], "materials"));
        return new { items = rows.Select(m => new { m.MaterialCode, m.Name, m.Specification, m.Brand, m.UnitCode }), limit = 20 };
    }

    [Description("读取指定项目当前维护的BOM明细，最多100条。kind只允许Standard、NonStandard、Electrical；结果不是发布快照，不推算CAD位置数量。")]
    public async Task<object> ReadProjectBom([Description("SearchProjects返回的项目ID")] Guid projectId, string kind,
        [Description("图号或名称关键词，可为空，最多100字")] string query, CancellationToken ct)
    {
        Count(); ValidateQuery(query);
        var bomKind = kind switch { "Standard" => BomKind.Standard, "NonStandard" => BomKind.NonStandard, "Electrical" => BomKind.Electrical,
            _ => throw new PdmRuleException("请选择标准件、非标件或电气件BOM。") };
        var permission = bomKind == BomKind.Electrical ? "bom.electrical.row.view" : "bom.mechanical.row.view";
        await access.RequireAsync(AiPermissions.Business, projectId, ct);
        foreach (var code in new[] { PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("bom"), permission }) await access.RequireAsync(code, projectId, ct);
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在或不可访问。");
        var user = await repository.FindUserAsync(access.Tenant.Username, ct) ?? throw new UnauthorizedAccessException("请重新登录。");
        var current = await workflow.GetReadOnlyBomAsync(projectId, bomKind, user.Username, user.Role, ct);
        var rows = current.Where(b => !b.IsManuallyExcluded && !b.IsPendingRemoval).GroupBy(b => b.RelationOptionId)
            .SelectMany(group => BomSummaryProjection.Group(group.ToArray(), includeFingerprint: false))
            .Where(b => b.DrawingNumber.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Sources.Add(new(project.Code + " BOM", projectId, [PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("bom"), permission], "bom"));
        return new { project = project.Code, kind, total = rows.Length, limit = 100,
            note = "当前维护明细；保留每行数量、发布标记与配置标识，不自动均分或跨配置合并。", items = rows.Take(100).Select(b => new { b.DrawingNumber, b.Name, b.Quantity, b.Unit, b.Specification, b.Brand, b.Revision, b.IsPublishedInBom, b.BomPublishedVersion, b.RelationOptionId }) };
    }
    private static void ValidateQuery(string query) { if (query is null || query.Length > 100) throw new PdmRuleException("查询关键词不能超过100字。"); }
}
