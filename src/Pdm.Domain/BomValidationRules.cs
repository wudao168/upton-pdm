namespace Upton.Pdm.Domain;

public sealed record BomValidationRules(
    IReadOnlyList<string> Standard,
    IReadOnlyList<string> NonStandard,
    IReadOnlyList<string> Electrical)
{
    public static BomValidationRules Default { get; } = new(
        BomValidationFieldCatalog.StandardDefaults,
        BomValidationFieldCatalog.NonStandardDefaults,
        BomValidationFieldCatalog.ElectricalDefaults);

    public IReadOnlyList<string> RequiredFields(BomKind kind) => kind switch
    {
        BomKind.Standard => Standard,
        BomKind.NonStandard => NonStandard,
        BomKind.Electrical => Electrical,
        _ => []
    };
}

public static class BomValidationFieldCatalog
{
    public const string DrawingNumber = "drawingNumber";
    public const string Name = "name";
    public const string Unit = "unit";
    public const string Specification = "specification";
    public const string Brand = "brand";
    public const string Material = "material";
    public const string SurfaceTreatment = "surfaceTreatment";
    public const string Weight = "weight";
    public const string Quantity = "quantity";
    public const string Revision = "revision";
    public const string Remark = "remark";

    public static IReadOnlyList<string> CoreFields { get; } = [DrawingNumber, Name, Unit, Quantity, Revision];

    public static IReadOnlyList<string> AllFields { get; } =
    [
        DrawingNumber, Name, Unit, Specification, Brand, Material,
        SurfaceTreatment, Weight, Quantity, Revision, Remark
    ];

    public static IReadOnlyList<string> StandardDefaults { get; } =
        [DrawingNumber, Name, Unit, Specification, Quantity, Revision];

    public static IReadOnlyList<string> NonStandardDefaults { get; } =
        [Name, Unit, Material, Quantity, Revision];

    public static IReadOnlyList<string> ElectricalDefaults { get; } =
        [DrawingNumber, Name, Unit, Quantity, Revision];

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? fields) =>
        (fields ?? []).Select(field => field?.Trim() ?? string.Empty)
            .Where(field => field.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> MissingFields(BomItem item, IReadOnlyList<string> requiredFields) =>
        requiredFields.Where(field => !HasValue(item, field)).ToArray();

    /// <summary>发布口径的必填字段：非标件允许先缺料号（料号由料品主档回写）。</summary>
    public static IReadOnlyList<string> ReleaseRequiredFields(BomKind kind, BomValidationRules rules) =>
        kind == BomKind.NonStandard
            ? rules.RequiredFields(kind).Where(field => !field.Equals(DrawingNumber, StringComparison.OrdinalIgnoreCase)).ToArray()
            : rules.RequiredFields(kind);

    /// <summary>
    /// 按当前校验口径判断"资料是否完整"：工作区行与已发布快照必须用同一口径，
    /// 否则会出现"BOM已补齐、已发布快照里仍记着旧的未完成"的情况。
    /// </summary>
    public static bool IsComplete(BomItem item, BomKind kind, BomValidationRules rules) =>
        kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical
        && MissingFields(item, ReleaseRequiredFields(kind, rules)).Count == 0;

    public static string Label(string field) => field switch
    {
        DrawingNumber => "物料编码",
        Name => "物料名称",
        Unit => "单位",
        Specification => "型号",
        Brand => "品牌",
        Material => "材质",
        SurfaceTreatment => "表面处理",
        Weight => "重量",
        Quantity => "数量",
        Revision => "版本",
        Remark => "备注",
        _ => field
    };

    private static bool HasValue(BomItem item, string field) => field switch
    {
        DrawingNumber => !string.IsNullOrWhiteSpace(item.DrawingNumber),
        Name => !string.IsNullOrWhiteSpace(item.Name),
        Unit => !string.IsNullOrWhiteSpace(item.Unit),
        Specification => !string.IsNullOrWhiteSpace(item.Specification),
        Brand => !string.IsNullOrWhiteSpace(item.Brand),
        Material => !string.IsNullOrWhiteSpace(item.Material),
        SurfaceTreatment => !string.IsNullOrWhiteSpace(item.SurfaceTreatment),
        Weight => !string.IsNullOrWhiteSpace(item.Weight),
        Quantity => item.Quantity > 0,
        Revision => !string.IsNullOrWhiteSpace(item.Revision),
        Remark => !string.IsNullOrWhiteSpace(item.Remark),
        _ => false
    };
}
