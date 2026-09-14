using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectContentResetStore : IProjectContentResetStore
{
    private static readonly HashSet<string> ExcludedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "project", "project_assignment", "project_responsible", "project_user_access", "project_serial_number",
        "project_folder", "project_folder_permission", "project_content_reset_snapshot", "audit_entry",
        "document", "document_version", "document_user_access", "project_file", "project_file_version"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;

    public MySqlProjectContentResetStore(IOptions<PdmDatabaseOptions> options) => connectionString = options.Value.ConnectionString;

    public async Task<ProjectContentResetInspection> InspectAsync(IReadOnlyList<Guid> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0) return new(new Dictionary<string, int>(), [], false);
        await using var connection = await OpenAsync(cancellationToken);
        var ids = projectIds.Distinct().ToArray();
        var counts = new Dictionary<string, int>
        {
            ["受控图档"] = await CountAsync(connection, "SELECT COUNT(*) FROM document WHERE project_id IN @Ids AND deleted_at IS NULL AND purged_at IS NULL", ids, cancellationToken),
            ["项目文件"] = await CountAsync(connection, ProjectFileCountSql, ids, cancellationToken),
            ["BOM物料"] = await CountAsync(connection, "SELECT COUNT(*) FROM bom_item WHERE project_id IN @Ids AND deleted_at IS NULL", ids, cancellationToken),
            ["结构快照"] = await CountAsync(connection, "SELECT COUNT(*) FROM reference_snapshot WHERE project_id IN @Ids", ids, cancellationToken),
            ["BOM版本"] = await CountAsync(connection, "SELECT COUNT(*) FROM bom_version WHERE project_id IN @Ids", ids, cancellationToken),
            ["发布包"] = await CountAsync(connection, "SELECT COUNT(*) FROM release_package WHERE project_id IN @Ids", ids, cancellationToken),
            ["图纸审核"] = await CountAsync(connection, "SELECT COUNT(*) FROM drawing_review_package WHERE project_id IN @Ids", ids, cancellationToken),
            ["项目计划"] = await CountAsync(connection, "SELECT COUNT(*) FROM project_plan WHERE project_id IN @Ids", ids, cancellationToken),
            ["验证计划"] = await CountAsync(connection, "SELECT COUNT(*) FROM project_validation_plan WHERE project_id IN @Ids", ids, cancellationToken),
            ["备料及通知"] = await CountAsync(connection, "SELECT COUNT(*) FROM user_notification WHERE project_id IN @Ids", ids, cancellationToken)
        };

        var blockers = new List<string>();
        if (await ExistsAsync(connection, PublishedBomBlockerSql, ids, cancellationToken))
            blockers.Add("存在已发布的BOM，项目内容不能重置。");
        if (await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM release_package WHERE project_id IN @Ids AND state IN ('ProcessReview','Approval','Publishing'))", ids, cancellationToken))
            blockers.Add("存在正在审批或发布的发布包，请先撤回或结束流程。");
        if (await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM drawing_review_package WHERE project_id IN @Ids AND state IN ('InReview','WritingProperties'))", ids, cancellationToken))
            blockers.Add("存在正在进行的图纸审核或属性写回。");
        if (await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM document WHERE project_id IN @Ids AND checked_out_by IS NOT NULL)", ids, cancellationToken))
            blockers.Add("存在已签出的图档，请先存档或释放编辑权限。");
        if (await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM cad_property_writeback w INNER JOIN bom_item b ON b.id=w.bom_item_id WHERE b.project_id IN @Ids AND w.status IN ('Pending','InProgress'))", ids, cancellationToken))
            blockers.Add("存在正在执行的CAD属性写回任务。");
        if (await HasPendingProjectOutboxAsync(connection, null, ids, cancellationToken))
            blockers.Add("存在尚未处理完成的外部集成事件，请等待同步完成后重试。");

        return new(counts, blockers, counts.Values.Any(value => value > 0));
    }

    public async Task<ProjectContentResetSnapshotSummary> ResetAsync(Guid projectId, string projectCode, IReadOnlyList<Guid> projectIds, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ids = projectIds.Distinct().ToArray();
        var snapshotId = Guid.NewGuid();
        try
        {
            await LockProjectsAsync(connection, transaction, ids, cancellationToken);
            var inspection = await InspectAsync(connection, transaction, ids, cancellationToken);
            if (inspection.Blockers.Count > 0) throw new PdmRuleException(string.Join("；", inspection.Blockers));

            var payload = await CaptureAsync(connection, transaction, ids, snapshotId, cancellationToken);
            var compressed = Compress(JsonSerializer.Serialize(payload, JsonOptions));
            var expiresAt = now.AddDays(30);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_content_reset_snapshot(id,project_id,project_code,included_project_ids_json,reason,summary_json,payload_json,created_by,created_at,expires_at) VALUES(@Id,@ProjectId,@ProjectCode,@ProjectIds,@Reason,@Summary,@Payload,@Actor,@CreatedAt,@ExpiresAt)",
                new
                {
                    Id = snapshotId,
                    ProjectId = projectId,
                    ProjectCode = projectCode,
                    ProjectIds = JsonSerializer.Serialize(ids, JsonOptions),
                    Reason = reason,
                    Summary = JsonSerializer.Serialize(inspection.Counts, JsonOptions),
                    Payload = compressed,
                    Actor = actor,
                    CreatedAt = now.UtcDateTime,
                    ExpiresAt = expiresAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));

            await SetSoftDeletedAsync(connection, transaction, ids, snapshotId, actor, reason, now, cancellationToken);
            await DeleteCapturedRowsAsync(connection, transaction, payload.Tables, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(snapshotId, projectId, projectCode, ids, reason, inspection.Counts, actor, now, expiresAt, null, null, null);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProjectContentResetSnapshotSummary>> ListSnapshotsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition(SnapshotSelect + " WHERE project_id=@ProjectId ORDER BY created_at DESC", new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(MapSnapshot).ToArray();
    }

    public async Task<ProjectContentResetSnapshotSummary?> FindSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(SnapshotSelect + " WHERE id=@SnapshotId", new { SnapshotId = snapshotId }, cancellationToken: cancellationToken));
        return row is null ? null : MapSnapshot(row);
    }

    public async Task<ProjectContentResetSnapshotSummary> RestoreAsync(Guid snapshotId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(SnapshotPayloadSelect + " WHERE id=@SnapshotId FOR UPDATE", new { SnapshotId = snapshotId }, transaction, cancellationToken: cancellationToken))
                ?? throw new PdmNotFoundException("项目重置快照不存在。");
            if (row.RestoredAt is not null) throw new PdmRuleException("该重置快照已经恢复。");
            if (row.PurgedAt is not null || row.ExpiresAt <= now.UtcDateTime) throw new PdmRuleException("该重置快照已超过30天恢复期限。");

            var payload = JsonSerializer.Deserialize<ResetPayload>(Decompress(row.PayloadJson ?? throw new PdmRuleException("项目重置快照内容不存在。")), JsonOptions)
                ?? throw new PdmRuleException("项目重置快照损坏，无法恢复。");
            await InsertCapturedRowsAsync(connection, transaction, payload.Tables, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET deleted_at=NULL,deleted_by=NULL,delete_reason=NULL,purged_at=NULL,reset_snapshot_id=NULL,row_version=row_version+1,updated_at=@Now WHERE reset_snapshot_id=@SnapshotId; UPDATE project_file SET deleted_at=NULL,deleted_by=NULL,reset_snapshot_id=NULL,updated_by=@Actor,updated_at=@Now WHERE reset_snapshot_id=@SnapshotId; UPDATE project_content_reset_snapshot SET restored_by=@Actor,restored_at=@Now WHERE id=@SnapshotId;",
                new { SnapshotId = snapshotId, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return MapSnapshot(row with { RestoredBy = actor, RestoredAt = now.UtcDateTime });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project_content_reset_snapshot SET payload_json=COMPRESS('{}'),purged_at=@Now WHERE expires_at<=@Now AND purged_at IS NULL",
            new { Now = now.UtcDateTime }, cancellationToken: cancellationToken));
    }

    private static async Task<ProjectContentResetInspection> InspectAsync(MySqlConnection connection, MySqlTransaction transaction, Guid[] ids, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>
        {
            ["受控图档"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM document WHERE project_id IN @Ids AND deleted_at IS NULL AND purged_at IS NULL", ids, cancellationToken),
            ["项目文件"] = await CountAsync(connection, transaction, ProjectFileCountSql, ids, cancellationToken),
            ["BOM物料"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM bom_item WHERE project_id IN @Ids AND deleted_at IS NULL", ids, cancellationToken),
            ["结构快照"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM reference_snapshot WHERE project_id IN @Ids", ids, cancellationToken),
            ["BOM版本"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM bom_version WHERE project_id IN @Ids", ids, cancellationToken),
            ["发布包"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM release_package WHERE project_id IN @Ids", ids, cancellationToken),
            ["图纸审核"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM drawing_review_package WHERE project_id IN @Ids", ids, cancellationToken),
            ["项目计划"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM project_plan WHERE project_id IN @Ids", ids, cancellationToken),
            ["验证计划"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM project_validation_plan WHERE project_id IN @Ids", ids, cancellationToken),
            ["备料及通知"] = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM user_notification WHERE project_id IN @Ids", ids, cancellationToken)
        };
        var blockers = new List<string>();
        if (await ExistsAsync(connection, transaction, PublishedBomBlockerSql, ids, cancellationToken)) blockers.Add("存在已发布的BOM，项目内容不能重置");
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM release_package WHERE project_id IN @Ids AND state IN ('ProcessReview','Approval','Publishing'))", ids, cancellationToken)) blockers.Add("存在正在审批或发布的发布包");
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM drawing_review_package WHERE project_id IN @Ids AND state IN ('InReview','WritingProperties'))", ids, cancellationToken)) blockers.Add("存在正在进行的图纸审核或属性写回");
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM document WHERE project_id IN @Ids AND checked_out_by IS NOT NULL)", ids, cancellationToken)) blockers.Add("存在已签出的图档");
        if (await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM cad_property_writeback w INNER JOIN bom_item b ON b.id=w.bom_item_id WHERE b.project_id IN @Ids AND w.status IN ('Pending','InProgress'))", ids, cancellationToken)) blockers.Add("存在正在执行的CAD属性写回任务");
        if (await HasPendingProjectOutboxAsync(connection, transaction, ids, cancellationToken)) blockers.Add("存在尚未处理完成的外部集成事件");
        return new(counts, blockers, counts.Values.Any(value => value > 0));
    }

    private static async Task<ResetPayload> CaptureAsync(MySqlConnection connection, MySqlTransaction transaction, Guid[] ids, Guid snapshotId, CancellationToken cancellationToken)
    {
        var metas = await LoadTableMetadataAsync(connection, transaction, cancellationToken);
        var snapshots = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas.Values.Where(meta => meta.Columns.Contains("project_id", StringComparer.OrdinalIgnoreCase) && !ExcludedTables.Contains(meta.Name)))
        {
            var rows = await ReadRowsAsync(connection, transaction, meta, $"`project_id` IN @Ids", new { Ids = ids }, cancellationToken);
            if (rows.Count > 0) snapshots[meta.Name] = new(meta.Name, rows);
        }

        if (metas.TryGetValue("bom_material_link", out var linkMeta))
        {
            var rows = await ReadRowsAsync(connection, transaction, linkMeta, "`bom_item_id` IN (SELECT id FROM bom_item WHERE project_id IN @Ids)", new { Ids = ids }, cancellationToken);
            if (rows.Count > 0) snapshots[linkMeta.Name] = new(linkMeta.Name, rows);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var relation in metas.Values.SelectMany(meta => meta.ForeignKeys).Where(relation => snapshots.ContainsKey(relation.ParentTable) && !ExcludedTables.Contains(relation.ChildTable)))
            {
                var parent = snapshots[relation.ParentTable];
                var parentValues = parent.Rows.Select(row => row.GetValueOrDefault(relation.ParentColumn)).Where(value => value is not null).Distinct().ToArray();
                if (parentValues.Length == 0 || !metas.TryGetValue(relation.ChildTable, out var childMeta)) continue;
                var parameters = new DynamicParameters();
                var clauses = new List<string>();
                for (var index = 0; index < parentValues.Length; index++)
                {
                    var name = $"p{index}";
                    parameters.Add(name, DeserializeCell(parentValues[index]!));
                    clauses.Add($"`{relation.ChildColumn}`=@{name}");
                }
                var rows = await ReadRowsAsync(connection, transaction, childMeta, string.Join(" OR ", clauses), parameters, cancellationToken);
                if (rows.Count == 0) continue;
                if (!snapshots.TryGetValue(childMeta.Name, out var existing))
                {
                    snapshots[childMeta.Name] = new(childMeta.Name, rows);
                    changed = true;
                }
                else
                {
                    var known = existing.Rows.Select(row => RowKey(childMeta, row)).ToHashSet(StringComparer.Ordinal);
                    var additions = rows.Where(row => known.Add(RowKey(childMeta, row))).ToArray();
                    if (additions.Length > 0)
                    {
                        existing.Rows.AddRange(additions);
                        changed = true;
                    }
                }
            }
        }
        return new(snapshotId, snapshots.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToList());
    }

    private static async Task SetSoftDeletedAsync(MySqlConnection connection, MySqlTransaction transaction, Guid[] ids, Guid snapshotId, string actor, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET deleted_at=@Now,deleted_by=@Actor,delete_reason=@Reason,purged_at=@Now,reset_snapshot_id=@SnapshotId,row_version=row_version+1,updated_at=@Now WHERE project_id IN @Ids AND deleted_at IS NULL AND purged_at IS NULL; UPDATE project_file f INNER JOIN project_folder folder ON folder.id=f.folder_id SET f.deleted_at=@Now,f.deleted_by=@Actor,f.reset_snapshot_id=@SnapshotId,f.updated_by=@Actor,f.updated_at=@Now WHERE f.deleted_at IS NULL AND (folder.target_project_id IN @Ids OR (folder.root_project_id IN @Ids AND folder.target_project_id IS NULL));",
            new { Ids = ids, SnapshotId = snapshotId, Actor = actor, Reason = reason, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task DeleteCapturedRowsAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<TableSnapshot> tables, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", transaction: transaction, cancellationToken: cancellationToken));
        try
        {
            foreach (var table in tables)
            {
                var primaryKeys = await LoadPrimaryKeysAsync(connection, transaction, table.Name, cancellationToken);
                if (primaryKeys.Count == 0) throw new InvalidOperationException($"项目内容表 {table.Name} 没有主键，已中止重置。");
                foreach (var row in table.Rows)
                {
                    var parameters = new DynamicParameters();
                    var predicates = new List<string>();
                    for (var index = 0; index < primaryKeys.Count; index++)
                    {
                        var parameter = $"k{index}";
                        parameters.Add(parameter, DeserializeCell(row[primaryKeys[index]]));
                        predicates.Add($"`{primaryKeys[index]}`=@{parameter}");
                    }
                    await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM `{table.Name}` WHERE {string.Join(" AND ", predicates)}", parameters, transaction, cancellationToken: cancellationToken));
                }
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=1", transaction: transaction, cancellationToken: cancellationToken));
        }
    }

    private static async Task InsertCapturedRowsAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<TableSnapshot> tables, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", transaction: transaction, cancellationToken: cancellationToken));
        try
        {
            foreach (var table in tables)
            foreach (var row in table.Rows)
            {
                var parameters = new DynamicParameters();
                var columns = row.Keys.ToArray();
                for (var index = 0; index < columns.Length; index++) parameters.Add($"v{index}", DeserializeCell(row[columns[index]]));
                var sql = $"INSERT INTO `{table.Name}` ({string.Join(',', columns.Select(column => $"`{column}`"))}) VALUES ({string.Join(',', columns.Select((_, index) => $"@v{index}"))})";
                await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=1", transaction: transaction, cancellationToken: cancellationToken));
        }
    }

    private static async Task<Dictionary<string, TableMeta>> LoadTableMetadataAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        var columns = await connection.QueryAsync<ColumnRow>(new CommandDefinition("SELECT table_name,column_name,extra FROM information_schema.columns WHERE table_schema=DATABASE() ORDER BY table_name,ordinal_position", transaction: transaction, cancellationToken: cancellationToken));
        var metas = columns.GroupBy(row => row.TableName, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => new TableMeta(group.Key, group.Where(row => !row.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).Select(row => row.ColumnName).ToList(), [], []), StringComparer.OrdinalIgnoreCase);
        var keys = await connection.QueryAsync<KeyRow>(new CommandDefinition("SELECT table_name,column_name,ordinal_position FROM information_schema.key_column_usage WHERE table_schema=DATABASE() AND constraint_name='PRIMARY' ORDER BY table_name,ordinal_position", transaction: transaction, cancellationToken: cancellationToken));
        foreach (var group in keys.GroupBy(row => row.TableName, StringComparer.OrdinalIgnoreCase)) if (metas.TryGetValue(group.Key, out var meta)) meta.PrimaryKeys.AddRange(group.OrderBy(row => row.OrdinalPosition).Select(row => row.ColumnName));
        var foreignKeys = await connection.QueryAsync<ForeignKeyRow>(new CommandDefinition("SELECT table_name child_table,column_name child_column,referenced_table_name parent_table,referenced_column_name parent_column FROM information_schema.key_column_usage WHERE table_schema=DATABASE() AND referenced_table_name IS NOT NULL", transaction: transaction, cancellationToken: cancellationToken));
        foreach (var relation in foreignKeys) if (metas.TryGetValue(relation.ChildTable, out var meta)) meta.ForeignKeys.Add(new(relation.ChildTable, relation.ChildColumn, relation.ParentTable, relation.ParentColumn));
        return metas;
    }

    private static async Task<List<Dictionary<string, SnapshotCell?>>> ReadRowsAsync(MySqlConnection connection, MySqlTransaction transaction, TableMeta meta, string predicate, object parameters, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {string.Join(',', meta.Columns.Select(column => $"`{column}`"))} FROM `{meta.Name}` WHERE {predicate}";
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        return rows.Select(row => ((IDictionary<string, object?>)row).ToDictionary(item => item.Key, item => SerializeCell(item.Value), StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static SnapshotCell? SerializeCell(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => new("bytes", Convert.ToBase64String(bytes)),
        DateTime dateTime => new("datetime", dateTime.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dateTimeOffset => new("datetimeoffset", dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)),
        bool boolean => new("bool", boolean ? "1" : "0"),
        sbyte or byte or short or ushort or int or uint or long or ulong => new("integer", Convert.ToString(value, CultureInfo.InvariantCulture)),
        float or double or decimal => new("decimal", Convert.ToString(value, CultureInfo.InvariantCulture)),
        Guid guid => new("guid", guid.ToString()),
        _ => new("string", Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static object? DeserializeCell(SnapshotCell? cell) => cell?.Kind switch
    {
        null => null,
        "bytes" => Convert.FromBase64String(cell.Value!),
        "datetime" => DateTime.Parse(cell.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "datetimeoffset" => DateTimeOffset.Parse(cell.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "bool" => cell.Value == "1",
        "integer" => long.Parse(cell.Value!, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(cell.Value!, CultureInfo.InvariantCulture),
        "guid" => Guid.Parse(cell.Value!),
        _ => cell.Value
    };

    private static string RowKey(TableMeta meta, IReadOnlyDictionary<string, SnapshotCell?> row) => string.Join('|', meta.PrimaryKeys.Select(key => row.GetValueOrDefault(key)?.Value ?? "null"));
    private static byte[] Compress(string value) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) using (var writer = new StreamWriter(gzip, Encoding.UTF8)) writer.Write(value); return output.ToArray(); }
    private static string Decompress(byte[] value) { using var input = new MemoryStream(value); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var reader = new StreamReader(gzip, Encoding.UTF8); return reader.ReadToEnd(); }
    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new MySqlConnection(connectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private static Task<int> CountAsync(MySqlConnection connection, string sql, Guid[] ids, CancellationToken token) => connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Ids = ids }, cancellationToken: token));
    private static Task<int> CountAsync(MySqlConnection connection, MySqlTransaction transaction, string sql, Guid[] ids, CancellationToken token) => connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Ids = ids }, transaction, cancellationToken: token));
    private static async Task<bool> ExistsAsync(MySqlConnection connection, string sql, Guid[] ids, CancellationToken token) => await CountAsync(connection, sql, ids, token) == 1;
    private static async Task<bool> ExistsAsync(MySqlConnection connection, MySqlTransaction transaction, string sql, Guid[] ids, CancellationToken token) => await CountAsync(connection, transaction, sql, ids, token) == 1;
    private static async Task<bool> HasPendingProjectOutboxAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid[] ids, CancellationToken token)
    {
        foreach (var id in ids)
        {
            var value = id.ToString();
            if (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM integration_outbox WHERE processed_at IS NULL AND (aggregate_id=@ProjectId OR CAST(payload_json AS CHAR) LIKE @ProjectPattern))",
                new { ProjectId = value, ProjectPattern = $"%{value}%" }, transaction, cancellationToken: token)) == 1) return true;
        }
        return false;
    }
    private static async Task LockProjectsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid[] ids, CancellationToken token)
    {
        var lockedIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM project WHERE id IN @Ids FOR UPDATE",
            new { Ids = ids }, transaction, cancellationToken: token))).ToArray();
        if (lockedIds.Length != ids.Length) throw new PdmNotFoundException("重置范围内有项目不存在。");
    }
    private static async Task<IReadOnlyList<string>> LoadPrimaryKeysAsync(MySqlConnection connection, MySqlTransaction transaction, string table, CancellationToken token) => (await connection.QueryAsync<string>(new CommandDefinition("SELECT column_name FROM information_schema.key_column_usage WHERE table_schema=DATABASE() AND table_name=@Table AND constraint_name='PRIMARY' ORDER BY ordinal_position", new { Table = table }, transaction, cancellationToken: token))).ToArray();

    private static ProjectContentResetSnapshotSummary MapSnapshot(SnapshotRow row) => new(row.Id, row.ProjectId, row.ProjectCode,
        JsonSerializer.Deserialize<Guid[]>(row.IncludedProjectIdsJson, JsonOptions) ?? [], row.Reason,
        JsonSerializer.Deserialize<Dictionary<string, int>>(row.SummaryJson, JsonOptions) ?? new(), row.CreatedBy,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)), row.RestoredBy,
        row.RestoredAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.RestoredAt.Value, DateTimeKind.Utc)), row.PurgedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PurgedAt.Value, DateTimeKind.Utc)));

    private const string SnapshotSelect = "SELECT id Id,project_id ProjectId,project_code ProjectCode,included_project_ids_json IncludedProjectIdsJson,reason Reason,summary_json SummaryJson,CAST(NULL AS BINARY) PayloadJson,created_by CreatedBy,created_at CreatedAt,expires_at ExpiresAt,restored_by RestoredBy,restored_at RestoredAt,purged_at PurgedAt FROM project_content_reset_snapshot";
    private const string SnapshotPayloadSelect = "SELECT id Id,project_id ProjectId,project_code ProjectCode,included_project_ids_json IncludedProjectIdsJson,reason Reason,summary_json SummaryJson,payload_json PayloadJson,created_by CreatedBy,created_at CreatedAt,expires_at ExpiresAt,restored_by RestoredBy,restored_at RestoredAt,purged_at PurgedAt FROM project_content_reset_snapshot";
    private const string ProjectFileCountSql = "SELECT COUNT(*) FROM project_file f INNER JOIN project_folder folder ON folder.id=f.folder_id WHERE f.deleted_at IS NULL AND (folder.target_project_id IN @Ids OR (folder.root_project_id IN @Ids AND folder.target_project_id IS NULL))";
    private const string PublishedBomBlockerSql = "SELECT EXISTS(SELECT 1 FROM release_package WHERE project_id IN @Ids AND (state='Published' OR published_at IS NOT NULL) UNION ALL SELECT 1 FROM bom_version WHERE project_id IN @Ids AND (state='Released' OR released_at IS NOT NULL) UNION ALL SELECT 1 FROM manufacturing_bom_baseline WHERE project_id IN @Ids)";
    private sealed record SnapshotCell(string Kind, string? Value);
    private sealed record TableSnapshot(string Name, List<Dictionary<string, SnapshotCell?>> Rows);
    private sealed record ResetPayload(Guid SnapshotId, List<TableSnapshot> Tables);
    private sealed record TableMeta(string Name, List<string> Columns, List<string> PrimaryKeys, List<ForeignKeyMeta> ForeignKeys);
    private sealed record ForeignKeyMeta(string ChildTable, string ChildColumn, string ParentTable, string ParentColumn);
    private sealed record ColumnRow(string TableName, string ColumnName, string Extra);
    private sealed record KeyRow(string TableName, string ColumnName, int OrdinalPosition);
    private sealed record ForeignKeyRow(string ChildTable, string ChildColumn, string ParentTable, string ParentColumn);
    private sealed record SnapshotRow(Guid Id, Guid ProjectId, string ProjectCode, string IncludedProjectIdsJson, string Reason, string SummaryJson, byte[]? PayloadJson, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt, string? RestoredBy, DateTime? RestoredAt, DateTime? PurgedAt);
}
