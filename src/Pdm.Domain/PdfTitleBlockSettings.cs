namespace Upton.Pdm.Domain;

public sealed record PdfTitleBlockField(string Key, string Name, string[] Labels);

public static class PdfTitleBlockFields
{
    public static IReadOnlyList<PdfTitleBlockField> All { get; } =
    [
        new("designer", "设计人员", ["设计", "设计人"]),
        new("drafter", "设计人员", ["制图", "制图人"]),
        new("designedOn", "存档日期", ["设计日期"]),
        new("reviewer", "主设审核", ["审核", "校对", "审核人"]),
        new("reviewedOn", "审核日期", ["审核日期", "校对日期"]),
        new("approver", "机械主管批准", ["批准", "批准人"]),
        new("approvedOn", "批准日期", ["批准日期"]),
        new("name", "物料名称", ["物料名称", "零件名称", "名称"]),
        new("specification", "型号", ["零件编号"]),
        new("material", "材质", ["材质", "材料"]),
        new("surfaceTreatment", "表面处理", ["表面处理"]),
        new("heatTreatment", "热处理", ["热处理"]),
        new("wearPart", "易损件", ["易损件"]),
        new("materialCode", "物料编码", ["物料编码", "料号"]),
        new("weight", "重量", ["重量", "质量", "WEIGHT"]),
        new("quantity", "本次下发数量", ["本次下发数量", "数量"]),
        new("revision", "版本", ["版本", "版次"]),
        new("projectNumber", "项目号", ["项目号", "项目编号"]),
        new("projectName", "项目名称", ["项目名称"])
    ];

    public static IReadOnlyDictionary<string, string> PersonLabels { get; } = new Dictionary<string, string>
    {
        ["designer"] = "设计", ["drafter"] = "制图", ["reviewer"] = "校对", ["approver"] = "批准"
    };

    public static string DefaultLabel(string key) => PersonLabels.GetValueOrDefault(key) ?? Get(key).Labels[0];

    public static string NormalizeLabel(string label) => string.Concat(label.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '：'));

    public static PdfTitleBlockField Get(string key) => All.Single(field => field.Key == key);
}

public sealed record PdfTitleBlockFillSettings
{
    public IReadOnlyList<string> EnabledFields { get; init; } = [];
    public IReadOnlyDictionary<string, string>? DrawingLabels { get; init; }
}

public sealed record PdfDrawingTitleBlockSnapshot(Guid SourceVersionId, string SourceSha256,
    IReadOnlyDictionary<string, string> Values, IReadOnlyList<Guid> SourceBomItemIds);

/// <summary>随发布包冻结，重转不得读取当前配置或当前物料属性。</summary>
public sealed record PdfTitleBlockSnapshot(IReadOnlyList<string> EnabledFields,
    IReadOnlyDictionary<Guid, PdfDrawingTitleBlockSnapshot> Drawings, bool QuantitiesFrozen = false)
{
    public bool UseReleaseApproval { get; init; }
    public IReadOnlyDictionary<string, string>? DrawingLabels { get; init; }
    public bool Enabled => EnabledFields.Count > 0;
}
