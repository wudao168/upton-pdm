namespace Upton.Pdm.Domain;

public enum EngineeringKitRevisionState
{
    Draft = 0,
    Released = 1
}

public enum EngineeringKitModelMode
{
    /// <summary>首次发布时按“标准代码-分类代码-序列号”自动生成型号。</summary>
    Auto = 0,
    /// <summary>型号由用户手动填写。</summary>
    Manual = 1
}

/// <summary>
/// 套件型号生成用的可维护代码项：维护“分类名称 + 代码”，型号里使用代码，界面上按名称选择。
/// </summary>
public sealed record EngineeringKitOptionEntry(
    string Name,
    string Code);

/// <summary>
/// 套件型号自动生成用的可维护选项：型号 = 标准代码-分类代码-序列号（序列号取套件流水号）。
/// 标准代码、分类代码都按“分类名称 + 代码”维护，选择时按名称匹配对应代码。
/// </summary>
public sealed record EngineeringKitOptionCatalog(
    IReadOnlyList<EngineeringKitOptionEntry> StandardCodes,
    IReadOnlyList<EngineeringKitOptionEntry> CategoryCodes)
{
    public static EngineeringKitOptionCatalog Empty { get; } = new([], []);

    public EngineeringKitOptionEntry? FindStandard(string? name) => Find(StandardCodes, name);

    public EngineeringKitOptionEntry? FindCategory(string? name) => Find(CategoryCodes, name);

    private static EngineeringKitOptionEntry? Find(IReadOnlyList<EngineeringKitOptionEntry> entries, string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        return entries.FirstOrDefault(item => string.Equals(item.Name?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record EngineeringKitComponent(
    Guid Id,
    Guid RevisionId,
    Guid MaterialId,
    string MaterialCode,
    string MaterialName,
    decimal Quantity,
    string Unit,
    bool IsOptional,
    int SortOrder);

public sealed record EngineeringKitRevision(
    Guid Id,
    Guid KitId,
    int VersionNumber,
    EngineeringKitRevisionState State,
    string? ChangeNote,
    IReadOnlyList<EngineeringKitComponent> Components,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? PublishedBy,
    DateTimeOffset? PublishedAt);

public sealed record EngineeringKit(
    Guid Id,
    string? Code,
    string? Model,
    string Name,
    string Brand,
    string? Description,
    Guid? CurrentReleasedRevisionId,
    IReadOnlyList<EngineeringKitRevision> Revisions,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion,
    EngineeringKitModelMode ModelMode = EngineeringKitModelMode.Auto,
    string? StandardName = null,
    string? StandardCode = null,
    string? CategoryName = null,
    string? CategoryCode = null)
{
    public EngineeringKitRevision? DraftRevision => Revisions
        .Where(item => item.State == EngineeringKitRevisionState.Draft)
        .OrderByDescending(item => item.VersionNumber)
        .FirstOrDefault();

    public EngineeringKitRevision? CurrentReleasedRevision => CurrentReleasedRevisionId is null
        ? null
        : Revisions.FirstOrDefault(item => item.Id == CurrentReleasedRevisionId.Value);
}

public sealed record EngineeringKitExpansionLine(
    Guid KitComponentId,
    Guid MaterialId,
    string MaterialCode,
    string MaterialName,
    decimal Quantity,
    string Unit,
    string? Material,
    string? Specification,
    string? Remark,
    string? Brand,
    string? SurfaceTreatment,
    string? Weight,
    bool IsOptional);

public sealed record EngineeringKitExpansion(
    Guid ReferenceId,
    Guid KitId,
    Guid RevisionId,
    string KitCode,
    string KitName,
    int VersionNumber,
    decimal KitQuantity,
    IReadOnlyList<EngineeringKitExpansionLine> Lines);
