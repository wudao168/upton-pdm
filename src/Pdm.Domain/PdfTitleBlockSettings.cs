namespace Upton.Pdm.Domain;

public sealed record PdfTitleBlockField(string Key, string Name, string[] Labels);

public static class PdfTitleBlockFields
{
    public static IReadOnlyList<PdfTitleBlockField> All { get; } =
    [
        new("designer", "设计人", ["设计", "设计人"]),
        new("drafter", "制图人", ["制图", "制图人"]),
        new("designedOn", "设计日期", ["设计日期"]),
        new("reviewer", "审核人", ["审核", "校对", "审核人"]),
        new("reviewedOn", "审核日期", ["审核日期", "校对日期"]),
        new("approver", "批准人", ["批准", "批准人"]),
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

    public static PdfTitleBlockField Get(string key) => All.Single(field => field.Key == key);
}

public sealed record PdfTitleBlockFillSettings
{
    public IReadOnlyList<string> EnabledFields { get; init; } = [];
}

public sealed record PdfDrawingTitleBlockSnapshot(Guid SourceVersionId, string SourceSha256,
    IReadOnlyDictionary<string, string> Values, IReadOnlyList<Guid> SourceBomItemIds);

/// <summary>随发布包冻结，重转不得读取当前配置或当前物料属性。</summary>
public sealed record PdfTitleBlockSnapshot(IReadOnlyList<string> EnabledFields,
    IReadOnlyDictionary<Guid, PdfDrawingTitleBlockSnapshot> Drawings, bool QuantitiesFrozen = false)
{
    public bool Enabled => EnabledFields.Count > 0;
}
