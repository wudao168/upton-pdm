using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveMaterialCommand(
    string? MaterialCode,
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
    long? ExpectedRowVersion = null,
    string? CategoryCode = null,
    string? PurchaseLink = null);

public sealed record CreateMaterialFromBomCommand(Guid ProjectId, Guid BomItemId);

public sealed record SaveMaterialCategoryRuleCommand(
    MaterialKind PdmKind,
    string U9CategoryCode,
    string U9CategoryName,
    MaterialSupplyMode DefaultSupplyMode,
    bool IsEnabled);

public sealed record SaveMaterialCategoryCommand(
    string Code,
    string Name,
    string? ParentCode,
    string? U9CategoryId,
    MaterialKind? PdmKind,
    MaterialSupplyMode DefaultSupplyMode,
    bool AllowCreate,
    bool IsVisible,
    bool IsActive,
    string? NumberPrefix,
    int SequenceLength,
    string? CounterScope,
    int SortOrder,
    long? ExpectedRowVersion = null);

public sealed record CalibrateMaterialCategoryCounterCommand(string LastMaterialCode);

public sealed record LinkBomMaterialCommand(Guid ProjectId, Guid BomItemId, Guid MaterialId);

public sealed record UpdateU9MaterialIntegrationCommand(
    string BaseUrl,
    string EnterpriseCode,
    string OrganizationCode,
    string UserCode,
    string ClientId,
    string? ClientSecret,
    string ItemCreatePath,
    string ItemQueryPath,
    bool WriteEnabled,
    string? ItemModifyPath = null,
    string? ItemDeletePath = null,
    IReadOnlyDictionary<string, string>? UnitCodeMappings = null);

public sealed record U9AuthenticationRequest(
    string BaseUrl,
    string EnterpriseCode,
    string OrganizationCode,
    string UserCode,
    string ClientId,
    string ClientSecret);

public sealed record U9AuthenticationResult(string Token);

public sealed record U9BusinessRowResult(
    bool IsSuccess,
    string? ErrorMessage,
    string? U9ItemId,
    string? U9ItemCode);

public sealed record U9BusinessBatchResult(
    int ResponseCode,
    string? ResponseMessage,
    IReadOnlyList<U9BusinessRowResult> Rows);

public sealed record U9ItemReference(
    string? U9ItemId,
    string? U9ItemCode,
    string? U9ItemName = null,
    string? U9Specification = null,
    string? U9CategoryCode = null,
    string? U9CategoryName = null,
    string? U9UnitCode = null,
    int? U9ItemFormAttribute = null);

public sealed record U9ItemQueryResult(
    int ResponseCode,
    string? ResponseMessage,
    IReadOnlyList<U9ItemReference> Items);

public sealed record MaterialRemovalReadiness(
    Guid MaterialId,
    string MaterialCode,
    int PdmReferenceCount,
    bool IsPdmMaster,
    bool LocalDeletePreconditionsPassed,
    bool U9ReferenceCheckAvailable,
    bool SynchronizedDeleteAvailable,
    string Decision);

public sealed record U9UomReference(string? U9UomId, string? U9UomCode);

public sealed record U9UomQueryResult(
    int ResponseCode,
    string? ResponseMessage,
    IReadOnlyList<U9UomReference> Units);

public sealed record U9CustomerReference(string Code, string Name);

public sealed record U9CustomerQueryResult(
    int ResponseCode,
    string? ResponseMessage,
    IReadOnlyList<U9CustomerReference> Customers,
    int RawCount);

public sealed record U9MaterialSampleItem(
    string U9ItemId,
    string MaterialCode,
    string Name,
    string CategoryCode,
    string CategoryName,
    MaterialKind Kind,
    MaterialSupplyMode SupplyMode,
    string UnitCode,
    string? Specification,
    bool ExistsInPdm,
    bool CanImport,
    string Decision);

public sealed record U9MaterialSamplePreview(
    IReadOnlyList<string> CategoryCodes,
    int LimitPerCategory,
    IReadOnlyList<U9MaterialSampleItem> Items,
    DateTimeOffset QueriedAt);

public sealed record U9MaterialSampleImportResult(
    U9MaterialSamplePreview Preview,
    int CreatedCount,
    int RefreshedCount,
    int SkippedCount,
    IReadOnlyList<PdmMaterial> Materials,
    DateTimeOffset ImportedAt);

public sealed record MaterialSyncExecutionResult(
    PdmMaterial Material,
    MaterialSyncTask Task,
    bool Created,
    bool AlreadyExisted,
    bool Updated = false);

