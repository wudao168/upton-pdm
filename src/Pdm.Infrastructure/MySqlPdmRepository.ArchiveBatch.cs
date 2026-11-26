using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<ArchiveBatchReceipt?> FindArchiveBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT result_json FROM archive_batch_receipt WHERE batch_id=@BatchId", new { BatchId = batchId }, cancellationToken: cancellationToken));
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ArchiveBatchReceipt>(json, jsonOptions);
    }

    public async Task<ArchiveBatchReceipt> CheckInBatchVersionsAsync(Guid batchId, string requestHash, string actor, Guid sessionId,
        IReadOnlyList<ArchiveBatchFile> files, CancellationToken cancellationToken)
    {
        var ordered = ArchiveBatchRules.Order(files);
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var trace = new ArchiveDatabaseTrace(logger, batchId, attempt, ordered.Count);
            try
            {
                return await CommitArchiveBatchTransactionAsync(batchId, requestHash, actor, sessionId, ordered, trace, cancellationToken);
            }
            catch (Exception error)
            {
                trace.Fail(error);
                if (error is not MySqlException databaseError || cancellationToken.IsCancellationRequested
                    || !IsTransientMySqlWriteFailure(databaseError)) throw;
                if (attempt == maxAttempts) throw new ArchiveBatchTemporarilyUnavailableException(error);
                // The previous transaction has been disposed. A fresh transaction first reads
                // this same batch receipt and can safely recover a lost commit acknowledgement.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }
    }

    private async Task<ArchiveBatchReceipt> CommitArchiveBatchTransactionAsync(Guid batchId, string requestHash, string actor, Guid sessionId,
        IReadOnlyList<ArchiveBatchFile> ordered, ArchiveDatabaseTrace trace, CancellationToken cancellationToken)
    {
        var planned = ordered.Select(file => file.DocumentId).ToHashSet();
        var projectId = ordered[0].Commit.ReferenceSnapshot.ProjectId;
        await using var connection = await OpenAsync(cancellationToken);
        trace.Thread = connection.ServerThread;
        trace.Enter("批次锁与项目锁");
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO archive_batch_receipt(batch_id,project_id,actor,request_hash) VALUES(@BatchId,@ProjectId,@Actor,@Hash)",
            new { BatchId = batchId, ProjectId = projectId, Actor = actor, Hash = requestHash }, transaction, cancellationToken: cancellationToken));
        var state = await connection.QuerySingleAsync<ArchiveBatchState>(new CommandDefinition(
            "SELECT request_hash AS RequestHash,result_json AS ResultJson FROM archive_batch_receipt WHERE batch_id=@BatchId FOR UPDATE",
            new { BatchId = batchId }, transaction, cancellationToken: cancellationToken));
        if (state.RequestHash == ArchiveBatchRules.AbandonedRequestHash)
            throw new PdmConflictException("原存档批次已结束，请按当前图档重新存档。");
        if (!string.IsNullOrWhiteSpace(state.ResultJson))
        {
            trace.Enter("沿用已提交回执");
            return ArchiveBatchRules.RequireMatchingReceipt(JsonSerializer.Deserialize<ArchiveBatchReceipt>(state.ResultJson, jsonOptions)!, actor, requestHash);
        }
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project WHERE id=@ProjectId FOR UPDATE",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var project = await FindProjectAsync(connection, transaction, projectId, cancellationToken);
        if (project is null || !project.IsActive) throw new PdmConflictException("项目不存在或已停用，不能提交存档。");
        trace.Enter("转入新图档登记");
        await MaterializeArchiveRegistrationsAsync(connection, transaction, batchId, projectId, actor, sessionId, planned, cancellationToken);
        var ids = ordered.Select(file => file.DocumentId).Concat(ordered.SelectMany(file => ArchiveBatchRules.References(file.Commit.ReferenceSnapshot.Root))
            .Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId!.Value)).Distinct().Order().ToArray();
        var lockedDocuments = new Dictionary<Guid, PdmDocument>();
        var lockedRows = new Dictionary<Guid, LockedDocumentRow>();
        trace.Enter("锁定本批图档及引用");
        foreach (var id in ids)
        {
            lockedRows.Add(id, await LockDocumentAsync(connection, transaction, id, cancellationToken));
            var locked = await FindDocumentAsync(connection, transaction, id, cancellationToken);
            if (locked is null || locked.ProjectId != projectId) throw new PdmRuleException("整批存档包含其他项目的图档，已取消提交。");
            lockedDocuments.Add(id, locked);
        }
        // These documents remain locked until commit. Repeated instances/configurations
        // can share immutable version-existence checks within this transaction.
        var verifiedReferences = new HashSet<(Guid DocumentId, string Revision)>();
        var previousDocuments = new List<PdmDocument>();
        var results = new Dictionary<Guid, DocumentCheckInResult>();
        trace.Enter("核对引用与更新图档状态");
        foreach (var file in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commit = ArchiveBatchRules.BindCommittedReferences(file.Commit, results);
            var previous = lockedDocuments[file.DocumentId];
            previousDocuments.Add(previous);
            if (file.RestoreHistoricalVersion)
            {
                ArchiveBatchRules.ValidateRestoreSession(previous, actor, file.ExpectedCheckoutSessionId);
                if (commit.SourceVersionId is not Guid sourceId
                    || await FindDocumentVersionAsync(connection, transaction, file.DocumentId, sourceId, cancellationToken) is null)
                    throw new PdmRuleException("恢复来源版本无效。");
                if (previous.CheckoutSessionId is null)
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE document SET checked_out_by=@Actor,checkout_session_id=@SessionId WHERE id=@Id",
                        new { Actor = actor, SessionId = sessionId, Id = file.DocumentId }, transaction, cancellationToken: cancellationToken));
                commit = commit with { IsHistoricalRestore = true };
            }
            foreach (var node in ArchiveBatchRules.References(commit.ReferenceSnapshot.Root).Where(node => node.Status != ReferenceNodeStatus.Virtual))
            {
                if (node.DocumentId is not Guid id) throw new PdmRuleException("引用图档未完整存档，整批提交已取消：" + node.FileName);
                var deferredContext = ArchiveDeferredReferences.IsDeferred(node) && planned.Contains(id);
                if (node.Revision is null && !deferredContext) throw new PdmRuleException("引用图档未完整存档，整批提交已取消：" + node.FileName);
                var document = lockedDocuments[id];
                if (document.DeletedAt.HasValue || document.PurgedAt.HasValue
                    || document.Kind != node.Kind || !string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new PdmRuleException("引用图档的项目或存档版本无效，整批提交已取消：" + node.FileName);
                if (deferredContext) continue;
                var reference = (DocumentId: id, Revision: node.Revision!.Value.Display);
                if (!verifiedReferences.Contains(reference))
                {
                    var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM document_version WHERE document_id=@Id AND revision_label=@Revision)",
                        new { Id = id, reference.Revision }, transaction, cancellationToken: cancellationToken));
                    if (!exists) throw new PdmRuleException("引用图档的项目或存档版本无效，整批提交已取消：" + node.FileName);
                    verifiedReferences.Add(reference);
                }
            }
            try
            {
                results[file.DocumentId] = await CheckInVersionCoreAsync(connection, transaction, file.DocumentId, actor, sessionId,
                    commit, file.DrawingReviewWritebackId, cancellationToken, batchId,
                    file.RestoreHistoricalVersion ? null : lockedRows[file.DocumentId]);
            }
            catch (MySqlException error)
            {
                // A slow rollback can mask the original timeout. Keep the failing
                // statement and server thread before transaction disposal begins.
                trace.Fail(error, file.DocumentId);
                throw;
            }
            lockedDocuments[file.DocumentId] = results[file.DocumentId].Document;
            if (results[file.DocumentId].Version is { } preparedVersion)
                verifiedReferences.Add((file.DocumentId, preparedVersion.Revision.Display));
            if (commit.ReferenceSnapshot.Root.Kind == DocumentKind.Drawing)
            {
                var models = DrawingModelRelations.KnownModels(commit.ReferenceSnapshot.Root)
                    .Select(node => node.DocumentId!.Value).Distinct().ToArray();
                // A partial read may add verified models, but cannot prove that an old relation was removed.
                if (!commit.ReferenceSnapshot.Root.StructureIndexPending)
                    await connection.ExecuteAsync(new CommandDefinition("DELETE FROM document_model_drawing_relation WHERE drawing_document_id=@Id",
                        new { Id = file.DocumentId }, transaction, cancellationToken: cancellationToken));
                foreach (var modelId in models)
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO document_model_drawing_relation(drawing_document_id,model_document_id,project_id,created_at,updated_at)
                        VALUES(@DrawingId,@ModelId,@ProjectId,@Now,@Now)
                        ON DUPLICATE KEY UPDATE updated_at=@Now
                        """, new { DrawingId = file.DocumentId, ModelId = modelId, ProjectId = projectId, Now = timeProvider.GetUtcNow().UtcDateTime },
                        transaction, cancellationToken: cancellationToken));
            }
        }
        trace.Enter("写入不可变版本和引用快照");
        foreach (var file in ordered)
        {
            var result = results[file.DocumentId];
            if (!result.VersionCreated || result.Version is null) continue;
            var root = ArchiveBatchRules.BindContextReferences(result.Version.ReferenceSnapshot, results);
            var version = result.Version with { ReferenceSnapshot = root };
            var snapshot = ArchiveSafety.Rehash(file.Commit.ReferenceSnapshot with { Root = root });
            var rootJson = JsonSerializer.Serialize(root, jsonOptions);
            // Never UPDATE a document_version, even one created in this transaction:
            // the immutable trigger deliberately protects every stored version.
            await InsertVersionAsync(connection, transaction, version, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE reference_snapshot SET root_json=@RootJson,sha256=@Sha256 WHERE id=@Id AND root_document_id=@DocumentId",
                new { RootJson = rootJson, snapshot.Sha256, Id = snapshot.SnapshotId, DocumentId = version.DocumentId }, transaction, cancellationToken: cancellationToken));
            // The document was read before its deferred version was inserted.
            var document = result.Document with
            {
                DrawingVersionNumber = version.DrawingVersionNumber,
                StoredVersionCount = result.Document.StoredVersionCount + 1
            };
            if (document.Kind == DocumentKind.Drawing)
                document = await AdoptArchivedReviewDrawingAsync(connection, transaction, document, version, cancellationToken);
            results[file.DocumentId] = result with { Version = version, Document = document };
        }
        trace.Enter("刷新工作引用");
        await RefreshWorkingReferenceRootsAsync(connection, transaction, projectId, actor, timeProvider.GetUtcNow(), cancellationToken);
        trace.Enter("归属已暂存文件");
        var receipt = new ArchiveBatchReceipt(batchId, projectId, actor, requestHash, ordered.Select(file => results[file.DocumentId]).ToArray()) { FollowupPending = true };
        await AdoptArchiveUploadsAsync(connection, transaction, receipt.Results, cancellationToken);
        trace.Enter("写入存档操作审计");
        var auditTime = timeProvider.GetUtcNow();
        await InsertAuditEntriesAsync(connection, transaction, ordered.Select(file => new AuditEntry(
            Guid.CreateVersion7(auditTime), auditTime, actor,
            file.RestoreHistoricalVersion ? "document.version.restore" : "document.checkin.batch",
            "PdmDocument", file.DocumentId.ToString(), "整批存档：" + batchId)), cancellationToken, archiveAuditCommandTimeoutSeconds);
        trace.Enter("登记存档后续任务");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO archive_batch_followup(batch_id,project_id,followup_json,next_attempt_at) VALUES(@BatchId,@ProjectId,@Json,@Now)",
            new { BatchId = batchId, ProjectId = projectId, Json = JsonSerializer.Serialize(new ArchiveBatchFollowup(receipt, previousDocuments), jsonOptions),
                Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        trace.Enter("保存存档提交回执");
        await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_batch_receipt SET result_json=@Json,committed_at=@Now WHERE batch_id=@BatchId",
            new { BatchId = batchId, Json = JsonSerializer.Serialize(receipt, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        trace.Enter("事务提交落盘");
        await transaction.CommitAsync(cancellationToken);
        trace.Enter("提交完成");
        return receipt;
    }

    private sealed record ArchiveBatchState(string RequestHash, string? ResultJson);

    private sealed class ArchiveDatabaseTrace(ILogger? logger, Guid batchId, int attempt, int count)
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private long last;
        private string phase = "打开数据库连接";
        public int Thread { get; set; }

        internal void Enter(string next)
        {
            var now = elapsed.ElapsedMilliseconds;
            logger?.LogInformation("存档写入 {BatchId} 尝试 {Attempt} 连接 {DbThread} 文件 {FileCount} 阶段 {Phase} 耗时 {StageMs} 毫秒 累计 {ElapsedMs} 毫秒 下一阶段 {NextPhase}",
                batchId, attempt, Thread, count, phase, now - last, now, next);
            phase = next;
            last = now;
        }

        internal void Fail(Exception error, Guid? documentId = null) =>
            logger?.LogWarning(error, "存档写入 {BatchId} 尝试 {Attempt} 连接 {DbThread} 阶段 {Phase} 图档 {DocumentId} 累计 {ElapsedMs} 毫秒",
                batchId, attempt, Thread, phase, documentId, elapsed.ElapsedMilliseconds);
    }
}
