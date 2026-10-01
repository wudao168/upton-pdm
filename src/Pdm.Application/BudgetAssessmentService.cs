using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record BudgetAssessmentItem(Guid Id, string Name, string? Model = null, string? Brand = null,
    string? Note = null, decimal? Quantity = null, decimal? UnitPrice = null, string? LaborCategory = null);
public sealed record BudgetAssessmentGroup(Guid Id, string Name, string Category, string? LaborCategory,
    IReadOnlyList<BudgetAssessmentItem> Items);
public sealed record SaveBudgetAssessmentCommand(IReadOnlyList<BudgetAssessmentGroup> Groups, long ExpectedRowVersion);
public sealed record BudgetAssessmentSheet(Guid ProjectId, string ProjectCode, string ProjectName, string? DesignLead,
    bool CanEdit, long RowVersion, IReadOnlyList<BudgetAssessmentGroup> Groups, decimal? Total, string? UpdatedBy, DateTimeOffset? UpdatedAt);
public sealed record BudgetAssessmentDirectory(IReadOnlyList<BudgetAssessmentSheet> Sheets, IReadOnlyDictionary<string, decimal?> Amounts,
    Guid RatesProjectId, long RatesRowVersion, bool CanEditLaborRates, IReadOnlyDictionary<string, decimal?> LaborRates);
public sealed record SaveAssessmentLaborRatesCommand(IReadOnlyDictionary<string, decimal?> Rates, long ExpectedRowVersion);

