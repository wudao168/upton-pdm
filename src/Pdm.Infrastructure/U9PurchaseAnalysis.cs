using System.Globalization;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class U9OpenApiClient
{
    public async Task<IReadOnlyList<PurchaseAnalysisLine>> QueryPurchaseAnalysisAsync(string baseUrl, string token,
        string organization, DateOnly from, DateOnly through, CancellationToken ct)
    {
        string[] fields = ["ID", "DocLineNo", "PurchaseOrder.ID", "PurchaseOrder.Org.Code", "PurchaseOrder.DocNo", "PurchaseOrder.BusinessDate",
            "PurchaseOrder.DocumentType.Code", "PurchaseOrder.DocumentType.Name", "PurchaseOrder.DocumentType.BusinessType",
            "PurchaseOrder.Cancel.Canceled", "Cancel.Canceled", "Status", "PurQtyTU", "NetMnyAC", "TradeUOM.Code", "TradeUOM.Name",
            "PurchaseOrder.AC.Code", "PurchaseOrder.AC.Name", "PurchaseOrder.Supplier.Code", "PurchaseOrder.Supplier.Name",
            "PurchaseOrder.PurOper.Code", "PurchaseOrder.PurOper.Name", "ItemInfo.ItemCode", "ItemInfo.ItemName", "ItemInfo.ItemID.SPECS",
            "ItemInfo.ItemID.DescFlexField.PubDescSeg3", "ItemInfo.ItemID.MainItemCategory.Code", "ItemInfo.ItemID.MainItemCategory.Name",
            "Project.Code", "SeiBanCode"];
        var result = new Dictionary<string, PurchaseAnalysisLine>();
        // The installed CommonEntity contract supports IN but not range predicates. Refresh bounded date windows
        // atomically so historical corrections and cancellations cannot survive an unreliable incremental cursor.
        for (var start = from; start <= through; start = start.AddDays(31)) {
            var end = start.AddDays(30) < through ? start.AddDays(30) : through;
            var dates = string.Join(',', Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00"));
            var rows = await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PM.PO.POLine", fields,
                [new { Field = "PurchaseOrder.Org.Code", Operator = "eq", Value = organization }, new { Field = "PurchaseOrder.BusinessDate", Operator = "in", Value = dates }], ct);
            foreach (var row in rows) {
                string Text(string field) => EntityText(row, field)?.Trim() ?? "";
                string Label(string field, string empty) => Text(field) is { Length: > 0 } value ? value : empty;
                if (Text("PurchaseOrder.Org.Code") != organization) throw new PdmRuleException("U9C返回了范围外组织数据，本次快照未替换。");
                var date = EntityDate(row, "PurchaseOrder.BusinessDate");
                if (date is null) throw new PdmRuleException("采购记录缺少有效业务日期，本次快照未替换。");
                var day = DateOnly.FromDateTime(date.Value.Date);
                if (day < start || day > end) throw new PdmRuleException("U9C日期筛选未生效，本次快照未替换。");
                if (Canceled(row, "PurchaseOrder") || ProcurementStatus("PO", Text("Status")) is not (>= 2 and <= 5)) continue;
                var quantity = Number(Text("PurQtyTU")); var amount = Number(Text("NetMnyAC"));
                if (quantity is null) throw new PdmRuleException("采购记录数量无效，本次快照未替换。");
                if (quantity <= 0 || amount < 0 || Text("PurchaseOrder.DocumentType.BusinessType").Contains("退")) continue;
                var currency = Text("PurchaseOrder.AC.Code");
                var material = Text("ItemInfo.ItemCode"); var orderId = Text("PurchaseOrder.ID");
                if (material.Length == 0 || orderId.Length == 0 || Text("TradeUOM.Code").Length == 0)
                    throw new PdmRuleException("采购记录缺少料号、订单标识或计量单位，请核对U9C资料。");
                var line = new PurchaseAnalysisLine(Text("ID"), organization, orderId, Text("PurchaseOrder.DocNo"),
                    int.TryParse(Text("DocLineNo"), out var n) ? n : 0, day,
                    Text("PurchaseOrder.DocumentType.Code"), Label("PurchaseOrder.DocumentType.Name", "类型待补充"),
                    material, Label("ItemInfo.ItemName", "名称待补充"), Text("ItemInfo.ItemID.SPECS"), Text("ItemInfo.ItemID.DescFlexField.PubDescSeg3"),
                    Text("ItemInfo.ItemID.MainItemCategory.Code"), Label("ItemInfo.ItemID.MainItemCategory.Name", "未分类"),
                    Text("PurchaseOrder.Supplier.Code"), Label("PurchaseOrder.Supplier.Name", "供应商待补充"),
                    Text("PurchaseOrder.PurOper.Code"), Label("PurchaseOrder.PurOper.Name", "姓名待补充"), Text("Project.Code"), Text("SeiBanCode"),
                    Text("TradeUOM.Code"), Label("TradeUOM.Name", "单位待补充"), quantity.Value,
                    currency.Length > 0 ? Label("PurchaseOrder.AC.Name", currency) + "（" + currency + "）" : "", currency.Length > 0 ? amount : null);
                if (!result.TryAdd(line.Id, line)) throw new PdmRuleException("采购记录重复，本次快照未替换。");
            }
        }
        return result.Values.ToArray();
    }
}
