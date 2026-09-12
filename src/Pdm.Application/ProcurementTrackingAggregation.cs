namespace Upton.Pdm.Application;

public static class ProcurementTrackingAggregation
{
    // Demand belongs to the material, PR quantity to the source line, and PO quantities to the batch.
    // Null on continuation rows means "already counted", never zero or proportional allocation.
    public static IReadOnlyList<ProjectProcurementTrackingItem> Group(
        IReadOnlyList<ProjectProcurementTrackingItem> items, IReadOnlyList<U9ProcurementSnapshotRow> source)
    {
        var result = new List<ProjectProcurementTrackingItem>();
        foreach (var material in items.GroupBy(row => (row.ProjectCode, row.SubprojectCode, Code(row.MaterialCode))))
        {
            var first = material.First();
            var rows = source.Where(row => Code(row.MaterialCode) == material.Key.Item3)
                .DistinctBy(row => (Code(row.OrganizationCode), row.RecordKind, Code(row.LineId))).ToArray();
            var prs = rows.Where(row => row.RecordKind == "PR").ToArray();
            var pos = rows.Where(row => row.RecordKind == "PO").ToArray();
            var movements = rows.Where(row =>
                    row.RecordKind is U9ProcurementRecordKinds.Receipt or U9ProcurementRecordKinds.MaterialIssue or U9ProcurementRecordKinds.MiscShipment or U9ProcurementRecordKinds.TransferReceipt or U9ProcurementRecordKinds.DirectStockIssue
                    && !row.IsCanceled && row.MovementDate.HasValue && row.MovementQuantity.HasValue)
                .OrderBy(row => row.MovementDate).ThenBy(row => row.DocumentNumber).ThenBy(row => row.LineNumber).ToArray();
            var linkedMovements = new HashSet<U9ProcurementSnapshotRow>();
            // Consume earlier project receipts once. Only the uncovered part of a direct
            // stock issue is deemed received; later receipts cannot erase that history.
            var deemedReceipts = new Dictionary<U9ProcurementSnapshotRow, WarehouseMovementDetail>();
            foreach (var organization in movements.GroupBy(row => Code(row.OrganizationCode)))
            {
                decimal available = 0;
                foreach (var movement in organization.OrderBy(row => row.MovementDate)
                    .ThenBy(row => row.RecordKind is U9ProcurementRecordKinds.Receipt or U9ProcurementRecordKinds.TransferReceipt ? 0 : 1))
                {
                    var quantity = movement.MovementQuantity!.Value;
                    if (movement.RecordKind is U9ProcurementRecordKinds.Receipt or U9ProcurementRecordKinds.TransferReceipt)
                        available = Math.Max(0, available + quantity);
                    else
                    {
                        var uncovered = Math.Max(0, quantity - available);
                        if (movement.RecordKind == U9ProcurementRecordKinds.DirectStockIssue && uncovered > 0)
                            deemedReceipts.Add(movement, Movement(movement) with { Kind = U9ProcurementRecordKinds.DeemedStockReceipt, Quantity = uncovered });
                        available = Math.Max(0, available - quantity);
                    }
                }
            }
            IReadOnlyList<WarehouseMovementDetail> MovementDetails(U9ProcurementSnapshotRow row) =>
                deemedReceipts.TryGetValue(row, out var deemed) ? [deemed, Movement(row)] : [Movement(row)];
            var purchaseRowIndexes = new List<int>();
            decimal Received(U9ProcurementSnapshotRow po) => movements.Where(row => row.RecordKind == U9ProcurementRecordKinds.Receipt
                && Code(row.SourcePoLineId) == Code(po.LineId) && Code(row.OrganizationCode) == Code(po.OrganizationCode))
                .Sum(row => row.MovementQuantity!.Value);
            // Unlinked project stock must cover the whole organization's outstanding balance.
            // Do not allocate the same transfer to every PO or guess which partial batch it covers.
            var coveredOrganizations = pos.Where(po => !po.IsCanceled).GroupBy(po => Code(po.OrganizationCode))
                .Where(group => movements.Where(row => row.RecordKind == U9ProcurementRecordKinds.TransferReceipt
                        && Code(row.OrganizationCode) == group.Key).Sum(row => row.MovementQuantity!.Value)
                        + deemedReceipts.Where(pair => Code(pair.Key.OrganizationCode) == group.Key).Sum(pair => pair.Value.Quantity)
                    >= group.Sum(po => Math.Max(0, po.PurchaseQuantity - Received(po))))
                .Select(group => group.Key).ToHashSet();
            var pairs = new List<(U9ProcurementSnapshotRow? Pr, U9ProcurementSnapshotRow? Po)>();
            foreach (var po in pos)
            {
                var pr = prs.SingleOrDefault(pr => Code(pr.LineId) == Code(po.SourcePrLineId)
                    && Code(pr.OrganizationCode) == Code(po.OrganizationCode));
                pairs.Add((pr, po));
            }
            foreach (var pr in prs)
            {
                var linked = pairs.Where(pair => pair.Pr == pr).Select(pair => pair.Po!).ToArray();
                if (linked.Length == 0 || (!pr.IsCanceled
                    && linked.Where(po => !po.IsCanceled).Sum(po => po.PurchaseQuantity) < pr.RequestedQuantity))
                    pairs.Add((pr, null));
            }
            if (pairs.Count == 0) pairs.Add((null, null));
            var countedPr = new HashSet<(string, string)>();
            var firstBatch = true;
            foreach (var batch in pairs.GroupBy(pair => new
            {
                PrNumber = Code(pair.Pr?.DocumentNumber),
                PrStatus = Status(pair.Pr, pair.Po is null ? "未请购" : "未关联请购"),
                PoNumber = Code(pair.Po?.DocumentNumber),
                PoStatus = Status(pair.Po, "未采购"),
                Arrival = Arrival(pair.Po),
                Delivery = pair.Po?.DeliveryDate?.Date,
                Organization = Code(pair.Pr?.OrganizationCode ?? pair.Po?.OrganizationCode)
            }).OrderBy(batch => batch.Key.PrNumber).ThenBy(batch => batch.Key.PoNumber).ThenBy(batch => batch.Key.Delivery))
            {
                var batchPr = batch.Where(pair => pair.Pr is not null).Select(pair => pair.Pr!).Distinct().ToArray();
                var batchPo = batch.Where(pair => pair.Po is not null).Select(pair => pair.Po!).Distinct().ToArray();
                var newPr = batchPr.Where(pr => countedPr.Add((Code(pr.OrganizationCode), Code(pr.LineId)))).ToArray();
                var activePr = newPr.Where(pr => !pr.IsCanceled).ToArray();
                var activePo = batchPo.Where(po => !po.IsCanceled).ToArray();
                var batchRow = first with
                {
                    Sequence = result.Count + 1,
                    Quantity = firstBatch ? material.Sum(row => row.Quantity) : null,
                    Remark = Join(material.Select(row => row.Remark)),
                    BomKind = Join(material.Select(row => row.BomKind)) ?? first.BomKind,
                    ReleasePackageNumber = Join(material.SelectMany(row => (row.ReleasePackageNumber ?? "").Split('、'))),
                    PurchaseRequisitionNumbers = batchPr.Select(pr => pr.DocumentNumber).Distinct().ToArray(),
                    PurchaseRequisitionStatus = batch.Key.PrStatus,
                    PurchaseRequisitionCreatedAt = batchPr.Select(pr => pr.SourceCreatedAt).Min(),
                    PurchaseRequisitionDeliveryDate = Latest(batchPr.Select(pr => pr.DeliveryDate)),
                    PurchaseOrderNumbers = batchPo.Select(po => po.DocumentNumber).Distinct().ToArray(),
                    BuyerName = Join(batchPo.Select(po => po.BuyerName)),
                    PurchaseOrderStatus = batch.Key.PoStatus,
                    PurchaseQuantity = activePo.Sum(po => po.PurchaseQuantity),
                    ArrivedQuantity = activePo.Sum(po => po.ArrivedQuantity),
                    PurchaseRemark = Join(activePo.Select(po => po.PurchaseRemark)),
                    PurchaseDeliveryDate = Latest(activePo.Select(po => po.DeliveryDate)),
                    LatestDeliveryDate = Latest(activePo.Select(po => po.LatestDeliveryDate ?? po.DeliveryDate)),
                    RequestedQuantity = newPr.Length == 0 && batchPr.Length > 0 ? null : activePr.Sum(pr => pr.RequestedQuantity),
                    ApprovedQuantity = newPr.Length == 0 && batchPr.Length > 0 ? null : activePr.Sum(pr => pr.ApprovedQuantity),
                    Details = batchPr.Select(pr => U9ProcurementService.Detail(pr, "请购", "项目+子项目+料号"))
                        .Concat(batchPo.Select(po => U9ProcurementService.Detail(po, "采购", batchPr.Length > 0 ? "源请购行" : "未关联请购"))).ToArray(),
                    HasDeliveryDelay = batch.Any(pair => pair.Po is { IsCanceled: false }
                        && pair.Pr is { IsCanceled: false }
                        && pair.Po.DeliveryDate?.Date > pair.Pr.DeliveryDate?.Date),
                    IsFullyReceived = activePo.Length > 0 && activePo.Sum(po => po.PurchaseQuantity) > 0
                        && activePo.All(po => Received(po) >= po.PurchaseQuantity || coveredOrganizations.Contains(Code(po.OrganizationCode)))
                };
                var receipts = movements.Where(row => row.RecordKind == U9ProcurementRecordKinds.Receipt
                    && !string.IsNullOrWhiteSpace(row.SourcePoLineId)
                    && batchPo.Any(po => Code(po.LineId) == Code(row.SourcePoLineId)
                        && Code(po.OrganizationCode) == Code(row.OrganizationCode))).ToArray();
                purchaseRowIndexes.Add(result.Count);
                result.Add(receipts.Length == 0 ? batchRow : batchRow with { WarehouseMovements = [Movement(receipts[0])] });
                foreach (var receipt in receipts) linkedMovements.Add(receipt);
                foreach (var receipt in receipts.Skip(1))
                    result.Add(batchRow with
                    {
                        Sequence = result.Count + 1,
                        Quantity = null, RequestedQuantity = null, ApprovedQuantity = null,
                        PurchaseQuantity = 0, ArrivedQuantity = 0, IsWarehouseMovementRow = true,
                        WarehouseMovements = [Movement(receipt)]
                    });
                firstBatch = false;
            }
            // Transfers and stock issues need no PO. Share the unique material/batch row for
            // display only; keep one document per direction and never mix organizations.
            if (purchaseRowIndexes.Count == 1 && (pos.Length == 0 || pos.Any(po => !po.IsCanceled)))
            {
                var index = purchaseRowIndexes[0];
                bool SameOrganization(U9ProcurementSnapshotRow row) => rows.All(other => Code(other.OrganizationCode) == Code(row.OrganizationCode));
                var transfer = movements.FirstOrDefault(row => row.RecordKind == U9ProcurementRecordKinds.TransferReceipt && SameOrganization(row));
                if (transfer is not null && result[index].WarehouseMovements.Count == 0)
                {
                    result[index] = result[index] with { WarehouseMovements = [Movement(transfer)] };
                    linkedMovements.Add(transfer);
                }
                var outbound = movements.FirstOrDefault(row =>
                    row.RecordKind is U9ProcurementRecordKinds.MaterialIssue or U9ProcurementRecordKinds.MiscShipment or U9ProcurementRecordKinds.DirectStockIssue
                    && SameOrganization(row) && (!deemedReceipts.ContainsKey(row) || result[index].WarehouseMovements.Count == 0));
                if (outbound is not null)
                {
                    result[index] = result[index] with { WarehouseMovements = [.. result[index].WarehouseMovements, .. MovementDetails(outbound)] };
                    linkedMovements.Add(outbound);
                }
            }
            // Further movements and ambiguous or unlinked records keep their own rows.
            foreach (var movement in movements.Where(row => !linkedMovements.Contains(row)))
            {
                result.Add(first with
                {
                    Sequence = result.Count + 1,
                    Quantity = null, RequestedQuantity = null, ApprovedQuantity = null,
                    PurchaseRequisitionNumbers = [], PurchaseRequisitionStatus = "",
                    PurchaseRequisitionCreatedAt = null, PurchaseRequisitionDeliveryDate = null,
                    PurchaseOrderNumbers = [], PurchaseOrderStatus = "", BuyerName = null,
                    PurchaseQuantity = 0, ArrivedQuantity = 0, PurchaseRemark = null,
                    PurchaseDeliveryDate = null, LatestDeliveryDate = null, HasDeliveryDelay = false,
                    Details = [], IsWarehouseMovementRow = true,
                    WarehouseMovements = MovementDetails(movement)
                });
            }
        }
        return result;
    }

    private static string Code(string? value) => (value ?? "").Trim().ToUpperInvariant();
    private static WarehouseMovementDetail Movement(U9ProcurementSnapshotRow row) =>
        new(row.RecordKind == U9ProcurementRecordKinds.DirectStockIssue ? U9ProcurementRecordKinds.MaterialIssue : row.RecordKind,
            row.DocumentNumber, row.LineNumber, row.MovementDate!.Value, row.MovementQuantity!.Value, row.MovementUnit) { LineId = row.LineId };
    private static string Status(U9ProcurementSnapshotRow? row, string empty) => row is null ? empty
        : U9ProcurementService.DescribeStatus(row.RecordKind, row.LineStatus, row.IsCanceled);
    private static string Arrival(U9ProcurementSnapshotRow? po) => po is null || po.IsCanceled ? ""
        : po.ArrivedQuantity <= 0 ? "未到货" : po.ArrivedQuantity < po.PurchaseQuantity ? "部分到货" : "全部到货";
    private static string? Join(IEnumerable<string?> values)
    {
        var text = string.Join("；", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        return text.Length == 0 ? null : text;
    }
    private static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> values) => values.Max();
}