public sealed class BudgetAssessmentService(IPdmRepository repository, IProjectBudgetRepository budgets, ProjectBudgetService budgetService, TimeProvider timeProvider)
{
    public async Task<BudgetAssessmentDirectory> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await budgetService.RequireReadAsync(projectId, actor, role, ct);
        var ratesProjectId = project.Id;
        var ratesSettings = await budgets.FindLaborRatesAsync(ct);
        var rates = ratesSettings?.Rates ?? new Dictionary<string, decimal?>();
        var projects = await repository.ListProjectsAsync(ct);
        var selected = projects.Where(item => item.Id == projectId || project.ParentProjectId is null && (item.RootProjectId == projectId || item.ParentProjectId == projectId))
            .OrderBy(item => item.Id == projectId ? 0 : 1).ThenBy(item => item.Code).ToArray();
        var sheets = new List<BudgetAssessmentSheet>();
        foreach (var item in selected)
        {
            if (!await repository.HasProjectContentReadAccessAsync(item.Id, actor, role, ct)) continue;
            var stored = await budgets.FindAsync(item.Id, ct);
            var groups = (stored?.AssessmentGroups ?? []).Select(group => group.Category != "Labor" ? group : group with {
                LaborCategory = null, Items = group.Items.Select(line => {
                    var category = line.LaborCategory ?? group.LaborCategory;
                    return line with { LaborCategory = category, UnitPrice = category != null && rates.TryGetValue(category, out var rate) ? rate : line.UnitPrice };
                }).ToArray()
            }).ToArray();
            var root = item.ParentProjectId is null ? item : await repository.FindProjectAsync(item.RootProjectId ?? item.ParentProjectId.Value, ct);
            var leads = item.DesignLeads.Count > 0 ? item.DesignLeads : !string.IsNullOrWhiteSpace(item.DesignLead) ? new[] { item.DesignLead! }
                : root?.DesignLeads.Count > 0 ? root.DesignLeads : new[] { root?.DesignLead ?? "" };
            sheets.Add(new(item.Id, item.Code, item.Name, string.Join("、", leads.Where(value => !string.IsNullOrEmpty(value))),
                leads.Contains(actor, StringComparer.OrdinalIgnoreCase), stored?.RowVersion ?? 0, groups, Sum(groups.Select(GroupTotal)), stored?.AssessmentUpdatedBy, stored?.AssessmentUpdatedAt));
        }
        var amounts = new Dictionary<string, decimal?>();
        foreach (var category in new[] { "Standard", "Nonstandard", "Equipment" }.Concat(ProjectBudgetCategories.Labor))
            amounts[category] = Sum(sheets.SelectMany(sheet => sheet.Groups).SelectMany(group => group.Items.Where(line => (group.Category == "Labor" ? line.LaborCategory : group.Category) == category)).Select(ItemTotal));
        return new(sheets, amounts, ratesProjectId, ratesSettings?.RowVersion ?? 0, CanManageRates(role), rates);
    }

    public async Task<BudgetAssessmentDirectory> SaveAsync(Guid projectId, SaveBudgetAssessmentCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var directory = await GetAsync(projectId, actor, role, ct);
        if (directory.Sheets.FirstOrDefault(sheet => sheet.ProjectId == projectId)?.CanEdit != true)
            throw new UnauthorizedAccessException("仅当前项目主设可维护预算评估。");
        if (command.Groups is null || command.Groups.Count > 100 || command.Groups.Any(group => group.Items is null) || command.Groups.Select(group => group.Id).Distinct().Count() != command.Groups.Count)
            throw new PdmRuleException("评估分组无效或重复，最多100组。");
        var current = await budgets.FindAsync(projectId, ct) ?? new(projectId, ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category)).ToArray(), ManualActualAmounts: true);
        var enteredGroups = command.Groups.Select(group => group with { Items = group.Items.Where(item => !IsBlank(item)).ToArray() });
        var groups = enteredGroups.Select(group => group.Category != "Labor" ? group : group with {
            LaborCategory = null, Items = group.Items.Select(line => {
                var category = line.LaborCategory ?? group.LaborCategory;
                var existing = current.AssessmentGroups?.Where(previous => previous.Category == "Labor").SelectMany(previous => previous.Items.Select(item => item with { LaborCategory = item.LaborCategory ?? previous.LaborCategory })).FirstOrDefault(previous => previous.Id == line.Id && previous.LaborCategory == category);
                return line with { LaborCategory = category, UnitPrice = category != null && directory.LaborRates.TryGetValue(category, out var rate) ? rate : existing?.UnitPrice };
            }).ToArray()
        }).ToArray();
        foreach (var group in groups)
        {
            if (group.Id == Guid.Empty || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 100 || group.Category is not ("Standard" or "Nonstandard" or "Equipment" or "Labor")
                || group.Items is null || group.Items.Count > 2000 || group.Items.Select(item => item.Id).Distinct().Count() != group.Items.Count)
                throw new PdmRuleException("请填写有效的分组名称、费用分类和人工费用项。");
            foreach (var item in group.Items)
                if (group.Category == "Labor" && !ProjectBudgetCategories.Labor.Contains(item.LaborCategory ?? "") || item.Id == Guid.Empty || group.Category != "Labor" && string.IsNullOrWhiteSpace(item.Name) || item.Name?.Length > 200 || item.Model?.Length > 200 || item.Brand?.Length > 200 || item.Note?.Length > 500
                    || item.Quantity < 0 || item.Quantity > 1_000_000 || item.UnitPrice < 0 || item.UnitPrice > 1_000_000_000)
                    throw new PdmRuleException("物料明细需填写名称，人工明细需选择人工类型；数量或工时应在0至100万之间，单价应在0至10亿之间。");
        }
        await budgets.SaveAsync(current with { AssessmentGroups = groups, AssessmentUpdatedBy = actor, AssessmentUpdatedAt = timeProvider.GetUtcNow(), UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() }, command.ExpectedRowVersion, ct);
        return await GetAsync(projectId, actor, role, ct);
    }
    public async Task<BudgetAssessmentDirectory> SaveLaborRatesAsync(Guid projectId, SaveAssessmentLaborRatesCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var directory = await GetAsync(projectId, actor, role, ct);
        if (!directory.CanEditLaborRates) throw new UnauthorizedAccessException("仅计划管理或管理员可设置工时单价。");
        if (command.Rates is null || command.Rates.Keys.Any(key => !ProjectBudgetCategories.Labor.Contains(key))
            || command.Rates.Values.Any(value => value < 0 || value > 1_000_000_000))
            throw new PdmRuleException("请填写有效的人工类型及工时单价。");
        await budgets.SaveLaborRatesAsync(new(ProjectBudgetCategories.Labor.ToDictionary(key => key, key => command.Rates.GetValueOrDefault(key)), UpdatedBy: actor, UpdatedAt: timeProvider.GetUtcNow()), command.ExpectedRowVersion, ct);
        return await GetAsync(projectId, actor, role, ct);
    }
    private static bool CanManageRates(UserRole role) => TenantContext.Current is { } tenant
        ? tenant.HasRole("PlanningManager") || tenant.HasRole("Administrator") : role is UserRole.PlanningManager or UserRole.Administrator;
    private static bool IsBlank(BudgetAssessmentItem item) => string.IsNullOrWhiteSpace(item.Name)
        && string.IsNullOrWhiteSpace(item.Model) && string.IsNullOrWhiteSpace(item.Brand) && string.IsNullOrWhiteSpace(item.Note)
        && item.Quantity is null && item.UnitPrice is null && string.IsNullOrWhiteSpace(item.LaborCategory);
    public static decimal? ItemTotal(BudgetAssessmentItem item) => item.Quantity.HasValue && item.UnitPrice.HasValue ? decimal.Round(item.Quantity.Value * item.UnitPrice.Value, 2, MidpointRounding.AwayFromZero) : null;
    public static decimal? GroupTotal(BudgetAssessmentGroup group) => Sum(group.Items.Select(ItemTotal));
    private static decimal? Sum(IEnumerable<decimal?> source) { var values = source.Where(value => value.HasValue).ToArray(); return values.Length == 0 ? null : values.Sum(value => value!.Value); }
}
