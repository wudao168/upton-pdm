using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>仅首次迁移使用旧角色规则，运行时不调用此处决定权限。</summary>
public static class AuthorizationMigration
{
    public static AuthorizationSettings EnableDingTalkSelfBindingDefaults(AuthorizationSettings settings)
    {
        if (settings.DingTalkSelfBindingDefaultsApplied) return settings;
        var defaultGrant = AuthorizationPolicy.NewRoleDefaultGrants.Single(g => g.Permission == PermissionCodes.DingTalkSelfBind);
        return settings with
        {
            DingTalkSelfBindingDefaultsApplied = true,
            Templates = settings.Templates.Select(role => role.Grants.Any(g => g.Permission == defaultGrant.Permission && g.Scopes.Contains("company"))
                ? role
                : role with { Grants = role.Grants.Where(g => g.Permission != defaultGrant.Permission).Append(defaultGrant).ToArray() }).ToArray()
        };
    }

    public static AuthorizationSettings PreserveLegacyPlatformUser(AuthorizationSettings settings, IReadOnlyList<UserAccount> users)
    {
        var legacy = users.FirstOrDefault(u => u.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
        if (legacy?.CompanyId is not Guid company || legacy.HasRole("platform_admin") || legacy.HasRole("developer")) return settings;
        var template = new AuthorizationTemplate("legacy-platform-user", "历史平台维护授权", "迁移原有admin账号的平台权限，可在此调整或撤销。", [new(AuthorizationActions.PlatformManage, ["company"]), new("system.login-policy.manage", ["company"])]);
        return settings with { Templates = settings.Templates.Append(template).ToArray(), Assignments = settings.Assignments.Append(new AuthorizationAssignment(Guid.NewGuid(), legacy.Username, template.Key, company, "company")).ToArray() };
    }
    public static AuthorizationSettings Create(RolePermissionDirectory directory, ProjectPermissionSettings positions)
    {
        positions = positions.Normalize();
        var added = AuthorizationActions.Definitions.Select(p => p.Code).ToHashSet();
        var templates = new List<AuthorizationTemplate>();
        foreach (var role in directory.Roles)
        {
            var grants = new Dictionary<string, HashSet<string>>();
            // 仓储目录已解析旧权限；显示用目录不保留格式标记，且可能包含兼容别名。
            // 再次展开会把已关闭的页签和粒度操作恢复为授权，迁移必须保留解析结果。
            var codes = role.Permissions.ToHashSet(StringComparer.Ordinal);
            var admin = role.BaseRole == UserRole.Administrator;
            var platform = role.Role is "platform_admin" or "developer";
            void Add(string permission, params string[] scopes)
            {
                if (!grants.TryGetValue(permission, out var current)) grants[permission] = current = [];
                current.UnionWith(scopes);
            }
            if (role.Role != "Administrator") Add(AuthorizationActions.BusinessNotifications, "company");
            var commercial = role.Role is "SalesManager" or "PresalesEngineer";
            var companyScope = role.Role == "SalesManager" ? "sales-contact" : role.Role == "PresalesEngineer" ? "presales-contact" : "company";
            foreach (var permission in RolePermissionCatalog.Permissions.Where(p => !p.Hidden && !added.Contains(p.Code)))
            {
                if (!codes.Contains(permission.Code) && !(permission.Code == PermissionCodes.ProjectBudgetView && codes.Contains(PermissionCodes.ProjectContentView))) continue;
                if (admin || permission.Scope != "project" || permission.ProjectAuthorization == "role"
                    || permission.Code == PermissionCodes.ProjectBudgetView && codes.Contains(permission.Code)) Add(permission.Code, permission.Scope == "project" ? companyScope : "company");
                else
                    foreach (var position in positions.Positions.Where(p => p.IsActive))
                        if (positions.Rules.GetValueOrDefault(position.Key, []).Contains(permission.Code)) Add(permission.Code, position.Key);
            }
            // 旧代码中的额外限制转换为模板范围，不再在业务服务重复检查角色。
            if (!admin && codes.Contains(PermissionCodes.ProjectStaffingManage))
                grants[PermissionCodes.ProjectStaffingManage] = role.Role == "PlanningManager" ? ["company"] : ["primary-project-manager", "division-manager"];
            if (!admin && codes.Contains(PermissionCodes.ProjectDesignerAssign))
            {
                Add(PermissionCodes.ProjectDesignerAssign, "project-manager", "MainDesigner", "division-manager");
                if (role.Role == "MechanicalManager") Add(PermissionCodes.ProjectDesignerAssign, "division-member");
            }
            if (!admin)
            {
                foreach (var operation in new[] { PermissionCodes.DocumentLockForceRelease, PermissionCodes.DocumentLockEmergencyRelease })
                    if (codes.Contains(operation)) grants[operation] = ["project-manager", "project-lead"];
                grants.Remove(PermissionCodes.DocumentRecycle);
                grants.Remove(PermissionCodes.ProjectContentReset);
            }
            if (codes.Contains(PermissionCodes.ProjectContentView))
            {
                Add(AuthorizationActions.FinancialBudgetRead, "MainManager", "ChildManager", "MainDesigner", "division-manager");
                Add(AuthorizationActions.FinancialActualRead, "MainManager", "ChildManager", "MainDesigner", "division-manager");
                Add(AuthorizationActions.FinancialBudgetMaintain, "MainDesigner", "division-manager");
                Add(AuthorizationActions.FinancialActualMaintain, "project-manager", "division-manager");
                if (role.Role == "CompanyExecutive")
                { Add(AuthorizationActions.FinancialActualRead, "company"); Add(AuthorizationActions.FinancialActualMaintain, "company"); }
                if (role.Role is "SupplyChain" or "ProcurementSpecialist" or "ProcurementManager") Add(AuthorizationActions.FinancialDepartmentRead, "company");
                Add(AuthorizationActions.FolderView, "company"); Add(AuthorizationActions.FolderDownload, "company");
                if (admin || role.BaseRole == UserRole.Engineer)
                { Add(AuthorizationActions.FolderUpload, "company"); Add(AuthorizationActions.FolderEdit, "company"); }
                else if (commercial) Add(AuthorizationActions.FolderUpload, "commercial-folder");
                if (admin) Add(AuthorizationActions.FolderManage, "company");
                if (codes.Contains(PermissionCodes.DocumentEdit) || codes.Contains("document.archive"))
                    Add(AuthorizationActions.ArchiveSubmit, admin ? ["company"] : ["project-manager", "root-design-lead", "current-designer"]);
            }
            if (role.Role is "PlanningManager" or "Administrator") Add(AuthorizationActions.LaborRatesManage, "company");
            if (role.Role == "StandardizationAssistant" || codes.Contains(PermissionCodes.StorageSettingsManage)) Add(AuthorizationActions.StockSynchronize, "company");
            if (admin || role.Role is "CompanyExecutive" or "PlanningManager") Add(AuthorizationActions.CompanyReporting, "company");
            if (role.Role == "developer") Add(AuthorizationActions.SelfReview, "company");
            if (admin || role.Role == "PlanningManager") Add("project.staffing.replace-self", "company");
            if (codes.Contains(PermissionCodes.ProjectContentView)) Add("project.phase-owners.manage", admin ? ["company"] : ["root-primary-manager", "division-manager"]);
            if (admin || role.Role == "Engineer") Add("release.mechanical", "company");
            if (codes.Contains(PermissionCodes.DrawingReviewSubmit)) Add(AuthorizationActions.ReviewManage, admin ? ["company"] : ["project-manager"]);
            if (admin || platform) Add(AuthorizationActions.ClientLogs, "company");
            if (platform) { Add(AuthorizationActions.PlatformManage, "company"); Add("system.login-policy.manage", "company"); }
            if (role.Role is "Engineer" or "ElectricalEngineer" or "CommissioningEngineer" or "HardwareEngineer" or "MechanicalManager" or "ProcessReviewer" or "Approver" or "TechnicalAssistant" or "StandardizationAssistant") Add(AuthorizationActions.TechnicalCandidate, "company");
            if (codes.Contains(PermissionCodes.ProjectContentView))
            {
                Add("workbench.view.manager", "MainManager", "ChildManager");
                Add("workbench.view.engineer", "MainDesigner", "Engineer");
                Add("workbench.view.division", "division-manager");
                Add("project.record.manage", admin ? ["company"] : ["MainManager", "ChildManager"]);
                Add("project.templates.assign", admin ? ["company"] : ["MainManager", "ChildManager", "division-manager"]);
                Add("project.opl.manage", admin ? ["company"] : ["MainManager"]);
                Add("project.risk.manage", admin ? ["company"] : ["MainManager", "ChildManager", "MainDesigner", "Engineer", "division-manager", "sales-contact", "presales-contact"]);
                Add("project.risk.close-others", admin ? ["company"] : ["MainManager", "division-manager"]);
                if (codes.Contains(PermissionCodes.ProjectNameplateEdit))
                {
                    Add("project.nameplate.execution.edit", admin ? ["company"] : ["current-designer"]);
                    Add("project.nameplate.hardware.edit", admin ? ["company"] : ["current-hardware"]);
                }
            }
            if (admin) { Add("material.application.manage-others", "company"); Add("workbench.all", "company"); }
            if (admin || platform) Add("work-hours.manage.company", "company");
            if (role.Role is "TechnicalAssistant" or "BusinessUnitManager") Add("work-hours.manage.department", "company");
            if (role.Role is "TechnicalAssistant" or "ProductionAssistant" or "SalesAssistant") Add("work-hours.roster.manage", "company");
            if (role.Role == "developer") Add("drawing-review.task.override", "company");
            var views = new Dictionary<string, string[]>
            {
                ["division"] = ["BusinessUnitManager"], ["manager"] = ["ProjectManager"],
                ["engineer"] = ["Engineer", "HardwareEngineer", "ElectricalEngineer", "CommissioningEngineer", "MechanicalManager", "TechnicalAssistant", "PresalesEngineer"],
                ["procurement"] = ["SupplyChain", "ProcurementManager", "ProcurementSpecialist"],
                ["production"] = ["ProductionManager", "ProductionAssistant", "ProductionViewer", "MachiningSupervisor", "AssemblySupervisor", "AssemblyFitter", "AssemblyElectrician", "ElectricalSupervisor"],
                ["sales"] = ["SalesManager", "SalesAssistant"], ["quality"] = ["QualityManager", "QualityInspector"]
            };
            foreach (var view in views) if (admin || view.Value.Contains(role.Role)) Add("workbench.view." + view.Key, "company");
            templates.Add(new(role.Role, role.Name, role.Description, grants.Where(g => g.Value.Count > 0).Select(g => new AuthorizationGrant(g.Key, g.Value.Order().ToArray())).ToArray(), role.Role));
        }
        var readCodes = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "project.tab.overview.view", "project.tab.project-plan.view", "project.tab.documents.view", "project.tab.bom.view", "project.tab.release.view", "project.plan.view", "document.view", "bom.mechanical.row.view", "bom.electrical.row.view", "release.view", AuthorizationActions.FolderView };
        templates.Add(new("project-reader", "项目查阅", "仅查看指定项目的计划、图档、BOM和发布内容，不含下载和财务数据。", readCodes.Select(c => new AuthorizationGrant(c, ["company"])).ToArray(), ProjectAssignable: true));
        return new(1, templates, [], positions.Positions, DateTimeOffset.UtcNow);
    }
}
