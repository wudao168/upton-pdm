namespace Upton.Pdm.Api;

public sealed record SaveMaterialRequest(
    string? MaterialCode,
    string Name,
    string Kind,
    string SupplyMode,
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

public sealed record CreateMaterialFromBomRequest(Guid ProjectId, Guid BomItemId);

public sealed record SaveMaterialCategoryRuleRequest(
    string PdmKind,
    string U9CategoryCode,
    string U9CategoryName,
    string DefaultSupplyMode,
    bool IsEnabled);

public sealed record SaveMaterialCategoryRequest(
    string Code,
    string Name,
    string? ParentCode,
    string? U9CategoryId,
    string? PdmKind,
    string DefaultSupplyMode,
    bool AllowCreate,
    bool IsVisible,
    bool IsActive,
    string? NumberPrefix,
    int SequenceLength,
    string? CounterScope,
    int SortOrder,
    long? ExpectedRowVersion = null);

public sealed record CalibrateMaterialCategoryCounterRequest(string LastMaterialCode);

public sealed record U9MaterialSampleRequest(
    IReadOnlyList<string>? CategoryCodes,
    int LimitPerCategory = 10);

public sealed record LinkBomMaterialRequest(Guid ProjectId, Guid BomItemId, Guid MaterialId);

public sealed record UpdateU9MaterialIntegrationRequest(
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
