using System.Data.Common;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private async Task RequireHandoffRecipientAsync(DbConnection connection, DbTransaction transaction, Guid documentId, string actor, CancellationToken ct)
    {
        var handoff = await connection.QuerySingleOrDefaultAsync<HandoffRow>(new CommandDefinition(
            "SELECT document_id DocumentId,project_id ProjectId,from_user FromUser,to_user ToUser,version_id VersionId,expires_at ExpiresAt,wait_source_key WaitSourceKey FROM document_edit_handoff WHERE document_id=@documentId FOR UPDATE",
            new { documentId }, transaction, cancellationToken: ct));
        if (handoff is not null && handoff.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime && !handoff.ToUser.Equals(actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException($"图档已定向交接给{handoff.ToUser}，预留5分钟接手，请稍后重试或联系交接人员。");
    }

    private async Task ReserveHandoffAsync(DbConnection connection, DbTransaction transaction, Guid documentId, string actor, DocumentVersionCommit commit, Guid versionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commit.HandoffTo)) return;
        var prior = await FindDocumentAsync(connection, transaction, documentId, ct) ?? throw new PdmNotFoundException("图档不存在。");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO document_edit_handoff(document_id,project_id,from_user,to_user,version_id,expires_at,wait_source_key)
            VALUES(@documentId,@ProjectId,@actor,@ToUser,@versionId,@Expires,@WaitSourceKey)
            ON DUPLICATE KEY UPDATE from_user=@actor,to_user=@ToUser,version_id=@versionId,expires_at=@Expires,wait_source_key=@WaitSourceKey
            """, new { documentId, commit.ReferenceSnapshot.ProjectId, actor, ToUser = commit.HandoffTo, versionId, Expires = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, WaitSourceKey = EditHandoff.WaitingKey(prior) }, transaction, cancellationToken: ct));
    }

    public async Task<PdmDocument> ReclaimCheckoutAsync(Guid documentId, Guid expectedSessionId, DateTimeOffset expectedCheckoutAt, DateTimeOffset? heartbeatBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EditLockRow>(new CommandDefinition(
            "SELECT checked_out_by,checkout_session_id,checked_out_at,row_version FROM document WHERE id=@documentId FOR UPDATE", new { documentId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("图档不存在。");
        var current = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (row.CheckoutSessionId != expectedSessionId || row.CheckedOutAt != expectedCheckoutAt.UtcDateTime || string.IsNullOrWhiteSpace(row.CheckedOutBy))
            throw new PdmConflictException("编辑会话已经变化，原接管请求已取消，请刷新后重试。");
        if (heartbeatBefore.HasValue && ((current.CheckoutLastHeartbeatAt ?? current.CheckedOutAt ?? current.UpdatedAt) >= heartbeatBefore.Value || current.CheckoutLeaseExpiresAt >= now))
            throw new PdmConflictException("编辑人员已恢复连接，已取消离线回收。");
        if (current.State is DocumentLifecycleState.InReview or DocumentLifecycleState.Obsolete
            || await IsDocumentUnderActiveDrawingReviewAsync(connection, transaction, documentId, cancellationToken))
            throw new PdmConflictException("审核、审批或作废图档不能通过普通编辑接管处理。");
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM archive_upload u WHERE u.document_id=@documentId AND u.checkout_session_id=@expectedSessionId AND u.state='Staged' AND u.expires_at>@Now AND NOT EXISTS(SELECT 1 FROM document_version v WHERE v.document_id=@documentId AND v.storage_relative_path=u.relative_path))",
                new { documentId, expectedSessionId, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken)))
            throw new PdmConflictException("图档存在尚未结束的存档上传，请先核对该批次结果，暂不接管。");
        await ReleaseEditLockAsync(connection, transaction, documentId, row.CheckedOutBy, expectedSessionId, row.RowVersion, cancellationToken);
        var result = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<EditHandoff>> ListEditHandoffsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<HandoffRow>(new CommandDefinition("SELECT document_id DocumentId,project_id ProjectId,from_user FromUser,to_user ToUser,version_id VersionId,expires_at ExpiresAt,wait_source_key WaitSourceKey FROM document_edit_handoff", cancellationToken: cancellationToken));
        return rows.Select(row => new EditHandoff(row.DocumentId, row.ProjectId, row.FromUser, row.ToUser, row.VersionId, new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc))) { WaitSourceKey = row.WaitSourceKey }).ToArray();
    }

    public async Task<bool> ExpireEditHandoffAsync(EditHandoff handoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM document_edit_handoff WHERE document_id=@DocumentId AND to_user=@ToUser AND version_id=@VersionId AND expires_at=@Expires AND expires_at<=@Now",
            new { handoff.DocumentId, handoff.ToUser, handoff.VersionId, Expires = handoff.ExpiresAt.UtcDateTime, Now = now.UtcDateTime }, cancellationToken: cancellationToken)) == 1;
    }

    private sealed class HandoffRow
    {
        public Guid DocumentId { get; init; }
        public Guid ProjectId { get; init; }
        public string FromUser { get; init; } = "";
        public string ToUser { get; init; } = "";
        public Guid VersionId { get; init; }
        public DateTime ExpiresAt { get; init; }
        public string? WaitSourceKey { get; init; }
    }
}
