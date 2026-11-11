using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectApplicationSettlementsTests
{
    [Theory]
    [InlineData("0", "1", "数量")]
    [InlineData("-1", "1", "数量")]
    [InlineData("1.5", "1", "整数")]
    [InlineData("1000001", "0", "数量")]
    [InlineData("1", "-1", "单价")]
    [InlineData("1", "1000000001", "单价")]
    [InlineData("1", "0.001", "两位小数")]
    [InlineData("2", "1000000000", "小计")]
    public void InvalidEnteredValuesAreRejectedEvenInDraft(string quantity, string price, string expected)
    {
        var line = new ProjectSettlementLine("设备款", Quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), UnitPrice: decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(expected, Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize([line], false)).Message);
    }

    [Fact]
    public void LimitsApplyToRowsAndAggregateAndDoNotOverflow()
    {
        Assert.Contains("100条", Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize(Enumerable.Repeat(new ProjectSettlementLine(""), 101).ToArray(), false)).Message);
        Assert.Contains("总金额", Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize([new("一", Quantity: 1, UnitPrice: 600_000_000m), new("二", Quantity: 1, UnitPrice: 400_000_000.01m)], true)).Message);
        Assert.Contains("数量", Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize([new("一", Quantity: decimal.MaxValue, UnitPrice: decimal.MaxValue)], false)).Message);
        Assert.Contains("资料无效", Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize([null!], false)).Message);
        var maximum = ProjectApplicationSettlements.Normalize([new("设备款", Quantity: 1_000_000m, UnitPrice: 1000m)], true);
        Assert.Equal(1_000_000_000m, ProjectApplicationSettlements.Total(maximum));
    }

    [Fact]
    public void BlankRowsArePreservedForDraftButMustBeCompletedOrRemovedForSubmission()
    {
        var draft = ProjectApplicationSettlements.Normalize([new(" "), new("名称", Quantity: 1), new("", Quantity: 1, UnitPrice: 0)], false);
        Assert.Equal(3, draft.Count);
        Assert.Null(ProjectApplicationSettlements.Total(draft));
        Assert.Contains("第1行", Assert.Throws<PdmRuleException>(() => ProjectApplicationSettlements.Normalize(draft, true)).Message);
        Assert.Empty(ProjectApplicationSettlements.Normalize(null, true));
        Assert.Empty(ProjectApplicationSettlements.Normalize([], true));
    }

    [Fact]
    public void BudgetInitializationSupportsLegacyWithoutSettlementAndSettlementWithoutBudgetAmounts()
    {
        var input = new ProjectApplicationInput(null, "客户", "项目", null, null, "", [], "", false);
        Assert.Null(ProjectApplicationBudgets.Create(Guid.NewGuid(), input, "planner", DateTimeOffset.UtcNow));
        var budget = ProjectApplicationBudgets.Create(Guid.NewGuid(), input with { SettlementLines = [new("设备款", "台", 2, 123.45m)] }, "planner", DateTimeOffset.UtcNow)!;
        Assert.All(budget.Lines, line => Assert.Null(line.BudgetAmount));
        Assert.Equal(246.90m, ProjectApplicationSettlements.Total(budget.SettlementLines));
    }
}
