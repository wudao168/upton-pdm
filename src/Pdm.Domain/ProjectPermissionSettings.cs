namespace Upton.Pdm.Domain;

public static class ProjectPermissionPositions
{
    public const string MainManager = "MainManager";
    public const string ChildManager = "ChildManager";
    public const string MainDesigner = "MainDesigner";
    public const string Engineer = "Engineer";
    public const string Other = "Other";
    public static readonly string[] All = [MainManager, ChildManager, MainDesigner, Engineer, Other];
    public static IReadOnlyList<ProjectPositionDefinition> BuiltIn { get; } =
    [
        new(MainManager, "主项目经理／协同项目经理", "负责的主项目及全部子项目", "subtree", true),
        new(ChildManager, "子项目负责人", "仅负责的子项目", "self", true),
        new(MainDesigner, "主设", "负责的主项目及全部子项目", "subtree", true),
        new(Engineer, "执行工程师／电气硬件", "执行工程师明确分配的项目；主项目电气硬件负责人覆盖主／子项目，子项目负责人仅限本项目", "self", true),
        new(Other, "其他人员", "同公司内未担任项目岗位、且有内容查看权限的人员（兜底规则）", "self", true)
    ];
}

public sealed record ProjectPositionDefinition(string Key, string Name, string Description, string Scope = "self", bool IsSystem = false, bool IsActive = true, int SortOrder = 0);

public sealed record ProjectPermissionSettings(IReadOnlyDictionary<string, IReadOnlyList<string>> Rules,
    IReadOnlyList<ProjectPositionDefinition>? CustomPositions = null, int SchemaVersion = 1)
{
    public IReadOnlyList<ProjectPositionDefinition> Positions => ProjectPermissionPositions.BuiltIn.Concat((CustomPositions ?? []).OrderBy(item => item.SortOrder).ThenBy(item => item.Name)).ToArray();
    public static string[] Operations => RolePermissionCatalog.Permissions.Where(item => item.Scope == "project" && item.ProjectAuthorization is "position" or "budget").Select(item => item.Code).ToArray();
    public static ProjectPermissionSettings Default { get; } = new(new Dictionary<string, IReadOnlyList<string>>
    {
        [ProjectPermissionPositions.MainManager] = [PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectPlanEdit, PermissionCodes.ProjectEdit, PermissionCodes.ProjectChildCreate, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.ChildManager] = [PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectPlanEdit, PermissionCodes.ProjectEdit, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.MainDesigner] = [PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectDesignerAssign, PermissionCodes.BomEdit, PermissionCodes.BomMechanicalEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.Engineer] = [PermissionCodes.BomEdit, PermissionCodes.BomMechanicalEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.Other] = []
    });

    public ProjectPermissionSettings Normalize()
    {
        var custom = (CustomPositions ?? []).Select(item => item with { Name = item.Name?.Trim() ?? "", Description = item.Description?.Trim() ?? "" }).ToArray();
        if (custom.Any(item => !System.Text.RegularExpressions.Regex.IsMatch(item.Key ?? "", "^custom-[a-f0-9]{32}$") || item.IsSystem
            || item.Name.Length is < 1 or > 60 || item.Description.Length > 300 || item.Scope is not ("self" or "subtree"))) throw new ArgumentException("自定义岗位名称、范围或标识无效。");
        if (custom.Select(item => item.Key).Distinct().Count() != custom.Length || ProjectPermissionPositions.BuiltIn.Concat(custom).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != custom.Length + ProjectPermissionPositions.All.Length)
            throw new ArgumentException("项目岗位名称或标识不能重复。");
        var keys = ProjectPermissionPositions.All.Concat(custom.Select(item => item.Key)).ToHashSet();
        if (Rules is null || Rules.Keys.Any(key => !keys.Contains(key))) throw new ArgumentException("包含未知的项目岗位。");
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!Rules.TryGetValue(key, out var values) || values is null) throw new ArgumentException("缺少项目岗位的授权配置。");
            if (values.Any(code => !Operations.Contains(code, StringComparer.Ordinal))) throw new ArgumentException("岗位包含未知或不适用的项目权限。");
            var expanded = FunctionPermissionCatalog.Expand(values).Where(Operations.Contains).ToHashSet(StringComparer.Ordinal);
            if (SchemaVersion < 2 && ProjectPermissionPositions.All.Contains(key)) expanded.UnionWith(RolePermissionCatalog.Permissions.Where(item => item.LegacyAllPositions).Select(item => item.Code));
            normalized[key] = expanded.Where(code => !RolePermissionCatalog.Permissions.Single(item => item.Code == code).Hidden).Order(StringComparer.Ordinal).ToArray();
        }
        return new(normalized, custom, 2);
    }
}
