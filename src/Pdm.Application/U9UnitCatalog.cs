using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class U9UnitCatalog
{
    public static bool RequiresWholeQuantity(string? unit) => unit?.Trim().ToUpperInvariant() is
        "001" or "002" or "003" or "007" or "008" or "010" or "011"
        or "EA" or "PCS" or "PC" or "个" or "件" or "只" or "台" or "双" or "片" or "支" or "组" or "套";

    public static void RequireWholeQuantity(decimal quantity, string? unit, string label)
    {
        if (RequiresWholeQuantity(unit) && quantity != decimal.Truncate(quantity))
            throw new PdmRuleException($"{label}为计件数量，必须填写整数，当前为{quantity:0.####}。");
    }

    private static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "001", "002", "003", "004", "005", "006", "007",
        "008", "009", "010", "011", "012", "013"
    };

    public static string Normalize(string? value)
    {
        var code = value?.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PdmRuleException("计量单位不能为空。");
        if (!Codes.Contains(code)) throw new PdmRuleException("计量单位必须使用U9C单位编码（001–013）。");
        return code;
    }

    public static string NormalizeInbound(string? value)
    {
        var unit = value?.Trim();
        if (string.IsNullOrWhiteSpace(unit)) throw new PdmRuleException("计量单位不能为空。");
        if (unit.Length > 80) throw new PdmRuleException("U9C库存计量单位编码不能超过80个字符。");
        return unit;
    }

    public static string NormalizeBomUnit(string? value)
    {
        var unit = value?.Trim();
        if (string.IsNullOrWhiteSpace(unit)) throw new PdmRuleException("计量单位不能为空。");
        if (Codes.Contains(unit)) return unit;
        return unit.ToUpperInvariant() switch
        {
            "EA" or "件" or "个" => "001",
            "台" => "002",
            "盒" => "004",
            "卷" => "005",
            "捆" => "006",
            "双" => "007",
            "片" => "008",
            "桶" => "009",
            "支" => "010",
            "组" or "套" => "011",
            "箱" => "012",
            "包" => "013",
            _ => throw new PdmRuleException($"BOM计量单位 {unit} 尚未使用U9C单位编码（001–013）。")
        };
    }
}
