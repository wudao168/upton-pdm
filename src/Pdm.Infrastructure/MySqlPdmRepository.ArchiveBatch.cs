using System.Text.Json;
using Dapper;
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
        var planned = ordered.Select(file => file.DocumentId).ToHashSet();
        var projectId = ordered[0].Commit.ReferenceSnapshot.ProjectId;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO archive_batch_receipt(batch_id,project_id,actor,request_hash) VALUES(@BatchId,@ProjectId,@Actor,@Hash)",
            new { BatchId = batchId, ProjectId = projectId, Actor = actor, Hash = requestHash }, transaction, cancellationToken: cancellationToken));
        var json = await connection.QuerySingleAsync<string?>(new CommandDefinition(
            "SELECT result_json FROM archive_batch_receipt WHERE batch_id=@BatchId FOR UPDATE", new { BatchId = batchId }, transaction, cancellationToken: cancellationToken));
        if (!string.IsNullOrWhiteSpace(json)) return ArchiveBatchRules.RequireMatchingReceipt(JsonSerializer.Deserialize<ArchiveBatchReceipt>(json, jsonOptions)!, actor, requestHash);
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project WHERE id=@ProjectId FOR UPDATE",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var project = await FindProjectAsync(connection, transaction, projectId, cancellationToken);
        if (project is null || !project.IsActive) throw new PdmConflictException("项目不存在或已停用，不能提交存档。");
        await MaterializeArchiveRegistrationsAsync(connection, transaction, batchId, projectId, actor, sessionId,
            ordered.Select(file => file.DocumentId).ToArray(), cancellationToken);
        var ids = ordered.Select(file => file.DocumentId).Concat(ordered.SelectMany(file => ArchiveBatchRules.References(file.Commit.ReferenceSnapshot.Root))
            .Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId!.Value)).Distinct().Order().ToArray();
        var lockedDocuments = new Dictionary<Guid, PdmDocument>();
        foreach (var id in ids)
        {
            await LockDocumentAsync(connection, transaction, id, cancellationToken);
            var locked = await FindDocumentAsync(connection, transaction, id, cancellationToken);
            if (locked is null || locked.ProjectId != projectId) throw new PdmRuleException("整批存档包含其他项目的图档，已取消提交。");
            lockedDocuments.Add(id, locked);
        }
        // These documents remain locked until commit. Repeated instances/configurations
        // can share immutable version-existence checks within this transaction.
        var verifiedReferences = new HashSet<(Guid DocumentId, string Revision)>();
        var previousDocuments = new List<PdmDocument>();
        var results = new Dictionary<Guid, DocumentCheckInResult>();
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
            results[file.DocumentId] = await CheckInVersionCoreAsync(connection, transaction, file.DocumentId, actor, sessionId,
                commit, file.DrawingReviewWritebackId, cancellationToken, batchId);
            lockedDocuments[file.DocumentId] = results[file.DocumentId].Document;
            if (results[file.DocumentId].Version is { } preparedVersion)
                verifiedReferences.Add((file.DocumentId, preparedVersion.Revision.Display));
            if (commit.ReferenceSnapshot.Root.Kind == DocumentKind.Drawing)
            {
                var models = commit.ReferenceSnapshot.Root.Children.Where(node => node.Kind is DocumentKind.Assembly or DocumentKind.Part)
                    .Where(node => node.Status is not (ReferenceNodeStatus.Virtual or ReferenceNodeStatus.Missing) && node.DocumentId.HasValue).Select(node => node.DocumentId!.Value).Distinct().ToArray();
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
            results[file.DocumentId] = result with { Version = version };
        }
        await RefreshWorkingReferenceRootsAsync(connection, transaction, projectId, actor, timeProvider.GetUtcNow(), cancellationToken);
        var receipt = new ArchiveBatchReceipt(batchId, projectId, actor, requestHash, ordered.Select(file => results[file.DocumentId]).ToArray()) { FollowupPending = true };
        foreach (var result in receipt.Results.Where(result => result.VersionCreated && result.Version is not null))
            await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_upload SET state='Adopted' WHERE document_id=@Id AND relative_path=@Path",
                new { Id = result.Document.Id, Path = result.Version!.StorageRelativePath.Replace('\\','/') }, transaction, cancellationToken: cancellationToken));
        foreach (var file in ordered)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
                VALUES(@Id,@Now,@Actor,@Action,'PdmDocument',@DocumentId,@Detail)
                """, new { Id = Guid.NewGuid(), Now = timeProvider.GetUtcNow().UtcDateTime, Actor = actor,
                    Action = file.RestoreHistoricalVersion ? "document.version.restore" : "document.checkin.batch",
                    DocumentId = file.DocumentId.ToString(), Detail = JsonSerializer.Serialize(new { detail = "整批存档：" + batchId }, jsonOptions) },
                transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO archive_batch_followup(batch_id,project_id,followup_json,next_attempt_at) VALUES(@BatchId,@ProjectId,@Json,@Now)",
            new { BatchId = batchId, ProjectId = projectId, Json = JsonSerializer.Serialize(new ArchiveBatchFollowup(receipt, previousDocuments), jsonOptions),
                Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_batch_receipt SET result_json=@Json,committed_at=@Now WHERE batch_id=@BatchId",
            new { BatchId = batchId, Json = JsonSerializer.Serialize(receipt, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }
}
