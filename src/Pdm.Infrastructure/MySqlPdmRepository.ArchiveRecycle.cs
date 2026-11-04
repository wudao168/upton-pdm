using System.Data.Common;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IArchiveRecycleStore
{
    public async Task<IReadOnlyList<ArchiveRecycleBatch>> ListRecycleArchiveBatchesAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<RecycleBatchRow>(new CommandDefinition(
            "SELECT batch_id Id,committed_at At,actor Actor,JSON_LENGTH(JSON_EXTRACT(result_json,'$.results')) FileCount FROM archive_batch_receipt WHERE project_id=@projectId AND committed_at IS NOT NULL ORDER BY committed_at DESC LIMIT 100",
            new { projectId }, cancellationToken: ct));
        return rows.Select(row => new ArchiveRecycleBatch(row.Id, AsUtc(row.At), row.Actor, row.FileCount)).ToArray();
    }

    public async Task<ArchiveRecycleInspection> InspectRecycleArchiveAsync(Guid projectId, Guid batchId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await InspectRecycleArchive(connection, null, projectId, batchId, ct);
    }

    private async Task<ArchiveRecycleInspection> InspectRecycleArchive(DbConnection connection, DbTransaction? tx, Guid projectId, Guid batchId, CancellationToken ct)
    {
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT result_json FROM archive_batch_receipt WHERE batch_id=@batchId AND project_id=@projectId AND committed_at IS NOT NULL", new { batchId, projectId }, tx, cancellationToken: ct));
        var receipt = json is null ? null : JsonSerializer.Deserialize<ArchiveBatchReceipt>(json, jsonOptions);
        if (receipt is null) throw new PdmNotFoundException("该项目中没有已成功提交的对应存档批次。");
        var ids = receipt.Results.Select(item => item.Document.Id).Distinct().ToArray();
        var rows = (await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            "SELECT * FROM document WHERE id IN @ids AND project_id=@projectId", new { ids, projectId }, tx, cancellationToken: ct))).ToArray();
        var versions = (await connection.QueryAsync<RecycleVersionRow>(new CommandDefinition(
            "SELECT id Id,document_id DocumentId,version_status Status,release_package_id ReleasePackageId FROM document_version WHERE document_id IN @ids", new { ids }, tx, cancellationToken: ct))).ToArray();
        var reviewIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT item.drawing_document_id FROM drawing_review_item item JOIN drawing_review_package package ON package.id=item.package_id WHERE item.drawing_document_id IN @ids AND package.state IN ('InReview','PendingSupervisorApproval','WritingProperties') AND item.drawing_state<>'ChangesRequested'",
            new { ids }, tx, cancellationToken: ct))).ToHashSet();
        var writebackIds = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT source_document_id FROM cad_property_writeback WHERE source_document_id IN @ids AND status IN ('Pending','InProgress')", new { ids }, tx, cancellationToken: ct));
        reviewIds.UnionWith(writebackIds);
        var documents = rows.Select(row => new ArchiveRecycleDocument(MapDocument(row),
            versions.Where(version => version.DocumentId == row.Id).Select(version => version.Id).ToArray(),
            versions.Any(version => version.DocumentId == row.Id && (version.Status == "Released" || version.ReleasePackageId.HasValue)), reviewIds.Contains(row.Id))).ToArray();
        var storedRelations = (await connection.QueryAsync<DocumentModelDrawingRelation>(new CommandDefinition(
            "SELECT r.model_document_id ModelDocumentId,r.drawing_document_id DrawingDocumentId FROM document_model_drawing_relation r JOIN document m ON m.id=r.model_document_id JOIN document d ON d.id=r.drawing_document_id WHERE (r.model_document_id IN @ids OR r.drawing_document_id IN @ids) AND m.deleted_at IS NULL AND m.purged_at IS NULL AND d.deleted_at IS NULL AND d.purged_at IS NULL",
            new { ids }, tx, cancellationToken: ct))).ToArray();
        // Legacy partial drawings can have verified model references without an
        // explicit relation row. Use the same compatibility reader as the workspace.
        var candidates = storedRelations.Concat(await ListDocumentRelationsAsync(projectId, ct))
            .Where(item => ids.Contains(item.ModelDocumentId) || ids.Contains(item.DrawingDocumentId)).Distinct().ToArray();
        var relatedIds = candidates.SelectMany(item => new[] { item.ModelDocumentId, item.DrawingDocumentId }).Distinct().ToArray();
        var relatedRows = relatedIds.Length == 0 ? [] : (await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            "SELECT * FROM document WHERE id IN @relatedIds AND deleted_at IS NULL AND purged_at IS NULL", new { relatedIds }, tx, cancellationToken: ct))).ToArray();
        var liveIds = relatedRows.Select(row => row.Id).ToHashSet();
        var relations = candidates.Where(item => liveIds.Contains(item.ModelDocumentId) && liveIds.Contains(item.DrawingDocumentId)).ToArray();
        documents = documents.Concat(relatedRows.Where(row => !ids.Contains(row.Id)).Select(row => new ArchiveRecycleDocument(MapDocument(row), [], false, false))).ToArray();
        var rootJsons = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT s.root_json FROM project_reference_root r JOIN reference_snapshot s ON s.id=r.reference_snapshot_id", transaction: tx, cancellationToken: ct));
        var roots = rootJsons.Select(value => JsonSerializer.Deserialize<DocumentReferenceNode>(value, jsonOptions)
            ?? throw new PdmRuleException("存在无法读取的当前引用结构，未执行回收。"));
        var references = ArchiveRecycleRules.References(roots).Where(item => ids.Contains(item.DocumentId)).ToArray();
        var pending = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM archive_batch_followup WHERE batch_id=@batchId)", new { batchId }, tx, cancellationToken: ct));
        return new(receipt, documents, relations, references, pending);
    }

    public async Task<IReadOnlyList<ArchiveRecycleSet>> ListArchiveRecycleSetsAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM archive_document_recycle_set WHERE project_id=@projectId ORDER BY created_at DESC LIMIT 100", new { projectId }, cancellationToken: ct));
        return rows.Select(json => JsonSerializer.Deserialize<ArchiveRecycleSet>(json, jsonOptions)!).ToArray();
    }

    public async Task<bool> IsDocumentInArchiveRecycleSetAsync(Guid projectId, Guid documentId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM archive_document_recycle_set WHERE project_id=@projectId AND restored_at IS NULL AND JSON_CONTAINS(JSON_EXTRACT(payload_json,'$.members[*].document.id'),JSON_QUOTE(@id)))",
            new { projectId, id = documentId.ToString() }, cancellationToken: ct));
    }

    public async Task<ArchiveRecycleSet> RecycleArchiveSetAsync(Guid projectId, Guid batchId, string scopeHash, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project WHERE id=@projectId FOR UPDATE", new { projectId }, tx, cancellationToken: ct));
        var preview = ArchiveRecycleRules.Preview(await InspectRecycleArchive(connection, tx, projectId, batchId, ct));
        ArchiveRecycleRules.RequirePreview(preview, scopeHash);
        var ids = preview.Members.Select(item => item.Document.Id).ToArray();
        var bomIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM bom_item WHERE project_id=@projectId AND source_document_id IN @ids AND deleted_at IS NULL AND item_source<>'Manual' AND is_manually_overridden=0 AND is_manually_retained=0 FOR UPDATE", new { projectId, ids }, tx, cancellationToken: ct))).ToArray();
        var set = new ArchiveRecycleSet(Guid.NewGuid(), projectId, batchId, reason, actor, now, now.AddDays(30), preview.Members, bomIds);
        foreach (var member in set.Members)
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET deleted_at=@at,deleted_by=@actor,delete_reason=@reason,updated_at=@at,row_version=row_version+1 WHERE id=@id AND row_version=@rowVersion AND deleted_at IS NULL AND purged_at IS NULL AND checked_out_by IS NULL",
                new { at = now.UtcDateTime, actor, reason, id = member.Document.Id, rowVersion = member.Document.RowVersion }, tx, cancellationToken: ct));
            if (affected != 1) throw new PdmConflictException("图档状态已变化，整批回收未执行，请重新预览。");
        }
        if (bomIds.Length > 0) await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_item SET deleted_at=@at,deleted_by=@actor,delete_reason=@reason,row_version=row_version+1,updated_at=@at WHERE id IN @bomIds",
            new { at = now.UtcDateTime, actor, reason = "存档批次回收：" + set.Id, bomIds }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO archive_document_recycle_set(id,project_id,batch_id,created_at,expires_at,payload_json) VALUES(@Id,@ProjectId,@BatchId,@at,@expires,@json)",
            new { set.Id, set.ProjectId, set.BatchId, at = now.UtcDateTime, expires = set.ExpiresAt.UtcDateTime, json = JsonSerializer.Serialize(set, jsonOptions) }, tx, cancellationToken: ct));
        await RebuildRecycleForest(connection, tx, projectId, ct);
        await RecycleSetAudit(connection, tx, set, actor, "document.recycle.set", now, ct);
        await tx.CommitAsync(ct); return set;
    }

    public async Task<ArchiveRecycleSet> RestoreArchiveSetAsync(Guid projectId, Guid setId, string actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project WHERE id=@projectId FOR UPDATE", new { projectId }, tx, cancellationToken: ct));
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM archive_document_recycle_set WHERE id=@setId AND project_id=@projectId FOR UPDATE", new { setId, projectId }, tx, cancellationToken: ct));
        var set = json is null ? null : JsonSerializer.Deserialize<ArchiveRecycleSet>(json, jsonOptions);
        if (set is null || set.RestoredAt.HasValue || now >= set.ExpiresAt) throw new PdmRuleException("回收批次不存在、已恢复或已超过30天恢复期限。");
        var ids = set.Members.Select(item => item.Document.Id).ToArray();
        var names = set.Members.Select(item => item.Document.FileName).ToArray();
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM document WHERE project_id=@projectId AND file_name IN @names AND id NOT IN @ids AND deleted_at IS NULL AND purged_at IS NULL)",
            new { projectId, names, ids }, tx, cancellationToken: ct))) throw new PdmConflictException("项目中已有同名图档，整批未恢复，请先核对冲突。");
        foreach (var member in set.Members)
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET deleted_at=NULL,deleted_by=NULL,delete_reason=NULL,updated_at=@at,row_version=row_version+1 WHERE id=@id AND row_version=@rowVersion AND deleted_at=@deletedAt AND purged_at IS NULL AND reset_snapshot_id IS NULL",
                new { at = now.UtcDateTime, id = member.Document.Id, rowVersion = member.Document.RowVersion + 1, deletedAt = set.CreatedAt.UtcDateTime }, tx, cancellationToken: ct));
            if (affected != 1) throw new PdmConflictException("回收图档已被单独恢复或状态已变化，整批未恢复，请刷新后核对。");
        }
        if (set.BomItemIds.Count > 0) await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_item SET deleted_at=NULL,deleted_by=NULL,delete_reason=NULL,row_version=row_version+1,updated_at=@at WHERE id IN @ids AND delete_reason=@reason AND deleted_at=@deletedAt",
            new { at = now.UtcDateTime, ids = set.BomItemIds, reason = "存档批次回收：" + set.Id, deletedAt = set.CreatedAt.UtcDateTime }, tx, cancellationToken: ct));
        set = set with { RestoredAt = now };
        await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_document_recycle_set SET restored_at=@at,payload_json=@json WHERE id=@setId",
            new { at = now.UtcDateTime, json = JsonSerializer.Serialize(set, jsonOptions), setId }, tx, cancellationToken: ct));
        await RebuildRecycleForest(connection, tx, projectId, ct);
        await RecycleSetAudit(connection, tx, set, actor, "document.restore.set", now, ct);
        await tx.CommitAsync(ct); return set;
    }

    private async Task RebuildRecycleForest(DbConnection connection, DbTransaction tx, Guid projectId, CancellationToken ct)
    {
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT r.reference_snapshot_id FROM project_reference_roots r JOIN document d ON d.id=r.root_document_id WHERE r.project_id=@projectId AND d.deleted_at IS NULL AND d.purged_at IS NULL ORDER BY d.file_name,d.id", new { projectId }, tx, cancellationToken: ct))).ToArray();
        var rows = ids.Length == 0 ? new Dictionary<Guid, ReferenceSnapshotRow>() : (await connection.QueryAsync<ReferenceSnapshotRow>(new CommandDefinition(
            "SELECT * FROM reference_snapshot WHERE id IN @ids", new { ids }, tx, cancellationToken: ct))).ToDictionary(row => row.Id);
        CadReferenceSnapshot? aggregate = null;
        foreach (var id in ids)
        {
            var row = rows[id];
            var root = JsonSerializer.Deserialize<DocumentReferenceNode>(row.RootJson, jsonOptions) ?? throw new PdmRuleException("当前装配结构无法读取，整批操作已取消。");
            aggregate = ProjectReferenceForest.Merge(aggregate, new(row.Id, row.ProjectId, row.RootDocumentId, AsUtc(row.CapturedAt), row.CapturedBy, root, row.Sha256));
        }
        if (aggregate is null) await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_reference_root WHERE project_id=@projectId", new { projectId }, tx, cancellationToken: ct));
        else
        {
            if (!rows.ContainsKey(aggregate.SnapshotId)) await InsertReferenceSnapshotAsync(connection, tx, aggregate, ct);
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_reference_root(project_id,reference_snapshot_id,updated_at) VALUES(@ProjectId,@Id,@at) ON DUPLICATE KEY UPDATE reference_snapshot_id=VALUES(reference_snapshot_id),updated_at=VALUES(updated_at)",
                new { aggregate.ProjectId, Id = aggregate.SnapshotId, at = aggregate.CapturedAt.UtcDateTime }, tx, cancellationToken: ct));
        }
    }
    private static Task<int> RecycleSetAudit(DbConnection connection, DbTransaction tx, ArchiveRecycleSet set, string actor, string action, DateTimeOffset now, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition("INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json) VALUES(@id,@at,@actor,@action,'PdmDocument',@entity,@detail)",
            new { id = Guid.NewGuid(), at = now.UtcDateTime, actor, action, entity = set.Id.ToString(), detail = JsonSerializer.Serialize(new { batchId = set.BatchId, documents = set.Members.Select(item => item.Document.Id), set.Reason }) }, tx, cancellationToken: ct));
    private sealed class RecycleBatchRow { public Guid Id { get; set; } public DateTime At { get; set; } public string Actor { get; set; } = ""; public int FileCount { get; set; } }
    private sealed record RecycleVersionRow(Guid Id, Guid DocumentId, string Status, Guid? ReleasePackageId);
}
