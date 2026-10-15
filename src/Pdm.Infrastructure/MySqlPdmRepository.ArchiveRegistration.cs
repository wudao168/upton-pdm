using System.Data.Common;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<PendingArchiveDocument?> FindPendingArchiveDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindPendingArchiveDocumentAsync(connection, null, documentId, cancellationToken);
    }

    public async Task<PdmDocument> ReserveArchiveDocumentAsync(RegisterDocumentCommand command, string actor,
        ArchiveRegistrationContext context, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var document = new PdmDocument(Guid.NewGuid(), command.ProjectId, command.DrawingNumber, command.Name, command.FileName,
            command.Kind, DocumentLifecycleState.Work, RevisionLabel.InitialWork(), actor, now)
        { FolderId = command.FolderId, CheckoutSessionId = context.SessionId, CheckoutMachine = context.MachineName,
            CheckedOutAt = now, CheckoutLastHeartbeatAt = now, CheckoutLeaseExpiresAt = now.AddHours(24), StoredVersionCount = 0 };
        var pending = new PendingArchiveDocument(context.BatchId, document, command);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pending_archive_document(document_id,batch_id,project_id,actor,session_id,file_name,pending_json,created_at,expires_at)
            VALUES(@Id,@BatchId,@ProjectId,@Actor,@SessionId,@FileName,@Json,@Now,@Expires)
            ON DUPLICATE KEY UPDATE document_id=document_id
            """, new { document.Id, context.BatchId, command.ProjectId, Actor = actor, context.SessionId, command.FileName,
                Json = JsonSerializer.Serialize(pending, jsonOptions), Now = now.UtcDateTime, Expires = now.AddHours(24).UtcDateTime }, cancellationToken: cancellationToken));
        var json = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT pending_json FROM pending_archive_document WHERE batch_id=@BatchId AND file_name=@FileName",
            new { context.BatchId, command.FileName }, cancellationToken: cancellationToken));
        var stored = JsonSerializer.Deserialize<PendingArchiveDocument>(json, jsonOptions)!;
        RequireArchiveRegistrationOwner(stored, context.BatchId, command.ProjectId, actor, context.SessionId);
        if (stored.Command != command) throw new PdmConflictException("同一存档批次的图档信息发生变化，请重新开始整批存档。");
        return stored.Document;
    }

    public async Task CancelArchiveRegistrationAsync(Guid batchId, string actor, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pending_archive_document WHERE batch_id=@BatchId AND actor=@Actor AND session_id=@SessionId",
            new { BatchId = batchId, Actor = actor, SessionId = sessionId }, cancellationToken: cancellationToken));
    }

    private async Task<PendingArchiveDocument?> FindPendingArchiveDocumentAsync(DbConnection connection, DbTransaction? transaction,
        Guid documentId, CancellationToken cancellationToken)
    {
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT pending_json FROM pending_archive_document WHERE document_id=@DocumentId", new { DocumentId = documentId },
            transaction, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<PendingArchiveDocument>(json, jsonOptions);
    }

    private static void RequireArchiveRegistrationOwner(PendingArchiveDocument pending, Guid batchId, Guid projectId, string actor, Guid sessionId)
    {
        if (pending.BatchId != batchId || pending.Document.ProjectId != projectId || pending.Document.CheckedOutBy != actor
            || pending.Document.CheckoutSessionId != sessionId)
            throw new PdmConflictException("新图档不属于当前存档批次或编辑会话，整批提交已取消。");
    }

    private async Task MaterializeArchiveRegistrationsAsync(DbConnection connection, DbTransaction transaction, Guid batchId, Guid projectId,
        string actor, Guid sessionId, IReadOnlyCollection<Guid> plannedIds, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT pending_json FROM pending_archive_document WHERE batch_id=@BatchId FOR UPDATE", new { BatchId = batchId },
            transaction, cancellationToken: cancellationToken));
        var pendingFiles = rows.Select(json => JsonSerializer.Deserialize<PendingArchiveDocument>(json, jsonOptions)!).ToArray();
        foreach (var pending in pendingFiles)
        {
            RequireArchiveRegistrationOwner(pending, batchId, projectId, actor, sessionId);
            if (pending.Document.CheckoutLeaseExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("新图档暂存登记已过期，请重新整体存档。");
            if (!plannedIds.Contains(pending.Document.Id)) throw new PdmRuleException("整批存档遗漏了已准备的新图档，全部提交已取消。");
            var d = pending.Document;
            if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM document WHERE project_id=@ProjectId AND file_name=@FileName AND purged_at IS NULL)",
                new { d.ProjectId, d.FileName }, transaction, cancellationToken: cancellationToken)))
                throw new PdmConflictException("项目中已存在同名图档，整批提交已取消：" + d.FileName);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO document(id,project_id,folder_id,drawing_number,name,file_name,source_fingerprint_sha256,kind,lifecycle_state,revision_label,
                    checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,checkout_lease_expires_at,row_version,created_at,updated_at)
                VALUES(@Id,@ProjectId,@FolderId,@DrawingNumber,@Name,@FileName,@Hash,@Kind,'Work','W1',@Actor,@Now,@SessionId,@Machine,@Now,@Lease,1,@Now,@Now)
                """, new { d.Id, d.ProjectId, d.FolderId, d.DrawingNumber, d.Name, d.FileName, Hash = pending.Command.SourceSha256,
                    Kind = d.Kind.ToString(), Actor = actor, SessionId = sessionId, Machine = d.CheckoutMachine,
                    Now = timeProvider.GetUtcNow().UtcDateTime, Lease = timeProvider.GetUtcNow().AddHours(24).UtcDateTime },
                transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO document_user_access(document_id,username,can_read,granted_at) VALUES(@Id,@Actor,1,@Now)",
                new { d.Id, Actor = actor, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        foreach (var pending in pendingFiles.Where(p => p.Command.RelatedModelDocumentId.HasValue))
        {
            var model = await FindDocumentAsync(connection, transaction, pending.Command.RelatedModelDocumentId!.Value, cancellationToken);
            if (model?.ProjectId != projectId || model.Kind is not (DocumentKind.Assembly or DocumentKind.Part))
                throw new PdmRuleException("工程图关联的模型无效，整批提交已取消。");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO document_model_drawing_relation(drawing_document_id,model_document_id,project_id,created_at,updated_at)
                VALUES(@DrawingId,@ModelId,@ProjectId,@Now,@Now)
                """, new { DrawingId = pending.Document.Id, ModelId = model.Id, ProjectId = projectId,
                    Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pending_archive_document WHERE batch_id=@BatchId",
            new { BatchId = batchId }, transaction, cancellationToken: cancellationToken));
    }
}
