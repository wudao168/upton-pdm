namespace Upton.Pdm.Domain;

public static class PermissionCodes
{
    public const string ProjectView = "project.view";
    public const string ProjectCreate = "project.create";
    public const string ProjectEdit = "project.edit";
    public const string ProjectChildCreate = "project.child.create";
    public const string ProjectDelete = "project.delete";
    public const string ProjectExecutionAssign = "project.execution.assign";
    public const string ProjectStaffingManage = "project.staffing.manage";
    public const string ProjectDesignerAssign = "project.designer.assign";
    public const string ProjectContentView = "project.content.view";
    public const string DocumentEdit = "document.edit";
    public const string DocumentLockRequestRelease = "document.lock.request-release";
    public const string DocumentLockForceRelease = "document.lock.force-release";
    public const string BomEdit = "bom.edit";
    public const string ReleaseManage = "release.manage";
    public const string ApprovalDecide = "approval.decide";
    public const string CustomerSettingsManage = "settings.customer.manage";
    public const string OrganizationSettingsManage = "settings.organization.manage";
    public const string FolderSettingsManage = "settings.folder.manage";
    public const string StorageSettingsManage = "settings.storage.manage";
    public const string RoleSettingsView = "system.role.view";
    public const string RoleSettingsEdit = "system.role.edit";
    public const string AuditView = "audit.view";
}

