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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE pending FROM pending_archive_document pending
            WHERE pending.expires_at<=@Now AND NOT EXISTS(SELECT 1 FROM archive_batch_receipt receipt WHERE receipt.batch_id=pending.batch_id AND receipt.result_json IS NOT NULL)
            """, new { Now = now }, transaction, cancellationToken: cancellationToken));
        var rows = (await connection.QueryAsync<ArchiveUploadRow>(new CommandDefinition("""
            SELECT session_id Id,project_id ProjectId,relative_path Path,file_length Length,sha256 Sha256
            FROM archive_upload WHERE state IN ('Staged','Collecting') AND expires_at<=@Now
            ORDER BY expires_at LIMIT @Limit FOR UPDATE SKIP LOCKED
            """, new { Now = now, Limit = Math.Clamp(limit, 1, 100) }, transaction, cancellationToken: cancellationToken))).ToArray();
        var collected = new List<ArchiveUploadCleanup>();
        foreach (var row in rows)
        {
            var referenced = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM document_version WHERE REPLACE(storage_relative_path,CHAR(92),'/')=@Path OR REPLACE(preview_storage_relative_path,CHAR(92),'/')=@Path)
                """, new { row.Path }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_upload SET state=@State WHERE session_id=@Id",
                new { row.Id, State = referenced ? "Adopted" : "Collecting" }, transaction, cancellationToken: cancellationToken));
            if (!referenced) collected.Add(new(row.Id, row.ProjectId, new(row.Path, row.Length, row.Sha256, timeProvider.GetUtcNow())));
        }
        await transaction.CommitAsync(cancellationToken);
        return collected;
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
