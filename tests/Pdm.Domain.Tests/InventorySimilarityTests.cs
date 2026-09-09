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
    [InlineData("M4-18", "M4-16", 80)]
    [InlineData("AB-CD", "ABCD", 80)]
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
    public async Task Repository_MatchesBeforePagination_AndIncludes75PercentAcrossWarehouses()
    {
        var repository = new InMemoryU9InventoryRepository();
        var rows = Enumerable.Range(0, 205).Select(i => Row($"A{i:D4}", "ZZZZ")).ToList();
        rows.Add(Row("Z001", "ABCE"));
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
        Assert.Equal(75m, second.Items[0].SimilarityPercent);
        Assert.Equal(3, (await repository.ListAsync(Filters("ABCD") with { PositiveStockOnly = false }, default)).Total);
        Assert.Empty((await repository.ListAsync(Filters("ABCD") with { Brand = "FESTO" }, default)).Items);
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

    private static U9InventoryFilters Filters(string? specification) => new(null, null, null, null, null, null, null, true, 1, 50, specification);
    private static U9InventorySourceRow Row(string code, string? specification) => new("7", code, "物料", specification,
        "01", "1号仓", null, null, null, "P1", "项目", null, 1, 1, 0, 0);
}
