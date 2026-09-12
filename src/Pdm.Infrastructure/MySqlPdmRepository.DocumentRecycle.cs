using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<PdmDocument>> ListDeletedDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,row_version,updated_at,deleted_at,deleted_by,delete_reason,purged_at,
                   (SELECT COUNT(*) FROM document_version v WHERE v.document_id=document.id) stored_version_count
            FROM document
            WHERE project_id=@ProjectId AND deleted_at IS NOT NULL AND purged_at IS NULL
            ORDER BY deleted_at DESC,drawing_number,kind
            """, new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(row => MapDocument(row)).ToArray();
    }

    public async Task<PdmDocument?> FindDocumentIncludingDeletedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,row_version,updated_at,deleted_at,deleted_by,delete_reason,purged_at,
                   (SELECT COUNT(*) FROM document_version v WHERE v.document_id=document.id) stored_version_count
            FROM document WHERE id=@DocumentId
            """, new { DocumentId = documentId }, cancellationToken: cancellationToken));
        return row is null ? null : MapDocument(row);
    }

    public async Task<PdmDocument> SetDocumentDeletedAsync(Guid documentId, bool deleted, long expectedRowVersion, string actor, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            deleted
                ? """
                  UPDATE document
                  SET deleted_at=@Now,deleted_by=@Actor,delete_reason=@Reason,row_version=row_version+1,updated_at=@Now
                  WHERE id=@DocumentId AND row_version=@ExpectedRowVersion AND deleted_at IS NULL AND purged_at IS NULL
                  """
                : """
                  UPDATE document
                  SET deleted_at=NULL,deleted_by=NULL,delete_reason=NULL,row_version=row_version+1,updated_at=@Now
                  WHERE id=@DocumentId AND row_version=@ExpectedRowVersion AND deleted_at IS NOT NULL AND purged_at IS NULL
                  """,
            new { DocumentId = documentId, ExpectedRowVersion = expectedRowVersion, Actor = actor, Reason = reason, Now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("图档状态已变化，请刷新后重试。");
        }
        await transaction.CommitAsync(cancellationToken);
        return await FindDocumentIncludingDeletedAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
    }

    public async Task<int> PurgeExpiredDeletedDocumentsAsync(DateTimeOffset cutoff, DateTimeOffset purgedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document
            SET purged_at=@PurgedAt,row_version=row_version+1,updated_at=@PurgedAt
            WHERE deleted_at IS NOT NULL AND deleted_at<@Cutoff AND purged_at IS NULL
            """,
            new { Cutoff = cutoff.UtcDateTime, PurgedAt = purgedAt.UtcDateTime },
            cancellationToken: cancellationToken));
    }
}
