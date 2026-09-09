using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlU9InventoryRepository : IU9InventoryRepository
{
    private readonly string connectionString;

    public MySqlU9InventoryRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。 ");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<U9InventorySyncSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<SettingsRow>(new CommandDefinition(
            "SELECT * FROM u9_inventory_sync_setting WHERE id=1",
            cancellationToken: cancellationToken));
        return MapSettings(row);
    }

    public async Task<U9InventorySyncSettings> SaveSettingsAsync(U9InventorySyncSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE u9_inventory_sync_setting
            SET auto_sync_enabled=@AutoSyncEnabled,
                sync_interval_minutes=@SyncIntervalMinutes,
                query_path=@QueryPath,
                updated_by=@UpdatedBy,
                updated_at=@UpdatedAt
            WHERE id=1
            """, settings, cancellationToken: cancellationToken));
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<U9InventorySyncRun?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<RunRow>(new CommandDefinition(
            "SELECT * FROM u9_inventory_sync_run ORDER BY started_at DESC LIMIT 1",
            cancellationToken: cancellationToken));
        return row is null ? null : MapRun(row);
    }

    public async Task<U9InventorySyncRun> SaveRunAsync(U9InventorySyncRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO u9_inventory_sync_run(
                id,trigger_kind,status,source_row_count,stored_row_count,material_count,last_error,started_at,completed_at)
            VALUES(@Id,@TriggerKind,@Status,@SourceRowCount,@StoredRowCount,@MaterialCount,@LastError,@StartedAt,@CompletedAt)
            ON DUPLICATE KEY UPDATE
                status=VALUES(status),source_row_count=VALUES(source_row_count),stored_row_count=VALUES(stored_row_count),
                material_count=VALUES(material_count),last_error=VALUES(last_error),completed_at=VALUES(completed_at)
            """, new
        {
            run.Id,
            run.TriggerKind,
            Status = run.Status.ToString(),
            run.SourceRowCount,
            run.StoredRowCount,
            run.MaterialCount,
            run.LastError,
            run.StartedAt,
            run.CompletedAt
        }, cancellationToken: cancellationToken));
        return run;
    }

    public async Task<IReadOnlySet<string>> ListActiveMaterialCodesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var codes = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT material_code
            FROM material_master
            WHERE is_archived=0 AND material_code IS NOT NULL AND TRIM(material_code)<>''
            """, cancellationToken: cancellationToken));
        return codes.Select(code => code.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task ReplaceSnapshotAsync(
        Guid snapshotRunId,
        IReadOnlyList<U9InventorySourceRow> rows,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await InsertRowsAsync(connection, transaction, snapshotRunId, rows, refreshedAt, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE u9_inventory_sync_setting SET current_snapshot_run_id=@SnapshotRunId WHERE id=1",
                new { SnapshotRunId = snapshotRunId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM u9_inventory_snapshot WHERE snapshot_run_id<>@SnapshotRunId",
                new { SnapshotRunId = snapshotRunId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RefreshMaterialAsync(
        string materialCode,
        IReadOnlyList<U9InventorySourceRow> rows,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var snapshotRunId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT current_snapshot_run_id FROM u9_inventory_sync_setting WHERE id=1 FOR UPDATE",
                transaction: transaction, cancellationToken: cancellationToken));
            if (snapshotRunId is null || snapshotRunId == Guid.Empty)
            {
                snapshotRunId = Guid.NewGuid();
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE u9_inventory_sync_setting SET current_snapshot_run_id=@SnapshotRunId WHERE id=1",
                    new { SnapshotRunId = snapshotRunId }, transaction, cancellationToken: cancellationToken));
            }
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM u9_inventory_snapshot
                WHERE snapshot_run_id=@SnapshotRunId AND material_code=@MaterialCode
                """, new { SnapshotRunId = snapshotRunId.Value, MaterialCode = materialCode.Trim() }, transaction, cancellationToken: cancellationToken));
            await InsertRowsAsync(connection, transaction, snapshotRunId.Value, rows, refreshedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<U9InventoryPage> ListAsync(U9InventoryFilters filters, CancellationToken cancellationToken)
    {
        filters = InventorySimilarity.Prepare(filters);
        await using var connection = await OpenAsync(cancellationToken);
        var page = Math.Max(1, filters.Page);
        var pageSize = Math.Clamp(filters.PageSize, 1, 200);
        var parameters = new
        {
            MaterialCode = Clean(filters.MaterialCode),
            ItemName = Like(filters.ItemName),
            Specification = Like(filters.Specification),
            Brand = Clean(filters.Brand),
            Warehouse = Like(filters.Warehouse),
            ProjectCode = Like(filters.ProjectCode),
            Subproject = Like(filters.Subproject),
            PositiveStockOnly = filters.PositiveStockOnly,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };
        const string fromAndFilters = """
            FROM u9_inventory_snapshot inventory
            INNER JOIN u9_inventory_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=inventory.snapshot_run_id
            LEFT JOIN material_master material ON material.material_code=inventory.material_code
            LEFT JOIN project project ON project.code=inventory.project_code
            WHERE (@MaterialCode IS NULL OR inventory.material_code=@MaterialCode)
              AND (@ItemName IS NULL OR inventory.item_name LIKE @ItemName)
              AND (@Specification IS NULL OR COALESCE(inventory.specification,material.specification,'') LIKE @Specification)
              AND (@Brand IS NULL OR COALESCE(material.brand,'')=@Brand)
              AND (@Warehouse IS NULL OR inventory.warehouse_code LIKE @Warehouse OR inventory.warehouse_name LIKE @Warehouse)
              AND (@ProjectCode IS NULL OR COALESCE(inventory.project_code,'') LIKE @ProjectCode)
              AND (@Subproject IS NULL OR COALESCE(inventory.subproject,'') LIKE @Subproject)
              AND (@PositiveStockOnly=0 OR inventory.stock_quantity>0)
            """;
        var total = filters.SimilarSpecification is null
            ? await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) " + fromAndFilters, parameters, cancellationToken: cancellationToken)) : 0;
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition("""
            SELECT inventory.snapshot_run_id,inventory.organization_code,inventory.warehouse_code,inventory.warehouse_name,
                   inventory.material_code,inventory.item_name,material.brand,
                   COALESCE(inventory.specification,material.specification) specification,
                   inventory.project_code,COALESCE(NULLIF(inventory.project_name,''),project.name) project_name,
                   inventory.subproject,inventory.stock_quantity,inventory.available_quantity,inventory.reserved_quantity,
                   inventory.unavailable_quantity,inventory.bin_code,inventory.bin_name,inventory.storage_type,inventory.refreshed_at
            """ + "\n" + fromAndFilters + "\n" + """
            ORDER BY inventory.warehouse_name,inventory.material_code,inventory.project_code,inventory.subproject,inventory.id
            """ + (filters.SimilarSpecification is null ? "\nLIMIT @Offset,@PageSize" : string.Empty), parameters, cancellationToken: cancellationToken));
        var items = rows.Select(MapSnapshot).ToArray();
        if (filters.SimilarSpecification is not null)
        {
            var matches = InventorySimilarity.Match(items, filters.SimilarSpecification, cancellationToken);
            total = matches.Length;
            items = matches.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        }
        var lastSuccessful = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("""
            SELECT completed_at FROM u9_inventory_sync_run
            WHERE status='Succeeded' AND completed_at IS NOT NULL
            ORDER BY completed_at DESC LIMIT 1
            """, cancellationToken: cancellationToken));
        using var optionReader = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT DISTINCT inventory.warehouse_name
            FROM u9_inventory_snapshot inventory
            INNER JOIN u9_inventory_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=inventory.snapshot_run_id
            WHERE inventory.warehouse_name<>''
            ORDER BY inventory.warehouse_name;

            SELECT DISTINCT material.brand
            FROM u9_inventory_snapshot inventory
            INNER JOIN u9_inventory_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=inventory.snapshot_run_id
            INNER JOIN material_master material ON material.material_code=inventory.material_code
            WHERE COALESCE(material.brand,'')<>''
            ORDER BY material.brand;

            SELECT DISTINCT inventory.project_code
            FROM u9_inventory_snapshot inventory
            INNER JOIN u9_inventory_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=inventory.snapshot_run_id
            WHERE COALESCE(inventory.project_code,'')<>''
            ORDER BY inventory.project_code;

            SELECT DISTINCT inventory.project_code,inventory.subproject
            FROM u9_inventory_snapshot inventory
            INNER JOIN u9_inventory_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=inventory.snapshot_run_id
            WHERE COALESCE(inventory.project_code,'')<>'' AND COALESCE(inventory.subproject,'')<>''
            ORDER BY inventory.project_code,inventory.subproject
            """, cancellationToken: cancellationToken));
        var warehouseNames = (await optionReader.ReadAsync<string>()).ToArray();
        var brandNames = (await optionReader.ReadAsync<string>()).ToArray();
        var projectCodes = (await optionReader.ReadAsync<string>()).ToArray();
        var subprojectOptions = (await optionReader.ReadAsync<U9InventorySubprojectOption>()).ToArray();
        return new U9InventoryPage(items, total, page, pageSize,
            warehouseNames, brandNames, projectCodes, subprojectOptions, Utc(lastSuccessful));
    }

    private static async Task InsertRowsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Guid snapshotRunId,
        IReadOnlyList<U9InventorySourceRow> rows,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        var values = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.MaterialCode))
            .Select(row => new
            {
                SnapshotRunId = snapshotRunId,
                OrganizationCode = row.OrganizationCode.Trim(),
                WarehouseCode = row.WarehouseCode?.Trim() ?? string.Empty,
                WarehouseName = string.IsNullOrWhiteSpace(row.WarehouseName) ? row.WarehouseCode?.Trim() ?? "—" : row.WarehouseName.Trim(),
                MaterialCode = row.MaterialCode.Trim(),
                ItemName = row.ItemName.Trim(),
                Specification = Clean(row.Specification),
                ProjectCode = Clean(row.ProjectCode),
                ProjectName = Clean(row.ProjectName),
                Subproject = Clean(row.Subproject),
                row.StockQuantity,
                row.AvailableQuantity,
                row.ReservedQuantity,
                row.UnavailableQuantity,
                BinCode = Clean(row.BinCode),
                BinName = Clean(row.BinName),
                StorageType = Clean(row.StorageType),
                RefreshedAt = refreshedAt
            }).ToArray();
        if (values.Length == 0) return;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO u9_inventory_snapshot(
                snapshot_run_id,organization_code,warehouse_code,warehouse_name,material_code,item_name,specification,
                project_code,project_name,subproject,stock_quantity,available_quantity,reserved_quantity,unavailable_quantity,
                bin_code,bin_name,storage_type,refreshed_at)
            VALUES(@SnapshotRunId,@OrganizationCode,@WarehouseCode,@WarehouseName,@MaterialCode,@ItemName,@Specification,
                @ProjectCode,@ProjectName,@Subproject,@StockQuantity,@AvailableQuantity,@ReservedQuantity,@UnavailableQuantity,
                @BinCode,@BinName,@StorageType,@RefreshedAt)
            """, values, transaction, cancellationToken: cancellationToken));
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Like(string? value) => string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim()}%";
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : Utc(value.Value);
    private static U9InventorySyncSettings MapSettings(SettingsRow row) => new(
        row.AutoSyncEnabled, row.SyncIntervalMinutes, row.QueryPath, row.CurrentSnapshotRunId, row.UpdatedBy, Utc(row.UpdatedAt));
    private static U9InventorySyncRun MapRun(RunRow row) => new(
        row.Id, row.TriggerKind, Enum.Parse<U9InventorySyncStatus>(row.Status), row.SourceRowCount,
        row.StoredRowCount, row.MaterialCount, row.LastError, Utc(row.StartedAt), Utc(row.CompletedAt));
    private static U9InventorySnapshotRow MapSnapshot(SnapshotRow row) => new(
        row.SnapshotRunId, row.OrganizationCode, row.WarehouseCode, row.WarehouseName, row.MaterialCode,
        row.ItemName, row.Brand, row.Specification, row.ProjectCode, row.ProjectName, row.Subproject,
        row.StockQuantity, row.AvailableQuantity, row.ReservedQuantity, row.UnavailableQuantity,
        row.BinCode, row.BinName, row.StorageType, Utc(row.RefreshedAt));

    private sealed class SettingsRow
    {
        public bool AutoSyncEnabled { get; init; }
        public int SyncIntervalMinutes { get; init; }
        public string QueryPath { get; init; } = string.Empty;
        public Guid? CurrentSnapshotRunId { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public string TriggerKind { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int SourceRowCount { get; init; }
        public int StoredRowCount { get; init; }
        public int MaterialCount { get; init; }
        public string? LastError { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    private sealed class SnapshotRow
    {
        public Guid SnapshotRunId { get; init; }
        public string OrganizationCode { get; init; } = string.Empty;
        public string WarehouseCode { get; init; } = string.Empty;
        public string WarehouseName { get; init; } = string.Empty;
        public string MaterialCode { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string? Brand { get; init; }
        public string? Specification { get; init; }
        public string? ProjectCode { get; init; }
        public string? ProjectName { get; init; }
        public string? Subproject { get; init; }
        public decimal StockQuantity { get; init; }
        public decimal AvailableQuantity { get; init; }
        public decimal ReservedQuantity { get; init; }
        public decimal UnavailableQuantity { get; init; }
        public string? BinCode { get; init; }
        public string? BinName { get; init; }
        public string? StorageType { get; init; }
        public DateTime RefreshedAt { get; init; }
    }
}
