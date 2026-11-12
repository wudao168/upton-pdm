namespace Upton.Pdm.Domain;

// 授权数据是唯一来源；角色名称只用于匹配成员，不包含任何隐含业务特权。
public sealed record AuthorizationGrant(string Permission, IReadOnlyList<string> Scopes);
public sealed record AuthorizationTemplate(string Key, string Name, string Description,
    IReadOnlyList<AuthorizationGrant> Grants, string? RoleCode = null, bool IsActive = true, bool ProjectAssignable = false);
public sealed record AuthorizationAssignment(Guid Id, string Username, string TemplateKey,
    Guid CompanyId, string Scope, Guid? TargetId = null);
public sealed record AuthorizationSettings(long Revision, IReadOnlyList<AuthorizationTemplate> Templates,
    IReadOnlyList<AuthorizationAssignment> Assignments, IReadOnlyList<ProjectPositionDefinition> Positions,
    DateTimeOffset MigratedAt, bool RolesUnified = false)
{
    public bool DingTalkSelfBindingDefaultsApplied { get; init; }
    public bool AiWebDefaultsApplied { get; init; }
}

public static class AuthorizationActions
{
    public const string FinancialBudgetRead = "financial.budget.read";
    public const string FinancialActualRead = "financial.actual.read";
    public const string FinancialBudgetMaintain = "financial.budget.maintain";
    public const string FinancialActualMaintain = "financial.actual.maintain";
    public const string FinancialDepartmentRead = "financial.department.read";
    public const string LaborRatesManage = "financial.labor-rates.manage";
    public const string StockSynchronize = "stock.synchronize";
    public const string CompanyReporting = "reporting.company.view";
    public const string SelfReview = "drawing-review.self-review";
    public const string ReviewManage = "drawing-review.manage";
    public const string BusinessNotifications = "notifications.business.receive";
    public const string ClientLogs = "system.client-logs.view";
    public const string PlatformManage = "system.platform.manage";
    public const string TechnicalCandidate = "project.staffing.candidate";
    public const string ArchiveSubmit = "document.archive.submit";
    public const string FolderView = "folder.view";
    public const string FolderDownload = "folder.download";
    public const string FolderUpload = "folder.upload";
    public const string FolderEdit = "folder.edit";
    public const string FolderManage = "folder.manage";

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } = new[]
    {
        new PermissionDefinition("project.templates.assign", "分配已批准的项目岗位模板", "项目分工", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.record.manage", "维护项目记录", "项目管理", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.opl.manage", "管理项目全部OPL", "项目OPL", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.risk.manage", "维护项目风险", "项目管理", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.risk.close-others", "关闭他人风险", "项目管理", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.nameplate.execution.edit", "维护铭牌机械信息", "项目铭牌", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.nameplate.hardware.edit", "维护和确认铭牌电气信息", "项目铭牌", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("drawing-review.task.override", "接管未分配给本人的审图任务", "图纸审核", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("material.application.manage-others", "管理他人的料号申请", "料品管理", Sensitive: true) { Scope = "system" },
        new PermissionDefinition("work-hours.manage.company", "管理公司工时", "工时管理", Sensitive: true) { Scope = "system" },
        new PermissionDefinition("work-hours.manage.department", "管理本部门工时", "工时管理", Sensitive: true) { Scope = "system" },
        new PermissionDefinition("work-hours.roster.manage", "管理所属事业部工时人员", "工时管理", Sensitive: true) { Scope = "system" },
        new PermissionDefinition("workbench.all", "查看全部工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.division", "使用事业部工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.manager", "使用项目经理工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.engineer", "使用工程师工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.procurement", "使用采购工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.production", "使用生产工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.sales", "使用销售工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("workbench.view.quality", "使用质量工作台视角", "项目工作台", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.staffing.replace-self", "变更本人的项目经理分配", "项目分工", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("project.phase-owners.manage", "配置项目阶段负责人", "项目分工", Sensitive: true) { Scope = "project" },
        new PermissionDefinition("release.mechanical", "发布标准件／非标件", "审批发布", Sensitive: true) { Scope = "project" },
        new PermissionDefinition(FinancialBudgetRead, "查看项目预算金额", "财务数据范围"),
        new PermissionDefinition(FinancialActualRead, "查看项目实际成本", "财务数据范围"),
        new PermissionDefinition(FinancialBudgetMaintain, "修改预算金额（汇总和评估共用）", "财务数据范围", "勾选预算汇总或预算评估的编辑权限时，系统会补选此项。", Sensitive: true) { Action = "edit" },
        new PermissionDefinition(FinancialActualMaintain, "维护项目实际成本", "财务数据范围", Sensitive: true),
        new PermissionDefinition(FinancialDepartmentRead, "查看所属部门财务明细", "财务数据范围"),
        new PermissionDefinition(LaborRatesManage, "维护工时单价", "财务数据范围", Sensitive: true),
        new PermissionDefinition(StockSynchronize, "立即同步常备物料", "常备物料", Sensitive: true) { Dependencies = [PermissionCodes.StockView] },
        new PermissionDefinition(CompanyReporting, "查看公司项目报表", "项目报表"),
        new PermissionDefinition(SelfReview, "审核本人图纸", "图纸审核", "独立授权；未授权时禁止审核本人图纸。", true),
        new PermissionDefinition(ReviewManage, "管理他人图纸审核任务", "图纸审核", Sensitive: true),
        new PermissionDefinition(BusinessNotifications, "接收业务消息和待办", "消息中心"),
        new PermissionDefinition(ClientLogs, "查看客户端诊断日志", "系统设置", Sensitive: true),
        new PermissionDefinition("system.login-policy.manage", "维护登录安全策略", "系统设置", Sensitive: true),
        new PermissionDefinition(PlatformManage, "管理跨公司平台", "系统设置", Sensitive: true),
        new PermissionDefinition(TechnicalCandidate, "可被分配为设计人员", "项目分工"),
        new PermissionDefinition(ArchiveSubmit, "提交项目图档存档", "项目图档", Sensitive: true),
        new PermissionDefinition(FolderView, "目录默认查看", "目录默认权限"),
        new PermissionDefinition(FolderDownload, "目录默认下载", "目录默认权限"),
        new PermissionDefinition(FolderUpload, "目录默认上传", "目录默认权限", Sensitive: true),
        new PermissionDefinition(FolderEdit, "目录默认编辑", "目录默认权限", Sensitive: true),
        new PermissionDefinition(FolderManage, "管理目录权限", "目录默认权限", Sensitive: true)
    }.Select(p => p with { Scope = p.Scope == "project" || p.Code.StartsWith("financial.") || p.Code.StartsWith("folder.") || p.Code.StartsWith("drawing-review.") || p.Code == ArchiveSubmit ? "project" : "system",
        Action = p.Action == "other" ? FunctionPermissionCatalog.Action(p.Code, p.Name) : p.Action,
        ProjectAuthorization = "role" }).Select(p => p.Scope == "project" ? p with { Dependencies = [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView] } : p).ToArray();

    public static IReadOnlyDictionary<string, string> Scopes { get; } = new Dictionary<string, string>
    {
        ["company"] = "授权公司／项目范围内全部", ["MainManager"] = "本人担任主项目经理（含子项目）",
        ["ChildManager"] = "本人担任子项目负责人", ["MainDesigner"] = "本人担任主设计（含子项目）",
        ["Engineer"] = "本人被分配为设计人员／电气硬件", ["Other"] = "未担任项目岗位的人员（历史规则）",
        ["primary-project-manager"] = "当前项目主项目经理", ["root-primary-manager"] = "主项目主项目经理（含子项目）",
        ["project-manager"] = "当前项目经理或协同经理", ["project-designer"] = "当前项目主设计或设计人员",
        ["current-designer"] = "当前项目设计人员", ["root-design-lead"] = "仅主项目主设计", ["current-hardware"] = "当前项目电气硬件负责人", ["project-lead"] = "当前项目主设计",
        ["division-manager"] = "本人管理的执行事业部", ["division-member"] = "本人所属执行事业部",
        ["sales-contact"] = "本人负责销售的项目", ["presales-contact"] = "本人负责售前的项目",
        ["commercial-folder"] = "售前／客户资料目录", ["release-folder"] = "发布资料目录"
    };
}
