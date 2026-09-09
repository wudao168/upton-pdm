namespace Upton.Pdm.Domain;

public enum EngineeringKitRevisionState
{
    Draft = 0,
    Released = 1
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
    string Name,
    string? Description,
    Guid? CurrentReleasedRevisionId,
    IReadOnlyList<EngineeringKitRevision> Revisions,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion)
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
