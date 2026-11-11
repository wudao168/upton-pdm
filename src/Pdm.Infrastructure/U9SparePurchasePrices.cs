using System.Globalization;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class U9OpenApiClient
{
    public async Task<IReadOnlyList<SparePurchasePrice>> QuerySparePurchasePricesAsync(string baseUrl, string token,
        string organizationCode, IReadOnlyList<string> materialCodes, DateTimeOffset synchronizedAt, CancellationToken ct)
    {
        const string header = "PurchaseOrder";
        string[] fields = ["ID", "CreatedOn", "ItemInfo.ItemCode", "PurchaseOrder.Org.Code", "PurchaseOrder.DocNo",
            "PurchaseOrder.BusinessDate", "PurchaseOrder.DocumentType.ShortName", "PurchaseOrder.Cancel.Canceled",
            "Status", "Cancel.Canceled", "PurQtyTU", "NetMnyAC", "TradeUOM.Code", "TradeUOM.Name"];
        var output = new List<SparePurchasePrice>(materialCodes.Count);
        foreach (var batch in materialCodes.Chunk(75))
        {
            var candidates = await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PM.PO.POLine", fields,
                [new { Field = "PurchaseOrder.Org.Code", Operator = "eq", Value = organizationCode },
                 new { Field = "ItemInfo.ItemCode", Operator = "in", Value = string.Join(',', batch) }], ct);
            var valid = candidates.Where(row => EntityText(row, "PurchaseOrder.Org.Code") == organizationCode
                && EntityText(row, "PurchaseOrder.DocumentType.ShortName") == "PO01"
                && !Canceled(row, header) && ProcurementStatus("PO", EntityText(row, "Status")) is >= 2 and <= 5
                && Number(EntityText(row, "PurQtyTU")) > 0)
                .ToLookup(row => EntityText(row, "ItemInfo.ItemCode") ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var code in batch)
            {
                var latest = valid[code].OrderByDescending(row => EntityDate(row, "PurchaseOrder.BusinessDate"))
                    .ThenByDescending(row => EntityDate(row, "CreatedOn"))
                    .ThenByDescending(row => Number(EntityText(row, "ID"))).FirstOrDefault();
                if (latest.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                {
                    output.Add(new(code, null, null, null, null, null, synchronizedAt));
                    continue;
                }
                // U9C uses transaction quantity and the untaxed accounting-currency line amount.
                // Dividing them yields the effective untaxed price per trade unit, including line discounts.
                var quantity = Number(EntityText(latest, "PurQtyTU"))!.Value;
                var net = Number(EntityText(latest, "NetMnyAC"));
                var date = EntityDate(latest, "PurchaseOrder.BusinessDate");
                var unit = EntityText(latest, "TradeUOM.Code");
                if (net is null or < 0 || date is null || string.IsNullOrWhiteSpace(unit))
                    throw new PdmRuleException("U9C最新采购行缺少有效的未税金额、采购日期或计量单位。");
                output.Add(new(code, net.Value / quantity, unit, EntityText(latest, "TradeUOM.Name"),
                    EntityText(latest, "PurchaseOrder.DocNo"), date, synchronizedAt));
            }
        }
        return output;
    }

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