public static class U9MaterialContract
{
    public const string CreatePath = "/webapi/ItemMaster/Create";
    public const string QueryPath = "/webapi/ItemMaster/Query";
    public const string ModifyPath = "/webapi/ItemMaster/Modify";
    public const string DeletePath = "/webapi/ItemMaster/Delete";
    public const string UomQueryPath = "/webapi/UOM/Query";
    public const string CustomerReferencePath = "/webapi/GetCommonReference/Create";
    public const string PurchaseLinkPublicSegment = "PubDescSeg1";
}

public sealed record U9ConnectionTestResult(
    string BaseUrl,
    string EnterpriseCode,
    string OrganizationCode,
    string UserCode,
    string ClientId,
    DateTimeOffset TestedAt);

public interface IU9OpenApiClient
{
    Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken);
    Task<U9BusinessBatchResult> PostBatchAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken);
    Task<U9ItemQueryResult> QueryItemsAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken);
    Task<U9UomQueryResult> QueryUomsAsync(
        string baseUrl,
        string token,
        string payloadJson,
        CancellationToken cancellationToken);
    Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
        string baseUrl,
        string token,
        string payloadJson,
        CancellationToken cancellationToken);
}

public interface IMaterialRepository
{
    Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken);
    Task<PdmMaterial?> FindMaterialAsync(Guid materialId, CancellationToken cancellationToken);
    Task<PdmMaterial?> FindMaterialByCodeAsync(string materialCode, CancellationToken cancellationToken);
    Task<PdmMaterial?> FindMaterialBySourceBomItemAsync(Guid bomItemId, CancellationToken cancellationToken);
    Task<bool> HasMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken);
    Task<int> CountMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken);
    Task<string> ReserveNextMaterialCodeAsync(MaterialCategory category, CancellationToken cancellationToken);
    Task<PdmMaterial> CreateMaterialAsync(PdmMaterial material, MaterialCategory category, CancellationToken cancellationToken);
    Task<PdmMaterial> UpsertU9MaterialAsync(PdmMaterial material, CancellationToken cancellationToken);
    Task<PdmMaterial> UpdateMaterialAsync(PdmMaterial material, long expectedRowVersion, CancellationToken cancellationToken);
    Task<(PdmMaterial Material, MaterialSyncTask Task)> UpdateAndEnqueueAsync(
        PdmMaterial material,
        long expectedRowVersion,
        MaterialSyncTask task,
        AuditEntry audit,
        CancellationToken cancellationToken);
    Task<PdmMaterial> ArchiveMaterialAsync(Guid materialId, long expectedRowVersion, string actor, DateTimeOffset archivedAt, CancellationToken cancellationToken);
    Task<PdmMaterial> DeleteLocalMaterialAsync(Guid materialId, long expectedRowVersion, bool u9AbsenceConfirmed, CancellationToken cancellationToken);
    Task LinkBomItemAsync(Guid bomItemId, Guid materialId, string actor, DateTimeOffset linkedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, CancellationToken cancellationToken);
    Task<MaterialCategory?> FindCategoryAsync(string categoryCode, CancellationToken cancellationToken);
    Task<MaterialCategory> SaveCategoryAsync(MaterialCategory category, long? expectedRowVersion, CancellationToken cancellationToken);
    Task<MaterialCategory> AdvanceCategoryCounterAsync(MaterialCategory category, long minimumValue, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(CancellationToken cancellationToken);
    Task<MaterialCategoryRule?> FindCategoryRuleAsync(MaterialKind kind, CancellationToken cancellationToken);
    Task<MaterialCategoryRule> SaveCategoryRuleAsync(MaterialCategoryRule rule, CancellationToken cancellationToken);
    Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAndEnqueueAsync(
        Guid materialId,
        long expectedRowVersion,
        string u9CategoryCode,
        MaterialSyncTask task,
        AuditEntry audit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken);
    Task<MaterialSyncTask?> FindSyncTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<MaterialSyncTask> RetrySyncTaskAsync(
        Guid taskId,
        string payloadJson,
        string payloadSha256,
        DateTimeOffset retriedAt,
        CancellationToken cancellationToken);
    Task<MaterialSyncTask> BeginSyncTaskAsync(Guid taskId, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task<(PdmMaterial Material, MaterialSyncTask Task)> CompleteSyncTaskAsync(
        Guid taskId,
        string? u9ItemId,
        string u9ItemCode,
        string responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken);
    Task<MaterialSyncTask> FailSyncTaskAsync(
        Guid taskId,
        MaterialSyncStatus status,
        string error,
        string? responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken);
    Task<U9MaterialIntegrationConfiguration> GetIntegrationConfigurationAsync(CancellationToken cancellationToken);
    Task<U9MaterialIntegrationConfiguration> SaveIntegrationConfigurationAsync(U9MaterialIntegrationConfiguration configuration, CancellationToken cancellationToken);
}

public interface IU9SecretProtector
{
    string Protect(string secret);
    string Unprotect(string ciphertext);
}
