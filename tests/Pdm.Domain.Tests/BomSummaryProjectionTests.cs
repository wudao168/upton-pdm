using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomSummaryProjectionTests
{
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
    public void TotalQuantity_UsesDatabasePrecisionAndExactRemainder()
    {
        var projectId = Guid.NewGuid();
        var items = Enumerable.Range(0, 3).Select(index => new BomItem(Guid.NewGuid(), projectId, BomKind.NonStandard,
            index + 1, "SAME", "物料", 1, "001", "钢", "MODEL", "W1", true)).ToArray();
        var group = Assert.Single(BomSummaryProjection.Group(items));
        var input = new BomItemInput(1, "SAME", "物料", 1, "001", "钢", "MODEL", "W1", true);
        var expanded = BomSummaryProjection.Expand(items,
            [new(group.SummaryItemIds!, group.SummaryFingerprint!, ["quantity"], input)], []);
        Assert.Equal(new[] { 0.3333m, 0.3333m, 0.3334m }, expanded.Select(item => item.Quantity));
        Assert.Equal(1, expanded.Sum(item => item.Quantity));
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
}
