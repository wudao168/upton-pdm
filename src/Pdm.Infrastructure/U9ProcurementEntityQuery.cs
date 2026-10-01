using System.Globalization;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class U9OpenApiClient
{
    private sealed record ProcurementEntity(string Entity, string Kind, string Header, string Subproject,
        string Item, string Quantity, string? Date = null, string? Unit = null);

    private async Task<U9ProcurementQueryResult> QueryProcurementEntitiesAsync(string baseUrl, string token,
        string organization, IReadOnlyCollection<string> projectCodes, CancellationToken cancellationToken)
    {
        var codes = projectCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0) return new(0, true, null, []);
        organization = Required(organization, "组织编码");
        var output = new List<U9ProcurementSourceRow>();
        var prIds = new List<string>();
        var orders = new Dictionary<string, U9ProcurementSourceRow>();
        var applyFields = new[] { "ID", "IssueApplyDoc.ID", "IssueApplyDoc.Org.Code", "ItemInfo.ID", "Project.Code", "Project.ShortName", "IssueOrgSeibanNo" };
        var applications = (await QueryProcurementScopedEntitiesAsync(baseUrl, token,
            "UFIDA.U9.IssueNew.IssueApplyBE.IssueApplyDocLine", applyFields, codes, "IssueOrgSeibanNo", null, [], cancellationToken))
            .Where(row => EntityText(row, "IssueApplyDoc.Org.Code") == organization)
            .ToDictionary(row => EntityText(row, "ID")!);
        ProcurementEntity[] entities = [
            new("UFIDA.U9.PR.PurchaseRequest.PRLine", "PR", "PR", "SeiBanCode", "ItemInfo.ItemCode", "ReqQtyReqUOM"),
            new("UFIDA.U9.PM.PO.POLine", "PO", "PurchaseOrder", "SeiBanCode", "ItemInfo.ItemCode", "PurQtyTU"),
            new("UFIDA.U9.PM.Rcv.RcvLine", "RCV", "Receivement", "SeiBanCode", "ItemInfo.ItemCode", "RcvQtyTU", "ConfirmDate", "TradeUOM.Name"),
            new("UFIDA.U9.IssueNew.MaterialDeliveryDocBE.MaterialDeliveryDocLine", "ISSUE", "MaterialDeliveryDoc", "SeibanCode", "ItemInfo.Code", "IssuedQtyUOM", "MaterialDeliveryDoc.ConfirmDate", "IssueUOM.Name"),
            new("UFIDA.U9.InvDoc.MiscShip.MiscShipmentL", "MISC", "MiscShip", "SeibanCode", "ItemInfo.ItemCode", "StoreUOMQty", "MiscShip.BusinessDate", "StoreUOM.Name"),
            new("UFIDA.U9.InvDoc.TransferForm.TransferFormSL", "TRANSFER", "TransferFormL.TransferForm", "SeibanCode", "ItemInfo.ItemCode", "StoreUOMQty", "TransferFormL.TransferForm.BusinessDate", "StoreUOM.Name")
        ];
        foreach (var spec in entities)
        {
            var h = spec.Header;
            var fields = new List<string> { "ID", "DocLineNo", h + ".ID", h + ".Org.Code", h + ".DocNo", h + ".BusinessDate", "CreatedOn",
                "Project.ID", "Project.Code", "Project.ShortName", spec.Subproject, spec.Item, spec.Item.Replace("Code", "Name"), spec.Quantity, h + ".Cancel.Canceled" };
            if (spec.Date is not null) fields.Add(spec.Date);
            if (spec.Unit is not null) fields.Add(spec.Unit);
            var extra = spec.Kind switch {
                "PR" => new[] { "Status", "Cancel.Canceled", "ApprovedQtyReqUOM", "DeliveryDate", "ItemInfo.ItemID.SPECS", "ItemInfo.ItemID.DescFlexField.PubDescSeg3" },
                "PO" => new[] { "NetMnyAC", "Status", "Cancel.Canceled", "TotalRecievedQtyTU", "PurchaseOrder.PurOper.Name", "PurchaseOrder.PurAdviceMemo", "PurchaseOrder.DocumentType.ShortName", "SrcDocInfo.SrcDocLine.EntityID", "ItemInfo.ItemID.SPECS", "ItemInfo.ItemID.DescFlexField.PubDescSeg3" },
                "RCV" => new[] { "Status", "Cancel.Canceled", "Receivement.RcvDocType.Code", "SrcPO.SrcDocLine.EntityID", "SrcPO.SrcDocLine.EntityType" },
                "ISSUE" => new[] { "MaterialDeliveryDoc.DocState", "MaterialDeliveryDoc.IssueType", "MaterialDeliveryDoc.CreatedOn", "ItemInfo.ID", "SourceDoc.SrcDocLine.EntityID", "SourceDoc.SrcDocLine.EntityType", "SourceDoc.SrcDoc.EntityID" },
                "MISC" => new[] { "MiscShip.Status", "MiscShip.CreatedOn" },
                _ => new[] { "TransferFormL.TransferForm.Status", "TransferFormType", "TransferFormL.TransferFormType", "TransferFormL.Project.ID", "TransferFormL.SeibanCode" }
            };
            fields.AddRange(extra);
            var linkedField = spec.Kind == "PO" ? "SrcDocInfo.SrcDocLine.EntityID" : spec.Kind == "ISSUE" ? "SourceDoc.SrcDocLine.EntityID" : null;
            var linkedIds = spec.Kind == "PO" ? prIds.ToArray() : spec.Kind == "ISSUE" ? applications.Keys.ToArray() : [];
            var rows = await QueryProcurementScopedEntitiesAsync(baseUrl, token, spec.Entity, fields.Distinct().ToArray(),
                codes, spec.Subproject, linkedField, linkedIds, cancellationToken);
            foreach (var row in rows)
            {
                if (EntityText(row, h + ".Org.Code") != organization) continue;
                var kind = spec.Kind;
                var canceled = EntityText(row, h + ".Cancel.Canceled") == "True" || EntityText(row, "Cancel.Canceled") == "True";
                var statusField = kind == "ISSUE" ? h + ".DocState" : kind is "MISC" or "TRANSFER" ? h + ".Status" : "Status";
                var statusText = EntityText(row, statusField);
                var status = ProcurementStatus(kind, statusText);
                if (kind == "PO" && EntityText(row, h + ".DocumentType.ShortName") != "PO01") continue;
                if (kind == "RCV" && (canceled || EntityText(row, h + ".RcvDocType.Code") != "RCV01" || status is not (4 or 5))) continue;
                if (kind == "ISSUE" && (canceled || status is not (2 or 3) || EntityText(row, h + ".IssueType") is not ("材料出库" or "0"))) continue;
                if (kind is "MISC" or "TRANSFER" && (canceled || status != 2)) continue;
                if (kind == "TRANSFER" && (EntityText(row, "TransferFormType") is not ("转换后" or "1")
                    || EntityText(row, "TransferFormL.TransferFormType") is not ("转换前" or "0")
                    || (EntityText(row, "Project.ID") == EntityText(row, "TransferFormL.Project.ID")
                        && (EntityText(row, spec.Subproject) ?? "") == (EntityText(row, "TransferFormL.SeibanCode") ?? "")))) continue;
                var projectCode = EntityText(row, "Project.Code");
                var projectName = EntityText(row, "Project.ShortName");
                var subproject = EntityText(row, spec.Subproject);
                if (kind == "ISSUE" && EntityText(row, "SourceDoc.SrcDocLine.EntityType") == "UFIDA.U9.IssueNew.IssueApplyBE.IssueApplyDocLine"
                    && applications.TryGetValue(EntityText(row, "SourceDoc.SrcDocLine.EntityID") ?? "", out var application)
                    && EntityText(application, "ItemInfo.ID") == EntityText(row, "ItemInfo.ID")
                    && EntityText(application, "IssueApplyDoc.ID") == EntityText(row, "SourceDoc.SrcDoc.EntityID"))
                {
                    projectCode = EntityText(application, "Project.Code"); projectName = EntityText(application, "Project.ShortName");
                    subproject = EntityText(application, "IssueOrgSeibanNo");
                    if (string.IsNullOrEmpty(EntityText(row, "Project.Code")) && string.IsNullOrEmpty(EntityText(row, spec.Subproject))) kind = "DIRECT";
                }
                if (spec.Kind == "ISSUE" && !codes.Contains(projectCode, StringComparer.OrdinalIgnoreCase) && !codes.Contains(subproject, StringComparer.OrdinalIgnoreCase)) continue;
                var mapped = new Dictionary<string, object?> {
                    ["OrganizationCode"] = EntityText(row, h + ".Org.Code"), ["RecordKind"] = kind, ["LineId"] = EntityText(row, "ID"),
                    ["SourcePrLineId"] = kind == "PR" ? EntityText(row, "ID") : kind == "PO" ? EntityText(row, "SrcDocInfo.SrcDocLine.EntityID") : null,
                    ["DocumentNumber"] = EntityText(row, h + ".DocNo"), ["LineNumber"] = EntityText(row, "DocLineNo"), ["LineStatus"] = status, ["IsCanceled"] = canceled,
                    ["BusinessDate"] = EntityText(row, h + ".BusinessDate"), ["SourceCreatedAt"] = EntityText(row, kind is "ISSUE" or "MISC" ? h + ".CreatedOn" : "CreatedOn"),
                    ["MaterialCode"] = EntityText(row, spec.Item), ["ItemName"] = EntityText(row, spec.Item.Replace("Code", "Name")),
                    ["Specification"] = EntityText(row, "ItemInfo.ItemID.SPECS"), ["Brand"] = EntityText(row, "ItemInfo.ItemID.DescFlexField.PubDescSeg3"),
                    ["ProjectCode"] = projectCode, ["ProjectName"] = projectName, ["Subproject"] = subproject,
                    ["RequestedQuantity"] = kind == "PR" ? EntityText(row, spec.Quantity) : "0", ["ApprovedQuantity"] = kind == "PR" ? EntityText(row, "ApprovedQtyReqUOM") : "0",
                    ["OrderNetAmount"] = kind == "PO" ? EntityText(row, "NetMnyAC") : null,
                    ["PurchaseQuantity"] = kind == "PO" ? EntityText(row, spec.Quantity) : "0", ["ArrivedQuantity"] = kind == "PO" ? EntityText(row, "TotalRecievedQtyTU") : "0",
                    ["BuyerName"] = EntityText(row, "PurchaseOrder.PurOper.Name"), ["PurchaseRemark"] = EntityText(row, "PurchaseOrder.PurAdviceMemo"),
                    ["DeliveryDate"] = kind == "PR" ? EntityText(row, "DeliveryDate") : null,
                    ["MovementDate"] = spec.Date is null ? null : EntityText(row, spec.Date), ["MovementQuantity"] = spec.Date is null ? null : EntityText(row, spec.Quantity),
                    ["MovementUnit"] = spec.Unit is null ? null : EntityText(row, spec.Unit)
                };
                using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(new { Data = new[] { mapped } }));
                var source = ReadProcurementRows(parsed.RootElement, organization).Single();
                if (spec.Date is not null && source.MovementQuantity == 0) continue;
                if (kind == "TRANSFER" && source.MovementQuantity <= 0) continue;
                if (kind == "PR") prIds.Add(source.LineId);
                if (kind == "PO") orders[source.LineId] = source;
                if (kind == "RCV" && EntityText(row, "SrcPO.SrcDocLine.EntityType") == "UFIDA.U9.PM.PO.POLine"
                    && orders.TryGetValue(EntityText(row, "SrcPO.SrcDocLine.EntityID") ?? "", out var po) && po.MaterialCode == source.MaterialCode)
                    source = source with { SourcePoLineId = po.LineId };
                output.Add(source);
            }
        }
        if (orders.Count > 0)
        {
            var shipRows = await QueryProcurementEntityPagesAsync(baseUrl, token, "UFIDA.U9.PM.PO.POShipLine",
                ["ID", "POLine.ID", "DeliveryDate", "PlanArriveDate"],
                [new { Logic = "or", Filters = orders.Keys.Select(id => new { Field = "POLine.ID", Operator = "eq", Value = id }).ToArray() }], cancellationToken);
            var ships = shipRows.GroupBy(row => EntityText(row, "POLine.ID")!).ToDictionary(group => group.Key, group => group.ToArray());
            for (var i = 0; i < output.Count; i++)
                if (output[i].RecordKind == "PO" && ships.TryGetValue(output[i].LineId, out var deliveries))
                {
                    DateTimeOffset? Latest(string field) => deliveries.Select(row => EntityDate(row, field) ?? EntityDate(row, "DeliveryDate")).Max();
                    output[i] = output[i] with { DeliveryDate = Latest("DeliveryDate"), LatestDeliveryDate = Latest("PlanArriveDate") };
                }
        }
        return new(0, true, null, output);
    }

    private async Task<IReadOnlyList<JsonElement>> QueryProcurementScopedEntitiesAsync(string baseUrl, string token,
        string entity, string[] fields, string[] codes, string subproject, string? linkedField, string[] linkedIds, CancellationToken ct)
    {
        var rows = new Dictionary<string, JsonElement>();
        var filters = codes.SelectMany(code => new[] { new { Field = "Project.Code", Operator = "eq", Value = code },
            new { Field = subproject, Operator = "eq", Value = code } }).ToList();
        if (linkedField is not null) filters.AddRange(linkedIds.Select(id => new { Field = linkedField, Operator = "eq", Value = id }));
        // Bound request size and deduplicate overlapping project and source-line scopes.
        foreach (var batch in filters.Chunk(200))
            foreach (var row in await QueryProcurementEntityPagesAsync(baseUrl, token, entity, fields,
                [new { Logic = "or", Filters = batch }], ct)) rows[EntityText(row, "ID")!] = row;
        return rows.Values.ToArray();
    }
    private async Task<IReadOnlyList<JsonElement>> QueryProcurementEntityPagesAsync(string baseUrl, string token,
        string entity, string[] fields, object[] filters, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var result = new List<JsonElement>();
        var ids = new HashSet<string>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUrl, "/webapi/CommonEntity/Query")) {
                Content = new StringContent(JsonSerializer.Serialize(new { EntityFullName = entity, ReturnFields = fields,
                    PageIndex = page, PageSize = pageSize, Filters = filters }), Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
            using var response = await SendAsync(request, "U9C采购实体查询", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new PdmRuleException($"U9C采购实体查询失败：HTTP {(int)response.StatusCode}。");
            using var document = ParseJson(await response.Content.ReadAsStringAsync(cancellationToken), "U9C采购实体查询响应不是有效JSON。");
            var root = document.RootElement;
            var code = ReadInt(root, "ResCode");
            if (code != 0 || ReadBool(root, "Success") == false)
                throw new PdmRuleException($"U9C采购实体查询失败（{entity}, ResCode={code}）：{ReadMessage(root)}。");
            var rows = ReadDataRows(root);
            foreach (var row in rows)
            {
                var id = EntityText(row, "ID");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new PdmRuleException($"U9C采购实体分页返回空或重复ID：{entity}；保留上一份快照。");
                result.Add(row.Clone());
            }
            if (rows.Count < pageSize) return result;
        }
    }

    private static string? EntityText(JsonElement row, string path)
    {
        foreach (var part in path.Split('.'))
            if (!TryGet(row, part, out row)) return null;
        return row.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : row.ToString();
    }
    private static DateTimeOffset? EntityDate(JsonElement row, string path)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { MovementDate = EntityText(row, path) }));
        return ReadWarehouseDate(json.RootElement);
    }
    private static int ProcurementStatus(string kind, string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        return (kind, value) switch {
            ("PR" or "PO", "开立") => 0, ("PR" or "PO", "核准中" or "审核中") => 1,
            ("PR" or "PO", "已核准") => 2, ("PR" or "PO", "自然关闭") => 3,
            ("PR" or "PO", "短缺关闭") => 4, ("PR" or "PO", "超额关闭") => 5,
            ("RCV", "已入库") => 4, ("RCV", "业务关闭") => 5,
            ("ISSUE", "已审核") => 2, ("ISSUE", "已关闭") => 3,
            ("MISC" or "TRANSFER", "已核准") => 2,
            _ => throw new PdmRuleException($"U9C采购实体返回未识别状态：{kind}/{value}；保留上一份快照。")
        };
    }
}
