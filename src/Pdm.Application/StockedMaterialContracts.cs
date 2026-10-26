using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record StockedMaterialScope(Guid CompanyId, string EnterpriseCode, string OrganizationCode);
public sealed record StockedMaterialSettings(int SyncIntervalMinutes = 240, bool NotifyLowStock = true,
    bool UseStandardAssistant = true, IReadOnlyList<string>? Recipients = null);
public sealed record StockedMaterialSnapshot(Guid MaterialId, MaterialStockPlan? Plan = null,
    decimal? StockQuantity = null, DateTimeOffset? InventorySyncedAt = null, Guid? LowStockEpisode = null,
    IReadOnlyList<string>? NotifiedRecipients = null, string? LastError = null,
    IReadOnlyList<U9InventorySourceRow>? InventoryRows = null)
{
    public bool IsFormal => Plan?.IsStocked == true;
    public string State => IsFormal ? "Formal" : "Draft";
    public bool IsLowStock => IsFormal && StockQuantity.HasValue && StockQuantity < Plan!.SafetyStockQuantity;
}
public sealed record StockedManagementMaterial(Guid Id, string MaterialCode, string Name, string? Specification,
    string? Brand, string UnitCode, string CategoryCode, string CategoryName, string? U9ItemId)
{
    // 仅供常备页展示，不写入通用料品主档，也不能用于BOM和其他业务。
    public PdmMaterial Display(DateTimeOffset at) => new(Id, MaterialCode, Name, MaterialKind.Standard, MaterialSupplyMode.Purchase,
        UnitCode, Specification, null, null, Brand, null, null, null, null, MaterialApprovalStatus.Approved,
        "system", at, CategoryCode, U9ItemId, MaterialCode, MaterialSyncStatus.Succeeded, "system", at, "system", at, 1,
        CategoryCode, U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C);
}
public static class StockedMaterialPolicy
{
    public static bool ManagementOnlyCategory(string? code) => code is "010402" or "010405" or "010502";
    public static bool ManagementOnlyCode(string code) => new[] { "010402", "010405", "010502" }.Any(prefix => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
public sealed record StockedMaterialCatalog(StockedMaterialScope Scope, long RowVersion,
    StockedMaterialSettings Settings, IReadOnlyList<StandardLibraryMembership> Memberships,
    IReadOnlyList<StockedMaterialSnapshot> Snapshots, DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? LastSuccessAt = null, string? LastError = null, bool SourceDiscoveryAttempted = false,
    IReadOnlyList<StockedManagementMaterial>? ManagementMaterials = null, bool BusinessSnapshotReady = false);
public sealed record StockedMaterialRow(PdmMaterial Material, IReadOnlyList<StandardLibraryCategory> Categories,
    StockedMaterialSnapshot Snapshot, MaterialAttachment? CoverImage, bool ManagementOnly = false);
public sealed record StockedMaterialPage(IReadOnlyList<StockedMaterialRow> Items, int Total, int LowStockCount,
    int Page, int PageSize, StockedMaterialScope Scope, DateTimeOffset? LastSuccessAt, string? LastError,
    int FormalCount = 0, int DraftCount = 0, int ManagementOnlyCount = 0);
public sealed record SaveStockedPlan(decimal SafetyStockQuantity, decimal ReorderQuantity, DateTimeOffset? ExpectedSyncedAt);
public sealed record StockedMaterialRecipient(string Username, string DisplayName, bool IsDefault);
public sealed record StockedMaterialSettingsView(StockedMaterialSettings Settings, long RowVersion,
    IReadOnlyList<StockedMaterialRecipient> Users, IReadOnlyList<string> EffectiveRecipients,
    StockedMaterialScope Scope, DateTimeOffset? LastSuccessAt, string? LastError);

public interface IStockedMaterialStore
{
    Task<StockedMaterialCatalog> ReadAsync(StockedMaterialScope scope, CancellationToken ct);
    Task<bool> SaveAsync(StockedMaterialCatalog catalog, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<StockedMaterialScope>> ListScopesAsync(CancellationToken ct);
}

// 串行化本服务实例内的维护、定时回读和成员变更；持久化版本继续防止多实例覆盖。
public sealed class StockedMaterialGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
