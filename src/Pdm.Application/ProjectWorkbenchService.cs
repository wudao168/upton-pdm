using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record WorkbenchBudget(decimal? Budget, decimal? Actual, decimal? Ordered, decimal? Estimated, decimal? Variance);
public sealed record WorkbenchProcurement(bool Published, int PendingOrder, int Missing, int Delayed, DateTimeOffset? RefreshedAt, string? SyncError);
public sealed record WorkbenchQuality(string State, int Total, int Pending, int Failed, int Records, int Unconfirmed);
public sealed record WorkbenchProject(Project Project, ProjectPlan? Plan, string PlanState, WorkbenchBudget? Budget,
    WorkbenchProcurement? Procurement, WorkbenchQuality? Quality, IReadOnlyList<string> Warnings, bool CanRegisterRisk, int? CompletionPercent, bool CanMaintainRecord = false, int? OplOpenCount = null);
public sealed record ProjectRiskIdentity(Guid Id, string Code, string? BusinessStatus, DateOnly? DeliveryDate);
public sealed record ProjectRiskProcurement(int Delayed, IReadOnlyList<ProjectRiskMaterial> Items);
public sealed record ProjectRiskMaterial(int Sequence, string MaterialCode, string MaterialName, string? Specification,
    decimal? Quantity, DateOnly? RequiredOn, DateOnly? ExpectedOn, IReadOnlyList<string> PurchaseOrders, string? ReleasePackageNumber);
public sealed record ProjectRiskRelease(string State, string Scope, bool LocksDocuments, string PreviewState,
    Guid Id, string Number, DateTimeOffset CreatedAt, string? Failure);
public sealed record ProjectRiskTask(Guid Id, string Name, string Stage, DateOnly PlannedStart, DateOnly PlannedFinish, DateOnly? ActualFinish,
    string Status, IReadOnlyList<Guid> PredecessorTaskIds);
public sealed record ProjectRiskStage(string Code, bool? ParticipatesInDelivery);
public sealed record ProjectRiskPlan(string ApprovalStatus, DateOnly PlannedFinish, IReadOnlyList<ProjectRiskTask> Tasks, IReadOnlyList<ProjectRiskStage> Stages);
public sealed record ProjectRiskOverviewProject(ProjectRiskIdentity Project, ProjectRiskPlan? Plan, ProjectRiskProcurement? Procurement,
    IReadOnlyList<ProjectRiskRelease> Releases, bool Incomplete);

public sealed record WorkbenchView(string Key, string Label);
public sealed record ProjectWorkbench(IReadOnlyList<WorkbenchView> Views, string View, DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkbenchProject> Projects, IReadOnlyList<ProjectWorkbenchRisk> Risks, IReadOnlyList<string>? Scopes = null);
public sealed record ProjectWorkbenchRisk(Guid Id, Guid ProjectId, string Title, string Impact, string Owner, DateOnly DueDate,
    string Severity, string CreatedBy, DateTimeOffset CreatedAt, bool Closed = false, string? ClosedBy = null, DateTimeOffset? ClosedAt = null, bool CanClose = false);
public sealed record RegisterWorkbenchRisk(string Title, string Impact, string Owner, DateOnly DueDate, string Severity);
public interface IProjectWorkbenchRiskRepository
{
    Task<IReadOnlyList<ProjectWorkbenchRisk>> ListAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct);
    Task<ProjectWorkbenchRisk?> FindAsync(Guid id, CancellationToken ct);
    Task SaveAsync(ProjectWorkbenchRisk risk, CancellationToken ct);
}

