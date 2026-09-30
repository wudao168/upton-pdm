namespace Upton.Pdm.Domain;

public static class ProjectPermissionPositions
{
    public const string MainManager = "MainManager";
    public const string ChildManager = "ChildManager";
    public const string MainDesigner = "MainDesigner";
    public const string Engineer = "Engineer";
    public const string Other = "Other";

    public static readonly string[] All = [MainManager, ChildManager, MainDesigner, Engineer, Other];
}

public sealed record ProjectPermissionSettings(IReadOnlyDictionary<string, IReadOnlyList<string>> Rules)
{
    public static readonly string[] Operations =
    [
        PermissionCodes.ProjectEdit,
        PermissionCodes.ProjectDelete,
        PermissionCodes.ProjectChildCreate,
        PermissionCodes.ProjectDesignerAssign,
        PermissionCodes.BomEdit,
        PermissionCodes.BomMechanicalEdit,
        PermissionCodes.BomElectricalEdit,
        PermissionCodes.ValidationPlanEdit,
        PermissionCodes.ReleaseManage
    ];

    public static ProjectPermissionSettings Default { get; } = new(new Dictionary<string, IReadOnlyList<string>>
    {
        [ProjectPermissionPositions.MainManager] = [PermissionCodes.ProjectEdit, PermissionCodes.ProjectChildCreate, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.ChildManager] = [PermissionCodes.ProjectEdit, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.MainDesigner] = [PermissionCodes.ProjectDesignerAssign, PermissionCodes.BomEdit, PermissionCodes.BomMechanicalEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.Engineer] = [PermissionCodes.BomEdit, PermissionCodes.BomMechanicalEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ValidationPlanEdit, PermissionCodes.ReleaseManage],
        [ProjectPermissionPositions.Other] = []
    });

    public ProjectPermissionSettings Normalize()
    {
        if (Rules is null || Rules.Keys.Any(key => !ProjectPermissionPositions.All.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("包含未知的项目岗位。");
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var position in ProjectPermissionPositions.All)
        {
            if (!Rules.TryGetValue(position, out var permissions) || permissions is null)
                throw new ArgumentException($"缺少{position}的项目权限配置。");
            if (permissions.Any(code => !Operations.Contains(code, StringComparer.Ordinal)))
                throw new ArgumentException($"{position}包含未知的项目权限。");
            normalized[position] = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        return new(normalized);
    }
}
