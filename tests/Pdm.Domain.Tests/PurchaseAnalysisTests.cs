using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class PurchaseAnalysisTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);
    public static PurchaseAnalysisLine Line(string id, string order, int days, decimal quantity = 10, decimal? amount = 100,
        string currency = "人民币", string unit = "个", string supplier = "S1") =>
        new(id, "ORG1", order, order, 1, Today.AddDays(-days), "PO01", "标准采购", "M1", "电机", "型号", "品牌", "0102", "机械外购",
            supplier, supplier, "B1", "张三", "P1", "", unit, unit, quantity, currency, amount);
    private static PurchaseAnalysisResult Analyze(PurchaseAnalysisLine[] rows, PurchaseAnalysisFilter? filter = null, bool cost = true) =>
        PurchaseAnalysisEngine.Build(rows, new(Today.ToDateTime(TimeOnly.MinValue), Today.ToDateTime(TimeOnly.MinValue), Today.AddYears(-2), Today),
            filter ?? new() { FrequentOrders = 2 }, Today, cost, true, false, false);

    [Fact]
    public void DuplicateLinesAndSplitOrdersDoNotInflateFrequencyAndPricesAreWeighted()
    {
        var a = Line("1", "A", 20, 10, 100); var b = Line("2", "A", 20, 30, 600); var c = Line("3", "B", 2, 10, 300);
        var result = Analyze([a, a, b, c]); var metric = Assert.Single(result.Frequent);
        Assert.Equal(2, metric.Orders); Assert.Equal(50, metric.Quantity); Assert.Equal(1000, metric.Amount); Assert.Equal(20, metric.AveragePrice);
        Assert.Equal(30, metric.LastPrice); Assert.Equal(18, metric.AverageInterval); Assert.Equal(25, metric.AverageBatch);
        Assert.Equal(2, result.OrderCount); Assert.Equal(3, result.DetailCount);
    }
    [Fact]
    public void UnitsCurrenciesAndMissingAmountsAreNeverSilentlyCombined()
    {
        var result = Analyze([Line("1", "A", 1), Line("2", "B", 2, unit: "箱"), Line("3", "C", 3, currency: "美元"), Line("4", "D", 4, amount: null)]);
        Assert.Equal(3, result.Volume.Count); Assert.Null(result.Amounts["人民币"]); Assert.Equal(100, result.Amounts["美元"]);
        Assert.Null(result.Volume.Single(m => m.Unit == "个" && m.Currency == "人民币").AveragePrice);
        Assert.Single(result.Trend); Assert.Null(result.Trend[0].Amount);
    }
    [Fact]
    public void FiltersDetailPaginationAndDatesUseOneConsistentPopulation()
    {
        var rows = Enumerable.Range(1, 65).Select(i => Line(i.ToString(), i.ToString(), 1, supplier: i <= 60 ? "S1" : "S2")).ToArray();
        var filter = new PurchaseAnalysisFilter { Suppliers = ["S1"], MinOrders = 5, Page = 2, From = Today.AddDays(-2), Through = Today };
        var result = Analyze(rows, filter);
        Assert.Equal(60, result.DetailCount); Assert.Equal(10, result.Details.Count); Assert.Equal(60, result.OrderCount);
        Assert.Empty(Analyze(rows, filter with { Through = Today.AddDays(-2), From = Today.AddDays(-3) }).Details);
        Assert.Throws<PdmRuleException>(() => Analyze(rows, filter with { From = Today.AddDays(1) }));
    }
    [Fact]
    public void PriceFilteringRequiresCostPermissionAndCatalogNeverAutoGrantsNewActions()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Analyze([Line("1", "A", 1, amount: null)], new() { MinAmount = 1 }, false));
        var result = Analyze([Line("1", "A", 1, amount: null)], cost: false);
        Assert.Empty(result.Spend); Assert.Empty(result.Prices); Assert.Empty(result.Amounts);
        var permissions = RolePermissionCatalog.Permissions.Where(p => p.Module == "采购分析").ToArray(); Assert.Equal(5, permissions.Length);
        Assert.All(permissions, p => Assert.True(FunctionPermissionCatalog.RequiresExplicitGrant(p.Code)));
        Assert.DoesNotContain(PurchaseAnalysisPermissions.View, RolePermissionCatalog.InitialPermissions("Administrator", UserRole.Administrator));
    }

    [Fact]
    public void SupplierComparisonUsesAllRowsAndIncompleteDenominatorHasNoShare()
    {
        var rows = new[] { Line("1", "A", 3, 10, 100), Line("2", "B", 2, 30, 600), Line("3", "C", 1, 10, 300, supplier: "S2") };
        var result = Analyze(rows, new() { DetailKey = PurchaseAnalysisEngine.MaterialKey(rows[0]) });
        Assert.Equal(2, result.PriceComparisons.Count); Assert.Equal(17.5m, result.PriceComparisons.Single(m => m.Code == "S1").AveragePrice);
        var partial = Analyze([rows[0], rows[2] with { NetAmount = null }]);
        Assert.All(partial.Suppliers, m => { Assert.Null(m.Share); Assert.Null(m.Abc); });
    }

    [Fact]
    public void NonProjectFilterExcludesSubprojectsAndFragmentWindowUsesAdjacentDistinctOrders()
    {
        var rows = new[] { Line("1", "A", 30), Line("2", "B", 27), Line("3", "C", 25), Line("4", "D", 1) with { ProjectCode = "", SubprojectCode = "SUB" },
            Line("5", "E", 0) with { ProjectCode = "", SubprojectCode = "" } };
        var result = Analyze(rows, new() { Projects = [""], FrequentOrders = 2 });
        Assert.Equal("5", Assert.Single(result.Details).Id);
        Assert.Equal(3, Assert.Single(Analyze(rows[..4], new() { FragmentDays = 3 }).Fragmented).FragmentOrders);
        Assert.DoesNotContain(Analyze(rows[..4]).Options["projects"], p => p.Value.Length == 0);
    }
}