public static class WorkbenchScope
{
    public static readonly WorkbenchView[] Views = [new("division", "事业部经理"), new("manager", "项目经理"), new("engineer", "工程师"),
        new("procurement", "采购"), new("production", "生产"), new("sales", "销售"), new("quality", "质量")];
    public static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static bool Contains(string? users, string actor) => (users ?? "").Split(['、', ',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(user => Same(user, actor));
    public static bool Manager(Project project, string actor) => Same(project.PrimaryProjectManager, actor) || project.CollaborativeProjectManagers.Any(user => Same(user, actor));
    public static bool Engineer(Project project, string actor) => Same(project.DesignLead, actor) || project.DesignLeads.Concat(project.Designers).Any(user => Same(user, actor));
    public static HashSet<Guid> ManagedUnits(OrganizationDirectory directory, string actor)
    {
        var ids = directory.Managers.Where(item => Same(item.PrimaryManager, actor) || item.CollaborativeManagers.Any(user => Same(user, actor))).Select(item => item.UnitId).ToHashSet();
        bool changed;
        do { changed = false; foreach (var unit in directory.Units) if (unit.ParentUnitId is Guid parent && ids.Contains(parent)) changed |= ids.Add(unit.Id); } while (changed);
        return ids;
    }
    public static IReadOnlyList<WorkbenchView> Available(IReadOnlyList<string> roles, bool administrator, bool managesUnits, bool managesProjects, bool assignedTasks)
    {
        bool Has(params string[] codes) => roles.Any(role => codes.Contains(role, StringComparer.OrdinalIgnoreCase));
        return Views.Where(view => administrator || view.Key switch
        {
            "division" => managesUnits || Has("BusinessUnitManager"),
            "manager" => managesProjects || Has("ProjectManager"),
            "engineer" => assignedTasks || Has("Engineer", "HardwareEngineer", "ElectricalEngineer", "CommissioningEngineer", "MechanicalManager", "TechnicalAssistant", "PresalesEngineer"),
            "procurement" => Has("SupplyChain", "ProcurementManager", "ProcurementSpecialist"),
            "production" => Has("ProductionManager", "ProductionAssistant", "ProductionViewer", "MachiningSupervisor", "AssemblySupervisor", "AssemblyFitter", "AssemblyElectrician", "ElectricalSupervisor"),
            "sales" => Has("SalesManager", "SalesAssistant"),
            "quality" => Has("QualityManager", "QualityInspector"),
            _ => false
        }).ToArray();
    }
    public static bool Matches(string view, Project root, IReadOnlyList<Project> family, string actor, bool administrator,
        IReadOnlySet<Guid> managedUnits, IReadOnlyDictionary<Guid, ProjectPlan> plans, ProjectContactInformation? contacts)
    {
        if (administrator) return true;
        return view switch
        {
            "division" => root.ExecutionUnitId is Guid unit && managedUnits.Contains(unit),
            "manager" => family.Any(project => Manager(project, actor)),
            "engineer" => family.Any(project => Engineer(project, actor) || project.PhaseOwners.Values.Any(people => Contains(people, actor)) || plans.TryGetValue(project.Id, out var plan) && (plan.ChangeDraftSource ?? plan).Tasks.Any(task => Contains(task.Assignee, actor))) || Contains(contacts?.PresalesPeople, actor),
            "sales" => Contains(contacts?.SalesPeople, actor),
            "procurement" or "production" or "quality" => true, // Already restricted to authorized projects in the active company.
            _ => false
        };
    }
}

public sealed class ProjectWorkbenchService(IPdmRepository repository, IProjectPlanningRepository planning,
    IProjectContactInformationRepository contacts, ProjectBudgetService budgets, U9ProcurementService procurement,
    IValidationPlanRepository quality, IQualityInspectionRepository inspections, IProjectWorkbenchRiskRepository risks, TimeProvider clock, IProjectOplRepository? opl = null)
{
    public async Task<ProjectWorkbench> GetAsync(string? requestedView, string actor, UserRole role, CancellationToken ct, string? scope = null, bool summaryOnly = false, Guid? projectId = null, bool calendarOnly = false)
    {
        var projects = (await repository.ListProjectsForUserAsync(actor, role, ct)).Where(project => project.IsActive && project.CanReadContent).ToArray();
        if (projectId is Guid requestedId)
        {
            if (!projects.Any(project => project.Id == requestedId)) throw new UnauthorizedAccessException("无权读取该项目详情。");
            projects = projects.Where(project => project.Id == requestedId || project.RootProjectId == requestedId || project.ParentProjectId == requestedId).ToArray();
        }
        // Repository capabilities are not a replacement for the content access check.
        var readable = new List<Project>();
        foreach (var project in projects) if (await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct)) readable.Add(project);
        projects = readable.ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var user = await repository.FindUserAsync(actor, ct);
        var roles = user?.EffectiveRoleCodes ?? TenantContext.Current?.EffectiveRoleCodes ?? [role.ToString()];
        var administrator = await repository.HasUserPermissionAsync(actor, role, "workbench.all", ct);
        var managedUnits = WorkbenchScope.ManagedUnits(directory, actor);
        var plans = (projects.Length == 0 ? [] : await planning.ListPlansAsync(projects.Select(project => project.Id).ToArray(), ct))
            .Where(plan => !plan.IsDeleted).ToDictionary(plan => plan.ProjectId);
        var propagatedTasks = calendarOnly ? plans.Values.Select(plan => plan.ChangeDraftSource ?? plan)
            .Where(plan => plan.ApprovalStatus == ProjectPlanApprovalStatus.Approved).SelectMany(plan => plan.Tasks)
            .Where(task => task.SourceTaskId.HasValue).Select(task => task.SourceTaskId!.Value).ToHashSet() : [];
        var viewPermissions = await repository.GetUserPermissionsAsync(actor, role, ct);
        var views = WorkbenchScope.Views.Where(v => administrator || viewPermissions.Contains("workbench.view." + v.Key)).ToArray();
        var selected = string.IsNullOrWhiteSpace(requestedView) ? views.FirstOrDefault()?.Key ?? "" : requestedView;
        if (selected.Length > 0 && !views.Any(view => view.Key == selected)) throw new UnauthorizedAccessException("无权使用该岗位视角。");
        var scopes = new List<string> { "personal", "company" };
        if (administrator || managedUnits.Count > 0) scopes.Add("division");
        if (views.Any(view => view.Key == "sales")) scopes.Add("sales");
        if (!string.IsNullOrWhiteSpace(scope) && !scopes.Contains(scope)) throw new UnauthorizedAccessException("无权使用该项目范围。");
        var result = new List<WorkbenchProject>();
        foreach (var root in projects.Where(project => project.ParentProjectId is null || !projects.Any(parent => parent.Id == (project.RootProjectId ?? project.ParentProjectId))))
        {
            var family = projects.Where(project => project.Id == root.Id || project.RootProjectId == root.Id || project.ParentProjectId == root.Id).ToArray();
            var commercial = scope is "personal" or "sales" || selected is "sales" or "engineer" ? await contacts.FindAsync(root.Id, ct) : null;
            var included = scope switch
            {
                "company" => true, // projects already passed repository and content-read authorization.
                "division" => administrator || root.ExecutionUnitId is Guid unitId && managedUnits.Contains(unitId),
                "personal" => family.Any(project => WorkbenchScope.Manager(project, actor) || WorkbenchScope.Engineer(project, actor)
                    || project.PhaseOwners.Values.Any(people => WorkbenchScope.Contains(people, actor))
                    || plans.TryGetValue(project.Id, out var personalPlan) && (personalPlan.ChangeDraftSource ?? personalPlan).Tasks.Any(task => WorkbenchScope.Contains(task.Assignee, actor)))
                    || WorkbenchScope.Contains(commercial?.SalesPeople, actor) || WorkbenchScope.Contains(commercial?.PresalesPeople, actor),
                "sales" => WorkbenchScope.Contains(commercial?.SalesPeople, actor),
                _ => WorkbenchScope.Matches(selected, root, family, actor, administrator, managedUnits, plans, commercial)
            };
            if (!included) continue;
            foreach (var project in family)
            {
                plans.TryGetValue(project.Id, out var plan);
                var effective = plan?.ChangeDraftSource ?? plan;
                var needsDeliveryDetails = !calendarOnly || NeedsCalendarDeliveryDetails(effective, propagatedTasks);
                var warnings = new List<string>();
                WorkbenchBudget? budget = null;
                WorkbenchProcurement? purchasing = null;
                WorkbenchQuality? acceptance = null;
                if (!summaryOnly && !calendarOnly && projectId is null && (selected is "division" or "manager"))
                {
                    try
                    {
                        var value = await budgets.GetAsync(project.Id, actor, role, ct);
                        budget = new(value.BudgetAmount, value.ActualAmount, value.Orders.Count > 0 && value.Orders.All(order => order.Amount.HasValue) ? value.Orders.Sum(order => order.Amount!.Value) : null, value.EstimatedAmount, value.EstimatedAmount - value.BudgetAmount);
                    }
                    catch (UnauthorizedAccessException) { warnings.Add("预算无查看权限"); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("预算暂时无法读取"); }
                }
                if (!summaryOnly && needsDeliveryDetails && (selected is "division" or "manager" or "procurement" or "production"))
                {
                    try
                    {
                        purchasing = await ReadProcurementSummaryAsync(project.Id, actor, role, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("采购跟踪暂时无法读取"); }
                }
                if (!summaryOnly && needsDeliveryDetails && (selected is "division" or "manager" or "production" or "quality"))
                {
                    try
                    {
                        var value = await quality.FindPlanAsync(project.Id, ct);
                        var records = await inspections.ListAsync(project.Id, ct);
                        acceptance = new(value?.State.ToString() ?? "Missing", value?.Items.Count ?? 0,
                            value?.Items.Count(item => string.IsNullOrWhiteSpace(item.Result)) ?? 0,
                            value?.Items.Count(item => IsFailedResult(item.Result)) ?? 0,
                            records.Count(record => record.Kind is "preAcceptance" or "finalAcceptance"),
                            value?.Items.Count(item => !string.IsNullOrWhiteSpace(item.Result) && !IsFailedResult(item.Result) && !IsPassedResult(item.Result)) ?? 0);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("验收情况暂时无法读取"); }
                }
                var canRegister = administrator || WorkbenchScope.Manager(root, actor) || root.ExecutionUnitId is Guid unit && managedUnits.Contains(unit)
                    || WorkbenchScope.Engineer(root, actor) || WorkbenchScope.Engineer(project, actor) || root.PhaseOwners.Values.Concat(project.PhaseOwners.Values).Any(value => WorkbenchScope.Contains(value, actor))
                    || ((plan?.ChangeDraftSource ?? plan)?.Tasks.Any(task => WorkbenchScope.Contains(task.Assignee, actor)) ?? false)
                    || WorkbenchScope.Contains(commercial?.SalesPeople, actor) || WorkbenchScope.Contains(commercial?.PresalesPeople, actor);
                result.Add(new(project, effective, plan?.ApprovalStatus.ToString() ?? "Missing", budget, purchasing, acceptance, warnings, canRegister,
                    effective?.ApprovalStatus == ProjectPlanApprovalStatus.Approved && effective.Tasks.Count > 0 ? ProjectPlanningService.CompletionPercent(effective.Tasks, effective.Stages) : null, administrator || WorkbenchScope.Manager(root, actor) || WorkbenchScope.Manager(project, actor)));
            }
        }
        if (summaryOnly && opl is not null)
        {
            var rootIds = result.Where(item => item.Project.ParentProjectId is null).Select(item => item.Project.Id).ToArray();
            try
            {
                var counts = await opl.CountOpenAsync(rootIds, ct);
                for (var i = 0; i < result.Count; i++)
                    if (rootIds.Contains(result[i].Project.Id)) result[i] = result[i] with { OplOpenCount = counts.GetValueOrDefault(result[i].Project.Id) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"开口问题数量统计失败：{ex}");
                for (var i = 0; i < result.Count; i++) result[i] = result[i] with { Warnings = [.. result[i].Warnings, "开口问题数量暂时无法读取"] };
            }
        }
        var visibleRisks = await risks.ListAsync(result.Select(item => item.Project.Id).ToArray(), ct);
        return new(views, selected, clock.GetUtcNow(), result, visibleRisks.Select(risk =>
        {
            var project = projects.First(item => item.Id == risk.ProjectId);
            var root = projects.FirstOrDefault(item => item.Id == (project.RootProjectId ?? project.ParentProjectId)) ?? project;
            return risk with { CanClose = administrator || WorkbenchScope.Same(risk.Owner, actor) || WorkbenchScope.Same(risk.CreatedBy, actor)
                || WorkbenchScope.Manager(root, actor) || root.ExecutionUnitId is Guid unit && managedUnits.Contains(unit) };
        }).ToArray(), scopes);
    }

    public async Task<IReadOnlyList<ProjectRiskOverviewProject>> GetRiskOverviewAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var visible = (await repository.ListProjectsForUserAsync(actor, role, ct))
            .Where(project => project.IsActive && project.CanReadContent && AllowsTab(project, "overview")).ToArray();
        var selected = visible.FirstOrDefault(project => project.Id == projectId)
            ?? throw new UnauthorizedAccessException("当前账号无权查看该项目风险。");
        var candidates = selected.ParentProjectId is null
            ? visible.Where(project => project.Id == projectId || project.RootProjectId == projectId || project.ParentProjectId == projectId)
                .OrderBy(project => project.Id != projectId).ThenBy(project => project.Code, StringComparer.OrdinalIgnoreCase).ToArray()
            : [selected];
        var readable = new List<Project>();
        foreach (var project in candidates)
            if (await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct)) readable.Add(project);
        if (!readable.Any(project => project.Id == projectId))
            throw new UnauthorizedAccessException("当前账号无权查看该项目风险。");

        var planIds = readable.Where(project => AllowsTab(project, "project-plan")
            && project.EffectiveProjectPermissions.Contains("project.plan.view")).Select(project => project.Id).ToHashSet();
        var plans = (planIds.Count == 0 ? [] : await planning.ListPlansAsync(planIds.ToArray(), ct))
            .Where(plan => !plan.IsDeleted).ToDictionary(plan => plan.ProjectId);
        var result = new List<ProjectRiskOverviewProject>();
        foreach (var project in readable)
        {
            plans.TryGetValue(project.Id, out var plan);
            var incomplete = false;
            ProjectRiskProcurement? purchasing = null;
            IReadOnlyList<ProjectRiskRelease> releases = [];
            if (AllowsTab(project, "procurement"))
            {
                try
                {
                    var value = await procurement.ListProjectAsync(project.Id, actor, role, ct);
                    var delayed = ProcurementDemandRows(value).Where(IsDelayedProcurement)
                        .Select(item => new ProjectRiskMaterial(item.Sequence, item.MaterialCode, item.MaterialName, item.Specification, item.Quantity,
                            item.Manufacturing?.RequiredOn ?? ToDate(item.PurchaseRequisitionDeliveryDate),
                            item.Manufacturing?.ForecastOn ?? ToDate(item.LatestDeliveryDate ?? item.PurchaseDeliveryDate),
                            item.PurchaseOrderNumbers, item.ReleasePackageNumber)).ToArray();
                    purchasing = new(delayed.Length, delayed);
                    incomplete = !string.IsNullOrWhiteSpace(value.LastRefreshError);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"项目采购风险读取失败：{ex}");
                    incomplete = true;
                }
            }
            if (AllowsTab(project, "release") && project.EffectiveProjectPermissions.Contains("release.view") && project.HasReleaseAttention)
            {
                try
                {
                    releases = (await repository.ListReleasePackagesAsync(project.Id, ct))
                        .Where(item => item.State == ReleasePackageState.PublishFailed || item.PreviewState == ReleasePreviewState.Failed)
                        .Select(item => new ProjectRiskRelease(item.State.ToString(), item.Scope.ToString(), item.LocksDocuments, item.PreviewState,
                            item.Id, item.Number, item.CreatedAt, item.State == ReleasePackageState.PublishFailed ? item.PublishError : item.PreviewError)).ToArray();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"项目发布风险读取失败：{ex}");
                    incomplete = true;
                }
            }
            var effective = plan?.ChangeDraftSource ?? plan;
            var schedule = effective?.ApprovalStatus == ProjectPlanApprovalStatus.Approved
                ? new ProjectRiskPlan(effective.ApprovalStatus.ToString(), effective.PlannedFinish,
                    effective.Tasks.Select(task => new ProjectRiskTask(task.Id, task.Name, task.Stage, task.PlannedStart, task.PlannedFinish,
                        task.ActualFinish, task.Status.ToString(), task.PredecessorTaskIds)).ToArray(),
                    effective.Stages.Select(stage => new ProjectRiskStage(stage.Code, stage.ParticipatesInDelivery)).ToArray())
                : null;
            result.Add(new(new(project.Id, project.Code, project.BusinessStatus, planIds.Contains(project.Id) ? project.DeliveryDate : null),
                schedule, purchasing, releases, incomplete));
        }
        return result;

        static bool AllowsTab(Project project, string tab) => project.EffectiveProjectPermissions.Contains(ProjectCenterPermissions.Tab(tab));
    }

    private async Task<WorkbenchProcurement> ReadProcurementSummaryAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var value = await procurement.ListProjectAsync(projectId, actor, role, ct);
        var items = ProcurementDemandRows(value).ToArray();
        return new(value.HasPublishedBom, items.Count(item => !item.IsFullyReceived && (item.Manufacturing is { } m
                ? m.AssignedQuantity < item.Quantity : item.PurchaseOrderNumbers.Count == 0)),
            items.Count(item => !item.IsFullyReceived), items.Count(IsDelayedProcurement),
            value.LastSuccessfulRefreshAt ?? value.ManufacturingReadAt, value.LastRefreshError);
    }

    private static IEnumerable<ProjectProcurementTrackingItem> ProcurementDemandRows(ProjectProcurementTrackingResult value) =>
        value.Items.Where(item => !item.IsWarehouseMovementRow && (item.Manufacturing is null || item.Quantity > 0));

    private static bool IsDelayedProcurement(ProjectProcurementTrackingItem item) => item.HasDeliveryDelay && !item.IsFullyReceived;

    private static DateOnly? ToDate(DateTimeOffset? value) => value.HasValue ? DateOnly.FromDateTime(value.Value.Date) : null;

    private bool NeedsCalendarDeliveryDetails(ProjectPlan? plan, IReadOnlySet<Guid> propagatedTasks)
    {
        if (plan?.ApprovalStatus != ProjectPlanApprovalStatus.Approved) return false;
        var stages = plan.Stages.Where(stage => stage.ParticipatesInDelivery == true).Select(stage => stage.Code).ToHashSet();
        var tasks = plan.Tasks.Where(task => stages.Contains(task.Stage)).ToArray();
        // Filtering child projects can show propagated source tasks again; retain their delivery checks.
        if (tasks.Any(task => propagatedTasks.Contains(task.Id))) return true;
        var dates = tasks.Where(task => task.PlannedFinish > new DateOnly(1900, 1, 1)).Select(task => task.PlannedFinish).ToArray();
        if (dates.Length == 0) return false;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
        var finish = dates.Max();
        return finish <= today.AddDays(7) && (finish >= today || tasks.Any(task => task.Status != ProjectPlanTaskStatus.Completed));
    }

    private static bool IsFailedResult(string? value) => new[] { "NG", "不合格", "不通过", "未通过", "Fail", "Failed", "失败" }.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);
    private static bool IsPassedResult(string? value) => new[] { "OK", "合格", "通过", "Pass", "Passed", "成功" }.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);

    public async Task<ProjectWorkbenchRisk> RegisterRiskAsync(Guid projectId, RegisterWorkbenchRisk command, string actor, UserRole role, CancellationToken ct)
    {
        await RequireRiskAccessAsync(projectId, actor, role, ct);
        var title = command.Title?.Trim() ?? "";
        var impact = command.Impact?.Trim() ?? "";
        if (title.Length is < 1 or > 150 || impact.Length is < 1 or > 1000 || command.DueDate == default || command.Severity is not ("warning" or "danger")) throw new PdmRuleException("请填写风险、影响、截止日期及严重程度。");
        var owner = await repository.FindUserAsync(command.Owner, ct);
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var companyUnits = directory.Units.Where(unit => unit.OrganizationId == project.OrganizationId).Select(unit => unit.Id).ToHashSet();
        if (owner is null || !owner.IsActive || !directory.Memberships.Any(member => WorkbenchScope.Same(member.Username, owner.Username) && companyUnits.Contains(member.UnitId)))
            throw new PdmRuleException("负责人必须是项目公司内的启用人员。");
        var risk = new ProjectWorkbenchRisk(Guid.NewGuid(), projectId, title, impact, owner.Username, command.DueDate, command.Severity, actor, clock.GetUtcNow());
        await risks.SaveAsync(risk, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "project.risk.register", nameof(Project), projectId.ToString(), title), ct);
        return risk;
    }

    public async Task CloseRiskAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var risk = await risks.FindAsync(id, ct) ?? throw new PdmNotFoundException("风险不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(risk.ProjectId, actor, role, ct)) throw new UnauthorizedAccessException("无权访问该项目。");
        var project = await repository.FindProjectAsync(risk.ProjectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct) ?? project;
        var units = WorkbenchScope.ManagedUnits(await repository.GetOrganizationDirectoryAsync(ct), actor);
        if (!WorkbenchScope.Same(risk.Owner, actor) && !WorkbenchScope.Same(risk.CreatedBy, actor) && !await AuthorizationPolicy.CanAsync(repository, risk.ProjectId, actor, "project.risk.close-others", ct))
            throw new UnauthorizedAccessException("当前账号没有关闭他人风险的权限。");
        if (risk.Closed) return;
        await risks.SaveAsync(risk with { Closed = true, ClosedBy = actor, ClosedAt = clock.GetUtcNow() }, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "project.risk.close", nameof(Project), risk.ProjectId.ToString(), risk.Title), ct);
    }

    private async Task RequireRiskAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权访问该项目。");
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct) ?? project;
        var units = WorkbenchScope.ManagedUnits(await repository.GetOrganizationDirectoryAsync(ct), actor);
        var staff = await contacts.FindAsync(root.Id, ct);
        var plan = await planning.FindPlanAsync(projectId, ct);
        if (!await AuthorizationPolicy.CanAsync(repository, projectId, actor, "project.risk.manage", ct)) throw new UnauthorizedAccessException("当前账号没有维护此项目风险的权限。");
    }
}
