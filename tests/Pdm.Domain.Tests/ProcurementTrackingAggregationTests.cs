using Upton.Pdm.Application;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProcurementTrackingAggregationTests
{
    [Theory]
    [InlineData(4, false)]
    [InlineData(10, true)]
    public void DirectStockIssueIsDeemedReceivedOnceAndKeepsOriginalPurchaseQuantities(int quantity, bool complete)
    {
        var issue = MovementRow("71", "DIRECT", quantity);
        var row = Assert.Single(ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), issue, issue]));
        Assert.Equal(complete, row.IsFullyReceived);
        Assert.Equal(10, row.PurchaseQuantity);
        Assert.Equal(5, row.ArrivedQuantity);
        Assert.Equal(quantity, row.WarehouseMovements.Single(m => m.Kind == "STOCKIN").Quantity);
        Assert.Equal(quantity, row.WarehouseMovements.Single(m => m.Kind == "ISSUE").Quantity);
        Assert.All(row.WarehouseMovements, m => Assert.Equal(issue.MovementDate, m.Date));
    }

    [Theory]
    [InlineData("RCV", 4, 6)]
    [InlineData("TRANSFER", 10, 0)]
    public void ExistingReceiptsOnlyLeaveAnUncoveredDirectStockBalance(string kind, int received, int deemed)
    {
        var receipt = MovementRow("31", kind, received) with { SourcePoLineId = "2" };
        var source = new[] { Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), receipt, MovementRow("71", "DIRECT", 10) };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], source);
        Assert.True(rows[0].IsFullyReceived);
        Assert.Equal(deemed, rows.SelectMany(r => r.WarehouseMovements).Where(m => m.Kind == "STOCKIN").Sum(m => m.Quantity));
        Assert.Equal(10, rows.SelectMany(r => r.WarehouseMovements).Where(m => m.Kind == "ISSUE").Sum(m => m.Quantity));
        Assert.All(rows, r => Assert.True(r.WarehouseMovements.Count(m => m.Kind is "RCV" or "TRANSFER" or "STOCKIN") <= 1));
    }

    [Fact]
    public void DirectStockWithoutPurchaseKeepsEachDocumentAndDoesNotReuseConsumedReceipts()
    {
        var at = DateTimeOffset.Parse("2026-09-09T08:00:00+08:00");
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [
            MovementRow("31", "TRANSFER", 4) with { MovementDate = at },
            MovementRow("51", "ISSUE", 4) with { MovementDate = at.AddHours(1) },
            MovementRow("71", "DIRECT", 3) with { MovementDate = at.AddHours(2) },
            MovementRow("72", "DIRECT", 3) with { MovementDate = at.AddHours(3), DocumentNumber = "DIRECT2" }]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(6, rows.SelectMany(r => r.WarehouseMovements).Where(m => m.Kind == "STOCKIN").Sum(m => m.Quantity));
        Assert.Equal(10, rows.SelectMany(r => r.WarehouseMovements).Where(m => m.Kind == "ISSUE").Sum(m => m.Quantity));
        Assert.Equal(10, rows.Sum(r => r.Quantity));
    }

    [Fact]
    public void InvalidDirectStockCannotCompleteAnotherOrganizationsOrder()
    {
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10),
            MovementRow("71", "DIRECT", 10) with { OrganizationCode = "002" },
            MovementRow("72", "DIRECT", 10) with { IsCanceled = true },
            MovementRow("73", "DIRECT", 10) with { MovementDate = null }]);
        Assert.False(rows[0].IsFullyReceived);
        Assert.Empty(rows[0].WarehouseMovements);
        Assert.Equal(10, rows.SelectMany(r => r.WarehouseMovements).Where(m => m.Kind == "STOCKIN").Sum(m => m.Quantity));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void TransferAndActualReceiptCompleteTheBatchOnlyWhenFullyCovered(int transferred, bool complete)
    {
        var receipt = MovementRow("31", "RCV", 6) with { SourcePoLineId = "2" };
        var transfer = MovementRow("41", "TRANSFER", transferred);
        var rows = ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), receipt, transfer, transfer]);
        Assert.Equal(complete, rows[0].IsFullyReceived);
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        Assert.Equal(5, rows.Sum(row => row.ArrivedQuantity));
        Assert.Equal(6 + transferred, rows.SelectMany(row => row.WarehouseMovements).Sum(row => row.Quantity));
    }

    [Fact]
    public void TransferIsSharedOnceAcrossAllOutstandingBatchesWithoutAssigningPartialStock()
    {
        var receipt = MovementRow("31", "RCV", 4) with { SourcePoLineId = "2" };
        var transfer = MovementRow("41", "TRANSFER", 5);
        var source = new[] { Pr("1", "PR1", 10), Po("2", "PO1", "1", 6), Po("3", "PO2", "1", 4), receipt, transfer };
        var partial = ProcurementTrackingAggregation.Group([Item(10)], source);
        Assert.All(partial.Where(row => !row.IsWarehouseMovementRow), row => Assert.False(row.IsFullyReceived));
        var full = ProcurementTrackingAggregation.Group([Item(10)], [.. source, transfer with { LineId = "42", MovementQuantity = 1 }]);
        Assert.All(full.Where(row => !row.IsWarehouseMovementRow), row => Assert.True(row.IsFullyReceived));
        Assert.Equal(10, full.SelectMany(row => row.WarehouseMovements).Sum(row => row.Quantity));
        Assert.Equal(10, full.Sum(row => row.PurchaseQuantity));
        Assert.Equal(2, full.SelectMany(row => row.WarehouseMovements).Count(row => row.Kind == "TRANSFER"));
        Assert.Equal(2, full.SelectMany(row => row.WarehouseMovements).Where(row => row.Kind == "TRANSFER").Select(row => row.LineId).Distinct().Count());
    }

    [Fact]
    public void TransfersCannotUseAnotherOrganizationOrCanceledRecordsOrOutboundQuantities()
    {
        var source = new[] { Pr("1", "PR1", 10), Po("2", "PO1", "1", 10),
            MovementRow("41", "TRANSFER", 10) with { OrganizationCode = "002" },
            MovementRow("42", "TRANSFER", 10) with { IsCanceled = true },
            MovementRow("43", "TRANSFER", 10) with { MovementDate = null },
            MovementRow("51", "ISSUE", 10), MovementRow("61", "MISC", 10) };
        Assert.False(ProcurementTrackingAggregation.Group([Item(10)], source)[0].IsFullyReceived);
    }

    [Fact]
    public void ExcessReceiptFromOnePoCannotCompleteADifferentPo()
    {
        var source = new[] { Pr("1", "PR1", 10), Po("2", "PO1", "1", 6), Po("3", "PO2", "1", 4),
            MovementRow("31", "RCV", 10) with { SourcePoLineId = "2" }, MovementRow("41", "TRANSFER", 1) };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], source);
        Assert.True(rows.Single(row => row.PurchaseOrderNumbers.Contains("PO1")).IsFullyReceived);
        Assert.False(rows.Single(row => row.PurchaseOrderNumbers.Contains("PO2")).IsFullyReceived);
    }

    [Fact]
    public void StockTransferAndDirectIssueRequireNoPurchaseAndPreserveTwoOutboundDocuments()
    {
        var transfer = MovementRow("41", "TRANSFER", 10);
        var issue = MovementRow("51", "ISSUE", 6);
        var misc = MovementRow("61", "MISC", 4);
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [transfer, issue, misc, issue]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "TRANSFER", "ISSUE" }, rows[0].WarehouseMovements.Select(row => row.Kind));
        Assert.Equal("MISC", Assert.Single(rows[1].WarehouseMovements).Kind);
        Assert.Equal(10, rows.SelectMany(row => row.WarehouseMovements).Where(row => row.Kind != "TRANSFER").Sum(row => row.Quantity));
        Assert.Equal(10, rows.Sum(row => row.Quantity));
        Assert.All(rows, row => Assert.Equal(0, row.PurchaseQuantity));
        var direct = Assert.Single(ProcurementTrackingAggregation.Group([Item(10)], [issue]));
        Assert.Equal(6, Assert.Single(direct.WarehouseMovements).Quantity);
    }

    private static U9ProcurementSnapshotRow MovementRow(string id, string kind, decimal quantity) => Pr(id, kind + "1", 0) with
    { RecordKind = kind, LineNumber = 10, MovementDate = DateTimeOffset.Parse("2026-09-09T00:00:00+08:00"), MovementQuantity = quantity };

    [Fact]
    public void ReceiptJoinsExactPurchaseLine_WithoutAnExtraMaterialRow()
    {
        var receipt = Pr("31", "RCV1", 0) with { RecordKind = "RCV", SourcePoLineId = "2", MovementDate = DateTimeOffset.UtcNow, MovementQuantity = 10 };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), receipt, receipt]);
        var row = Assert.Single(rows);
        Assert.Equal("PO1", Assert.Single(row.PurchaseOrderNumbers));
        Assert.Equal("RCV1", Assert.Single(row.WarehouseMovements).DocumentNumber);
        Assert.Equal(10, row.PurchaseQuantity);
        Assert.False(row.IsWarehouseMovementRow);
    }

    [Fact]
    public void MultipleReceiptsExpandOnlyTheirPurchaseBatch_AndDoNotRepeatTotals()
    {
        var receipt = Pr("31", "RCV1", 0) with { RecordKind = "RCV", SourcePoLineId = "2", MovementDate = DateTimeOffset.UtcNow, MovementQuantity = 3 };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [Pr("1", "PR1", 10),
            Po("2", "PO1", "1", 6), Po("3", "PO2", "1", 4), receipt,
            receipt with { LineId = "32", DocumentNumber = "RCV2", MovementQuantity = 3 }]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(10, rows.Sum(row => row.Quantity));
        Assert.Equal(10, rows.Sum(row => row.RequestedQuantity));
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        Assert.Equal(5, rows.Sum(row => row.ArrivedQuantity));
        Assert.Equal(6, rows.SelectMany(row => row.WarehouseMovements).Sum(row => row.Quantity));
        Assert.Empty(rows.Single(row => row.PurchaseOrderNumbers.Contains("PO2")).WarehouseMovements);
        Assert.All(rows.Where(row => row.WarehouseMovements.Count > 0), row => Assert.Contains("PO1", row.PurchaseOrderNumbers));
    }

    [Theory]
    [InlineData(null, "001", "RCV")]
    [InlineData("unknown", "001", "RCV")]
    [InlineData("2", "002", "RCV")]
    [InlineData("2", "002", "ISSUE")]
    [InlineData("2", "002", "MISC")]
    public void MissingOrMismatchedReceiptLinks_AndIssues_RemainSeparate(string? sourcePo, string org, string kind)
    {
        var movement = Pr("31", "MOV1", 0) with { RecordKind = kind, SourcePoLineId = sourcePo, OrganizationCode = org,
            MovementDate = DateTimeOffset.UtcNow, MovementQuantity = 1 };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), movement]);
        Assert.Equal(2, rows.Count);
        Assert.Empty(rows[0].WarehouseMovements);
        Assert.True(rows[1].IsWarehouseMovementRow);
    }

    [Theory]
    [InlineData("ISSUE")]
    [InlineData("MISC")]
    public void FirstOutboundSharesUniquePurchaseAndReceiptRow(string kind)
    {
        var receipt = Pr("31", "RCV1", 0) with { RecordKind = "RCV", SourcePoLineId = "2",
            MovementDate = DateTimeOffset.UtcNow, MovementQuantity = 10 };
        var issue = receipt with { RecordKind = kind, LineId = "41", DocumentNumber = "OUT1", SourcePoLineId = null };
        var row = Assert.Single(ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), receipt, issue, issue]));
        Assert.Equal(2, row.WarehouseMovements.Count);
        Assert.Equal(10, row.WarehouseMovements.Single(m => m.Kind == "RCV").Quantity);
        Assert.Equal(10, row.WarehouseMovements.Single(m => m.Kind == kind).Quantity);
        Assert.Equal(10, row.PurchaseQuantity);
        Assert.False(row.IsWarehouseMovementRow);
    }

    [Fact]
    public void IssueAndMiscRemainTwoOutboundRowsWithoutRepeatingPurchaseTotals()
    {
        var issued = Pr("41", "CRKD1", 0) with { RecordKind = "ISSUE",
            MovementDate = DateTimeOffset.UtcNow, MovementQuantity = 6 };
        var misc = issued with { RecordKind = "MISC", LineId = "51", DocumentNumber = "MIS1",
            MovementDate = issued.MovementDate.Value.AddDays(1), MovementQuantity = 4 };
        var rows = ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("2", "PO1", "1", 10), issued, misc]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("ISSUE", Assert.Single(rows[0].WarehouseMovements).Kind);
        Assert.Equal("MISC", Assert.Single(rows[1].WarehouseMovements).Kind);
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        Assert.Equal(10, rows.Sum(row => row.Quantity));
        Assert.True(rows[1].IsWarehouseMovementRow);
        Assert.Null(rows[1].Quantity);
    }

    [Fact]
    public void WarehouseDocumentsStayOnSeparateRowsWithoutMultiplyingPurchaseQuantities()
    {
        var received = Pr("31", "RCV1", 0) with { RecordKind = "RCV", MovementDate = DateTimeOffset.Parse("2026-09-08T09:30:00+08:00"), MovementQuantity = 3, MovementUnit = "个" };
        var issued = received with { RecordKind = "ISSUE", LineId = "41", DocumentNumber = "CRKD1", MovementQuantity = 2 };
        var misc = received with { RecordKind = "MISC", LineId = "51", DocumentNumber = "Mis1", MovementQuantity = 1, MovementUnit = "盒" };
        var rows = ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("2", "PO1", "1", 4), Po("3", "PO2", "1", 6),
                received, issued, misc, misc, misc with { LineId = "52", IsCanceled = true }]);
        Assert.Equal(5, rows.Count);
        Assert.Equal(10, rows.Sum(row => row.Quantity));
        Assert.Equal(10, rows.Sum(row => row.RequestedQuantity));
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        var movements = rows.Where(row => row.IsWarehouseMovementRow).ToArray();
        Assert.Equal(3, movements.Length);
        Assert.All(movements, row =>
        {
            Assert.Single(row.WarehouseMovements);
            Assert.Null(row.Quantity);
            Assert.Empty(row.PurchaseOrderNumbers);
            Assert.Equal(0, row.PurchaseQuantity);
        });
        Assert.Equal(new[] { "ISSUE", "MISC", "RCV" }, movements.SelectMany(row => row.WarehouseMovements).Select(row => row.Kind).OrderBy(kind => kind));
        Assert.Equal(2, movements.Single(row => row.WarehouseMovements[0].Kind == "ISSUE").WarehouseMovements[0].Quantity);
        Assert.Equal("盒", movements.Single(row => row.WarehouseMovements[0].Kind == "MISC").WarehouseMovements[0].Unit);
    }

    [Theory]
    [InlineData(0, "开立", "开立")]
    [InlineData(1, "核准中", "审核中")]
    [InlineData(2, "已核准", "已核准")]
    [InlineData(3, "自然关闭", "自然关闭")]
    [InlineData(4, "短缺关闭", "短缺关闭")]
    [InlineData(5, "超额关闭", "超额关闭")]
    public void StatusLabelsMatchVerifiedU9NativeEnums(int status, string prStatus, string poStatus)
    {
        var row = Assert.Single(ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10) with { LineStatus = status }, Po("1", "PO1", "1", 10) with { LineStatus = status }]));
        Assert.Equal(prStatus, row.PurchaseRequisitionStatus);
        Assert.Equal(poStatus, row.PurchaseOrderStatus);
        Assert.Equal(prStatus, row.Details.Single(detail => detail.Kind == "请购").LineStatus);
        Assert.Equal(poStatus, row.Details.Single(detail => detail.Kind == "采购").LineStatus);
        Assert.All(row.Details, detail => Assert.Equal(status, detail.RawLineStatus));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void ArrivalQuantityDoesNotRewriteNativePurchaseStatus(int arrived)
    {
        var row = Assert.Single(ProcurementTrackingAggregation.Group([Item(10)],
            [Pr("1", "PR1", 10), Po("1", "PO1", "1", 10) with { ArrivedQuantity = arrived }]));
        Assert.Equal("已核准", row.PurchaseOrderStatus);
        Assert.Equal(10, row.PurchaseQuantity);
        Assert.Equal(arrived, row.ArrivedQuantity);
    }

    [Fact]
    public void CreationDateComesFromPrAndBuyerFromItsOrderWithoutChangingQuantities()
    {
        var created = DateTimeOffset.Parse("2026-09-08T09:30:00+08:00");
        var pr = Pr("1", "PR1", 10) with { SourceCreatedAt = created, BusinessDate = created.AddDays(-2) };
        var rows = ProcurementTrackingAggregation.Group([Item(10)], [pr,
            Po("1", "PO1", "1", 4) with { BuyerName = "张永珊" },
            Po("2", "PO2", "1", 6) with { BuyerName = "孟丹" }]);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(created, row.PurchaseRequisitionCreatedAt));
        Assert.Equal(new[] { "张永珊", "孟丹" }, rows.Select(row => row.BuyerName));
        Assert.Equal(10, rows.Sum(row => row.RequestedQuantity));
        Assert.Equal(10, rows.Sum(row => row.PurchaseQuantity));
        var missing = Assert.Single(ProcurementTrackingAggregation.Group([Item(1)], [Pr("1", "PR1", 1)]));
        Assert.Null(missing.PurchaseRequisitionCreatedAt);
        Assert.Null(missing.BuyerName);
    }

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
