namespace Upton.Pdm.Domain;

public sealed record MaterialStockPlan(
    string EnterpriseCode,
    string OrganizationCode,
    string MaterialCode,
    decimal SafetyStockQuantity,
    decimal? ReorderQuantity,
    bool IsEffective,
    string? UnitCode,
    string? UnitName,
    DateTimeOffset SyncedAt)
{
    public bool IsStocked => IsEffective && SafetyStockQuantity > 0;
    public decimal? StockQuantity { get; init; }
    public DateTimeOffset? InventorySyncedAt { get; init; }
    public int? InventoryPlanningMethod { get; init; }
    public int? InventoryPlanTime { get; init; }
    public bool? IsRequiredShipperSupply { get; init; }
    public int? SupplyMethod { get; init; }
}
