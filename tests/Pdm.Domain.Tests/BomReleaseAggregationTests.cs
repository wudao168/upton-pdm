using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Pdm.Domain.Tests;

public sealed class BomReleaseAggregationTests
{
    [Fact]
    public void Build_PreservesParentRelationshipsAndAggregatesPurchaseDemand()
    {
        var first = Item("1001", 2, "PARENT-A");
        var second = Item("1001", 3, "PARENT-B");
        var third = Item("1001", 4, "PARENT-A");
        var package = Package([first, second, third]);

        var summary = BomReleaseAggregation.Build(package);

        Assert.Equal(2, summary.ProductionStructure.Count);
        var parentA = Assert.Single(summary.ProductionStructure, line => line.ParentMaterialCode == "PARENT-A");
        Assert.Equal(6, parentA.Quantity);
        Assert.Equal(new[] { first.Id, third.Id }.Order(), parentA.SourceBomItemIds);
        var parentB = Assert.Single(summary.ProductionStructure, line => line.ParentMaterialCode == "PARENT-B");
        Assert.Equal(3, parentB.Quantity);

        var demand = Assert.Single(summary.PurchaseDemand);
        Assert.Equal("1001", demand.MaterialCode);
        Assert.Equal("001", demand.UnitCode);
        Assert.Equal(9, demand.Quantity);
        Assert.Equal(64, summary.Sha256.Length);
    }

    [Fact]
    public void Build_KeepsUnassignedDuplicateRowsSeparateInProductionSummary()
    {
        var first = Item("1001", 1, null);
        var second = Item("1001", 2, null);

        var summary = BomReleaseAggregation.Build(Package([first, second]));

        Assert.Equal(2, summary.ProductionStructure.Count);
        Assert.All(summary.ProductionStructure, line => Assert.Null(line.ParentMaterialCode));
        Assert.Equal(3, Assert.Single(summary.PurchaseDemand).Quantity);
    }

    private static BomItem Item(string materialCode, decimal quantity, string? parentMaterialCode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, materialCode, "测试物料", quantity, "001", null, "M1", "W1", true)
        {
            ParentDrawingNumber = parentMaterialCode
        };

    private static ReleasePackage Package(IReadOnlyList<BomItem> items) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "RP-TEST", ReleasePackageState.Published, Guid.NewGuid(), "S-V01", "E-V01", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
        {
            Scope = ReleaseScope.StandardFormal,
            StandardBomSnapshot = items
        };
}
