using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectApplicationBudgets
{
    public static IReadOnlyDictionary<string, decimal?> Normalize(IReadOnlyDictionary<string, decimal?>? amounts, bool required)
    {
        if (amounts?.Keys.Any(key => !ProjectBudgetCategories.All.Contains(key)) == true)
            throw new PdmRuleException("预算费用分类无效，请刷新页面后重新填写。");
        if (amounts?.Values.Any(amount => amount is < 0 or > 1_000_000_000m || amount.HasValue && decimal.Round(amount.Value, 2) != amount.Value) == true)
            throw new PdmRuleException("预算金额应为0至10亿元，最多保留两位小数。");
        var result = ProjectBudgetCategories.All.ToDictionary(key => key, key => amounts?.GetValueOrDefault(key));
        if (required && result.Values.Any(amount => !amount.HasValue))
            throw new PdmRuleException("请补齐物料成本和人工成本预算；无费用的项目请填写0，也可先保存草稿。");
        return result;
    }

    public static ProjectBudget? Create(Guid projectId, ProjectApplicationInput input, string actor, DateTimeOffset at)
    {
        var settlementLines = ProjectApplicationSettlements.Normalize(input.SettlementLines, required: true);
        if (input.BudgetAmounts?.Values.Any(amount => amount.HasValue) != true && settlementLines.Count == 0) return null;
        return new(projectId, ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, input.BudgetAmounts?.GetValueOrDefault(category))).ToArray(),
            RowVersion: 1, UpdatedBy: actor, UpdatedAt: at, ManualActualAmounts: true, SettlementLines: settlementLines);
    }
}
