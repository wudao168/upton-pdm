using System.Text.RegularExpressions;

namespace Upton.Pdm.Domain;

public sealed record FunctionRoute(string Method, string Path);

/// <summary>功能、权限依赖和接口操作在此声明一次，两种授权页面共用此目录。</summary>
public static class FunctionPermissionCatalog
{
    public const string SchemaMarker = "system.permission-profile.v2";
    public static IReadOnlyList<PermissionDefinition> Build(IReadOnlyList<PermissionDefinition> existing)
    {
        var result = existing.Select(Describe).ToList();
        // 新功能在这里声明一次（legacy 传 null），两类设置和接口校验自动接入，默认不授权。
        void Add(string code, string name, string module, string? legacy, bool allPositions, params FunctionRoute[] routes) => result.Add(new(code, name, module)
        {
            Scope = "project", Action = Action(code, name), LegacyPermission = legacy,
            ProjectAuthorization = "position", LegacyAllPositions = allPositions,
            Dependencies = [PermissionCodes.ProjectContentView], Routes = routes
        });
        FunctionRoute Route(string method, string path) => new(method, "/api" + path);
        foreach (var item in new[] { ("create", "创建项目计划", "POST", "/generate"), ("update", "编辑项目计划", "PUT", ""), ("delete", "清空项目计划", "DELETE", ""), ("baseline", "建立计划基线", "POST", "/baseline"), ("submit", "提交计划审批", "POST", "/submit"), ("progress", "维护任务进度", "PUT", "/tasks/{taskId}/progress"), ("stage", "维护项目阶段", "PUT", "/stage"), ("reuse", "复用项目计划", "POST", "/reuse"), ("schedule", "补充阶段计划", "POST", "/stage-schedule"), ("change", "提交计划变更", "POST", "/change-request"), ("change-complete", "完成计划变更", "POST", "/change-complete"), ("change-abandon", "放弃计划变更", "POST", "/change-abandon") })
            Add("project.plan." + item.Item1, item.Item2, "项目计划", PermissionCodes.ProjectPlanEdit, false, Route(item.Item3, "/projects/{projectId}/plan" + item.Item4));
        Add("project.plan.view", "查看项目计划", "项目计划", PermissionCodes.ProjectContentView, true, Route("GET", "/projects/{projectId}/plan"), Route("GET", "/projects/{projectId}/plan/portfolio"), Route("GET", "/projects/{projectId}/plan/versions"));
        foreach (var item in new[] { ("create", "新建项目文件夹", "POST", "/folders/{folderId}/children"), ("edit", "编辑项目文件夹", "PATCH", "/folders/{folderId}"), ("move", "移动项目文件夹", "POST", "/folders/{folderId}/move"), ("delete", "删除项目文件夹", "DELETE", "/folders/{folderId}"), ("view", "查看项目文件", "GET", "/files"), ("upload", "上传项目文件", "POST", "/folders/{folderId}/file-uploads"), ("download", "下载项目文件", "GET", "/files/{fileId}/content"), ("rename", "重命名项目文件", "PATCH", "/files/{fileId}"), ("description", "编辑项目文件说明", "PUT", "/files/{fileId}/description"), ("relocate", "移动项目文件", "POST", "/files/{fileId}/move"), ("recycle", "回收项目文件", "DELETE", "/files/{fileId}"), ("restore", "恢复项目文件", "POST", "/files/{fileId}/restore") })
            Add("project.file." + item.Item1, item.Item2, "项目文件", PermissionCodes.ProjectContentView, true, Route(item.Item3, "/projects/{projectId}" + item.Item4));
        foreach (var item in new[] { ("view", "查看OPL", "GET", ""), ("create", "创建OPL问题", "POST", ""), ("edit", "编辑OPL问题", "PUT", "/{issueId}"), ("complete", "完成OPL问题", "POST", "/{issueId}/complete"), ("download", "下载OPL附件", "GET", "/{issueId}/attachments/{attachmentId}") })
            Add("project.opl." + item.Item1, item.Item2, "项目OPL", PermissionCodes.ProjectContentView, true, Route(item.Item3, "/projects/{projectId}/opl" + item.Item4));
        foreach (var item in new[] { ("register", "登记图档", "POST", "/projects/{projectId}/documents/register"), ("checkout", "签出图档", "POST", "/documents/{documentId}/checkout"), ("archive", "存档图档", "POST", "/documents/{documentId}/checkin"), ("restore", "恢复图档历史版本", "POST", "/documents/{documentId}/versions/{versionId}/restore") })
            Add("document." + item.Item1, item.Item2, "项目图档", PermissionCodes.DocumentEdit, true, Route(item.Item3, item.Item4));
        Add("document.view", "查看图档及版本", "项目图档", PermissionCodes.ProjectContentView, true, Route("GET", "/projects/{projectId}/documents"), Route("GET", "/documents/{documentId}/versions"), Route("POST", "/documents/version-verifications"));
        foreach (var item in new[] { ("create", "创建发布包", "POST", "/release-packages"), ("edit", "编辑发布包草稿", "PUT", "/release-packages/{releasePackageId}/draft"), ("delete", "删除发布包草稿", "DELETE", "/release-packages/{releasePackageId}/draft"), ("submit", "提交发布包", "POST", "/release-packages/{releasePackageId}/submit"), ("withdraw", "撤回发布包", "POST", "/release-packages/{releasePackageId}/withdraw") })
            Add("release." + item.Item1, item.Item2, "审批发布", PermissionCodes.ReleaseManage, false, Route(item.Item3, item.Item4));
        Add("release.view", "查看发布记录", "审批发布", PermissionCodes.ProjectContentView, true, Route("GET", "/projects/{projectId}/release-packages"));
        foreach (var category in new[] { ("mechanical", "标准件／非标件BOM", PermissionCodes.BomMechanicalEdit), ("electrical", "电气件BOM", PermissionCodes.BomElectricalEdit) })
        {
            foreach (var action in new[] { ("create", "新增明细"), ("update", "编辑明细"), ("delete", "删除明细"), ("restore", "恢复明细"), ("import", "导入明细") })
                Add($"bom.{category.Item1}.row.{action.Item1}", action.Item2, category.Item2, category.Item3, false);
            var kinds = category.Item1 == "electrical" ? new[] { "electrical" } : new[] { "standard", "nonstandard" };
            Add($"bom.{category.Item1}.row.view", "查看明细", category.Item2, PermissionCodes.ProjectContentView, true, kinds.Select(kind => Route("GET", $"/projects/{{projectId}}/boms/{kind}")).ToArray());
            Add($"bom.{category.Item1}.row.export", "导出明细", category.Item2, PermissionCodes.ProjectContentView, true, kinds.Select(kind => Route("GET", $"/projects/{{projectId}}/boms/{kind}/export")).ToArray());
            var import = result.FindIndex(item => item.Code == $"bom.{category.Item1}.row.import");
            result[import] = result[import] with { Routes = kinds.Select(kind => Route("POST", $"/projects/{{projectId}}/boms/{kind}/import")).ToArray(), Dependencies = [PermissionCodes.ProjectContentView, $"bom.{category.Item1}.row.create", $"bom.{category.Item1}.row.update", $"bom.{category.Item1}.row.delete"] };
        }
        foreach (var operation in result.Where(item => item.Scope == "project" && (item.LegacyPermission is not null || item.Routes.Count > 0) && item.Action != "view").ToArray())
        {
            var view = result.FirstOrDefault(item => item.Module == operation.Module && item.Code.EndsWith(".view"));
            if (view is not null) result[result.IndexOf(operation)] = operation with { Dependencies = operation.Dependencies.Append(view.Code).Distinct().ToArray() };
        }
        result.Add(new(SchemaMarker, "权限配置格式", "系统内部") { Hidden = true });
        var bundles = result.Where(item => item.LegacyPermission is not null && item.LegacyPermission != PermissionCodes.ProjectContentView).Select(item => item.LegacyPermission!).ToHashSet();
        var keys = result.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != result.Count || result.SelectMany(item => item.Dependencies).Any(code => !keys.Contains(code))) throw new InvalidOperationException("功能目录存在重复权限或缺失的依赖，请检查功能声明。");
        return result.Select(item => bundles.Contains(item.Code) ? item with { Hidden = true } : item).ToArray();
    }

    private static PermissionDefinition Describe(PermissionDefinition item)
    {
        var project = item.Code.StartsWith("validation-plan.") || item.Code.StartsWith("project.") || item.Code.StartsWith("quality.") || item.Code.StartsWith("document.") || item.Code.StartsWith("bom.") || item.Code.StartsWith("drawing-review.") || item.Code.StartsWith("release.") || item.Code.StartsWith("approval.") || item.Code.StartsWith("production.drawing.");
        var dependencies = new List<string>();
        if (project && item.Code != PermissionCodes.ProjectView) dependencies.Add(PermissionCodes.ProjectView);
        if (project && item.Code != PermissionCodes.ProjectContentView && (item.Module is "项目内容" or "质量资料" or "图纸审核" or "审批发布" || item.Code == PermissionCodes.ProjectBudgetView)) dependencies.Add(PermissionCodes.ProjectContentView);
        if (item.Code.StartsWith("project.budget.") && item.Code != PermissionCodes.ProjectBudgetView) dependencies.Add(PermissionCodes.ProjectBudgetView);
        if (item.Code == PermissionCodes.ProjectBudgetSettlementEdit) dependencies.Add(PermissionCodes.ProjectBudgetSettlementView);
        if (item.Code.StartsWith("program-template.") && item.Code != PermissionCodes.ProgramTemplateView) dependencies.Add(PermissionCodes.ProgramTemplateView);
        if (item.Code == PermissionCodes.StandardLibraryManage) dependencies.Add(PermissionCodes.StandardLibraryView);
        if (item.Code == PermissionCodes.ProductionSupplierManage) dependencies.Add(PermissionCodes.ProductionSupplierView);
        if (item.Code is PermissionCodes.MaterialRelationManage or PermissionCodes.MaterialRelationPublish) dependencies.Add(PermissionCodes.MaterialRelationView);
        if (item.Code == PermissionCodes.MaterialManage) dependencies.Add(PermissionCodes.MaterialApply);
        if (item.Code == PermissionCodes.MaterialApply) dependencies.Add(PermissionCodes.MaterialView);
        if (item.Code == PermissionCodes.RoleSettingsEdit) dependencies.Add(PermissionCodes.RoleSettingsView);
        if (item.Code == PermissionCodes.ProjectPermissionSettingsEdit) dependencies.Add(PermissionCodes.ProjectPermissionSettingsView);
        var position = item.Code is PermissionCodes.ProjectPlanEdit or PermissionCodes.ProjectEdit or PermissionCodes.ProjectDelete or PermissionCodes.ProjectChildCreate or PermissionCodes.ProjectDesignerAssign or PermissionCodes.BomEdit or PermissionCodes.BomMechanicalEdit or PermissionCodes.BomElectricalEdit or PermissionCodes.ValidationPlanEdit or PermissionCodes.ReleaseManage;
        return item with { Scope = project ? "project" : "system", Action = Action(item.Code, item.Name), Dependencies = dependencies, ProjectAuthorization = item.Code == PermissionCodes.ProjectBudgetView ? "budget" : position ? "position" : "role" };
    }

    public static string Action(string code, string name) => Regex.IsMatch(code, @"\.(view|download)$") || Regex.IsMatch(name, "查看|浏览|下载|查询") ? "view"
        : Regex.IsMatch(code, @"\.(delete|recycle)$") || Regex.IsMatch(name, "删除|回收|作废") ? "delete"
        : Regex.IsMatch(code, @"\.(create|register|upload)$") || Regex.IsMatch(name, "新建|创建|登记|上传") ? "create"
        : Regex.IsMatch(code, @"\.(edit|update|rename|description)$") || Regex.IsMatch(name, "编辑|维护|配置") ? "edit" : "other";

    public static IReadOnlySet<string> Expand(IEnumerable<string> source)
    {
        var values = source.ToHashSet(StringComparer.Ordinal);
        foreach (var operation in RolePermissionCatalog.Permissions.Where(item => item.LegacyPermission is not null))
            if (values.Contains(operation.LegacyPermission!) && (operation.LegacyPermission != PermissionCodes.ProjectContentView || !values.Contains(SchemaMarker))) values.Add(operation.Code);
        return values;
    }

    public static bool Contains(IReadOnlySet<string> permissions, string code) => permissions.Contains(code)
        || RolePermissionCatalog.Permissions.Any(item => item.LegacyPermission == code && permissions.Contains(item.Code));

    public static IReadOnlySet<string> Effective(IEnumerable<string> source)
    {
        var values = Expand(source).ToHashSet(StringComparer.Ordinal);
        foreach (var item in RolePermissionCatalog.Permissions.Where(item => item.LegacyPermission is not null))
            if (values.Contains(item.Code)) values.Add(item.LegacyPermission!);
        values.Remove(SchemaMarker);
        return values;
    }
}
