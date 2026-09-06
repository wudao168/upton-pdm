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
    int SortOrder);

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
    IReadOnlyList<MaterialRelationGroup> Groups);

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
    long RowVersion);

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
