namespace Upton.Pdm.Domain;

public enum MaterialKind
{
    Electrical = 0,
    Standard = 1,
    NonStandard = 2
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
    string? PurchaseLink = null);

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
    DateTimeOffset UpdatedAt);

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
    IReadOnlyDictionary<string, string>? UnitCodeMappings = null);

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
    IReadOnlyDictionary<string, string>? UnitCodeMappings = null);
