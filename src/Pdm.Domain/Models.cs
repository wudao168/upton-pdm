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

    public Guid? ExecutionUnitId { get; init; }

    public string? ExecutionUnitName { get; init; }

    public string? PrimaryProjectManager { get; init; }

    public IReadOnlyList<string> CollaborativeProjectManagers { get; init; } = [];

    public string? DesignLead { get; init; }

    public IReadOnlyList<string> Designers { get; init; } = [];

    public bool CanAssignExecutionUnit { get; init; }

    public bool CanManageMainStaffing { get; init; }

    public bool CanAssignDesigners { get; init; }

    public bool CanReadContent { get; init; }

    public int? DocumentCount { get; init; }

    public string? BusinessStatus { get; init; }
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

public sealed record PdmCustomer(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    string SourceSystem = "legacy",
    DateTimeOffset? LastSyncedAt = null);

public sealed record PdmSystemSettings(string VaultRoot, string ReleaseRoot)
{
    public int CheckoutHeartbeatSeconds { get; init; } = 180;

    public int CheckoutLeaseMinutes { get; init; } = 15;

    public int CheckoutOfflineGraceMinutes { get; init; } = 60;

    public int CheckoutReminderHours { get; init; } = 4;

    public int CheckoutStrongReminderHours { get; init; } = 8;

    public int CheckoutOverdueHours { get; init; } = 24;

    public int CheckoutForceReleaseHours { get; init; } = 48;
}

public sealed record ProjectNumberingOptions(
    IReadOnlyList<ProjectOrganization> Organizations,
    IReadOnlyList<ProjectTypeDefinition> ProjectTypes,
    IReadOnlyList<EquipmentTypeDefinition> EquipmentTypes);

public sealed record OrganizationUnit(
    Guid Id,
    Guid OrganizationId,
    Guid? ParentUnitId,
    string Code,
    string Name,
    OrganizationUnitKind Kind,
    bool IsActive,
    int SortOrder);

public sealed record OrganizationMembership(Guid UnitId, string Username, bool IsPrimary);

public sealed record OrganizationUnitManagers(Guid UnitId, string PrimaryManager, IReadOnlyList<string> CollaborativeManagers);

public sealed record OrganizationDirectoryUser(string Username, string DisplayName, UserRole Role, bool IsActive);

public sealed record OrganizationDirectory(
    IReadOnlyList<ProjectOrganization> Organizations,
    IReadOnlyList<OrganizationUnit> Units,
    IReadOnlyList<OrganizationMembership> Memberships,
    IReadOnlyList<OrganizationUnitManagers> Managers,
    IReadOnlyList<OrganizationDirectoryUser> Users);

public sealed record PermissionDefinition(
    string Code,
    string Name,
    string Module,
    string? Description = null,
    bool Sensitive = false);

public sealed record RoleDefinition(
    UserRole Role,
    string Name,
    string Description,
    bool IsSystemAdministrator = false);

public sealed record RolePermissionSettings(
    UserRole Role,
    string Name,
    string Description,
    bool IsSystemAdministrator,
    IReadOnlyList<string> Permissions);

public sealed record RolePermissionDirectory(
    IReadOnlyList<PermissionDefinition> Permissions,
    IReadOnlyList<RolePermissionSettings> Roles);

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
    DateTimeOffset UpdatedAt)
{
    public Guid? FolderId { get; init; }

    public DateTimeOffset? CheckedOutAt { get; init; }

    public Guid? CheckoutSessionId { get; init; }

    public string? CheckoutMachine { get; init; }

    public DateTimeOffset? CheckoutLastHeartbeatAt { get; init; }

    public DateTimeOffset? CheckoutLeaseExpiresAt { get; init; }

    public string? CheckoutReleaseRequestedBy { get; init; }

    public DateTimeOffset? CheckoutReleaseRequestedAt { get; init; }

    public string? CheckoutReleaseRequestReason { get; init; }
}

public sealed record DocumentModelDrawingRelation(
    Guid ModelDocumentId,
    Guid DrawingDocumentId);

public sealed record DocumentWhereUsed(
    Guid DocumentId,
    Guid ParentDocumentId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string ParentDrawingNumber,
    string ParentName,
    string ParentFileName,
    DocumentKind ParentKind,
    DocumentLifecycleState ParentState,
    RevisionLabel ParentRevision,
    string InstancePath,
    string Configuration,
    int Quantity);

public sealed record EditSessionHeartbeat(
    Guid SessionId,
    DateTimeOffset ServerTime,
    DateTimeOffset LeaseExpiresAt,
    IReadOnlyList<Guid> ActiveDocumentIds,
    IReadOnlyList<Guid> LostDocumentIds,
    PdmSystemSettings Settings);

public sealed record EditLockSummary(
    Guid DocumentId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string DrawingNumber,
    string DocumentName,
    string FileName,
    string CheckedOutBy,
    DateTimeOffset CheckedOutAt,
    string? CheckoutMachine,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset LeaseExpiresAt,
    EditLockConnectionState ConnectionState,
    EditLockAttentionLevel AttentionLevel,
    string? ReleaseRequestedBy,
    DateTimeOffset? ReleaseRequestedAt,
    string? ReleaseRequestReason,
    bool OwnedByCurrentUser,
    bool CanRequestRelease,
    bool CanForceRelease);

public sealed record FolderPermissionRule(
    Guid Id,
    FolderPrincipalType PrincipalType,
    string PrincipalKey,
    FolderAccess Access);

public sealed record ProjectFolder(
    Guid Id,
    Guid RootProjectId,
    Guid? ParentFolderId,
    Guid? TargetProjectId,
    string FolderKey,
    string TemplateKey,
    string Name,
    ProjectFolderPurpose Purpose,
    int SortOrder,
    bool IsSystem,
    bool InheritPermissions)
{
    public FolderAccess EffectiveAccess { get; init; }

    public IReadOnlyList<FolderPermissionRule> Permissions { get; init; } = [];
}

public sealed record ProjectFolderTemplateNode(
    string FolderKey,
    string? ParentKey,
    string Name,
    ProjectFolderPurpose Purpose,
    int SortOrder,
    bool IsSystem,
    bool InheritPermissions)
{
    public IReadOnlyList<FolderPermissionRule> Permissions { get; init; } = [];
}

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
