using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public sealed record ReportDrawingVersion(Guid ProjectId, Guid DocumentId, string Name, string Revision, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseChangeReasonSelection> Reasons);
public sealed record ReportDrawing(Guid Id, string Name, DateTimeOffset FirstPublishedAt, IReadOnlyList<ReportDrawingChange> Changes);
public sealed record ReportDrawingChange(DateTimeOffset At, string Revision, IReadOnlyList<string> Reasons);
public sealed record ReportFinance(decimal? Settlement, decimal? Budget, decimal? Actual, decimal? Estimated, IReadOnlyList<ReportExpense> Expenses);
public sealed record ReportExpense(string Category, decimal? Budget, decimal? Actual, decimal? Estimated);
public sealed record ReportProjectDetail(Guid ProjectId, ReportFinance? Finance, DateTimeOffset? StageEnteredAt, IReadOnlyList<ReportDrawing>? Drawings, IReadOnlyList<string> Warnings);
public sealed record ProjectReportingView(ProjectWorkbench Workbench, bool CanViewCompany, string ScopeLabel, IReadOnlyList<ReportProjectDetail> Details);

/// <summary>经营报表仅组合已授权数据，不改变工作台和财务业务权限。</summary>
public sealed class ProjectReportingService(IPdmRepository repository, ProjectWorkbenchService workbench,
    ProjectBudgetService budgets, IProjectPlanningRepository planning)
{
    public static bool CompanyAccess(UserAccount user, UserRole role) => TenantContext.Current?.HasPermission(AuthorizationActions.CompanyReporting) == true;

    public static HashSet<Guid> AllowedUnits(OrganizationDirectory directory, string actor, Guid companyId)
    {
        var units = directory.Units.Where(u => u.IsActive && u.OrganizationId == companyId).ToDictionary(u => u.Id);
        var roots = new HashSet<Guid>();
        var owned = directory.Memberships.Where(m => WorkbenchScope.Same(m.Username, actor)).Select(m => m.UnitId)
            .Concat(directory.Managers.Where(m => WorkbenchScope.Same(m.PrimaryManager, actor) || m.CollaborativeManagers.Any(n => WorkbenchScope.Same(n, actor))).Select(m => m.UnitId));
        foreach (var id in owned)
        {
            Guid? current = id; var seen = new HashSet<Guid>();
            while (current is Guid next && seen.Add(next) && units.TryGetValue(next, out var unit))
            {
                if (unit.Kind == OrganizationUnitKind.BusinessDivision) { roots.Add(next); break; }
                current = unit.ParentUnitId;
            }
        }
        bool changed;
        do { changed = false; foreach (var unit in units.Values) if (unit.ParentUnitId is Guid parent && roots.Contains(parent)) changed |= roots.Add(unit.Id); } while (changed);
        return roots;
    }

    public async Task<ProjectReportingView> GetAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectView, ct))
            throw new UnauthorizedAccessException("当前账号无权查看项目报表。");
        var user = await repository.FindUserAsync(actor, ct) ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
        var company = TenantContext.CompanyId ?? user.CompanyId;
        if (company is null) throw new UnauthorizedAccessException("请先选择所属公司。");
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var companyAccess = await repository.HasUserPermissionAsync(actor, role, AuthorizationActions.CompanyReporting, ct);
        var allowed = AllowedUnits(directory, actor, company.Value);
        var source = await workbench.GetAsync(null, actor, role, ct, scope: "company", summaryOnly: true);
        var roots = source.Projects.Where(p => p.Project.ParentProjectId is null && p.Project.OrganizationId == company
            && (companyAccess || p.Project.ExecutionUnitId is Guid unit && allowed.Contains(unit))).ToArray();
        var rootIds = roots.Select(p => p.Project.Id).ToHashSet();
        var items = source.Projects.Where(p => p.Project.OrganizationId == company && (rootIds.Contains(p.Project.Id)
            || rootIds.Contains(p.Project.RootProjectId ?? p.Project.ParentProjectId ?? Guid.Empty))).ToArray();
        var ids = items.Select(p => p.Project.Id).ToHashSet();
        IReadOnlyList<ReportDrawingVersion>? drawingHistory = null;
        try { drawingHistory = await repository.ListReportDrawingVersionsAsync(ids, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"报表图纸元数据读取失败: {ex}"); }
        var details = new List<ReportProjectDetail>();
        foreach (var root in roots)
        {
            var warnings = new List<string>(); ReportFinance? finance = null; DateTimeOffset? entered = null;
            try
            {
                var value = await budgets.GetAsync(root.Project.Id, actor, role, ct);
                // 部门局部成本不能伪装为整个项目的成本。
                if (!value.IsDepartmentScoped)
                    finance = new(value.SettlementAmount, value.BudgetAmount, value.ActualAmount, value.EstimatedAmount,
                        value.Rows.Select(r => new ReportExpense(r.Input.Category, r.BudgetAmount, r.ActualAmount, r.EstimatedAmount)).ToArray());
                else warnings.Add("仅有部门费用权限，未计入项目预算汇总");
            }
            catch (UnauthorizedAccessException) { warnings.Add("金额或预算无查看权限"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"报表预算读取失败 {root.Project.Id}: {ex}"); warnings.Add("金额或预算读取失败，请刷新重试"); }
            if (root.Plan is { ApprovalStatus: ProjectPlanApprovalStatus.Approved } plan)
            {
                try
                {
                    var history = (await planning.ListVersionsAsync(plan.Id, ct)).Where(v => v.Snapshot.ApprovalStatus == ProjectPlanApprovalStatus.Approved).OrderByDescending(v => v.CreatedAt).ToArray();
                    foreach (var version in history) { if (version.Snapshot.CurrentStage != plan.CurrentStage) break; entered = version.CreatedAt; }
                    // 无历史不使用更新时间冒充进入阶段的时间。
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"报表阶段历史读取失败: {ex}"); warnings.Add("阶段历史读取失败，请刷新重试"); }
            }
            List<ReportDrawing>? drawings = drawingHistory is null ? null : [];
            if (drawingHistory is null) warnings.Add("正式图纸记录读取失败，请刷新重试");
            else
            {
                var familyIds = items.Where(p => p.Project.Id == root.Project.Id || (p.Project.RootProjectId ?? p.Project.ParentProjectId) == root.Project.Id).Select(p => p.Project.Id).ToHashSet();
                foreach (var group in drawingHistory.Where(v => familyIds.Contains(v.ProjectId)).GroupBy(v => v.DocumentId))
                {
                    var formal = group.OrderBy(v => v.PublishedAt).DistinctBy(v => v.Revision).ToArray();
                    drawings!.Add(new(group.Key, formal[0].Name, formal[0].PublishedAt,
                        formal.Skip(1).Select(v => new ReportDrawingChange(v.PublishedAt, v.Revision,
                            v.Reasons.Select(r => string.IsNullOrWhiteSpace(r.Reason) ? "未填写" : r.Reason.Trim()).Distinct().DefaultIfEmpty("未填写").ToArray())).ToArray()));
                }
            }
            details.Add(new(root.Project.Id, finance, entered, drawings, warnings));
        }
        return new(source with { Projects = items, Risks = source.Risks.Where(r => ids.Contains(r.ProjectId)).ToArray(), Scopes = companyAccess ? ["company", "division"] : ["division"] },
            companyAccess, companyAccess ? "公司授权项目" : "所属事业部授权项目", details);
    }
}
