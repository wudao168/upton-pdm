using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class U9BomContract
{
    public const string QueryPath = "/webapi/BOM/Query";
    public const string CreatePath = "/webapi/BOM/Create";
    public const string ModifyPath = "/webapi/BOM/Modify";
    public const string DeletePath = "/webapi/BOM/Delete";
    public const string BatchUnApprovePath = "/webapi/BOM/BatchUnApprove";
    public const string BipQueryPagePath = "/webapi/BOM/BIPQueryPage";
}

public enum U9BomWriteOperation
{
    Create,
    Modify,
    Delete
}

public sealed record U9BomQueryCommand(
    string ItemCode,
    string? BomVersionCode = null,
    int? Lot = null,
    string? ProductUomCode = null);

public sealed record U9BomComponentReference(
    int? Sequence,
    string? ItemId,
    string? ItemCode,
    string? ItemName,
    string? ItemVersionCode,
    decimal? UsageQty,
    string? IssueUomCode,
    string? IssueUomName,
    decimal? ParentQty,
    int? ComponentType,
    bool? IsEffective,
    DateTimeOffset? EffectiveDate,
    DateTimeOffset? DisableDate,
    string? Remark,
    string? ProjectMapNum,
    int? IssueStyle,
    int? SupplyStyle,
    bool? IsPhantomPart,
    bool? IsDelete);

public sealed record U9BomReference(
    string? ItemId,
    string? ItemCode,
    string? ItemName,
    string? BomVersionCode,
    string? OrganizationCode,
    string? OrganizationName,
    int? AlternateType,
    int? Lot,
    string? ProductUomCode,
    string? ProductUomName,
    DateTimeOffset? EffectiveDate,
    DateTimeOffset? DisableDate,
    int? Status,
    int? BomSort,
    int? BomType,
    string? ProjectMapNum,
    string? Explain,
    string? EcoCode,
    bool? IsCostRoll,
    int? ItemSource,
    int? SysState,
    IReadOnlyList<U9BomComponentReference> Components,
    string? OtherId = null);

public sealed record U9BomComponentCommand(
    int Sequence,
    string ItemCode,
    decimal UsageQty,
    string IssueUomCode,
    decimal ParentQty = 1,
    string? ItemVersionCode = null,
    bool IsEffective = true,
    DateOnly? EffectiveDate = null,
    DateOnly? DisableDate = null,
    string? Remark = null,
    int ComponentType = 0,
    int IssueStyle = 0,
    int SupplyStyle = 0,
    bool IsPhantomPart = false,
    bool IsDelete = false);

public sealed record U9BomWriteCommand(
    U9BomWriteOperation Operation,
    string ItemCode,
    string BomVersionCode,
    string ProductUomCode,
    int Lot,
    IReadOnlyList<U9BomComponentCommand> Components,
    DateOnly? EffectiveDate = null,
    DateOnly? DisableDate = null,
    int BomSort = 0,
    int BomType = 0,
    string? ProjectMapNum = null,
    string? Explain = null,
    bool AllowEmptyCreate = false);

public sealed record U9BomWritePreview(
    U9BomWriteOperation Operation,
    string Path,
    string RequestPreview,
    string RequestSha256,
    string BaselineSha256,
    string RequiredConfirmation,
    int AddedComponentCount,
    int RetainedHistoricalComponentCount,
    DateTimeOffset GeneratedAt);

public sealed record U9BomWriteExecution(
    U9BomWritePreview Preview,
    U9BusinessBatchResult WriteResult,
    U9BomQueryResult Verification,
    DateTimeOffset ExecutedAt);

public sealed record U9BomQueryResult(
    int ResponseCode,
    string? ResponseMessage,
    IReadOnlyList<U9BomReference> Boms);

public sealed record U9BomOperationReference(
    long Id,
    long CurrentSysVersion,
    string OtherId);

public sealed record U9BomQueryExecution(
    string QueryPath,
    string RequestPreview,
    DateTimeOffset QueriedAt,
    U9BomQueryResult Result);

public interface IU9BomQueryClient
{
    Task<U9AuthenticationResult> AuthenticateAsync(
        U9AuthenticationRequest request,
        CancellationToken cancellationToken);
    Task<U9BomQueryResult> QueryBomsAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken);
    Task<U9BomOperationReference?> QueryBomOperationAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        string itemCode,
        string bomVersionCode,
        string otherId,
        CancellationToken cancellationToken);
}
