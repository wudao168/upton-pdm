using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectBudgetService(IPdmRepository repository, IProjectBudgetRepository budgets,
    IU9ProcurementRepository procurement, TimeProvider timeProvider, IMaterialRepository materials)
{
    public async Task<ProjectBudgetView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireReadAsync(projectId, actor, role, ct);
        var budget = await budgets.FindAsync(projectId, ct) ?? new(projectId, ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category)).ToArray());
        var reserve = IsPlanning(role);
        var canEditActual = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectBudgetActualEdit, ct);
        var canEdit = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectBudgetEdit, ct);
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct)
            ?? throw new PdmNotFoundException("主项目不存在。");
        var sources = await procurement.ListForProjectAsync(root.Code, project.ParentProjectId is null ? null : project.Code, ct);
        var nonstandard = await repository.GetBomAsync(projectId, BomKind.NonStandard, ct);
        var nonstandardCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in nonstandard)
        {
            var material = await materials.FindMaterialBySourceBomItemAsync(item.Id, ct);
            nonstandardCodes.Add(material?.U9ItemCode ?? material?.MaterialCode ?? item.DrawingNumber);
        }
        var orders = sources.Where(row => row.RecordKind == U9ProcurementRecordKinds.PurchaseOrder && !row.IsCanceled)
            .DistinctBy(row => (row.OrganizationCode, row.LineId)).Select(row =>
            {
                var key = $"{row.OrganizationCode}:{row.LineId}";
                var category = budget.OrderCategories?.GetValueOrDefault(key)
                    ?? (nonstandardCodes.Contains(row.MaterialCode) ? "Nonstandard" : "Standard");
                return new ProjectBudgetOrder(key, row.DocumentNumber, row.LineNumber, row.MaterialCode, row.ItemName, row.OrderNetAmount, category);
            }).ToArray();
        var rows = budget.Lines.Select(line => NormalizeLegacyActual(NormalizeLegacyBudget(line), budget.ManualActualAmounts)).Where(line => reserve || line.Category != ProjectBudgetCategories.Reserve).Select(line =>
        {
            var matching = orders.Where(order => order.Category == line.Category).ToArray();
            decimal? orderAmount = matching.Length == 0 ? null : matching.All(order => order.Amount.HasValue) ? matching.Sum(order => order.Amount!.Value) : null;
            return Calculate(line, orderAmount, matching.Any(order => order.Amount is null));
        }).ToArray();
        return new(projectId, rows, Total(rows.Select(row => row.BudgetAmount)), Total(rows.Select(row => row.ActualAmount)),
            Total(rows.Select(row => row.EstimatedAmount)), Overrun(rows), canEdit || canEditActual, reserve,
            budget.RowVersion, budget.UpdatedBy, budget.UpdatedAt, orders, canEdit, canEditActual);
    }

    public async Task<ProjectBudgetView> SaveAsync(Guid projectId, SaveProjectBudgetCommand command, string actor, UserRole role, CancellationToken ct)
    {
        await RequireReadAsync(projectId, actor, role, ct);
        var canEdit = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectBudgetEdit, ct);
        var canEditActual = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectBudgetActualEdit, ct);
        if (!canEdit && !canEditActual)
            throw new UnauthorizedAccessException("当前账号没有预算维护权限。");
        if (command.Lines is null || command.Lines.Select(line => line.Category).Distinct().Count() != command.Lines.Count
            || command.Lines.Any(line => !ProjectBudgetCategories.All.Contains(line.Category)))
            throw new PdmRuleException("预算分类无效或重复。");
        var current = await budgets.FindAsync(projectId, ct);
        var reserve = IsPlanning(role);
        if (!reserve && command.Lines.Any(line => line.Category == ProjectBudgetCategories.Reserve))
            throw new UnauthorizedAccessException("风险预留仅计划管理可维护。");
        var required = ProjectBudgetCategories.All.Where(category => reserve || category != ProjectBudgetCategories.Reserve).ToArray();
        if (required.Any(category => !command.Lines.Any(line => line.Category == category))) throw new PdmRuleException("请填写完整的预算分类。");
        foreach (var line in command.Lines)
        {
            decimal?[] amounts = [line.BudgetAmount, line.ActualAmount, line.RemainingAmount, line.PlannedHours, line.HourlyRate,
                line.ActualHours, line.ActualHourlyRate, line.RemainingHours];
            if (amounts.Any(value => value < 0 || value > 1_000_000_000m)) throw new PdmRuleException("金额和工时应在0至10亿之间。");
            if (line.Note?.Length > 500) throw new PdmRuleException("备注最多500字。");
        }
        if (command.OrderCategories?.Any(pair => pair.Value is not ("Standard" or "Nonstandard" or "Equipment")) == true)
            throw new PdmRuleException("采购订单只能归入标准件、非标件或外购设备。");
        foreach (var line in command.Lines)
        {
            var previous = NormalizeLegacyActual(NormalizeLegacyBudget(current?.Lines.FirstOrDefault(item => item.Category == line.Category) ?? new(line.Category)), current?.ManualActualAmounts ?? true);
            if (!canEditActual && line.Category != ProjectBudgetCategories.Reserve && (line.ActualAmount != previous.ActualAmount || line.ActualHours != previous.ActualHours))
                throw new UnauthorizedAccessException("当前账号没有实际成本及工时维护权限。");
            if (!canEdit && (line with { ActualAmount = previous.ActualAmount, ActualHours = previous.ActualHours }) != previous)
                throw new UnauthorizedAccessException("当前账号没有预算维护权限。");
        }
        if (!canEdit)
        {
            var view = await GetAsync(projectId, actor, role, ct);
            if (command.OrderCategories?.Any(pair => view.Orders.FirstOrDefault(order => order.Key == pair.Key)?.Category != pair.Value) == true)
                throw new UnauthorizedAccessException("当前账号没有采购订单分类维护权限。");
        }
        var lines = command.Lines.Select(line => ProjectBudgetCategories.Labor.Contains(line.Category) ? line with { PlannedHours = null } : line).ToList();
        if (!reserve) lines.Add(current?.Lines.FirstOrDefault(line => line.Category == ProjectBudgetCategories.Reserve) ?? new(ProjectBudgetCategories.Reserve));
        await budgets.SaveAsync(new(projectId, lines, UpdatedBy: actor, UpdatedAt: timeProvider.GetUtcNow(),
            OrderCategories: command.OrderCategories, ManualActualAmounts: true, AssessmentGroups: current?.AssessmentGroups, AssessmentUpdatedBy: current?.AssessmentUpdatedBy, AssessmentUpdatedAt: current?.AssessmentUpdatedAt, AssessmentLaborRates: current?.AssessmentLaborRates), command.ExpectedRowVersion, ct);
        return await GetAsync(projectId, actor, role, ct);
    }

    internal async Task<Project> RequireReadAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有项目内容查看权限。");
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct);
        var settings = await repository.GetProjectPermissionSettingsAsync(ct);
        var lead = root is not null && ProjectPermissionPolicy.Allows(project, root, actor, settings, PermissionCodes.ProjectBudgetView);
        if (!lead && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectBudgetView, ct))
            throw new UnauthorizedAccessException("当前账号没有预算查看权限。");
        return project;
    }

    private static bool IsPlanning(UserRole role) => TenantContext.Current is { } tenant
        ? tenant.HasRole("PlanningManager") : role == UserRole.PlanningManager;

    public static ProjectBudgetRow Calculate(ProjectBudgetLine line, decimal? orderAmount = null, bool missingOrderAmount = false)
    {
        var labor = ProjectBudgetCategories.Labor.Contains(line.Category);
        decimal? budget = line.BudgetAmount;
        decimal? actual = line.ActualAmount;
        decimal? remaining = labor ? Product(line.RemainingHours, line.ActualHourlyRate) : line.RemainingAmount;
        decimal? estimated = actual.HasValue && remaining.HasValue && !missingOrderAmount
            ? Math.Max(actual.Value, orderAmount ?? 0m) + remaining.Value : null;
        if (line.Category == ProjectBudgetCategories.Reserve) estimated = actual;
        decimal? variance = budget.HasValue && estimated.HasValue ? estimated.Value - budget.Value : null;
        var alert = budget.HasValue && (actual > budget || orderAmount > budget || estimated > budget) ? "Overrun"
            : !budget.HasValue || !estimated.HasValue ? "Incomplete"
            : budget > 0 && estimated >= budget * .9m ? "Warning" : "Normal";
        return new(line, budget, actual, orderAmount, estimated, variance, alert);
    }
    private static ProjectBudgetLine NormalizeLegacyBudget(ProjectBudgetLine line) => ProjectBudgetCategories.Labor.Contains(line.Category) && line.PlannedHours.HasValue
        ? line with { BudgetAmount = line.BudgetAmount ?? Product(line.PlannedHours, line.HourlyRate), PlannedHours = null }
        : line;
    private static ProjectBudgetLine NormalizeLegacyActual(ProjectBudgetLine line, bool manual) => !manual && ProjectBudgetCategories.Labor.Contains(line.Category)
        ? line with { ActualAmount = line.ActualAmount ?? Product(line.ActualHours, line.ActualHourlyRate) } : line;
    private static decimal? Product(decimal? hours, decimal? rate) => hours.HasValue && rate.HasValue ? decimal.Round(hours.Value * rate.Value, 2) : null;
    private static decimal? Total(IEnumerable<decimal?> values) { var items = values.ToArray(); return items.All(value => value.HasValue) ? items.Sum(value => value!.Value) : null; }
    private static decimal? Overrun(ProjectBudgetRow[] rows)
    {
        var budget = Total(rows.Select(row => row.BudgetAmount));
        var estimate = Total(rows.Select(row => row.EstimatedAmount));
        return budget.HasValue && estimate.HasValue ? Math.Max(estimate.Value - budget.Value, 0) : null;
    }
}
