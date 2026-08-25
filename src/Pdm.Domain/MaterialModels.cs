namespace Upton.Pdm.Domain;

public enum MaterialKind
{
    Electrical = 0,
    Standard = 1,
    NonStandard = 2,
    Product = 3
}

public enum MaterialSupplyMode
{
    Purchase = 0,
    Manufacture = 1,
    Outsource = 2
}

public enum MaterialApprovalStatus
{
    Draft = 0,
    Approved = 1
}

public enum MaterialCodeApplicationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public sealed record MaterialCodeApplication(
    Guid Id,
    Guid ProjectId,
    Guid? BomItemId,
    MaterialCodeApplicationStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionComment,
    Guid? MaterialId,
    string? MaterialCode,
    long RowVersion,
    ProjectBomHeaderKind? BomHeaderKind = null)
{
    public string? BomItemName { get; init; }
    public string? ApplicationName { get; init; }
    public string? ProjectCode { get; init; }
    public string? ProjectName { get; init; }
    public string? CategoryCode { get; init; }
    public string? RequestedMaterialCode { get; init; }
    public string? Specification { get; init; }
    public string? Brand { get; init; }
    public string? Remark { get; init; }
}

public enum MaterialCodeResolutionStatus
{
    Matched = 0,
    NoMatch = 1,
    Ambiguous = 2,
    ApplicationPending = 3,
    ApplicationApproved = 4
}

public sealed record MaterialCodeResolution(
    Guid BomItemId,
    MaterialCodeResolutionStatus Status,
    PdmMaterial? Material,
    IReadOnlyList<PdmMaterial> Candidates,
    MaterialCodeApplication? Application);

public enum MaterialSyncStatus
{
    NotQueued = 0,
    PreviewReady = 1,
    Pending = 2,
    Succeeded = 3,
    Failed = 4,
    NeedsReview = 5,
    Superseded = 6
}

public enum MaterialSyncOperation
{
    Create = 0,
    Update = 1
}

public enum MaterialDataSource
{
    Pdm = 0,
    U9C = 1
}

public enum MaterialMasterOwner
{
    Pdm = 0,
    U9C = 1
}

public enum MaterialAttachmentKind
{
    Model3D = 0,
    Document = 1
}

public sealed record MaterialAttachment(
    Guid Id,
    Guid MaterialId,
    MaterialAttachmentKind Kind,
    string OriginalFileName,
    string StorageRoot,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt);

public sealed record MaterialCategory(
    string Code,
    string Name,
    string? ParentCode,
    string? U9CategoryId,
    MaterialKind? PdmKind,
    MaterialSupplyMode DefaultSupplyMode,
    bool AllowCreate,
    bool IsVisible,
    bool IsActive,
    string NumberPrefix,
    int SequenceLength,
    string CounterScope,
    int SortOrder,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion,
    long CurrentSequence = 0);

public sealed record MaterialCategoryRule(
    MaterialKind PdmKind,
    string U9CategoryCode,
    string U9CategoryName,
    MaterialSupplyMode DefaultSupplyMode,
    bool IsEnabled,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record PdmMaterial(
    Guid Id,
    string MaterialCode,
    string Name,
    MaterialKind Kind,
    MaterialSupplyMode SupplyMode,
    string UnitCode,
    string? Specification,
    string? Material,
    string? Remark,
    string? Brand,
    string? SurfaceTreatment,
    decimal? Weight,
    string? WeightUnit,
    Guid? SourceBomItemId,
    MaterialApprovalStatus ApprovalStatus,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? U9CategoryCode,
    string? U9ItemId,
    string? U9ItemCode,
    MaterialSyncStatus SyncStatus,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion,
    string? CategoryCode = null,
    bool IsArchived = false,
    string? ArchivedBy = null,
    DateTimeOffset? ArchivedAt = null,
    bool U9SyncConfirmed = false,
    MaterialDataSource SourceSystem = MaterialDataSource.Pdm,
    MaterialMasterOwner MasterOwner = MaterialMasterOwner.Pdm,
    DateTimeOffset? LastU9SyncedAt = null,
    string? PurchaseLink = null,
    int ReferenceCount = 0,
    string? SelectionAdvice = null,
    decimal? ReferencePrice = null,
    string? Model3DLink = null,
    string? DocumentLink = null,
    bool IsRecommended = false)
{
    public int Model3DAttachmentCount { get; init; }

    public int DocumentAttachmentCount { get; init; }
}

public sealed record MaterialRemovalResult(
    PdmMaterial Material,
    bool Deleted,
    bool Archived);

public sealed record MaterialSyncTask(
    Guid Id,
    Guid MaterialId,
    MaterialSyncOperation Operation,
    MaterialSyncStatus Status,
    string CorrelationId,
    string PayloadJson,
    string PayloadSha256,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    string? ResponsePreview,
    string? U9ItemId,
    string? U9ItemCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? MaterialCode { get; init; }
    public string? MaterialName { get; init; }
    public string? CategoryCode { get; init; }
    public Guid? ProjectId { get; init; }
    public string? ProjectCode { get; init; }
    public string? ProjectName { get; init; }
    public ProjectBomHeaderKind? BomHeaderKind { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset? RequestedAt { get; init; }
}

public sealed record U9MaterialIntegrationConfiguration(
    string BaseUrl,
    string EnterpriseCode,
    string OrganizationCode,
    string UserCode,
    string ClientId,
    string ClientSecretCiphertext,
    string ItemCreatePath,
    string ItemQueryPath,
    bool WriteEnabled,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    string ItemModifyPath = "/webapi/ItemMaster/Modify",
    string ItemDeletePath = "/webapi/ItemMaster/Delete",
    IReadOnlyDictionary<string, string>? UnitCodeMappings = null,
    string CustomerQueryPath = "/webapi/GetCommonReference/Create",
    string BomCreatePath = "/webapi/BOM/Create",
    string BomQueryPath = "/webapi/BOM/Query",
    string BomModifyPath = "/webapi/BOM/Modify",
    string BomDeletePath = "/webapi/BOM/Delete",
    string BomBatchUnapprovePath = "/webapi/BOM/BatchUnApprove",
    string BomBipQueryPagePath = "/webapi/BOM/BIPQueryPage");

public sealed record U9MaterialIntegrationSettings(
    string BaseUrl,
    string EnterpriseCode,
    string OrganizationCode,
    string UserCode,
    string ClientId,
    bool ClientSecretConfigured,
    string ItemCreatePath,
    string ItemQueryPath,
    bool WriteEnabled,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    string ItemModifyPath = "/webapi/ItemMaster/Modify",
    string ItemDeletePath = "/webapi/ItemMaster/Delete",
    IReadOnlyDictionary<string, string>? UnitCodeMappings = null,
    string CustomerQueryPath = "/webapi/GetCommonReference/Create",
    string BomCreatePath = "/webapi/BOM/Create",
    string BomQueryPath = "/webapi/BOM/Query",
    string BomModifyPath = "/webapi/BOM/Modify",
    string BomDeletePath = "/webapi/BOM/Delete",
    string BomBatchUnapprovePath = "/webapi/BOM/BatchUnApprove",
    string BomBipQueryPagePath = "/webapi/BOM/BIPQueryPage");
