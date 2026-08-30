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
    public const string MaterialView = "material.view";
    public const string MaterialManage = "material.manage";
    public const string DrawingReviewSubmit = "drawing-review.submit";
    public const string DrawingReviewAnnotate = "drawing-review.annotate";
    public const string DrawingReviewDecide = "drawing-review.decide";
    public const string ReleaseManage = "release.manage";
    public const string ApprovalDecide = "approval.decide";
    public const string ApprovalEmergencySubstitute = "approval.emergency-substitute";
    public const string ProgramTemplateView = "program-template.view";
    public const string ProgramTemplateSubmit = "program-template.submit";
    public const string ProgramTemplateReview = "program-template.review";
    public const string ProgramTemplateApprove = "program-template.approve";
    public const string ProgramTemplateManage = "program-template.manage";
    public const string StandardLibraryView = "standard-library.view";
    public const string StandardLibraryManage = "standard-library.manage";
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
        new(PermissionCodes.ProjectView, "查看项目清单", "项目管理", "查看当前公司项目元数据；项目图档与业务内容仍按内容权限和项目分工控制。"),
        new(PermissionCodes.ProjectCreate, "创建主项目", "项目管理"),
        new(PermissionCodes.ProjectEdit, "编辑项目基本信息", "项目管理", "可修改创建项目时填写的业务信息；涉及编号字段时由系统校验并重新生成。"),
        new(PermissionCodes.ProjectChildCreate, "创建子项目", "项目管理"),
        new(PermissionCodes.ProjectDelete, "删除空项目", "项目管理", "仅可删除无子项目、图档、BOM、结构快照及审批发布包的项目；成功删除后释放系统编号。", Sensitive: true),
        new(PermissionCodes.ProjectExecutionAssign, "分配执行事业部", "项目分工", "仅可分配本人所属公司的项目。"),
        new(PermissionCodes.ProjectStaffingManage, "配置项目经理与设计负责人", "项目分工", "非管理员还必须是执行事业部负责人。"),
        new(PermissionCodes.ProjectDesignerAssign, "分配子项目设计人员", "项目分工", "非管理员还必须是主项目设计负责人。"),
        new(PermissionCodes.ProjectContentView, "查看项目图档与业务内容", "项目内容", "拥有此权限的角色可查看所属公司内项目的图档、BOM、发布和版本内容。"),
        new(PermissionCodes.DocumentEdit, "登记、签出和存档图档", "项目内容", Sensitive: true),
        new(PermissionCodes.DocumentLockRequestRelease, "催办并申请释放编辑权限", "项目内容"),
        new(PermissionCodes.DocumentLockForceRelease, "强制释放超时编辑权限", "项目内容", "仅限本人负责项目，系统管理员不受项目岗位限制。", Sensitive: true),
        new(PermissionCodes.BomEdit, "维护项目BOM", "项目内容"),
        new(PermissionCodes.MaterialView, "查看料品管理", "料品管理", "查看料品主档、审批状态和U9C同步结果。"),
        new(PermissionCodes.MaterialManage, "维护料品主档", "料品管理", "新增、修改、批准、停用或删除料品，并执行U9C料品同步。", Sensitive: true),
        new(PermissionCodes.DrawingReviewSubmit, "发起图纸审核", "图纸审核", "按当前非标件BOM冻结3D和2D图档版本。", Sensitive: true),
        new(PermissionCodes.DrawingReviewAnnotate, "添加和处理图纸批注", "图纸审核"),
        new(PermissionCodes.DrawingReviewDecide, "审核3D和2D图纸", "图纸审核", "设计者不能审核自己生成的图档版本。", Sensitive: true),
        new(PermissionCodes.ReleaseManage, "创建并提交发布包", "审批发布", Sensitive: true),
        new(PermissionCodes.ApprovalDecide, "处理发布审批", "审批发布", Sensitive: true),
        new(PermissionCodes.ApprovalEmergencySubstitute, "紧急代批当前节点", "审批发布", "仅在紧急情况下替代当前审批人，必须填写原因，后续节点仍正常流转。", Sensitive: true),
        new(PermissionCodes.ProgramTemplateView, "查看和下载程序模板", "程序模板", "查看集团已发布的PLC、HMI程序模板。"),
        new(PermissionCodes.ProgramTemplateSubmit, "上传和提交程序模板", "程序模板", "维护本人创建的草稿并提交审核。", Sensitive: true),
        new(PermissionCodes.ProgramTemplateReview, "审核程序模板", "程序模板", "仅能处理分配给本人的电气组织审核任务。", Sensitive: true),
        new(PermissionCodes.ProgramTemplateApprove, "批准程序模板", "程序模板", "处理集团标准化主管池中的最终批准任务。", Sensitive: true),
        new(PermissionCodes.ProgramTemplateManage, "管理程序模板", "程序模板", "停用、恢复和维护集团程序模板。", Sensitive: true),
        new(PermissionCodes.StandardLibraryView, "查看标准库", "标准库", "浏览推荐料品并下载受控的3D和产品资料。"),
        new(PermissionCodes.StandardLibraryManage, "管理标准库", "标准库", "维护标准分类、成员、封面及推荐属性。", Sensitive: true),
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
                PermissionCodes.DrawingReviewSubmit,
                PermissionCodes.DrawingReviewAnnotate,
                PermissionCodes.ReleaseManage,
                PermissionCodes.ProgramTemplateView,
                PermissionCodes.StandardLibraryView),
            [UserRole.PlanningManager] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectExecutionAssign, PermissionCodes.ProjectContentView, PermissionCodes.ProgramTemplateView),
            [UserRole.ProcessReviewer] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide, PermissionCodes.ApprovalDecide, PermissionCodes.ProgramTemplateView, PermissionCodes.StandardLibraryView, PermissionCodes.MaterialView, PermissionCodes.MaterialManage),
            [UserRole.Approver] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide, PermissionCodes.ApprovalDecide, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateApprove, PermissionCodes.StandardLibraryView, PermissionCodes.MaterialView, PermissionCodes.MaterialManage),
            [UserRole.ProductionViewer] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProgramTemplateView),
            [UserRole.BusinessUnitManager] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide, PermissionCodes.ApprovalDecide, PermissionCodes.ApprovalEmergencySubstitute, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateReview),
            [UserRole.Administrator] = Set(Permissions.Select(permission => permission.Code).ToArray()),
            [UserRole.PlatformAdministrator] = Set(
                PermissionCodes.CustomerSettingsManage,
                PermissionCodes.OrganizationSettingsManage,
                PermissionCodes.FolderSettingsManage,
                PermissionCodes.StorageSettingsManage,
                PermissionCodes.RoleSettingsView,
                PermissionCodes.RoleSettingsEdit,
                PermissionCodes.AuditView)
        };

    public static IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(UserRole.Engineer.ToString(), "机械工程师", "负责机械图档、BOM及发布资料准备。", UserRole.Engineer, true),
        new("ElectricalEngineer", "电气工程师", "负责电气图档、BOM及发布资料准备。", UserRole.Engineer, true),
        new("CommissioningEngineer", "调试工程师", "负责调试图档、问题记录及相关BOM维护。", UserRole.Engineer, true),
        new("HardwareEngineer", "硬件工程师", "负责硬件图档、BOM及发布资料准备。", UserRole.Engineer, true),
        new("MechanicalManager", "机械经理", "负责机械专业复核、发布准备及受控编辑协调。", UserRole.Approver, true),
        new("TechnicalAssistant", "技术助理", "协助维护技术图档、BOM和项目资料。", UserRole.Engineer, true),
        new(UserRole.BusinessUnitManager.ToString(), "事业部经理", "负责事业部项目分工、审批及紧急代批。", UserRole.Approver, true),
        new(UserRole.ProcessReviewer.ToString(), "标准化工程师", "负责标准化检查并处理分配的审批任务。", UserRole.ProcessReviewer, true),
        new("ProjectManager", "项目经理", "负责项目建立、人员分工及发布组织。", UserRole.Engineer, true),
        new("SupplyChain", "供应链", "查看负责范围内的项目、BOM和生产资料。", UserRole.ProductionViewer, true),
        new("ProcurementSpecialist", "采购专员", "查看负责范围内的项目、BOM和采购资料。", UserRole.ProductionViewer, true),
        new("ProcurementManager", "采购经理", "查看采购资料并处理分配的审批任务。", UserRole.Approver, true),
        new("ProductionManager", "生产经理", "查看生产资料并处理分配的审批任务。", UserRole.Approver, true),
        new("ProductionAssistant", "生产助理", "协助查看和组织负责范围内的生产资料。", UserRole.ProductionViewer, true),
        new("MachiningSupervisor", "机加主管", "查看负责范围内的机加图档和生产资料。", UserRole.ProductionViewer, true),
        new("MachiningOperator", "机加人员", "查看分配给本人的机加图档和生产资料。", UserRole.ProductionViewer, true),
        new("AssemblySupervisor", "装配主管", "查看负责范围内的装配图档和生产资料。", UserRole.ProductionViewer, true),
        new("AssemblyFitter", "装配钳工", "查看分配给本人的装配图档和生产资料。", UserRole.ProductionViewer, true),
        new("ElectricalSupervisor", "电工主管", "查看负责范围内的电气装配图档和生产资料。", UserRole.ProductionViewer, true),
        new("AssemblyElectrician", "装配电工", "查看分配给本人的电气装配图档和生产资料。", UserRole.ProductionViewer, true),
        new(UserRole.PlanningManager.ToString(), "计划管理", "按所属公司分配项目执行事业部。", UserRole.PlanningManager, true),
        new(UserRole.ProductionViewer.ToString(), "生产物料员", "查看负责范围内的BOM和生产物料资料。", UserRole.ProductionViewer, true),
        new(UserRole.Approver.ToString(), "标准化主管", "负责标准化审批和批准结论。", UserRole.Approver, true),
        new(UserRole.Administrator.ToString(), "系统管理员", "管理所属公司并在公司范围内拥有完整业务权限。", UserRole.Administrator, true, true),
        new("platform_admin", "平台管理员", "跨公司维护平台设置；不自动获得项目、图档和BOM业务权限。", UserRole.PlatformAdministrator, true, true),
        new("developer", "开发者", "跨公司访问全部业务数据并拥有全部系统权限。", UserRole.Administrator, true, true)
    ];

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> InitialRoleDefaults { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [UserRole.Engineer.ToString()] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.BomEdit, PermissionCodes.ReleaseManage, PermissionCodes.ProgramTemplateView, PermissionCodes.StandardLibraryView),
            ["ElectricalEngineer"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.BomEdit, PermissionCodes.ReleaseManage, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateSubmit, PermissionCodes.StandardLibraryView),
            ["CommissioningEngineer"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.BomEdit, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateSubmit, PermissionCodes.StandardLibraryView),
            ["HardwareEngineer"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.BomEdit, PermissionCodes.ReleaseManage, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateSubmit, PermissionCodes.StandardLibraryView),
            ["MechanicalManager"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.DocumentLockForceRelease, PermissionCodes.BomEdit, PermissionCodes.ReleaseManage, PermissionCodes.ApprovalDecide, PermissionCodes.ProgramTemplateView, PermissionCodes.StandardLibraryView),
            ["TechnicalAssistant"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit, PermissionCodes.DocumentLockRequestRelease, PermissionCodes.BomEdit, PermissionCodes.ProgramTemplateView, PermissionCodes.StandardLibraryView),
            [UserRole.BusinessUnitManager.ToString()] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectStaffingManage, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ProjectContentView, PermissionCodes.ApprovalDecide, PermissionCodes.ApprovalEmergencySubstitute, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateReview),
            [UserRole.ProcessReviewer.ToString()] = Defaults[UserRole.ProcessReviewer],
            ["ProjectManager"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectCreate, PermissionCodes.ProjectEdit, PermissionCodes.ProjectChildCreate, PermissionCodes.ProjectStaffingManage, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ProjectContentView, PermissionCodes.ReleaseManage, PermissionCodes.ProgramTemplateView),
            ["SupplyChain"] = Defaults[UserRole.ProductionViewer],
            ["ProcurementSpecialist"] = Defaults[UserRole.ProductionViewer],
            ["ProcurementManager"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide, PermissionCodes.ApprovalDecide, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateApprove, PermissionCodes.StandardLibraryView),
            ["ProductionManager"] = Set(PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide, PermissionCodes.ApprovalDecide, PermissionCodes.ProgramTemplateView, PermissionCodes.ProgramTemplateApprove, PermissionCodes.StandardLibraryView),
            ["ProductionAssistant"] = Defaults[UserRole.ProductionViewer],
            ["MachiningSupervisor"] = Defaults[UserRole.ProductionViewer],
            ["MachiningOperator"] = Defaults[UserRole.ProductionViewer],
            ["AssemblySupervisor"] = Defaults[UserRole.ProductionViewer],
            ["AssemblyFitter"] = Defaults[UserRole.ProductionViewer],
            ["ElectricalSupervisor"] = Defaults[UserRole.ProductionViewer],
            ["AssemblyElectrician"] = Defaults[UserRole.ProductionViewer],
            [UserRole.PlanningManager.ToString()] = Defaults[UserRole.PlanningManager],
            [UserRole.ProductionViewer.ToString()] = Defaults[UserRole.ProductionViewer],
            [UserRole.Approver.ToString()] = Defaults[UserRole.Approver],
            [UserRole.Administrator.ToString()] = Defaults[UserRole.Administrator],
            ["platform_admin"] = Defaults[UserRole.PlatformAdministrator],
            ["developer"] = Defaults[UserRole.Administrator]
        };

    public static IReadOnlySet<string> InitialPermissions(string roleCode, UserRole baseRole) =>
        InitialRoleDefaults.GetValueOrDefault(roleCode, Defaults[baseRole]);

    public static bool IsKnown(string code) => Permissions.Any(permission => string.Equals(permission.Code, code, StringComparison.Ordinal));

    public static IReadOnlySet<string> Normalize(UserRole role, IEnumerable<string> codes)
    {
        if (role == UserRole.Administrator) return Defaults[UserRole.Administrator];
        if (role == UserRole.PlatformAdministrator) return Defaults[UserRole.PlatformAdministrator];
        var normalized = codes.Where(IsKnown).ToHashSet(StringComparer.Ordinal);
        if (normalized.Any(code => code.StartsWith("project.", StringComparison.Ordinal)
                || code is PermissionCodes.DocumentEdit or PermissionCodes.DocumentLockRequestRelease or PermissionCodes.DocumentLockForceRelease or PermissionCodes.BomEdit
                    or PermissionCodes.DrawingReviewSubmit or PermissionCodes.DrawingReviewAnnotate or PermissionCodes.DrawingReviewDecide or PermissionCodes.ReleaseManage or PermissionCodes.ApprovalDecide))
            normalized.Add(PermissionCodes.ProjectView);
        if (normalized.Any(code => code is PermissionCodes.DocumentEdit or PermissionCodes.DocumentLockRequestRelease or PermissionCodes.DocumentLockForceRelease or PermissionCodes.BomEdit
                or PermissionCodes.DrawingReviewSubmit or PermissionCodes.DrawingReviewAnnotate or PermissionCodes.DrawingReviewDecide or PermissionCodes.ReleaseManage or PermissionCodes.ApprovalDecide))
            normalized.Add(PermissionCodes.ProjectContentView);
        if (normalized.Any(code => code is PermissionCodes.ProgramTemplateSubmit or PermissionCodes.ProgramTemplateReview or PermissionCodes.ProgramTemplateApprove or PermissionCodes.ProgramTemplateManage))
            normalized.Add(PermissionCodes.ProgramTemplateView);
        if (normalized.Contains(PermissionCodes.StandardLibraryManage)) normalized.Add(PermissionCodes.StandardLibraryView);
        if (normalized.Contains(PermissionCodes.MaterialManage)) normalized.Add(PermissionCodes.MaterialView);
        if (normalized.Contains(PermissionCodes.RoleSettingsEdit)) normalized.Add(PermissionCodes.RoleSettingsView);
        return normalized;
    }

    private static IReadOnlySet<string> Set(params string[] codes) => codes.ToHashSet(StringComparer.Ordinal);
}
