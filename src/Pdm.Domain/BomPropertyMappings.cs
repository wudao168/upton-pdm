namespace Upton.Pdm.Domain;

public sealed record BomPropertyMapping(
    string PdmPropertyKey,
    string PdmPropertyName,
    string SolidWorksProperty,
    string Source,
    bool MappingEditable);

public static class BomPropertyMappingCatalog
{
    public const string SolidWorksSource = "SolidWorks";
    public const string AssemblySource = "Assembly";
    public const string PdmSource = "Pdm";

    private sealed record Definition(
        string Key,
        string Name,
        string Source,
        bool Editable,
        Func<PdmSystemSettings, string> LegacyValue);

    private static readonly IReadOnlyList<Definition> Definitions =
    [
        new("kind", "物料分类", SolidWorksSource, true, _ => "物料分类"),
        new("wearPart", "易损件", SolidWorksSource, true, _ => "易损件"),
        new("unit", "单位", SolidWorksSource, true, settings => settings.BomUnitProperty),
        new("drawingNumber", "物料编码", SolidWorksSource, true, settings => settings.BomDrawingNumberProperty),
        new("name", "物料名称", SolidWorksSource, true, settings => settings.BomNameProperty),
        new("specification", "型号", SolidWorksSource, true, settings => settings.BomSpecificationProperty),
        new("remark", "备注信息", SolidWorksSource, true, settings => settings.BomDescriptionProperty),
        new("brand", "品牌", SolidWorksSource, true, settings => settings.BomBrandProperty),
        new("material", "材质", SolidWorksSource, true, settings => settings.BomMaterialProperty),
        new("surfaceTreatment", "表面处理", SolidWorksSource, true, settings => settings.BomSurfaceTreatmentProperty),
        new("weight", "重量", SolidWorksSource, true, settings => settings.BomWeightProperty),
        new("quantity", "数量", AssemblySource, false, _ => string.Empty),
        new("revision", "版本", PdmSource, false, _ => string.Empty)
    ];

    public static IReadOnlyList<BomPropertyMapping> Normalize(PdmSystemSettings settings)
    {
        var requested = (settings.BomPropertyMappings ?? Array.Empty<BomPropertyMapping>())
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping?.PdmPropertyKey))
            .GroupBy(mapping => mapping.PdmPropertyKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<BomPropertyMapping>();
        foreach (var definition in Definitions)
        {
            requested.TryGetValue(definition.Key, out var mapping);
            var solidWorksProperty = definition.Editable
                ? mapping?.SolidWorksProperty?.Trim() ?? definition.LegacyValue(settings).Trim()
                : definition.LegacyValue(settings).Trim();
            result.Add(new(
                definition.Key,
                definition.Name,
                solidWorksProperty,
                definition.Source,
                definition.Editable));
        }

        var knownKeys = Definitions.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(requested.Values
            .Where(mapping => !knownKeys.Contains(mapping.PdmPropertyKey))
            .Select(mapping => new BomPropertyMapping(
                mapping.PdmPropertyKey.Trim(),
                string.IsNullOrWhiteSpace(mapping.PdmPropertyName) ? mapping.PdmPropertyKey.Trim() : mapping.PdmPropertyName.Trim(),
                mapping.SolidWorksProperty?.Trim() ?? string.Empty,
                SolidWorksSource,
                true))
            .OrderBy(mapping => mapping.PdmPropertyName, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    public static PdmSystemSettings Apply(PdmSystemSettings settings)
    {
        var mappings = Normalize(settings);
        string Value(string key, string fallback) =>
            mappings.FirstOrDefault(mapping => string.Equals(mapping.PdmPropertyKey, key, StringComparison.OrdinalIgnoreCase))?.SolidWorksProperty
            ?? fallback;
        return settings with
        {
            BomDrawingNumberProperty = Value("drawingNumber", settings.BomDrawingNumberProperty),
            BomNameProperty = Value("name", settings.BomNameProperty),
            BomDescriptionProperty = Value("remark", settings.BomDescriptionProperty),
            BomMaterialProperty = Value("material", settings.BomMaterialProperty),
            BomSpecificationProperty = Value("specification", settings.BomSpecificationProperty),
            BomUnitProperty = Value("unit", settings.BomUnitProperty),
            BomBrandProperty = Value("brand", settings.BomBrandProperty),
            BomSurfaceTreatmentProperty = Value("surfaceTreatment", settings.BomSurfaceTreatmentProperty),
            BomWeightProperty = Value("weight", settings.BomWeightProperty),
            BomPropertyMappings = mappings
        };
    }

    public static string SolidWorksProperty(PdmSystemSettings settings, string key, string fallback) =>
        Normalize(settings).FirstOrDefault(mapping =>
            string.Equals(mapping.PdmPropertyKey, key, StringComparison.OrdinalIgnoreCase))?.SolidWorksProperty
        ?? fallback;
}
