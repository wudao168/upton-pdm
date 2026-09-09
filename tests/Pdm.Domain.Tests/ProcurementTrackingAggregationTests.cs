using Upton.Pdm.Application;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProcurementTrackingAggregationTests
{
    [Fact]
    public void UnrequestedMaterialMergesOccurrencesButNotDifferentCodes()
    {
        var rows = ProcurementTrackingAggregation.Group([Item(2), Item(3), Item(1) with { MaterialCode = "B" }], []);
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Quantity);
        Assert.Equal("未请购", rows[0].PurchaseRequisitionStatus);
        Assert.Equal(6, rows.Sum(row => row.Quantity));
    }

    [Fact]
    public void SamePrAndSameStatusMergeDifferentPrsStaySeparate()
    {
        var rows = ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 2), Pr("2", "PR1", 3), Pr("3", "PR2", 5)]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].RequestedQuantity);
        Assert.Equal(5, rows[1].RequestedQuantity);
        Assert.Null(rows[1].Quantity);
        Assert.Equal(10, rows.Sum(row => row.Quantity));
    }

    [Fact]
    public void SamePrWithDifferentStatesSplits()
    {
        var rows = ProcurementTrackingAggregation.Group([Item(5)],
            [Pr("1", "PR1", 2), Pr("2", "PR1", 3) with { LineStatus = 1 }]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.PurchaseRequisitionStatus).Distinct().Count());
    }

    [Fact]
    public void MultipleOrdersNeverMultiplyPrOrDemandAndDuplicateSourceIsCountedOnce()
    {
        var po = Po("2", "PO2", "1", 6);
        var rows = ProcurementTrackingAggregation.Group([Item(4), Item(6)],
            [Pr("1", "PR1", 10), Po("1", "PO1", "1", 4), po, po]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows.Sum(row => row.Quantity));
        Assert.Equal(10, rows.Sum(row => row.RequestedQuantity));
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        Assert.Equal(5, rows.Sum(row => row.ArrivedQuantity));
        Assert.Null(rows[1].RequestedQuantity);
    }

    [Fact]
    public void SameOrderAndDateMergeButDifferentDatesOrArrivalStatesSplit()
    {
        var first = Po("1", "PO1", "1", 2);
        var rows = ProcurementTrackingAggregation.Group([Item(8)], [Pr("1", "PR1", 8), first,
            first with { LineId = "2", LineNumber = 2 },
            first with { LineId = "3", DeliveryDate = first.DeliveryDate!.Value.AddDays(1) },
            first with { LineId = "4", ArrivedQuantity = 2 }]);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.PurchaseQuantity == 4);
        Assert.Equal(8, rows.Sum(row => row.PurchaseQuantity));
    }

    [Fact]
    public void CanceledOrdersAndUnlinkedOrdersRemainSeparateAndDoNotStealPr()
    {
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [Pr("1", "PR1", 10),
            Po("1", "PO1", "1", 10) with { IsCanceled = true }, Po("2", "PO2", null, 3)]);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.PurchaseOrderStatus == "已取消" && row.PurchaseQuantity == 0 && row.ArrivedQuantity == 0);
        Assert.Contains(rows, row => row.PurchaseRequisitionStatus == "未关联请购" && row.PurchaseRequisitionNumbers.Count == 0);
        Assert.Equal(3, rows.Sum(row => row.PurchaseQuantity));
        Assert.Equal(10, rows.Sum(row => row.RequestedQuantity));
    }

    [Fact]
    public void PartialOrderRetainsUnpurchasedPrAndAnyLateLineWarnsEvenWhenMaxDatesMaskIt()
    {
        var pr = Pr("1", "PR1", 6);
        var rows = ProcurementTrackingAggregation.Group([Item(12)], [pr,
            Pr("2", "PR1", 6) with { DeliveryDate = pr.DeliveryDate!.Value.AddDays(10) },
            Po("1", "PO1", "1", 4), Po("2", "PO1", "2", 6)]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.PurchaseOrderStatus == "未采购");
        Assert.True(rows.Single(row => row.PurchaseOrderNumbers.Count > 0).HasDeliveryDelay);
        Assert.Equal(12, rows.Sum(row => row.RequestedQuantity));
    }

    private static ProjectProcurementTrackingItem Item(decimal quantity) => new(1, "P1", "P1-1", "A", "平垫", "M4", null, "国优",
        quantity, "标准件", "R1", [], "未请购", null, [], "未采购", 0, 0, null, null, null, 0, 0, []);
    private static U9ProcurementSnapshotRow Pr(string id, string number, decimal quantity) => new(Guid.Empty, "001", "PR", id, null,
        number, int.Parse(id), 2, false, null, "A", "平垫", "M4", "国优", "P1", null, "P1-1", quantity, quantity,
        0, 0, null, DateTimeOffset.Parse("2026-09-10T00:00:00+08:00"), null, DateTimeOffset.UtcNow);
    private static U9ProcurementSnapshotRow Po(string id, string number, string? prId, decimal quantity) => Pr(id, number, 0) with
    { RecordKind = "PO", SourcePrLineId = prId, PurchaseQuantity = quantity, ArrivedQuantity = quantity / 2,
        DeliveryDate = DateTimeOffset.Parse("2026-09-12T00:00:00+08:00") };
}
