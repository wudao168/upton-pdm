namespace Upton.Pdm.Application;

public enum U9InventorySyncStatus
{
    Running,
    Succeeded,
    Failed
}

public sealed record U9InventorySourceRow(
    string OrganizationCode,
    string MaterialCode,
    string ItemName,
    string? Specification,
    string? WarehouseCode,
    string WarehouseName,
    string? BinCode,
    string? BinName,
    string? StorageType,
    string? ProjectCode,
    string? ProjectName,
    string? Subproject,
    decimal StockQuantity,
    decimal AvailableQuantity,
    decimal ReservedQuantity,
    decimal UnavailableQuantity);

public sealed record U9InventoryQueryResult(
    int ResponseCode,
    bool Success,
    string? ResponseMessage,
    IReadOnlyList<U9InventorySourceRow> Rows);

public interface IU9InventoryClient
{
    Task<U9InventoryQueryResult> QueryInventoryAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        string? materialCode,
        CancellationToken cancellationToken);
}

public sealed record U9InventorySyncSettings(
    bool AutoSyncEnabled,
    int SyncIntervalMinutes,
    string QueryPath,
    Guid? CurrentSnapshotRunId,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

public sealed record U9InventorySyncRun(
    Guid Id,
    string TriggerKind,
    U9InventorySyncStatus Status,
    int SourceRowCount,
    int StoredRowCount,
    int MaterialCount,
    string? LastError,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record U9InventorySnapshotRow(
    Guid SnapshotRunId,
    string OrganizationCode,
    string WarehouseCode,
    string WarehouseName,
    string MaterialCode,
    string ItemName,
    string? Brand,
    string? Specification,
    string? ProjectCode,
    string? ProjectName,
    string? Subproject,
    decimal StockQuantity,
    decimal AvailableQuantity,
    decimal ReservedQuantity,
    decimal UnavailableQuantity,
    string? BinCode,
    string? BinName,
    string? StorageType,
    DateTimeOffset RefreshedAt)
{
    public decimal? SimilarityPercent { get; init; }
}

public sealed record U9InventorySubprojectOption(
    string ProjectCode,
    string Subproject);

public sealed record U9InventoryPage(
    IReadOnlyList<U9InventorySnapshotRow> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> WarehouseNames,
    IReadOnlyList<string> BrandNames,
    IReadOnlyList<string> ProjectCodes,
    IReadOnlyList<U9InventorySubprojectOption> SubprojectOptions,
    DateTimeOffset? LastSuccessfulRefreshAt);

public sealed record U9InventoryFilters(
    string? MaterialCode,
    string? ItemName,
    string? Specification,
    string? Brand,
    string? Warehouse,
    string? ProjectCode,
    string? Subproject,
    bool PositiveStockOnly,
    int Page,
    int PageSize,
    string? SimilarSpecification = null);

public interface IU9InventoryRepository
{
    Task<U9InventorySyncSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<U9InventorySyncSettings> SaveSettingsAsync(U9InventorySyncSettings settings, CancellationToken cancellationToken);
    Task<U9InventorySyncRun?> GetLatestRunAsync(CancellationToken cancellationToken);
    Task<U9InventorySyncRun> SaveRunAsync(U9InventorySyncRun run, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> ListActiveMaterialCodesAsync(CancellationToken cancellationToken);
    Task ReplaceSnapshotAsync(Guid snapshotRunId, IReadOnlyList<U9InventorySourceRow> rows, DateTimeOffset refreshedAt, CancellationToken cancellationToken);
    Task RefreshMaterialAsync(string materialCode, IReadOnlyList<U9InventorySourceRow> rows, DateTimeOffset refreshedAt, CancellationToken cancellationToken);
    Task<U9InventoryPage> ListAsync(U9InventoryFilters filters, CancellationToken cancellationToken);
}
