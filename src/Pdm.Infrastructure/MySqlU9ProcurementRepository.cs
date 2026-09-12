using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlU9ProcurementRepository : IU9ProcurementRepository
{
    private readonly string connectionString;

    public MySqlU9ProcurementRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。 ");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<U9ProcurementSyncSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<SettingsRow>(new CommandDefinition(
            "SELECT * FROM u9_procurement_sync_setting WHERE id=1", cancellationToken: cancellationToken));
        return MapSettings(row);
    }

    public async Task<U9ProcurementSyncSettings> SaveSettingsAsync(U9ProcurementSyncSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE u9_procurement_sync_setting
            SET auto_sync_enabled=@AutoSyncEnabled,
                sync_interval_minutes=@SyncIntervalMinutes,
                query_path=@QueryPath,
                updated_by=@UpdatedBy,
                updated_at=@UpdatedAt
            WHERE id=1
            """, settings, cancellationToken: cancellationToken));
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<U9ProcurementSyncRun?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<RunRow>(new CommandDefinition(
            "SELECT * FROM u9_procurement_sync_run ORDER BY started_at DESC LIMIT 1", cancellationToken: cancellationToken));
        return row is null ? null : MapRun(row);
    }

    public async Task<U9ProcurementSyncRun> SaveRunAsync(U9ProcurementSyncRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO u9_procurement_sync_run(
                id,trigger_kind,status,source_row_count,stored_row_count,project_count,last_error,started_at,completed_at)
            VALUES(@Id,@TriggerKind,@Status,@SourceRowCount,@StoredRowCount,@ProjectCount,@LastError,@StartedAt,@CompletedAt)
            ON DUPLICATE KEY UPDATE
                status=VALUES(status),source_row_count=VALUES(source_row_count),stored_row_count=VALUES(stored_row_count),
                project_count=VALUES(project_count),last_error=VALUES(last_error),completed_at=VALUES(completed_at)
            """, new
        {
            run.Id,
            run.TriggerKind,
            Status = run.Status.ToString(),
            run.SourceRowCount,
            run.StoredRowCount,
            run.ProjectCount,
            run.LastError,
            run.StartedAt,
            run.CompletedAt
        }, cancellationToken: cancellationToken));
        return run;
    }

    public async Task ReplaceSnapshotAsync(
        Guid snapshotRunId,
        IReadOnlyList<U9ProcurementSourceRow> rows,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var values = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.LineId)
                    && !string.IsNullOrWhiteSpace(row.MaterialCode)
                    && !string.IsNullOrWhiteSpace(row.DocumentNumber))
                .GroupBy(row => (row.RecordKind, row.LineId))
                .Select(group => group.Last())
                .Select(row => new
                {
                    SnapshotRunId = snapshotRunId,
                    OrganizationCode = row.OrganizationCode.Trim(),
                    RecordKind = row.RecordKind.Trim(),
                    LineId = row.LineId.Trim(),
                    SourcePrLineId = Clean(row.SourcePrLineId),
                    SourcePoLineId = Clean(row.SourcePoLineId),
                    DocumentNumber = row.DocumentNumber.Trim(),
                    row.LineNumber,
                    row.LineStatus,
                    row.IsCanceled,
                    row.BusinessDate,
                    row.SourceCreatedAt,
                    BuyerName = Clean(row.BuyerName),
                    row.MovementDate,
                    row.MovementQuantity,
                    MovementUnit = Clean(row.MovementUnit),
                    MaterialCode = row.MaterialCode.Trim(),
                    ItemName = row.ItemName.Trim(),
                    Specification = Clean(row.Specification),
                    Brand = Clean(row.Brand),
                    ProjectCode = Clean(row.ProjectCode),
                    ProjectName = Clean(row.ProjectName),
                    Subproject = Clean(row.Subproject),
                    row.RequestedQuantity,
                    row.ApprovedQuantity,
                    row.PurchaseQuantity,
                    row.ArrivedQuantity,
                    PurchaseRemark = Clean(row.PurchaseRemark),
                    row.DeliveryDate,
                    row.LatestDeliveryDate,
                    RefreshedAt = refreshedAt
                }).ToArray();
            if (values.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO u9_procurement_snapshot(
                        snapshot_run_id,organization_code,record_kind,line_id,source_pr_line_id,document_number,
                        line_number,line_status,is_canceled,business_date,material_code,item_name,specification,brand,
                        project_code,project_name,subproject,requested_quantity,approved_quantity,purchase_quantity,
                        arrived_quantity,purchase_remark,delivery_date,latest_delivery_date,refreshed_at,source_created_at,buyer_name,
                        movement_date,movement_quantity,movement_unit,source_po_line_id)
                    VALUES(@SnapshotRunId,@OrganizationCode,@RecordKind,@LineId,@SourcePrLineId,@DocumentNumber,
                        @LineNumber,@LineStatus,@IsCanceled,@BusinessDate,@MaterialCode,@ItemName,@Specification,@Brand,
                        @ProjectCode,@ProjectName,@Subproject,@RequestedQuantity,@ApprovedQuantity,@PurchaseQuantity,
                        @ArrivedQuantity,@PurchaseRemark,@DeliveryDate,@LatestDeliveryDate,@RefreshedAt,@SourceCreatedAt,@BuyerName,
                        @MovementDate,@MovementQuantity,@MovementUnit,@SourcePoLineId)
                    """, values, transaction, cancellationToken: cancellationToken));
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE u9_procurement_sync_setting SET current_snapshot_run_id=@SnapshotRunId WHERE id=1",
                new { SnapshotRunId = snapshotRunId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM u9_procurement_snapshot WHERE snapshot_run_id<>@SnapshotRunId",
                new { SnapshotRunId = snapshotRunId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<U9ProcurementSnapshotRow>> ListForProjectAsync(
        string rootProjectCode,
        string? subprojectCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var parameters = new
        {
            RootProjectCode = rootProjectCode.Trim(),
            SubprojectCode = Clean(subprojectCode)
        };
        const string projectMatch = """
            ((@SubprojectCode IS NULL
                AND snapshot.project_code=@RootProjectCode
                AND COALESCE(snapshot.subproject,'')='')
             OR (@SubprojectCode IS NOT NULL
                AND ((snapshot.project_code=@RootProjectCode AND snapshot.subproject=@SubprojectCode)
                     OR snapshot.project_code=@SubprojectCode)))
            """;
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition("""
            SELECT snapshot.*
            FROM u9_procurement_snapshot snapshot
            INNER JOIN u9_procurement_sync_setting setting
                ON setting.id=1 AND setting.current_snapshot_run_id=snapshot.snapshot_run_id
            WHERE
            """ + projectMatch + """
              OR (snapshot.record_kind IN ('RCV','ISSUE','MISC','TRANSFER','DIRECT') AND @SubprojectCode IS NOT NULL
                  AND COALESCE(snapshot.project_code,'')='' AND snapshot.subproject=@SubprojectCode)
              OR (snapshot.record_kind='PO' AND snapshot.source_pr_line_id IN (
                    SELECT requisition.line_id
                    FROM u9_procurement_snapshot requisition
                    WHERE requisition.snapshot_run_id=snapshot.snapshot_run_id
                      AND requisition.record_kind='PR'
                      AND
            """ + projectMatch.Replace("snapshot.", "requisition.", StringComparison.Ordinal) + """
              ))
            ORDER BY snapshot.record_kind,snapshot.document_number,snapshot.line_number
            """, parameters, cancellationToken: cancellationToken));
        return rows.Select(MapSnapshot).ToArray();
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : Utc(value.Value);
    private static U9ProcurementSyncSettings MapSettings(SettingsRow row) => new(
        row.AutoSyncEnabled, row.SyncIntervalMinutes, row.QueryPath, row.CurrentSnapshotRunId, row.UpdatedBy, Utc(row.UpdatedAt));
    private static U9ProcurementSyncRun MapRun(RunRow row) => new(
        row.Id, row.TriggerKind, Enum.Parse<U9InventorySyncStatus>(row.Status), row.SourceRowCount,
        row.StoredRowCount, row.ProjectCount, row.LastError, Utc(row.StartedAt), Utc(row.CompletedAt));
    private static U9ProcurementSnapshotRow MapSnapshot(SnapshotRow row) => new(
        row.SnapshotRunId, row.OrganizationCode, row.RecordKind, row.LineId, row.SourcePrLineId,
        row.DocumentNumber, row.LineNumber, row.LineStatus, row.IsCanceled, Utc(row.BusinessDate),
        row.MaterialCode, row.ItemName, row.Specification, row.Brand, row.ProjectCode, row.ProjectName,
        row.Subproject, row.RequestedQuantity, row.ApprovedQuantity, row.PurchaseQuantity, row.ArrivedQuantity,
        row.PurchaseRemark, Utc(row.DeliveryDate), Utc(row.LatestDeliveryDate), Utc(row.RefreshedAt))
    {
        SourceCreatedAt = Utc(row.SourceCreatedAt),
        BuyerName = row.BuyerName,
        MovementDate = Utc(row.MovementDate),
        MovementQuantity = row.MovementQuantity,
        MovementUnit = row.MovementUnit,
        SourcePoLineId = row.SourcePoLineId
    };

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
        public int ProjectCount { get; init; }
        public string? LastError { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    private sealed class SnapshotRow
    {
        public Guid SnapshotRunId { get; init; }
        public string OrganizationCode { get; init; } = string.Empty;
        public string RecordKind { get; init; } = string.Empty;
        public string LineId { get; init; } = string.Empty;
        public string? SourcePrLineId { get; init; }
        public string? SourcePoLineId { get; init; }
        public string DocumentNumber { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public int LineStatus { get; init; }
        public bool IsCanceled { get; init; }
        public DateTime? BusinessDate { get; init; }
        public DateTime? SourceCreatedAt { get; init; }
        public string? BuyerName { get; init; }
        public DateTime? MovementDate { get; init; }
        public decimal? MovementQuantity { get; init; }
        public string? MovementUnit { get; init; }
        public string MaterialCode { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string? Specification { get; init; }
        public string? Brand { get; init; }
        public string? ProjectCode { get; init; }
        public string? ProjectName { get; init; }
        public string? Subproject { get; init; }
        public decimal RequestedQuantity { get; init; }
        public decimal ApprovedQuantity { get; init; }
        public decimal PurchaseQuantity { get; init; }
        public decimal ArrivedQuantity { get; init; }
        public string? PurchaseRemark { get; init; }
        public DateTime? DeliveryDate { get; init; }
        public DateTime? LatestDeliveryDate { get; init; }
        public DateTime RefreshedAt { get; init; }
    }
}
