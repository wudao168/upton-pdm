using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Pdm.Domain.Tests;

public sealed class BomReleaseAggregationTests
{
    [Theory]
    [InlineData(0, 1, 4)]
    [InlineData(1, 1, 3)]
    [InlineData(4, 1, 0)]
    [InlineData(6, 1, 0)]
    [InlineData(4, 3, 8)]
    public void FormalRelease_OnlyDeductsPriorLongLeadFromPurchaseDemand(decimal prior, int multiplier, decimal expected)
    {
        var items = new[] { Item("1001", 1, "A"), Item("1001", 3, "B") };
        var package = Package(items) with { WholeSetMultiplier = multiplier };
        var summary = BomReleaseAggregation.Build(package, [Item("1001", prior, "A") with { Unit = "个" }]);

        Assert.Equal(4 * multiplier, summary.ProductionStructure.Sum(line => line.Quantity));
        Assert.Equal(expected, summary.PurchaseDemand.Sum(line => line.Quantity));
        Assert.All(summary.PurchaseDemand, line => Assert.True(line.Quantity > 0));
        Assert.Equal(4, package.StandardBomSnapshot.Sum(item => item.Quantity));
    }

    [Fact]
    public void LongLeadHistory_OnlyIncludesPublishedPackagesFromSameProjectBeforeFormalPublication()
    {
        var package = Package([Item("1001", 4, "A")]);
        var prior = package with { Id = Guid.NewGuid(), Scope = ReleaseScope.StandardLongLead,
            PublishedAt = package.CreatedAt.AddDays(-1), StandardBomSnapshot = [Item("1001", 1, "A")], WholeSetMultiplier = 2 };
        var items = BomReleaseAggregation.PriorLongLeadItems(package, [prior,
            prior with { Id = Guid.NewGuid(), State = ReleasePackageState.Rejected },
            prior with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            prior with { Id = Guid.NewGuid(), PublishedAt = package.CreatedAt.AddDays(1) }]);
        Assert.Equal(2, Assert.Single(items).Quantity);
    }

    [Fact]
    public void Deduction_DoesNotCrossMaterialOrUnitAndDoesNotAffectLongLeadRelease()
    {
        var package = Package([Item("1001", 4, "A")]);
        var previous = new[] { Item("1001", 4, "A") with { Unit = "盒" }, Item("1002", 4, "A") };
        Assert.Equal(4, Assert.Single(BomReleaseAggregation.Build(package, previous).PurchaseDemand).Quantity);
        Assert.Equal(4, Assert.Single(BomReleaseAggregation.Build(package with { Scope = ReleaseScope.StandardLongLead }, [Item("1001", 4, "A")]).PurchaseDemand).Quantity);
    }

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

    [Fact]
    public void Build_AppliesWholeSetMultiplierAndSkipsNoPublishItems()
    {
        var included = Item("1001", 2, "PARENT-A");
        var excluded = Item("1002", 5, "PARENT-A") with
        {
            IsReleaseExcluded = true,
            ReleaseExclusionReason = "其他项目已发布"
        };
        var package = Package([included, excluded]) with { WholeSetMultiplier = 3 };

        var summary = BomReleaseAggregation.Build(package);

        Assert.Equal(6, Assert.Single(summary.ProductionStructure).Quantity);
        Assert.Equal("1001", Assert.Single(summary.PurchaseDemand).MaterialCode);
        Assert.Equal(6, Assert.Single(summary.PurchaseDemand).Quantity);
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
