using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomSummaryProjectionTests
{
    [Fact]
    public void GroupKeepsPublishedSourcesSeparateFromNewSameCodeSources()
    {
        var old = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "SAME", "同料号", 1, "001", null, "MODEL", "W1", true)
            { DisplayRevision = "S-P-V07" };
        var first = old with { Id = Guid.NewGuid(), DisplayRevision = "未发布", Quantity = 2 };
        var second = first with { Id = Guid.NewGuid(), Quantity = 3 };
        var groups = BomSummaryProjection.Group([old, first, second]);
        Assert.Equal(2, groups.Count);
        Assert.Equal(1, groups[0].Quantity);
        Assert.Equal(5, groups[1].Quantity);
        var edit = new BomSummaryEdit(groups[1].SummaryItemIds!, groups[1].SummaryFingerprint!, ["remark"],
            new(1, "SAME", "同料号", 5, "001", null, "MODEL", "W1", true, Remark: "新增部分"));
        var saved = BomSummaryProjection.Expand([old, first, second], [edit], []);
        Assert.Null(saved.Single(row => row.Id == old.Id).Remark);
        Assert.All(saved.Where(row => row.Id != old.Id), row => Assert.Equal("新增部分", row.Remark));
    }
    [Fact]
    public void FlatSummaryRetainsFractionalPrecisionForDivisibleUnits()
    {
        var project=Guid.NewGuid();
        var first=new BomItem(Guid.NewGuid(),project,BomKind.Standard,1,"ROLL","可拆分卷料",1,"005",null,null,"W1",true);
        var second=first with {Id=Guid.NewGuid(),Sequence=2};var group=BomSummaryProjection.Group([first,second]).Single();
        var input=new BomItemInput(1,first.DrawingNumber,first.Name,2.5m,"005",null,null,"W1",true);
        var result=BomSummaryProjection.Expand([first,second],[new(group.SummaryItemIds!,group.SummaryFingerprint!,["quantity"],input,
            new Dictionary<Guid,decimal>{{first.Id,1},{second.Id,1.5m}})],[]);
        Assert.Equal(2.5m, Assert.Single(result).Quantity);
        Assert.True(result[0].IsSummaryQuantity);
    }
    [Fact]
    public void HierarchyCountsCombineDuplicateMaterialsAndUseCurrentValidationRules()
    {
        var projectId = Guid.NewGuid();
        var first = new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1, "SAME", "物料", 1, "001", null, "MODEL", "A", true);
        var second = first with { Id = Guid.NewGuid(), Sequence = 2, IsComplete = false };
        var excluded = first with { Id = Guid.NewGuid(), DrawingNumber = "DELETED", IsManuallyExcluded = true };
        var rules = new BomValidationRules(["brand"], [], []);
        var missing = BomHierarchyMetric.Count(projectId, BomKind.Standard, [first, second, excluded], rules);
        Assert.Equal(1, missing.ItemCount);
        Assert.Equal(1, missing.EffectiveCount);
        Assert.Equal(1, missing.UnresolvedCount);
        var complete = BomHierarchyMetric.Count(projectId, BomKind.Standard, [first with { Brand = "UPTON" }, second with { Brand = "UPTON" }], rules);
        Assert.Equal(1, complete.ItemCount);
        Assert.Equal(0, complete.UnresolvedCount);
        Assert.NotNull(Assert.Single(BomSummaryProjection.Group([first, second])).SummaryFingerprint);
    }
    [Fact]
    public void InventoryReplacement_MarkerIsSavedPerInstanceAndExposedOnTheSummary()
    {
        var projectId = Guid.NewGuid();
        var first = new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1, "OLD", "原物料", 2, "001", null, "OLD-MODEL", "W1", true);
        var second = first with { Id = Guid.NewGuid(), Sequence = 2, Quantity = 3 };
        var group = Assert.Single(BomSummaryProjection.Group([first, second]));
        var edit = new BomItemInput(1, "STOCK", "库存物料", 5, "001", null, "STOCK-MODEL", "W1", true,
            InventoryReplacementMaterialCode: "STOCK");
        var expanded = BomSummaryProjection.Expand([first, second], [new(group.SummaryItemIds!, group.SummaryFingerprint!,
            ["drawingNumber", "name", "specification", "inventoryReplacementMaterialCode"], edit)], []);
        Assert.All(expanded, item => Assert.Equal("STOCK", item.InventoryReplacementMaterialCode));
        Assert.Equal(5, expanded.Sum(item => item.Quantity));
        var stockItem = first with { Sequence = 2, DrawingNumber = "STOCK", InventoryReplacementMaterialCode = "STOCK" };
        var unmarked = second with { Sequence = 1, DrawingNumber = "STOCK" };
        Assert.Equal("STOCK", Assert.Single(BomSummaryProjection.Group([unmarked, stockItem])).InventoryReplacementMaterialCode);
        var kept = BomSummaryProjection.Expand([stockItem], [], []);
        Assert.Equal("STOCK", Assert.Single(kept).InventoryReplacementMaterialCode);
    }

    [Fact]
    public void GroupAndPatch_PreserveInstanceIdentityAndUneditedAttributes()
    {
        var projectId = Guid.NewGuid();
        var items = Enumerable.Range(0, 3100).Select(index => new BomItem(Guid.NewGuid(), projectId,
            BomKind.Standard, index + 1, $"CODE-{index % 31}", "物料", index % 2 + 1, "001", "钢", "MODEL", "W1", true)
        { SourceDocumentId = Guid.NewGuid(), SourceInstancePath = $"ROOT/{index}", Brand = $"BRAND-{index}", ParentDrawingNumber = "ROOT" }).ToArray();
        var groups = BomSummaryProjection.Group(items);
        Assert.Equal(31, groups.Count);
        Assert.Equal(items.Sum(item => item.Quantity), groups.Sum(item => item.Quantity));
        Assert.Equal(3100, groups.Sum(item => item.SummaryItemIds!.Count));
        var group = groups[0];
        var input = new BomItemInput(1, group.DrawingNumber, group.Name, group.Quantity, group.Unit, null, "NEW", "W1", true);
        var expanded = BomSummaryProjection.Expand(items,
            [new(group.SummaryItemIds!, group.SummaryFingerprint!, ["specification"], input)], []);
        var byId = expanded.ToDictionary(item => item.Id!.Value);
        foreach (var original in items)
        {
            var current = byId[original.Id];
            Assert.Equal(original.SourceInstancePath, current.SourceInstancePath);
            Assert.Equal(original.SourceDocumentId, current.SourceDocumentId);
            Assert.Equal(original.Brand, current.Brand);
            Assert.Equal(original.Quantity, current.Quantity);
            Assert.Equal(group.SummaryItemIds!.Contains(original.Id) ? "NEW" : original.Specification, current.Specification);
        }
        Assert.Throws<PdmConflictException>(() => BomSummaryProjection.Expand(
            items.Select(item => item.Id == group.Id ? item with { Quantity = 999 } : item).ToArray(),
            [new(group.SummaryItemIds!, group.SummaryFingerprint!, ["specification"], input)], []));
    }

    [Fact]
    public void TotalQuantityIsSavedOnceWithoutAllocatingBackToOccurrences()
    {
        var projectId = Guid.NewGuid();
        var items = Enumerable.Range(0, 2).Select(index => new BomItem(Guid.NewGuid(), projectId, BomKind.NonStandard,
            index + 1, "SAME", "物料", 1, "001", "钢", "MODEL", "W1", true)).ToArray();
        var group = Assert.Single(BomSummaryProjection.Group(items));
        var input = new BomItemInput(1, "SAME", "物料", 3, "001", "钢", "MODEL", "W1", true);
        var edit = new BomSummaryEdit(group.SummaryItemIds!, group.SummaryFingerprint!, ["quantity"], input);
        var expanded = BomSummaryProjection.Expand(items, [edit], []);
        Assert.Equal(3, Assert.Single(expanded).Quantity);
        Assert.True(expanded[0].IsSummaryQuantity);
        Assert.Equal(items[0].Id, expanded[0].Id);
        Assert.All(items, item => Assert.Equal(1, item.Quantity));
        Assert.Throws<PdmRuleException>(()=>BomSummaryProjection.Expand(items,[edit with {Values=input with {Quantity=1.5m}}],[]));
    }

    [Fact]
    public void FourLegacyHalfOccurrencesCanBeChangedFromTwoToFourWithoutRoundingOrDistribution()
    {
        var project=Guid.NewGuid();
        var source=Enumerable.Range(0,4).Select(index=>new BomItem(Guid.NewGuid(),project,BomKind.Standard,index+1,"01021000173","DLVC-L150",0.5m,"001",null,"DLVC-L150","W1",true)
            {Source="Auto",SourceDocumentId=Guid.NewGuid(),SourceInstancePath=$"ASM/PART-{index}"}).ToArray();
        var group=Assert.Single(BomSummaryProjection.Group(source));Assert.Equal(2,group.Quantity);
        var saved=BomSummaryProjection.Expand(source,[new(group.SummaryItemIds!,group.SummaryFingerprint!,["quantity"],new(1,group.DrawingNumber,group.Name,4,"001",null,group.Specification,"W1",true))],[]);
        Assert.Equal(4,Assert.Single(saved).Quantity);Assert.True(saved[0].IsSummaryQuantity);
        Assert.Equal(source[0].SourceDocumentId,saved[0].SourceDocumentId);
        Assert.All(source,row=>Assert.Equal(0.5m,row.Quantity));
    }

    [Fact]
    public void Group_ReusesUniqueCodeForMatchingUncodedSourceInstances()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "CODE", "物料", 1, "001", null, "M", "W1", true)
            { SourceDocumentId = Guid.NewGuid(), Brand = "BRAND" };
        var uncoded = item with { Id = Guid.NewGuid(), Sequence = 2, DrawingNumber = "" };
        var group = Assert.Single(BomSummaryProjection.Group([uncoded, item]));
        Assert.Equal("CODE", group.DrawingNumber);
        Assert.Equal(2, group.SummaryItemIds!.Count);
        Assert.Equal(2, group.Quantity);
    }

    [Fact]
    public void GroupSave_PreservesOrderAndOnlyRemovesRequestedKitGroups()
    {
        var first = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "FIRST", "物料", 1, "001", null, "M", "W1", true);
        var second = first with { Id = Guid.NewGuid(), Sequence = 2, DrawingNumber = "SECOND" };
        var kit = first with { Id = Guid.NewGuid(), Sequence = 3, DrawingNumber = "KIT", EngineeringKitReferenceId = Guid.NewGuid() };
        var groups = BomSummaryProjection.Group([first, second, kit]);
        var removed = groups.Single(item => item.Id == kit.Id);
        var expanded = BomSummaryProjection.Expand([first, second, kit],
            [new(removed.SummaryItemIds!, removed.SummaryFingerprint!, ["removeEngineeringKit"], new(1, "KIT", "物料", 1, "001", null, "M", "W1", true))], [],
            [second.Id, first.Id]);
        Assert.Equal(new[] { second.Id, first.Id }, expanded.Select(item => item.Id!.Value));
        Assert.Equal(new[] { 1, 2 }, expanded.Select(item => item.Sequence));
    }

    [Fact]
    public void Group_SeparatesRevisionsAndDeletedSourceOwnership()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "SAME", "物料", 1, "001", null, "M", "W1", true);
        Assert.Equal(4, BomSummaryProjection.Group([item, item with { Id = Guid.NewGuid(), Revision = "W2" },
            item with { Id = Guid.NewGuid(), IsManuallyExcluded = true },
            item with { Id = Guid.NewGuid(), IsManuallyExcluded = true, SourceDocumentId = Guid.NewGuid() }]).Count);
    }
    [Fact]
    public void Group_KeepsSameAccessoryCodeWithItsOwnMainAndPreservesManualQuantity()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "SHARED", "共用配件", 2, "001", null, "M", "W1", true);
        var mainA = Guid.NewGuid(); var mainB = Guid.NewGuid();
        var rows = new[] { item with { RelationMainBomItemId = mainA, Source = "MaterialRelation" },
            item with { Id = Guid.NewGuid(), Quantity = 3, RelationMainBomItemId = mainB, Source = "MaterialRelation" },
            item with { Id = Guid.NewGuid(), Quantity = 5 } };
        var grouped = BomSummaryProjection.Group(rows);
        Assert.Equal(3, grouped.Count);
        Assert.Equal(10, grouped.Sum(row => row.Quantity));
        Assert.Equal(rows.Select(row => row.RelationMainBomItemId), grouped.Select(row => row.RelationMainBomItemId));
        Assert.Equal(3, BomSummaryProjection.Expand(rows, [], []).Count);
    }

    [Fact]
    public void Save_CompatibilityKitMarkerDoesNotOverwriteSourceAndRejectsRealSourceChanges()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "SAME", "已有物料", 1, "001", null, "M", "W1", true);
        var group = BomSummaryProjection.Group([item]).Single();
        var input = new BomItemInput(1, "SAME", "已有物料", 2, "001", null, "M", "W1", true);
        var edit = new BomSummaryEdit(group.SummaryItemIds!, group.SummaryFingerprint!, ["quantity", "engineeringKitReference"], input);
        Assert.Equal(2, BomSummaryProjection.Expand([item], [edit], []).Single().Quantity);
        Assert.Throws<PdmRuleException>(() => BomSummaryProjection.Expand([item], [edit with { Values = input with { EngineeringKitReferenceId = Guid.NewGuid() } }], []));
        Assert.Throws<PdmRuleException>(() => BomSummaryProjection.Expand([item], [edit with { Fields = ["unknownField"] }], []));
    }

}
