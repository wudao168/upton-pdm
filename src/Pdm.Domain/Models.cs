namespace Upton.Pdm.Domain;

public sealed record Project(
    Guid Id,
    string Code,
    string Name,
    string Owner,
    string VaultLocation,
    string ReleaseLocation,
    bool IsActive);

public sealed record PdmDocument(
    Guid Id,
    Guid ProjectId,
    string DrawingNumber,
    string Name,
    string FileName,
    DocumentKind Kind,
    DocumentLifecycleState State,
    RevisionLabel Revision,
    string? CheckedOutBy,
    DateTimeOffset UpdatedAt);

public sealed record BomItem(
    Guid Id,
    Guid ProjectId,
    BomKind Kind,
    int Sequence,
    string DrawingNumber,
    string Name,
    decimal Quantity,
    string Unit,
    string? Material,
    string? Specification,
    string Revision,
    bool IsComplete);

public sealed record ApprovalTask(
    Guid Id,
    Guid ReleasePackageId,
    ApprovalStage Stage,
    string Assignee,
    string? DecisionBy,
    ApprovalDecision? Decision,
    string? Comment,
    DateTimeOffset? DecidedAt);

public sealed record ReleasePackage(
    Guid Id,
    Guid ProjectId,
    string Number,
    ReleasePackageState State,
    Guid ReferenceSnapshotId,
    string MechanicalBomRevision,
    string ElectricalBomRevision,
    IReadOnlyList<ApprovalTask> ApprovalTasks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedPath);

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string EntityType,
    string EntityId,
    string Detail);