public static class RolePermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionCodes.ProjectView, "查看负责项目", "项目管理", "仍受项目岗位和组织数据范围限制。"),
        new(PermissionCodes.ProjectCreate, "创建主项目", "项目管理"),
        new(PermissionCodes.ProjectEdit, "编辑项目基本信息", "项目管理", "可修改创建项目时填写的业务信息；涉及编号字段时由系统校验并重新生成。"),
        new(PermissionCodes.ProjectChildCreate, "创建子项目", "项目管理"),
        new(PermissionCodes.ProjectDelete, "删除空项目", "项目管理", "仅可删除无子项目、图档、BOM、结构快照及审批发布包的项目；成功删除后释放系统编号。", Sensitive: true),
        new(PermissionCodes.ProjectExecutionAssign, "分配执行事业部", "项目分工", "仅可分配本人所属公司的项目。"),
        new(PermissionCodes.ProjectStaffingManage, "配置项目经理与设计负责人", "项目分工", "非管理员还必须是执行事业部负责人。"),
        new(PermissionCodes.ProjectDesignerAssign, "分配子项目设计人员", "项目分工", "非管理员还必须是主项目设计负责人。"),
        new(PermissionCodes.ProjectContentView, "查看项目图档与业务内容", "项目内容", "项目经理仅看状态；设计、审批岗位按分配范围查看内容。"),
        new(PermissionCodes.DocumentEdit, "登记、签出和存档图档", "项目内容", Sensitive: true),
        new(PermissionCodes.DocumentLockRequestRelease, "催办并申请释放编辑权限", "项目内容"),
        new(PermissionCodes.DocumentLockForceRelease, "强制释放超时编辑权限", "项目内容", "仅限本人负责项目，系统管理员不受项目岗位限制。", Sensitive: true),
        new(PermissionCodes.BomEdit, "维护BOM和料品", "项目内容"),
        new(PermissionCodes.ReleaseManage, "创建并提交发布包", "审批发布", Sensitive: true),
        new(PermissionCodes.ApprovalDecide, "处理发布审批", "审批发布", Sensitive: true),
        new(PermissionCodes.CustomerSettingsManage, "配置U9C客户同步", "系统设置", "复用U9C OAuth配置并定期同步客户编码和名称。", Sensitive: true),
        new(PermissionCodes.OrganizationSettingsManage, "维护公司与组织结构", "系统设置", Sensitive: true),
        new(PermissionCodes.FolderSettingsManage, "维护文件夹模板与目录权限", "系统设置", Sensitive: true),
        new(PermissionCodes.StorageSettingsManage, "维护编号、设备类型和存储位置", "系统设置", Sensitive: true),
        new(PermissionCodes.RoleSettingsView, "查看角色权限", "角色权限"),
        new(PermissionCodes.RoleSettingsEdit, "修改角色权限", "角色权限", Sensitive: true),
        new(PermissionCodes.AuditView, "查看全局审计", "系统审计", Sensitive: true)
    ];

    public static IReadOnlyDictionary<UserRole, IReadOnlySet<string>> Defaults { get; } =
        new Dictionary<UserRole, IReadOnlySet<string>>
        {
            [UserRole.Engineer] = Set(
                PermissionCodes.ProjectView,
                PermissionCodes.ProjectCreate,
                PermissionCodes.ProjectEdit,
                PermissionCodes.ProjectChildCreate,
                PermissionCodes.ProjectStaffingManage,
                PermissionCodes.ProjectDesignerAssign,
                PermissionCodes.ProjectContentView,
                PermissionCodes.DocumentEdit,
                PermissionCodes.DocumentLockRequestRelease,
                PermissionCodes.DocumentLockForceRelease,
                PermissionCodes.BomEdit,
                PermissionCodes.ReleaseManage),
            [UserRole.PlanningManager] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectExecutionAssign),
            [UserRole.ProcessReviewer] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ApprovalDecide),
            [UserRole.Approver] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ApprovalDecide),
            [UserRole.ProductionViewer] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView),
            [UserRole.Administrator] = Set(Permissions.Select(permission => permission.Code).ToArray())
        };

    public static IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(UserRole.Engineer.ToString(), "工程师", "承担设计、图档、BOM及发布准备工作。", UserRole.Engineer, true),
        new(UserRole.PlanningManager.ToString(), "计划管理", "按所属公司分配项目执行事业部。", UserRole.PlanningManager, true),
        new(UserRole.ProcessReviewer.ToString(), "工艺审核", "处理分配给本人的工艺审核任务。", UserRole.ProcessReviewer, true),
        new(UserRole.Approver.ToString(), "批准人", "处理分配给本人的批准任务。", UserRole.Approver, true),
        new(UserRole.ProductionViewer.ToString(), "生产查看", "按后续项目岗位或目录授权查看生产资料。", UserRole.ProductionViewer, true),
        new(UserRole.Administrator.ToString(), "系统管理员", "固定拥有全部权限，防止系统管理锁死。", UserRole.Administrator, true, true)
    ];

    public static bool IsKnown(string code) => Permissions.Any(permission => string.Equals(permission.Code, code, StringComparison.Ordinal));

    public static IReadOnlySet<string> Normalize(UserRole role, IEnumerable<string> codes)
    {
        if (role == UserRole.Administrator) return Defaults[UserRole.Administrator];
        var normalized = codes.Where(IsKnown).ToHashSet(StringComparer.Ordinal);
        if (normalized.Any(code => code.StartsWith("project.", StringComparison.Ordinal)
                || code is PermissionCodes.DocumentEdit or PermissionCodes.DocumentLockRequestRelease or PermissionCodes.DocumentLockForceRelease or PermissionCodes.BomEdit or PermissionCodes.ReleaseManage or PermissionCodes.ApprovalDecide))
            normalized.Add(PermissionCodes.ProjectView);
        if (normalized.Any(code => code is PermissionCodes.DocumentEdit or PermissionCodes.DocumentLockRequestRelease or PermissionCodes.DocumentLockForceRelease or PermissionCodes.BomEdit or PermissionCodes.ReleaseManage or PermissionCodes.ApprovalDecide))
            normalized.Add(PermissionCodes.ProjectContentView);
        if (normalized.Contains(PermissionCodes.RoleSettingsEdit)) normalized.Add(PermissionCodes.RoleSettingsView);
        return normalized;
    }

    private static IReadOnlySet<string> Set(params string[] codes) => codes.ToHashSet(StringComparer.Ordinal);
}
