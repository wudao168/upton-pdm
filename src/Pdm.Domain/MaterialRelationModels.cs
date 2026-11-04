namespace Upton.Pdm.Domain;

public enum MaterialRelationRevisionState
{
    Draft,
    Published,
    Superseded
}

public enum MaterialRelationSelectionMode
{
    Single,
    Multiple
}

public enum MaterialRelationQuantityMode
{
    PerMainQuantity,
    Fixed
}

public enum MaterialRelationReviewDecision
{
    Selected,
    NoAccessory
}

public sealed record MaterialRelationOption(
    Guid Id,
    Guid MaterialId,
    string MaterialCode,
    string MaterialName,
    MaterialKind MaterialKind,
    string UnitCode,
    MaterialRelationQuantityMode QuantityMode,
    decimal QuantityPerSet,
    bool IsDefault,
    int SortOrder,
    string? SelectionAdvice = null,
    string? Specification = null,
    string? Brand = null);

public sealed record MaterialRelationGroup(
    Guid Id,
    string Name,
    bool IsRequired,
    MaterialRelationSelectionMode SelectionMode,
    int MinSelection,
    int? MaxSelection,
    bool AutoSelectUnique,
    int SortOrder,
    IReadOnlyList<MaterialRelationOption> Options);

public sealed record MaterialRelationMainMaterial(Guid MaterialId, string MaterialCode, string MaterialName,
    string? Specification = null, string? Brand = null, string? Remark = null)
{
    public IReadOnlyList<StandardLibraryCategory> StandardLibraryCategories { get; init; } = [];
    public bool IsEngineeringKit { get; init; }
}

public sealed record MaterialRelationRevision(
    Guid Id,
    int Version,
    MaterialRelationRevisionState State,
    string? ChangeNote,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? PublishedBy,
    DateTimeOffset? PublishedAt,
    long RowVersion,
    IReadOnlyList<MaterialRelationGroup> Groups)
{
    public IReadOnlyList<MaterialRelationMainMaterial> MainMaterials { get; init; } = [];
}

public sealed record MaterialRelationTemplate(
    Guid Id,
    Guid MainMaterialId,
    string MainMaterialCode,
    string MainMaterialName,
    string Name,
    bool IsArchived,
    MaterialRelationRevision? PublishedRevision,
    MaterialRelationRevision? DraftRevision,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    public IReadOnlyList<StandardLibraryCategory> StandardLibraryCategories { get; init; } = [];
    public IReadOnlyList<MaterialRelationMainMaterial> MainMaterials => Members(DraftRevision ?? PublishedRevision);
    public IReadOnlyList<MaterialRelationMainMaterial> PublishedMainMaterials => PublishedRevision is null ? [] : Members(PublishedRevision);
    private IReadOnlyList<MaterialRelationMainMaterial> Members(MaterialRelationRevision? revision) => revision?.MainMaterials is { Count: > 0 } members
        ? members : [new(MainMaterialId, MainMaterialCode, MainMaterialName) { StandardLibraryCategories = StandardLibraryCategories }];
}

public sealed record MaterialRelationSelection(
    Guid ProjectId,
    Guid MainBomItemId,
    Guid AccessoryBomItemId,
    Guid RevisionId,
    Guid GroupId,
    Guid OptionId,
    decimal ExpectedQuantity,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record MaterialRelationAllocationGroup(Guid GroupId, string GroupName, IReadOnlyList<Guid> OptionIds,
    bool ConfirmNoAccessory = false, string? NoAccessoryReason = null)
{
    public IReadOnlyList<MaterialRelationAllocatedOption> Options { get; init; } = [];
}

public sealed record MaterialRelationAllocatedOption(Guid OptionId, string MaterialCode, string MaterialName,
    string? Specification, string? Brand, string UnitCode, MaterialRelationQuantityMode QuantityMode,
    decimal QuantityPerSet, decimal Quantity);

public sealed record MaterialRelationAllocation(Guid Id, string Name, decimal Quantity, Guid RevisionId, int RevisionVersion,
    IReadOnlyList<MaterialRelationAllocationGroup> Groups);

public sealed record MaterialRelationReview(
    Guid ProjectId,
    Guid MainBomItemId,
    Guid RevisionId,
    Guid GroupId,
    MaterialRelationReviewDecision Decision,
    decimal MainQuantity,
    string? Reason,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    string? BomFingerprint = null);
