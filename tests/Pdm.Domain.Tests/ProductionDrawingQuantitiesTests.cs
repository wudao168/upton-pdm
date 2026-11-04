using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProductionDrawingQuantitiesTests
{
    [Theory]
    [InlineData(BomKind.Standard,ReleaseScope.StandardLongLead,ReleaseScope.StandardFormal,ReleaseScope.StandardSupplement)]
    [InlineData(BomKind.NonStandard,ReleaseScope.NonStandardLongLead,ReleaseScope.NonStandardWithDrawing,ReleaseScope.NonStandardSupplement)]
    [InlineData(BomKind.Electrical,ReleaseScope.ElectricalLongLead,ReleaseScope.ElectricalFormal,ReleaseScope.ElectricalSupplement)]
    public void EarlyCreditIsConsumedAtFirstCoverageAndNotReusedAfterIncreaseDeletionOrRestore(BomKind kind,ReleaseScope earlyScope,ReleaseScope formalScope,ReleaseScope supplementScope)
    {
        var first=Item("FIRST",1) with{Kind=kind};
        var deferred=Item("DEFERRED",6) with{Kind=kind};
        var template=Package([]);
        ReleasePackage At(string number,int minutes,ReleaseScope scope,params BomItem[] items)
        {
            var package=template with{Id=Guid.NewGuid(),Number=number,Scope=scope,PublishedAt=template.PublishedAt!.Value.AddMinutes(minutes),
                StandardBomVersionId=kind==BomKind.Standard?Guid.NewGuid():null,
                NonStandardBomVersionId=kind==BomKind.NonStandard?Guid.NewGuid():null,
                ElectricalBomVersionId=kind==BomKind.Electrical?Guid.NewGuid():null};
            return kind switch{BomKind.Standard=>package with{StandardBomSnapshot=items},
                BomKind.NonStandard=>package with{NonStandardBomSnapshot=items},_=>package with{ElectricalBomSnapshot=items}};
        }
        var early=At("EARLY",0,earlyScope,deferred with{Quantity=4});
        var formal=At("FORMAL",1,formalScope,first);
        var added=At("ADD",2,supplementScope,first,deferred);
        var increased=At("INCREASE",3,supplementScope,first,deferred with{Quantity=7});
        var deleted=At("DELETE",4,supplementScope,first);
        var restored=At("RESTORE",5,supplementScope,first,deferred with{Quantity=5});
        var history=new[]{early,formal,added,increased,deleted,restored};
        ReleaseQuantityLine Line(ReleasePackage package)=>ReleaseQuantityComparison.Build(package,kind,history).Single(line=>line.MaterialCode=="DEFERRED");
        Assert.Equal((0m,4m,6m,2m),(Line(added).PreviousBaselineQuantity,Line(added).EarlyReleasedCredit,Line(added).TargetQuantity,Line(added).DemandChange));
        Assert.Equal((6m,0m,7m,1m),(Line(increased).PreviousBaselineQuantity,Line(increased).EarlyReleasedCredit,Line(increased).TargetQuantity,Line(increased).DemandChange));
        Assert.Equal((7m,0m,0m,-7m),(Line(deleted).PreviousBaselineQuantity,Line(deleted).EarlyReleasedCredit,Line(deleted).TargetQuantity,Line(deleted).DemandChange));
        Assert.Equal(kind,Line(deleted).Kind);
        Assert.Equal([deferred.Id],Line(deleted).SourceBomItemIds);
        Assert.Equal((0m,0m,5m,5m),(Line(restored).PreviousBaselineQuantity,Line(restored).EarlyReleasedCredit,Line(restored).TargetQuantity,Line(restored).DemandChange));
        Assert.Equal(2,Assert.Single(BomReleaseAggregation.BuildForHistory(added,history).PurchaseDemand).Quantity);
        Assert.Equal(1,Assert.Single(BomReleaseAggregation.BuildForHistory(increased,history).PurchaseDemand).Quantity);
        Assert.Empty(BomReleaseAggregation.BuildForHistory(deleted,history).PurchaseDemand);
        Assert.Equal(-7,BomReleaseAggregation.BuildForHistory(deleted,history).DemandChanges.Single(line=>line.MaterialCode=="DEFERRED").DemandChange);
        Assert.Equal(5,Assert.Single(BomReleaseAggregation.BuildForHistory(restored,history).PurchaseDemand).Quantity);
        var quantities=ReleaseQuantityComparison.Build(added,kind,history);
        using var archive=new ZipArchive(new MemoryStream(BomWorkbook.WriteSupplementRelease(ReleaseQuantityComparison.FrozenItems(added,kind),quantities)));
        using var stream=archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml=XDocument.Load(stream);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows=xml.Descendants(ns+"row").ToArray();
        var headers=rows[0].Elements(ns+"c").Select(cell=>cell.Value).ToArray();
        var values=rows.Single(row=>row.Elements(ns+"c").Any(cell=>cell.Value=="DEFERRED")).Elements(ns+"c").Select(cell=>cell.Value).ToArray();
        Assert.Equal("4",values[Array.IndexOf(headers,"首次提前抵扣")]);
        Assert.Equal("2",values[Array.IndexOf(headers,"新增需求")]);
        Assert.Equal("6",values[Array.IndexOf(headers,"BOM目标总量")]);
    }

    [Fact]
    public void FirstSupplementCoverageDeductsEarlyQuantityThatFormalReleaseDeferred()
    {
        var first = Item("FIRST",1);
        var deferred = Item("DEFERRED",6);
        var change = Package([first,deferred]) with {Scope=ReleaseScope.NonStandardSupplement};
        var early = change with {Id=Guid.NewGuid(),Scope=ReleaseScope.NonStandardLongLead,
            PublishedAt=change.PublishedAt!.Value.AddDays(-2),NonStandardBomSnapshot=[deferred with {Quantity=4}]};
        var formal = change with {Id=Guid.NewGuid(),Scope=ReleaseScope.NonStandardWithDrawing,
            PublishedAt=change.PublishedAt!.Value.AddDays(-1),NonStandardBomSnapshot=[first]};
        var quantity = ProductionDrawingQuantities.Build(change,[early,formal]).Single(item=>item.MaterialKey=="DEFERRED|001");
        Assert.Equal(6,quantity.TotalQuantity);
        Assert.Equal(2,quantity.NetIssueQuantity);
    }

    [Fact]
    public void SupplementDrawingUsesIncrementInsteadOfCumulativeBomAndKeepsEveryPublication()
    {
        var item = Item("M-1", 1);
        var first = Package([item]);
        var second = first with { Id = Guid.NewGuid(), Number = "SECOND", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = first.PublishedAt!.Value.AddMinutes(1), NonStandardBomSnapshot = [item with { Quantity = 3 }] };
        var third = second with { Id = Guid.NewGuid(), Number = "THIRD", PublishedAt = second.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [item with { Quantity = 7 }] };
        var history = new[] { first, second, third };
        Assert.Equal(1, Assert.Single(ProductionDrawingQuantities.Build(first, history)).NetIssueQuantity);
        var increment = Assert.Single(ProductionDrawingQuantities.Build(second, history));
        Assert.Equal(3, increment.TotalQuantity);
        Assert.Equal(1, increment.PriorReleasedQuantity);
        Assert.Equal(2, increment.NetIssueQuantity);
        Assert.Equal(4, Assert.Single(ProductionDrawingQuantities.Build(third, history)).NetIssueQuantity);
        Assert.Equal(3, second.NonStandardBomSnapshot[0].Quantity);
    }
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
        Assert.Equal("1", values[Array.IndexOf(headers, "前期已发布")]);
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
