using System.Text.Json;
using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class U9MaterialPayloadFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string CreatePayload(
        PdmMaterial material,
        MaterialCategoryRule rule,
        string organizationCode,
        string correlationId,
        string? u9UnitCode = null)
    {
        var unit = Archive(u9UnitCode ?? material.UnitCode);
        var data = new Dictionary<string, object?>
        {
            ["OtherID"] = correlationId,
            ["Code"] = material.MaterialCode,
            ["Code1"] = material.MaterialCode,
            ["Name"] = material.Name,
            ["SPECS"] = material.Specification,
            ["Description"] = BuildDescription(material),
            ["MainItemCategory"] = Archive(rule.U9CategoryCode),
            ["Org"] = Archive(organizationCode),
            ["ItemFormAttribute"] = material.SupplyMode switch
            {
                MaterialSupplyMode.Purchase => 9,
                MaterialSupplyMode.Manufacture => 10,
                MaterialSupplyMode.Outsource => 4,
                _ => throw new PdmRuleException("PDM供给方式无法映射到U9C料品形态属性。")
            },
            ["ConverRatioRule"] = 0,
            ["InventoryUOM"] = unit,
            ["InventorySecondUOM"] = unit,
            ["PurchaseUOM"] = unit,
            ["SalesUOM"] = unit,
            ["ManufactureUOM"] = unit,
            ["MaterialOutUOM"] = unit,
            ["PriceUOM"] = unit,
            ["CostUOM"] = unit,
            ["Weight"] = material.Weight,
            ["WeightUom"] = material.Weight is null || string.IsNullOrWhiteSpace(material.WeightUnit)
                ? null
                : Archive(material.WeightUnit),
            ["DescFlexField"] = string.IsNullOrWhiteSpace(material.PurchaseLink)
                ? null
                : new Dictionary<string, object?>
                {
                    [U9MaterialContract.PurchaseLinkPublicSegment] = material.PurchaseLink
                },
            ["IsDualUOM"] = false,
            ["IsMultyUOM"] = false,
            ["IsDualQuantity"] = false,
            ["IsVarRatio"] = true,
            ["IsInventoryEnable"] = true,
            ["IsPurchaseEnable"] = material.SupplyMode == MaterialSupplyMode.Purchase,
            ["IsSalesEnable"] = true,
            ["IsBuildEnable"] = material.SupplyMode != MaterialSupplyMode.Purchase,
            ["IsOutsideOperationEnable"] = material.SupplyMode == MaterialSupplyMode.Outsource,
            ["IsMRPEnable"] = true,
            ["InventoryInfo"] = new Dictionary<string, object?>
            {
                ["InventoryPlanningMethod"] = 4,
                ["PurchaseControlMode"] = 0,
                ["TurnOverRate"] = 1,
                ["ReserveMode"] = -1,
                ["SupplyMethod"] = -1
            },
            ["MrpInfo"] = new Dictionary<string, object?>
            {
                ["MRPPlanningType"] = 1
            },
            ["IsBOMEnable"] = true,
            ["Effective"] = new Dictionary<string, object?> { ["IsEffective"] = "true" }
        };
        var nonNullData = data
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return JsonSerializer.Serialize(new[] { nonNullData }, JsonOptions);
    }

    public static string QueryPayload(string materialCode, string correlationId) =>
        JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["ItemMaster"] = Archive(materialCode),
                ["OtherID"] = correlationId
            }
        }, JsonOptions);

    public static string DeletePayload(PdmMaterial material, U9ItemReference item, string correlationId)
    {
        var data = new Dictionary<string, object?>
        {
            ["Code"] = material.MaterialCode,
            ["ID"] = long.TryParse(item.U9ItemId, out var itemId) ? itemId : null,
            ["OtherID"] = correlationId
        };
        return JsonSerializer.Serialize(new[]
        {
            data.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value)
        }, JsonOptions);
    }

    public static string UomQueryPayload(string unitCode, string correlationId) =>
        JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["Code"] = unitCode.Trim(),
                ["OtherID"] = correlationId
            }
        }, JsonOptions);

    public static string ModifyPayload(PdmMaterial material, string correlationId, string? u9UnitCode = null)
    {
        var attributes = new List<Dictionary<string, object?>>
        {
            Attribute("Name", material.Name),
            Attribute("SPECS", material.Specification ?? string.Empty),
            Attribute("Description", BuildDescription(material) ?? string.Empty),
            EntityAttribute("MainItemCategory", material.CategoryCode ?? material.U9CategoryCode ?? string.Empty),
            EntityAttribute("InventoryUOM", u9UnitCode ?? material.UnitCode),
            Attribute($"DescFlexField.{U9MaterialContract.PurchaseLinkPublicSegment}", material.PurchaseLink ?? string.Empty)
        };
        if (material.Weight is not null) attributes.Add(Attribute("Weight", material.Weight.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["Code"] = material.MaterialCode,
                ["OtherID"] = correlationId,
                ["Attributes"] = attributes
            }
        }, JsonOptions);
    }

    public static string ResolveUnitCode(string pdmUnitCode) => U9UnitCatalog.Normalize(pdmUnitCode);

    private static Dictionary<string, object?> Archive(string code) => new() { ["Code"] = code.Trim() };

    private static Dictionary<string, object?> Attribute(string name, string value) => new()
    {
        ["AttributeName"] = name,
        ["AttributeValue"] = value
    };

    private static Dictionary<string, object?> EntityAttribute(string name, string code) => new()
    {
        ["AttributeName"] = name,
        ["EntityValue"] = Archive(code)
    };

    private static string? BuildDescription(PdmMaterial material)
    {
        var parts = new[]
        {
            material.Material is null ? null : $"材质：{material.Material}",
            material.Brand is null ? null : $"品牌：{material.Brand}",
            material.SurfaceTreatment is null ? null : $"表面处理：{material.SurfaceTreatment}",
            material.Remark
        };
        var description = string.Join("；", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }
}
