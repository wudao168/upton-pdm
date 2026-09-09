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
                result.Add(first with
                {
                    Sequence = result.Count + 1,
                    Quantity = firstBatch ? material.Sum(row => row.Quantity) : null,
                    Remark = Join(material.Select(row => row.Remark)),
                    BomKind = Join(material.Select(row => row.BomKind)) ?? first.BomKind,
                    ReleasePackageNumber = Join(material.SelectMany(row => (row.ReleasePackageNumber ?? "").Split('、'))),
                    PurchaseRequisitionNumbers = batchPr.Select(pr => pr.DocumentNumber).Distinct().ToArray(),
                    PurchaseRequisitionStatus = batch.Key.PrStatus,
                    PurchaseRequisitionDeliveryDate = Latest(batchPr.Select(pr => pr.DeliveryDate)),
                    PurchaseOrderNumbers = batchPo.Select(po => po.DocumentNumber).Distinct().ToArray(),
                    PurchaseOrderStatus = batch.Key.PoStatus + (batch.Key.Arrival.Length == 0 ? "" : $"（{batch.Key.Arrival}）"),
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
                        && pair.Po.DeliveryDate?.Date > pair.Pr.DeliveryDate?.Date)
                });
                firstBatch = false;
            }
        }
        return result;
    }

    private static string Code(string? value) => (value ?? "").Trim().ToUpperInvariant();
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
