using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlMaterialRepository
{
    public async Task SaveStockPlansAsync(IReadOnlyList<MaterialStockPlan> plans, CancellationToken cancellationToken)
    {
        if (plans.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO material_stock_plan(enterprise_code,organization_code,material_code,safety_stock_quantity,
                reorder_quantity,is_effective,unit_code,unit_name,synced_at)
            VALUES(@EnterpriseCode,@OrganizationCode,@MaterialCode,@SafetyStockQuantity,@ReorderQuantity,@IsEffective,@UnitCode,@UnitName,@SyncedAt)
            ON DUPLICATE KEY UPDATE
                safety_stock_quantity=IF(VALUES(synced_at)>=synced_at,VALUES(safety_stock_quantity),safety_stock_quantity),
                reorder_quantity=IF(VALUES(synced_at)>=synced_at,VALUES(reorder_quantity),reorder_quantity),
                is_effective=IF(VALUES(synced_at)>=synced_at,VALUES(is_effective),is_effective),
                unit_code=IF(VALUES(synced_at)>=synced_at,VALUES(unit_code),unit_code),
                unit_name=IF(VALUES(synced_at)>=synced_at,VALUES(unit_name),unit_name),synced_at=GREATEST(synced_at,VALUES(synced_at))
            """, plans.Select(plan => new { plan.EnterpriseCode, plan.OrganizationCode, plan.MaterialCode,
                plan.SafetyStockQuantity, plan.ReorderQuantity, plan.IsEffective, plan.UnitCode, plan.UnitName,
                SyncedAt = plan.SyncedAt.UtcDateTime }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialStockPlan>> ListStockedMaterialPlansAsync(string enterpriseCode, string organizationCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<StockPlanRow>(new CommandDefinition("""
            SELECT enterprise_code,organization_code,material_code,safety_stock_quantity,reorder_quantity,is_effective,unit_code,unit_name,synced_at
            FROM material_stock_plan WHERE enterprise_code=@EnterpriseCode AND organization_code=@OrganizationCode
                AND is_effective=1 AND safety_stock_quantity>0 ORDER BY material_code
            """, new { EnterpriseCode = enterpriseCode, OrganizationCode = organizationCode }, cancellationToken: cancellationToken));
        return rows.Select(row => new MaterialStockPlan(row.EnterpriseCode, row.OrganizationCode, row.MaterialCode,
            row.SafetyStockQuantity, row.ReorderQuantity, row.IsEffective, row.UnitCode, row.UnitName,
            new DateTimeOffset(DateTime.SpecifyKind(row.SyncedAt, DateTimeKind.Utc)))).ToArray();
    }

    private sealed class StockPlanRow
    {
        public string EnterpriseCode { get; init; } = string.Empty;
        public string OrganizationCode { get; init; } = string.Empty;
        public string MaterialCode { get; init; } = string.Empty;
        public decimal SafetyStockQuantity { get; init; }
        public decimal? ReorderQuantity { get; init; }
        public bool IsEffective { get; init; }
        public string? UnitCode { get; init; }
        public string? UnitName { get; init; }
        public DateTime SyncedAt { get; init; }
    }
}
