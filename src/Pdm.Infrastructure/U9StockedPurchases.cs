using System.Globalization;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class U9OpenApiClient
{
    public async Task<IReadOnlyList<StockedPurchaseLine>?> QueryStockedPurchasesAsync(string baseUrl, string token,
        string organizationCode, IReadOnlyList<string> materialCodes, DateOnly from, DateOnly through, CancellationToken cancellationToken)
    {
        if (materialCodes.Count == 0) return [];
        var codes = materialCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 当前U9C实体接口支持in，不支持日期范围比较；业务日期为零点，按日传入完整三个月窗口。
        var dates = string.Join(',', Enumerable.Range(0, through.DayNumber - from.DayNumber + 1)
            .Select(day => from.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00"));
        string[] prFields = ["ID", "PR.Org.Code", "PR.BusinessDate", "PR.Cancel.Canceled", "ItemInfo.ItemCode",
            "ReqQtyReqUOM", "ReqUOM.Code", "Status", "Cancel.Canceled"];
        var requisitions = new Dictionary<string, JsonElement>();
        foreach (var batch in materialCodes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
            foreach (var row in await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PR.PurchaseRequest.PRLine", prFields,
                [new { Field = "PR.Org.Code", Operator = "eq", Value = organizationCode },
                 new { Field = "PR.BusinessDate", Operator = "in", Value = dates },
                 new { Field = "ItemInfo.ItemCode", Operator = "in", Value = string.Join(',', batch) }], cancellationToken))
            {
                if (EntityText(row, "PR.Org.Code") != organizationCode || !codes.Contains(EntityText(row, "ItemInfo.ItemCode") ?? "")) continue;
                var date = BusinessDate(row);
                if (date < from || date > through || Canceled(row, "PR") || ProcurementStatus("PR", EntityText(row, "Status")) is 4 or 5) continue;
                requisitions[EntityText(row, "ID")!] = row;
            }
        if (requisitions.Count == 0) return [];
        string[] poFields = ["ID", "PurchaseOrder.Org.Code", "PurchaseOrder.Cancel.Canceled", "ItemInfo.ItemCode",
            "SrcDocInfo.SrcDocLine.EntityID", "PurQtyTU", "TotalRecievedQtyTU", "TradeUOM.Code", "Status", "Cancel.Canceled"];
        var orders = new Dictionary<string, JsonElement>();
        foreach (var batch in requisitions.Keys.Chunk(100))
            foreach (var row in await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PM.PO.POLine", poFields,
                [new { Field = "PurchaseOrder.Org.Code", Operator = "eq", Value = organizationCode },
                 new { Field = "SrcDocInfo.SrcDocLine.EntityID", Operator = "in", Value = string.Join(',', batch) }], cancellationToken))
            {
                if (EntityText(row, "PurchaseOrder.Org.Code") != organizationCode || Canceled(row, "PurchaseOrder")
                    || ProcurementStatus("PO", EntityText(row, "Status")) is 4 or 5) continue;
                var sourceId = EntityText(row, "SrcDocInfo.SrcDocLine.EntityID") ?? "";
                if (!requisitions.TryGetValue(sourceId, out var pr) || EntityText(pr, "ItemInfo.ItemCode") != EntityText(row, "ItemInfo.ItemCode")) continue;
                orders[EntityText(row, "ID")!] = row;
            }
        var expectedDates = new Dictionary<string, DateOnly?>();
        foreach (var batch in orders.Where(item => Quantity(item.Value, "PurQtyTU") > Quantity(item.Value, "TotalRecievedQtyTU"))
            .Select(item => item.Key).Chunk(100))
            foreach (var ship in await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PM.PO.POShipLine",
                ["ID", "POLine.ID", "PlanArriveDate", "DeliveryDate"],
                [new { Field = "POLine.ID", Operator = "in", Value = string.Join(',', batch) }], cancellationToken))
            {
                var id = EntityText(ship, "POLine.ID") ?? "";
                if (!batch.Contains(id)) continue;
                var value = EntityDate(ship, "PlanArriveDate") ?? EntityDate(ship, "DeliveryDate");
                if (value is not null && (!expectedDates.TryGetValue(id, out var before) || before is null || DateOnly.FromDateTime(value.Value.Date) > before))
                    expectedDates[id] = DateOnly.FromDateTime(value.Value.Date);
            }
        var result = new List<StockedPurchaseLine>();
        foreach (var (id, pr) in requisitions)
        {
            var unit = EntityText(pr, "ReqUOM.Code");
            if (string.IsNullOrWhiteSpace(unit)) throw new PdmRuleException("U9C请购行缺少计量单位，已保留上次采购汇总。");
            var linked = orders.Values.Where(row => EntityText(row, "SrcDocInfo.SrcDocLine.EntityID") == id).ToArray();
            if (linked.Any(row => EntityText(row, "TradeUOM.Code") != unit))
                throw new PdmRuleException("U9C请购与采购的计量单位不一致，暂不能直接汇总，请核对单位换算后重试。");
            result.Add(new(id, organizationCode, EntityText(pr, "ItemInfo.ItemCode")!, BusinessDate(pr), Quantity(pr, "ReqQtyReqUOM"),
                linked.Sum(row => Quantity(row, "PurQtyTU")), linked.Sum(row => Quantity(row, "TotalRecievedQtyTU")), unit,
                ExpectedArrivalDate: linked.Select(row => expectedDates.GetValueOrDefault(EntityText(row, "ID")!)).Max()));
        }
        return result;
    }

    private static bool Canceled(JsonElement row, string header) =>
        EntityText(row, "Cancel.Canceled") is "True" or "true" or "1" || EntityText(row, header + ".Cancel.Canceled") is "True" or "true" or "1";
    private static DateOnly BusinessDate(JsonElement row) => EntityDate(row, "PR.BusinessDate") is { } value
        ? DateOnly.FromDateTime(value.Date) : throw new PdmRuleException("U9C请购行缺少有效业务日期，已保留上次采购汇总。");
    private static decimal Quantity(JsonElement row, string field) =>
        decimal.TryParse(EntityText(row, field), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value
            : throw new PdmRuleException("U9C请购或采购数量缺失、格式异常，已保留上次采购汇总。");
}
