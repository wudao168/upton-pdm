namespace Upton.Pdm.Domain;

public sealed record ReleaseItemComment(
    Guid Id,
    Guid ReleasePackageId,
    Guid BomItemId,
    string MaterialKey,
    string MaterialCode,
    string MaterialName,
    string? Specification,
    string? SourceInstancePath,
    string Comment,
    string CreatedBy,
    DateTimeOffset CreatedAt);
