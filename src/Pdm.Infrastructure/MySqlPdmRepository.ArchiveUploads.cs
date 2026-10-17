using System.Data;
using System.Data.Common;
using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task RegisterArchiveUploadAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var context = session.ArchiveContext ?? throw new PdmRuleException("缺少整批上传归属。");
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO archive_upload(session_id,batch_id,document_id,project_id,actor,checkout_session_id,relative_path,file_length,sha256,expires_at,state)
            VALUES(@Id,@BatchId,@DocumentId,@ProjectId,@Owner,@CheckoutSessionId,@Path,@TotalLength,@ExpectedSha256,@Expires,'Staged')
            """, new { session.Id, context.BatchId, context.DocumentId, session.ProjectId, session.Owner, context.CheckoutSessionId,
                Path = session.TargetRelativePath!.Replace('\\','/'), session.TotalLength, session.ExpectedSha256, Expires = session.ExpiresAt.UtcDateTime }, cancellationToken: cancellationToken));
    }

    private async Task ValidateArchiveUploadAsync(DbConnection connection, DbTransaction transaction, Guid documentId, string actor,
        Guid sessionId, Guid? batchId, StoredFile file, CancellationToken cancellationToken)
    {
        var upload = await connection.QuerySingleOrDefaultAsync<ArchiveUploadRow>(new CommandDefinition(
            "SELECT session_id Id,document_id DocumentId,batch_id BatchId,actor Actor,checkout_session_id CheckoutSessionId,file_length Length,sha256 Sha256,expires_at Expires,state State FROM archive_upload WHERE relative_path=@Path FOR UPDATE",
            new { DocumentId = documentId, Path = file.RelativePath.Replace('\\','/') }, transaction, cancellationToken: cancellationToken));
        if (upload is null) return; // Legacy files have no ownership record and are never collected.
        if (upload.DocumentId != documentId || upload.Length != file.Length || !string.Equals(upload.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("待存档文件与服务器上传记录不一致。");
        if (upload.State == "Adopted") return;
        if (upload.State != "Staged" || upload.Expires <= timeProvider.GetUtcNow().UtcDateTime
            || upload.BatchId != batchId || upload.CheckoutSessionId != sessionId || !string.Equals(upload.Actor, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("暂存文件已过期或不属于当前存档批次，请重新存档。");
    }

    public async Task<IReadOnlyList<ArchiveUploadCleanup>> ClaimExpiredArchiveUploadsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var count = Math.Clamp(limit, 1, 100);
        var pendingRows = (await connection.QueryAsync<ArchiveUploadRow>(new CommandDefinition("""
            SELECT document_id Id,batch_id BatchId FROM pending_archive_document
            WHERE expires_at<=@Now ORDER BY expires_at,document_id LIMIT @Limit
            """, new { Now = now, Limit = count }, cancellationToken: cancellationToken))).ToArray();
        foreach (var pending in pendingRows)
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // Match the foreground commit order: receipt, then reservation/upload. Do not
            // wait on a live batch or lock an entire expired range before checking receipts.
            if (!await TryLockArchiveCleanupBatchAsync(connection, transaction, pending.BatchId, true, cancellationToken)) continue;
            var id = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
                SELECT document_id FROM pending_archive_document WHERE document_id=@Id AND expires_at<=@Now
                FOR UPDATE SKIP LOCKED
                """, new { pending.Id, Now = now }, transaction, cancellationToken: cancellationToken));
            if (id.HasValue)
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pending_archive_document WHERE document_id=@Id",
                    new { Id = id.Value }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        var candidates = (await connection.QueryAsync<ArchiveUploadRow>(new CommandDefinition("""
            SELECT session_id Id,batch_id BatchId
            FROM archive_upload WHERE state IN ('Staged','Collecting') AND expires_at<=@Now
            ORDER BY expires_at,session_id LIMIT @Limit
            """, new { Now = now, Limit = count }, cancellationToken: cancellationToken))).ToArray();
        var collected = new List<ArchiveUploadCleanup>();
        foreach (var candidate in candidates)
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await TryLockArchiveCleanupBatchAsync(connection, transaction, candidate.BatchId, false, cancellationToken)) continue;
            var row = await connection.QuerySingleOrDefaultAsync<ArchiveUploadRow>(new CommandDefinition("""
                SELECT session_id Id,project_id ProjectId,relative_path Path,file_length Length,sha256 Sha256
                FROM archive_upload WHERE session_id=@Id AND batch_id=@BatchId AND state IN ('Staged','Collecting') AND expires_at<=@Now
                FOR UPDATE SKIP LOCKED
                """, new { candidate.Id, candidate.BatchId, Now = now }, transaction, cancellationToken: cancellationToken));
            if (row is null) continue;
            var referenced = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM document_version WHERE cleanup_storage_path=@Path)
                    OR EXISTS(SELECT 1 FROM document_version WHERE cleanup_preview_path=@Path)
                """, new { row.Path }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_upload SET state=@State WHERE session_id=@Id",
                new { row.Id, State = referenced ? "Adopted" : "Collecting" }, transaction, cancellationToken: cancellationToken));
            if (!referenced) collected.Add(new(row.Id, row.ProjectId, new(row.Path, row.Length, row.Sha256, timeProvider.GetUtcNow())));
            await transaction.CommitAsync(cancellationToken);
        }
        return collected;
    }

    private static async Task<bool> TryLockArchiveCleanupBatchAsync(DbConnection connection, DbTransaction transaction,
        Guid batchId, bool preserveCommitted, CancellationToken cancellationToken)
    {
        var receipt = await connection.QuerySingleOrDefaultAsync<ArchiveCleanupReceipt>(new CommandDefinition(
            "SELECT result_json IS NOT NULL Committed FROM archive_batch_receipt WHERE batch_id=@Id FOR UPDATE SKIP LOCKED",
            new { Id = batchId }, transaction, cancellationToken: cancellationToken));
        if (receipt is not null) return !preserveCommitted || !receipt.Committed;
        // An existing row skipped above belongs to another transaction. A genuinely absent
        // receipt is safe for an expired upload: foreground validation rejects expiry too.
        return !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM archive_batch_receipt WHERE batch_id=@Id)", new { Id = batchId },
            transaction, cancellationToken: cancellationToken));
    }

    private sealed class ArchiveCleanupReceipt
    {
        public bool Committed { get; init; }
    }

    public async Task CompleteArchiveUploadCleanupAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_upload SET state='Collected' WHERE session_id=@Id AND state='Collecting'",
            new { Id = sessionId }, cancellationToken: cancellationToken));
    }

    private sealed class ArchiveUploadRow
    {
        public Guid Id { get; init; }
        public Guid BatchId { get; init; }
        public Guid ProjectId { get; init; }
        public Guid DocumentId { get; init; }
        public Guid CheckoutSessionId { get; init; }
        public string Actor { get; init; } = "";
        public string Path { get; init; } = "";
        public long Length { get; init; }
        public string Sha256 { get; init; } = "";
        public DateTime Expires { get; init; }
        public string State { get; init; } = "";
    }
}
