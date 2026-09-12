using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class U9ProcurementRecordKinds
{
    public const string PurchaseRequisition = "PR";
    public const string PurchaseOrder = "PO";
    public const string Receipt = "RCV";
    public const string MaterialIssue = "ISSUE";
    public const string MiscShipment = "MISC";
    public const string TransferReceipt = "TRANSFER";
    public const string DirectStockIssue = "DIRECT";
    public const string DeemedStockReceipt = "STOCKIN";
}

public sealed record U9ProcurementSourceRow(
    string OrganizationCode,
    string RecordKind,
    string LineId,
    string? SourcePrLineId,
    string DocumentNumber,
    int LineNumber,
    int LineStatus,
    bool IsCanceled,
    DateTimeOffset? BusinessDate,
    string MaterialCode,
    string ItemName,
    string? Specification,
    string? Brand,
    string? ProjectCode,
    string? ProjectName,
    string? Subproject,
    decimal RequestedQuantity,
    decimal ApprovedQuantity,
    decimal PurchaseQuantity,
    decimal ArrivedQuantity,
    string? PurchaseRemark,
    DateTimeOffset? DeliveryDate,
    DateTimeOffset? LatestDeliveryDate)
{
    public string? SourcePoLineId { get; init; }
    public DateTimeOffset? SourceCreatedAt { get; init; }
    public string? BuyerName { get; init; }
    public DateTimeOffset? MovementDate { get; init; }
    public decimal? MovementQuantity { get; init; }
    public string? MovementUnit { get; init; }
}

public sealed record U9ProcurementQueryResult(
    int ResponseCode,
    bool Success,
    string? ResponseMessage,
    IReadOnlyList<U9ProcurementSourceRow> Rows);

public interface IU9ProcurementClient
{
    Task<U9ProcurementQueryResult> QueryProcurementAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        IReadOnlyCollection<string> projectCodes,
        CancellationToken cancellationToken);
}

public sealed record U9ProcurementSyncSettings(
    bool AutoSyncEnabled,
    int SyncIntervalMinutes,
    string QueryPath,
    Guid? CurrentSnapshotRunId,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

public sealed record U9ProcurementSyncRun(
    Guid Id,
    string TriggerKind,
    U9InventorySyncStatus Status,
    int SourceRowCount,
    int StoredRowCount,
    int ProjectCount,
    string? LastError,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record U9ProcurementSnapshotRow(
    Guid SnapshotRunId,
    string OrganizationCode,
    string RecordKind,
    string LineId,
    string? SourcePrLineId,
    string DocumentNumber,
    int LineNumber,
    int LineStatus,
    bool IsCanceled,
    DateTimeOffset? BusinessDate,
    string MaterialCode,
    string ItemName,
    string? Specification,
    string? Brand,
    string? ProjectCode,
    string? ProjectName,
    string? Subproject,
    decimal RequestedQuantity,
    decimal ApprovedQuantity,
    decimal PurchaseQuantity,
    decimal ArrivedQuantity,
    string? PurchaseRemark,
    DateTimeOffset? DeliveryDate,
    DateTimeOffset? LatestDeliveryDate,
    DateTimeOffset RefreshedAt)
{
    public string? SourcePoLineId { get; init; }
    public DateTimeOffset? SourceCreatedAt { get; init; }
    public string? BuyerName { get; init; }
    public DateTimeOffset? MovementDate { get; init; }
    public decimal? MovementQuantity { get; init; }
    public string? MovementUnit { get; init; }
}

public interface IU9ProcurementRepository
{
    Task<U9ProcurementSyncSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<U9ProcurementSyncSettings> SaveSettingsAsync(U9ProcurementSyncSettings settings, CancellationToken cancellationToken);
    Task<U9ProcurementSyncRun?> GetLatestRunAsync(CancellationToken cancellationToken);
    Task<U9ProcurementSyncRun> SaveRunAsync(U9ProcurementSyncRun run, CancellationToken cancellationToken);
    Task ReplaceSnapshotAsync(Guid snapshotRunId, IReadOnlyList<U9ProcurementSourceRow> rows, DateTimeOffset refreshedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<U9ProcurementSnapshotRow>> ListForProjectAsync(string rootProjectCode, string? subprojectCode, CancellationToken cancellationToken);
}

public sealed record ProcurementDocumentDetail(
    string Kind,
    string DocumentNumber,
    int LineNumber,
    string LineStatus,
    int RawLineStatus,
    bool IsCanceled,
    decimal Quantity,
    decimal ArrivedQuantity,
    string? Remark,
    DateTimeOffset? BusinessDate,
    DateTimeOffset? DeliveryDate,
    DateTimeOffset? LatestDeliveryDate,
    string MatchKind);

public sealed record WarehouseMovementDetail(
    string Kind, string DocumentNumber, int LineNumber,
    DateTimeOffset Date, decimal Quantity, string? Unit)
{
    public string? LineId { get; init; }
}

public sealed record ProjectProcurementTrackingItem(
    int Sequence,
    string ProjectCode,
    string? SubprojectCode,
    string MaterialCode,
    string MaterialName,
    string? Specification,
    string? Remark,
    string? Brand,
    decimal? Quantity,
    string BomKind,
    string? ReleasePackageNumber,
    IReadOnlyList<string> PurchaseRequisitionNumbers,
    string PurchaseRequisitionStatus,
    DateTimeOffset? PurchaseRequisitionDeliveryDate,
    IReadOnlyList<string> PurchaseOrderNumbers,
    string PurchaseOrderStatus,
    decimal PurchaseQuantity,
    decimal ArrivedQuantity,
    string? PurchaseRemark,
    DateTimeOffset? PurchaseDeliveryDate,
    DateTimeOffset? LatestDeliveryDate,
    decimal? RequestedQuantity,
    decimal? ApprovedQuantity,
    IReadOnlyList<ProcurementDocumentDetail> Details)
{
    public bool HasDeliveryDelay { get; init; }
    public DateTimeOffset? PurchaseRequisitionCreatedAt { get; init; }
    public string? BuyerName { get; init; }
    public IReadOnlyList<WarehouseMovementDetail> WarehouseMovements { get; init; } = [];
    public bool IsWarehouseMovementRow { get; init; }
    public bool IsFullyReceived { get; init; }
}

public sealed record ProjectProcurementTrackingResult(
    Guid ProjectId,
    string ProjectCode,
    string? SubprojectCode,
    IReadOnlyList<ProjectProcurementTrackingItem> Items,
    DateTimeOffset? LastSuccessfulRefreshAt,
    string? LastRefreshError,
    bool HasPublishedBom);
