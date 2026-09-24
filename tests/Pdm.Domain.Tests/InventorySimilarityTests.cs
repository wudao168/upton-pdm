using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class InventorySimilarityTests
{
    [Theory]
    [InlineData(" abcd ", "ＡＢＣＤ", 100)]
    [InlineData("ABCD", "ABCE", 75)]
    [InlineData("ABCD", "ABEF", 50)]
    [InlineData("M4-18", "M4-16", 75)]
    [InlineData("AB-CD", "ABCD", 100)]
    [InlineData("", "ABCD", 0)]
    public void Score_UsesNormalizedEditDistanceAndPreservesDigitsAndSymbols(string left, string right, int score)
        => Assert.Equal(score, InventorySimilarity.Score(left, right));

    [Theory]
    [InlineData("")]
    [InlineData("A-1")]
    [InlineData("----")]
    public void Prepare_RejectsShortQueries(string query)
        => Assert.Throws<PdmRuleException>(() => InventorySimilarity.Prepare(Filters(query)));

    [Fact]
    public void Prepare_RejectsExcessiveLength()
        => Assert.Throws<PdmRuleException>(() => InventorySimilarity.Prepare(Filters(new string('A', 257))));

    [Fact]
    public void SimilarMode_KeepsOnlyBrandAndStockScope()
    {
        var result = InventorySimilarity.Prepare(Filters("ＡＢＣＤ") with {
            MaterialCode = "other", ItemName = "other", Specification = "other", Warehouse = "other",
            ProjectCode = "other", Subproject = "other", Brand = "FESTO" });
        Assert.Equal("ABCD", result.SimilarSpecification);
        Assert.Equal("FESTO", result.Brand);
        Assert.True(result.PositiveStockOnly);
        Assert.Null(result.MaterialCode);
        Assert.Null(result.Specification);
        Assert.Null(result.ProjectCode);
        Assert.Null(result.Warehouse);
    }

    [Fact]
    public async Task Repository_MatchesBeforePagination_AndIncludes80PercentAcrossWarehouses()
    {
        var repository = new InMemoryU9InventoryRepository();
        var rows = Enumerable.Range(0, 205).Select(i => Row($"A{i:D4}", "ZZZZ")).ToList();
        rows.Add(Row("Z001", "ABCDD"));
        rows.Add(Row("Z002", "ＡＢＣＤ") with { WarehouseName = "2号仓", ProjectCode = "P2" });
        rows.Add(Row("Z003", "ABCD") with { StockQuantity = 0 });
        rows.Add(Row("Z004", "ABEF"));
        rows.Add(Row("Z005", null));
        await repository.ReplaceSnapshotAsync(Guid.NewGuid(), rows, DateTimeOffset.UtcNow, default);
        var first = await repository.ListAsync(Filters("ABCD") with { PageSize = 1 }, default);
        Assert.Equal(2, first.Total);
        Assert.Equal("Z002", Assert.Single(first.Items).MaterialCode);
        Assert.Equal(100m, first.Items[0].SimilarityPercent);
        var second = await repository.ListAsync(Filters("ABCD") with { Page = 2, PageSize = 1 }, default);
        Assert.Equal("Z001", Assert.Single(second.Items).MaterialCode);
        Assert.Equal(80m, second.Items[0].SimilarityPercent);
        Assert.Equal(3, (await repository.ListAsync(Filters("ABCD") with { PositiveStockOnly = false }, default)).Total);
        Assert.Equal(2, (await repository.ListAsync(Filters("ABCD") with { Brand = "FESTO" }, default)).Total);
        var normal = await repository.ListAsync(Filters(null) with { Specification = "ZZZZ" }, default);
        Assert.Equal(205, normal.Total);
        Assert.All(normal.Items, row => Assert.Null(row.SimilarityPercent));
    }

    [Fact]
    public void Match_DoesNotRoundBelowThresholdUp_AndHonorsCancellation()
    {
        var row = new U9InventorySnapshotRow(Guid.NewGuid(), "7", "01", "仓库", "001", "物料", "FESTO",
            new string('A', 149) + new string('B', 50), "P1", null, null, 1, 1, 0, 0, null, null, null, DateTimeOffset.UtcNow);
        Assert.Empty(InventorySimilarity.Match([row], new string('A', 199), default));
        Assert.Throws<OperationCanceledException>(() => InventorySimilarity.Match([row], "ABCD", new CancellationToken(true)));
    }

    [Fact]
    public void Match_PrioritizesPreferredBrand_OnlyAfterModelMatches()
    {
        var preferred = Snapshot("A", "CDQ2B32-100", "SMC");
        var otherBrand = Snapshot("B", "CDQ2B32-100", "FESTO");
        var unrelated = Snapshot("C", "XXXX-100", "SMC");

        var matches = InventorySimilarity.Match([otherBrand, preferred, unrelated], "CDQ2B32-50", default, "SMC");

        Assert.Equal(["A", "B"], matches.Select(row => row.MaterialCode));
        Assert.Equal(87m, matches[0].SimilarityPercent);
        Assert.Equal("主型号一致，末段数字不同；品牌一致", matches[0].SimilarityReason);
        Assert.Equal(85m, matches[1].SimilarityPercent);
        Assert.Equal("主型号一致，末段数字不同", matches[1].SimilarityReason);
    }

    private static U9InventoryFilters Filters(string? specification) => new(null, null, null, null, null, null, null, true, 1, 50, specification);
    private static U9InventorySourceRow Row(string code, string? specification) => new("7", code, "物料", specification,
        "01", "1号仓", null, null, null, "P1", "项目", null, 1, 1, 0, 0);
    private static U9InventorySnapshotRow Snapshot(string code, string specification, string brand) => new(Guid.NewGuid(), "7", "01", "仓库", code,
        "物料", brand, specification, "P1", null, null, 1, 1, 0, 0, null, null, null, DateTimeOffset.UtcNow);
}
