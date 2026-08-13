namespace Upton.Pdm.Domain;

public sealed record Project(
    Guid Id,
    string Code,
    string Name,
    string Owner,
    string VaultLocation,
    string ReleaseLocation,
    bool IsActive)
{
    public string? ProjectAlias { get; init; }

    public Guid? OrganizationId { get; init; }

    public string? OrganizationName { get; init; }

    public string? ProjectTypeCode { get; init; }

    public int? EquipmentTypeCode { get; init; }

    public string? CustomerCode { get; init; }

    public string? CustomerName { get; init; }

    public int? CustomerProjectSequence { get; init; }

    public string? DeviceModel { get; init; }

    public DateOnly? SignedDate { get; init; }

    public int Quantity { get; init; } = 1;

    public Guid? ParentProjectId { get; init; }

    public int? ChildSequence { get; init; }

    public IReadOnlyList<string> SerialNumbers { get; init; } = [];

    public IReadOnlyList<string> ResponsibleUsers { get; init; } = [];
}

public sealed record ProjectOrganization(
    Guid Id,
    string Name,
    string ProjectCompanyCode,
    string ModelCompanyCode,
    string CrmCompanyName,
    bool IsActive,
    int CurrentProjectSequence = 0,
    int CurrentSerialSequence = 0);

public sealed record ProjectTypeDefinition(string Code, string Name, bool IsActive);

public sealed record EquipmentTypeDefinition(int Code, string Name, bool IsActive);

public sealed record PdmCustomer(Guid Id, string Code, string Name, bool IsActive);

public sealed record PdmSystemSettings(string VaultRoot, string ReleaseRoot);

public sealed record ProjectNumberingOptions(
    IReadOnlyList<ProjectOrganization> Organizations,
    IReadOnlyList<ProjectTypeDefinition> ProjectTypes,
    IReadOnlyList<EquipmentTypeDefinition> EquipmentTypes);

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
    string? PublishedPath)
{
    public IReadOnlyList<BomItem> MechanicalBomSnapshot { get; init; } = [];

    public IReadOnlyList<BomItem> ElectricalBomSnapshot { get; init; } = [];

    public string? PublishError { get; init; }
}

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string EntityType,
    string EntityId,
    string Detail);
