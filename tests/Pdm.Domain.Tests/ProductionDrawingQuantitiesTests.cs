using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProductionDrawingQuantitiesTests
{
    [Fact]
    public void Quantities_ApplyMultiplierDeductEarlyReleaseAndKeepFrozenSingleSetUnchanged()
    {
        var item = Item("M-1", 2);
        var package = Package([item]) with { WholeSetMultiplier = 3 };
        var prior = package with { Id = Guid.NewGuid(), Scope = ReleaseScope.NonStandardLongLead,
            WholeSetMultiplier = 2, NonStandardBomSnapshot = [item with { Quantity = 1 }], PublishedAt = package.PublishedAt!.Value.AddDays(-1) };

        var quantity = Assert.Single(ProductionDrawingQuantities.Build(package, [prior]));

        Assert.Equal(2, quantity.SingleSetQuantity);
        Assert.Equal(3, quantity.WholeSetMultiplier);
        Assert.Equal(6, quantity.TotalQuantity);
        Assert.Equal(2, quantity.PriorReleasedQuantity);
        Assert.Equal(4, quantity.NetIssueQuantity);
        Assert.Equal(2, package.NonStandardBomSnapshot[0].Quantity);
        Assert.Equal(BomReleaseAggregation.Build(package, BomReleaseAggregation.PriorLongLeadItems(package, [prior]))
            .PurchaseDemand.Single().Quantity, quantity.NetIssueQuantity);
    }

    [Fact]
    public void Quantities_DeduplicateRowsAggregateOnlyMatchingMaterialAndUnitAndSkipExcludedRows()
    {
        var first = Item("M-1", 2);
        var second = Item("m-1", 3) with { Unit = "个" };
        var package = Package([first, first, second, Item("M-1", 4) with { Unit = "盒" }, Item("M-2", 9),
            Item("EXCLUDED", 100) with { IsReleaseExcluded = true }, Item("PENDING", 100) with { IsPendingRemoval = true },
            Item("MANUAL", 100) with { IsManuallyExcluded = true }]);

        var quantities = ProductionDrawingQuantities.Build(package, []);

        Assert.Equal(3, quantities.Count);
        Assert.Equal(5, quantities.Single(item => item.MaterialKey == "M-1|001").TotalQuantity);
        Assert.Equal(2, quantities.Single(item => item.MaterialKey == "M-1|001").SourceBomItemIds.Count);
        Assert.Equal(4, quantities.Single(item => item.MaterialKey == "M-1|004").TotalQuantity);
        Assert.Equal(9, quantities.Single(item => item.MaterialKey == "M-2|001").TotalQuantity);
    }

    [Fact]
    public void Quantities_IgnoreLaterRejectedOtherProjectHistoryAndClampOverIssuedDemand()
    {
        var item = Item("M-1", 2);
        var package = Package([item]);
        var prior = package with { Id = Guid.NewGuid(), Scope = ReleaseScope.NonStandardLongLead,
            PublishedAt = package.PublishedAt!.Value.AddDays(-1), NonStandardBomSnapshot = [item with { Quantity = 3 }] };
        var history = new[] { prior, prior with { Id = Guid.NewGuid(), PublishedAt = package.PublishedAt.Value.AddDays(1) },
            prior with { Id = Guid.NewGuid(), State = ReleasePackageState.Rejected },
            prior with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() } };

        var quantity = Assert.Single(ProductionDrawingQuantities.Build(package, history));

        Assert.Equal(3, quantity.PriorReleasedQuantity);
        Assert.Equal(0, quantity.NetIssueQuantity);
        var supplement = Assert.Single(ProductionDrawingQuantities.Build(package with { Scope = ReleaseScope.NonStandardSupplement }, history));
        Assert.Equal(0, supplement.PriorReleasedQuantity);
        Assert.Equal(2, supplement.NetIssueQuantity);
    }

    [Fact]
    public void Quantities_LegacyMechanicalSnapshotAndStableTrackingIdentityRemainSupported()
    {
        var item = Item("M-NEW", 2);
        var package = Package([]) with { MechanicalBomSnapshot = [item], WholeSetMultiplier = 0 };
        var prior = package with { Id = Guid.NewGuid(), Scope = ReleaseScope.NonStandardLongLead,
            PublishedAt = package.PublishedAt!.Value.AddDays(-1), NonStandardBomSnapshot = [item with { DrawingNumber = "M-OLD", Quantity = 1 }] };

        var quantity = Assert.Single(ProductionDrawingQuantities.Build(package, [prior]));

        Assert.Equal(1, quantity.WholeSetMultiplier);
        Assert.Equal(1, quantity.PriorReleasedQuantity);
        Assert.Equal(1, quantity.NetIssueQuantity);
    }

    [Fact]
    public void Workbook_RepeatedDrawingReferencesExportOneMaterialWithAllFourQuantities()
    {
        var item = Item("M-1", 2);
        var package = Package([item]) with { WholeSetMultiplier = 3 };
        var prior = package with { Id = Guid.NewGuid(), Scope = ReleaseScope.NonStandardLongLead,
            WholeSetMultiplier = 1, PublishedAt = package.PublishedAt!.Value.AddDays(-1), NonStandardBomSnapshot = [item with { Quantity = 1 }] };
        var quantities = ProductionDrawingQuantities.Build(package, [prior]);
        var bytes = BomWorkbook.WriteProductionRelease([item, item], [.. quantities, .. quantities]);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").ToArray();
        var headers = rows[0].Elements(ns + "c").Select(cell => cell.Value).ToArray();
        var values = rows[1].Elements(ns + "c").Select(cell => cell.Value).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("6", values[Array.IndexOf(headers, "BOM总量")]);
        Assert.Equal("1", values[Array.IndexOf(headers, "已提前发布")]);
        Assert.Equal("5", values[Array.IndexOf(headers, "本次新增下发")]);
        Assert.Equal("2", values[Array.IndexOf(headers, "单套数量")]);
        Assert.Equal("3", values[Array.IndexOf(headers, "整套数量")]);
        Assert.Throws<PdmRuleException>(() => BomWorkbook.WriteProductionRelease([item], []));
    }

    private static BomItem Item(string code, decimal quantity) => new(Guid.NewGuid(), Guid.NewGuid(),
        BomKind.NonStandard, 1, code, "生产零件", quantity, "001", null, null, "A", true);

    private static ReleasePackage Package(IReadOnlyList<BomItem> items) => new(Guid.NewGuid(), Guid.NewGuid(),
        "RP-QUANTITIES", ReleasePackageState.Published, Guid.NewGuid(), "N-V01", "E-V01", [],
        DateTimeOffset.Parse("2026-10-14T10:00:00+08:00"), DateTimeOffset.Parse("2026-10-14T11:00:00+08:00"), null)
    { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = items };
}
