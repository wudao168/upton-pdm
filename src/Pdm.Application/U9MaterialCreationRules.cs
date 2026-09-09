using System.Globalization;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

// Approved organization-7 templates. Other categories retain their existing behavior.
public static class U9MaterialCreationRules
{
    private static readonly string[] AttributePaths =
    [
        "ItemFormAttribute", "IsPurchaseEnable", "IsBuildEnable", "IsOutsideOperationEnable",
        "IsMRPEnable", "IsBOMEnable", "IsSalesEnable", "IsInventoryEnable", "IsVarRatio", "Effective.IsEffective", "CostCurrency.Code",
        "InventoryInfo.PurchaseControlMode", "InventoryInfo.TurnOverRate",
        "InventoryInfo.LotControlMode", "InventoryInfo.IsBalanceByProject", "InventoryInfo.IsInvCalculateBySeiban",
        "MrpInfo.MRPPlanningType", "MrpInfo.ForecastContorlType", "MrpInfo.IsTraceRequirement",
        "MrpInfo.IsControlByDC", "MrpInfo.DemandRule",
        "MfgInfo.IsInheritBomMasterNo", "MfgInfo.DesignationRule", "MfgInfo.IsExpandByOrder", "MfgInfo.BuildShrinkageRate",
        "PurchaseInfo.IsNeedRequest", "PurchaseInfo.ReceiptModeAllowModify",
        "SaleInfo.IsReturnable", "SaleInfo.IsRMAAllowModify",
        "PurchaseInfo.IsPUTradePathModify", "PurchaseInfo.IsPURtnTradePathModify",
        "SaleInfo.IsSDTradePathModify", "SaleInfo.IsSDRtnTradePathModify",
        "SaleInfo.SupplySource", "SaleInfo.DemandTransType", "SaleInfo.SupplyOrg.Code"
    ];

    public static async Task<ProjectBomHeaderKind?> FindHeaderKindAsync(
        IMaterialRepository repository, Guid materialId, CancellationToken cancellationToken) =>
        (await repository.ListMaterialCodeApplicationsAsync(null, null, cancellationToken))
        .Where(application => application.MaterialId == materialId && application.BomHeaderKind is not null)
        .OrderByDescending(application => application.RequestedAt)
        .Select(application => application.BomHeaderKind).FirstOrDefault();

    public static bool Apply(Dictionary<string, object?> data, PdmMaterial material, string category,
        string organizationCode, ProjectBomHeaderKind? headerKind)
    {
        var purchased = headerKind is null && category is "0101" or "0102"
            && material.SupplyMode == MaterialSupplyMode.Purchase;
        var master = category is "0301" or "0302" && material.Kind == MaterialKind.Product
            && material.SupplyMode == MaterialSupplyMode.Manufacture;
        var virtualBom = category == "0201"
            && headerKind is ProjectBomHeaderKind.Standard or ProjectBomHeaderKind.NonStandard or ProjectBomHeaderKind.Electrical;
        if (!purchased && !master && !virtualBom) return false;
        if (organizationCode.Trim() != "7")
            throw new PdmRuleException("该料品创建模板仅核准用于U9C组织7；切换组织后请先核对模板及币种档案。");

        data["ItemFormAttribute"] = virtualBom ? 6 : purchased ? 9 : 10;
        data["IsPurchaseEnable"] = true;
        data["IsBuildEnable"] = true;
        data["IsOutsideOperationEnable"] = true;
        // C001 is the RMB archive Code verified through ItemMaster/Query, not a database ID or ISO code.
        data["CostCurrency"] = new Dictionary<string, object?> { ["Code"] = "C001" };
        var inventory = (Dictionary<string, object?>)data["InventoryInfo"]!;
        inventory["PurchaseControlMode"] = 1;
        inventory["TurnOverRate"] = 0;
        inventory["LotControlMode"] = 2;
        inventory["IsBalanceByProject"] = true;
        inventory["IsInvCalculateBySeiban"] = true;
        var mrp = (Dictionary<string, object?>)data["MrpInfo"]!;
        mrp["MRPPlanningType"] = 0;
        mrp["ForecastContorlType"] = 1;
        mrp["IsTraceRequirement"] = true;
        mrp["IsControlByDC"] = true;
        mrp["DemandRule"] = 0;
        data["MfgInfo"] = new Dictionary<string, object?>
        {
            ["IsInheritBomMasterNo"] = true, ["DesignationRule"] = 1,
            ["IsExpandByOrder"] = true, ["BuildShrinkageRate"] = 1
        };
        data["PurchaseInfo"] = new Dictionary<string, object?>
        {
            ["IsNeedRequest"] = !master, ["ReceiptModeAllowModify"] = true,
            ["IsPUTradePathModify"] = true, ["IsPURtnTradePathModify"] = true
        };
        data["SaleInfo"] = new Dictionary<string, object?>
        {
            ["IsReturnable"] = true, ["IsRMAAllowModify"] = true,
            ["IsSDTradePathModify"] = true, ["IsSDRtnTradePathModify"] = true,
            ["SupplySource"] = 4, ["DemandTransType"] = 4,
            ["SupplyOrg"] = new Dictionary<string, object?> { ["Code"] = organizationCode.Trim() }
        };
        return true;
    }

    public static IReadOnlyDictionary<string, string?> ReadAttributes(JsonElement row)
    {
        var result = new Dictionary<string, string?>();
        foreach (var path in AttributePaths)
        {
            var current = row;
            foreach (var part in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object) { current = default; break; }
                current = current.EnumerateObject().FirstOrDefault(property =>
                    property.Name.Equals(part, StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("m_" + part, StringComparison.OrdinalIgnoreCase)).Value;
            }
            result[path] = current.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number when current.TryGetDecimal(out var number) => number.ToString("G29", CultureInfo.InvariantCulture),
                JsonValueKind.String => current.GetString()?.Trim(),
                _ => null
            };
        }
        return result;
    }

    public static IReadOnlyList<string> Compare(string payload, IReadOnlyDictionary<string, string?> actual)
    {
        using var document = JsonDocument.Parse(payload);
        var row = document.RootElement[0];
        // Historical/unsupported templates are not silently migrated by readback.
        if (!row.TryGetProperty("MfgInfo", out _) || !row.TryGetProperty("CostCurrency", out _)) return [];
        return ReadAttributes(row).Where(pair => pair.Value is not null
                && (!actual.TryGetValue(pair.Key, out var value)
                    || !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => $"{pair.Key} 期望={pair.Value} 回查={(actual.GetValueOrDefault(pair.Key) ?? "<缺失>")}")
            .ToArray();
    }
}
