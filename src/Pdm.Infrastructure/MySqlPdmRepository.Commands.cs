using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<PdmDocument>> ListCheckedOutDocumentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,updated_at
            FROM document WHERE checked_out_by IS NOT NULL ORDER BY checked_out_at
            """, cancellationToken: cancellationToken));
        return rows.Select(MapDocument).ToArray();
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return CheckoutAsync(documentId, actor, Guid.NewGuid(), "legacy-client", now.AddMinutes(15), null, cancellationToken);
    }

    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
        => await CheckoutAsync(documentId, actor, sessionId, machineName, leaseExpiresAt, null, cancellationToken);

    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, Guid? drawingReviewWritebackId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new PdmRuleException("编辑会话编号不能为空。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lockedDocumentId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM document WHERE id=@DocumentId FOR UPDATE",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        if (!lockedDocumentId.HasValue) throw new PdmNotFoundException("图档不存在。 ");
        var drawingReviewLocked = await IsDocumentUnderActiveDrawingReviewAsync(connection, transaction, documentId, cancellationToken);
        var controlledWriteback = drawingReviewWritebackId.HasValue
            && await IsActiveDrawingReviewWritebackAsync(connection, transaction, documentId, drawingReviewWritebackId.Value, cancellationToken);
        if (drawingReviewLocked && !controlledWriteback)
            throw new PdmConflictException("图档正在进行图纸审核，不能获取编辑权限。");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document
            SET checked_out_by=@Actor,
                checked_out_at=COALESCE(checked_out_at,@Now),
                checkout_session_id=@SessionId,
                checkout_machine=@MachineName,
                checkout_last_heartbeat_at=@Now,
                checkout_lease_expires_at=@LeaseExpiresAt,
                checkout_release_requested_by=NULL,
                checkout_release_requested_at=NULL,
                checkout_release_request_reason=NULL,
                updated_at=@Now,
                row_version=row_version+1
            WHERE id=@DocumentId
              AND (checked_out_by IS NULL
                   OR (checked_out_by=@Actor
                       AND (checkout_machine=@MachineName OR checkout_machine IS NULL OR checkout_machine='')))
            """,
            new { DocumentId = documentId, Actor = actor, SessionId = sessionId, MachineName = machineName.Trim(), Now = now, LeaseExpiresAt = leaseExpiresAt.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            var current = await FindDocumentAsync(connection, transaction, documentId, cancellationToken);
            throw current is null
                ? new PdmNotFoundException("图档不存在。 ")
                : new PdmConflictException($"图档正在由{current.CheckedOutBy}编辑。 ");
        }

        var updated = await FindDocumentAsync(connection, transaction, documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<Guid>> HeartbeatCheckoutSessionAsync(Guid sessionId, string actor, string machineName, IReadOnlyList<Guid> documentIds, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || documentIds.Count == 0) return [];
        var ids = documentIds.Distinct().ToArray();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document
            SET checkout_machine=@MachineName,checkout_last_heartbeat_at=@Now,checkout_lease_expires_at=@LeaseExpiresAt
            WHERE id IN @DocumentIds AND checked_out_by=@Actor AND checkout_session_id=@SessionId
            """,
            new { DocumentIds = ids, Actor = actor, SessionId = sessionId, MachineName = machineName.Trim(), Now = now, LeaseExpiresAt = leaseExpiresAt.UtcDateTime },
            cancellationToken: cancellationToken));
        var active = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM document WHERE id IN @DocumentIds AND checked_out_by=@Actor AND checkout_session_id=@SessionId",
            new { DocumentIds = ids, Actor = actor, SessionId = sessionId }, cancellationToken: cancellationToken));
        return active.ToArray();
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, string sha256, CancellationToken cancellationToken)
    {
        var document = await FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CompleteEditWithoutChangesAsync(documentId, actor, document.CheckoutSessionId.Value, sha256, cancellationToken);
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, Guid sessionId, string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<EditLockRow>(new CommandDefinition(
            "SELECT checked_out_by,checkout_session_id,row_version FROM document WHERE id=@DocumentId FOR UPDATE",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("图档不存在。");
        EnsureSessionOwner(current, actor, sessionId, "结束编辑");
        var latestSha256 = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT sha256 FROM document_version WHERE document_id=@DocumentId ORDER BY created_at DESC LIMIT 1",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(latestSha256)) throw new PdmConflictException("图档尚无存档版本，必须先提交W1。");
        if (!string.Equals(latestSha256, sha256, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("文件已经发生变更，请使用提交存档。");
        await ReleaseEditLockAsync(connection, transaction, documentId, actor, sessionId, current.RowVersion, cancellationToken);
        var updated = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        var document = await FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await DiscardCheckoutAsync(documentId, actor, document.CheckoutSessionId.Value, cancellationToken);
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<EditLockRow>(new CommandDefinition(
            "SELECT checked_out_by,checkout_session_id,row_version FROM document WHERE id=@DocumentId FOR UPDATE",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("图档不存在。");
        EnsureSessionOwner(current, actor, sessionId, "放弃编辑");
        await ReleaseEditLockAsync(connection, transaction, documentId, actor, sessionId, current.RowVersion, cancellationToken);
        var updated = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<PdmDocument> RequestCheckoutReleaseAsync(Guid documentId, string requestedBy, string reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document SET checkout_release_requested_by=@RequestedBy,checkout_release_requested_at=@Now,
                checkout_release_request_reason=@Reason,updated_at=@Now,row_version=row_version+1
            WHERE id=@DocumentId AND checked_out_by IS NOT NULL
            """,
            new { DocumentId = documentId, RequestedBy = requestedBy, Reason = reason.Trim(), Now = now }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档当前没有可申请释放的编辑权限。");
        return await FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
    }

    public async Task<PdmDocument> ForceReleaseCheckoutAsync(Guid documentId, string releasedBy, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<EditLockRow>(new CommandDefinition(
            "SELECT checked_out_by,checkout_session_id,row_version FROM document WHERE id=@DocumentId FOR UPDATE",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("图档不存在。");
        if (string.IsNullOrWhiteSpace(current.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可释放。");
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document SET checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,
                checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,
                checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1
            WHERE id=@DocumentId AND row_version=@RowVersion
            """,
            new { DocumentId = documentId, current.RowVersion, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档编辑状态已经变化，请刷新后重试。");
        var updated = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task ReleaseEditLockAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Guid documentId, string actor, Guid sessionId, long rowVersion, CancellationToken cancellationToken)
    {
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document SET checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,
                checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,
                checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1
            WHERE id=@DocumentId AND checked_out_by=@Actor AND checkout_session_id=@SessionId AND row_version=@RowVersion
            """,
            new { DocumentId = documentId, Actor = actor, SessionId = sessionId, RowVersion = rowVersion, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档编辑状态已经变化，请刷新后重试。");
    }

    private static void EnsureSessionOwner(EditLockRow current, string actor, Guid sessionId, string action)
    {
        if (!string.Equals(current.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase) || current.CheckoutSessionId != sessionId)
            throw new PdmConflictException($"编辑会话已经失效，不能{action}。请另存本地修改或重新获取权限。");
    }

    private sealed class EditLockRow
    {
        public string? CheckedOutBy { get; init; }
        public Guid? CheckoutSessionId { get; init; }
        public long RowVersion { get; init; }
    }

    public async Task<PdmDocument> CheckInAsync(Guid documentId, string actor, RevisionLabel nextRevision, CadReferenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document
            SET revision_label=@Revision,checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,
                checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,
                checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1
            WHERE id = @DocumentId AND checked_out_by = @Actor
            """,
            new { DocumentId = documentId, Actor = actor, Revision = nextRevision.Display, Now = now },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            var current = await FindDocumentAsync(connection, transaction, documentId, cancellationToken);
            throw current is null
                ? new PdmNotFoundException("图档不存在。 ")
                : new PdmConflictException("只有当前编辑人员可以提交存档。 ");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reference_snapshot(id, project_id, root_document_id, captured_at, captured_by, sha256, root_json)
            VALUES (@Id, @ProjectId, @RootDocumentId, @CapturedAt, @CapturedBy, @Sha256, @RootJson)
            """,
            new
            {
                Id = snapshot.SnapshotId,
                snapshot.ProjectId,
                snapshot.RootDocumentId,
                CapturedAt = snapshot.CapturedAt.UtcDateTime,
                snapshot.CapturedBy,
                snapshot.Sha256,
                RootJson = JsonSerializer.Serialize(snapshot.Root, jsonOptions)
            },
            transaction,
            cancellationToken: cancellationToken));

        var updated = await FindDocumentAsync(connection, transaction, documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO release_package(
                id, project_id, package_number, state, release_scope, workflow_code, workflow_version,
                selected_bom_item_ids_json, creates_manufacturing_baseline, locks_documents,
                reference_snapshot_id, mechanical_bom_revision,
                electrical_bom_revision, mechanical_bom_snapshot_json, electrical_bom_snapshot_json,
                standard_bom_version_id, non_standard_bom_version_id, electrical_bom_version_id,
                standard_bom_revision, non_standard_bom_revision, standard_bom_snapshot_json, non_standard_bom_snapshot_json,
                change_number, change_reason, effective_serial_from, effective_serial_to,
                published_at, published_path, publish_error, row_version, created_at)
            VALUES (
                @Id, @ProjectId, @PackageNumber, @State, @Scope, @WorkflowCode, @WorkflowVersion,
                @SelectedBomItemIds, @CreatesManufacturingBaseline, @LocksDocuments,
                @ReferenceSnapshotId, @MechanicalBomRevision,
                @ElectricalBomRevision, @MechanicalBomSnapshot, @ElectricalBomSnapshot,
                @StandardBomVersionId, @NonStandardBomVersionId, @ElectricalBomVersionId,
                @StandardBomRevision, @NonStandardBomRevision, @StandardBomSnapshot, @NonStandardBomSnapshot,
                @ChangeNumber, @ChangeReason, @EffectiveSerialFrom, @EffectiveSerialTo,
                NULL, NULL, NULL, 1, @CreatedAt)
            """,
            new
            {
                package.Id,
                package.ProjectId,
                PackageNumber = package.Number,
                State = package.State.ToString(),
                Scope = package.Scope.ToString(),
                package.WorkflowCode,
                package.WorkflowVersion,
                SelectedBomItemIds = JsonSerializer.Serialize(package.SelectedBomItemIds, jsonOptions),
                package.CreatesManufacturingBaseline,
                package.LocksDocuments,
                package.ReferenceSnapshotId,
                package.MechanicalBomRevision,
                package.ElectricalBomRevision,
                MechanicalBomSnapshot = JsonSerializer.Serialize(package.MechanicalBomSnapshot, jsonOptions),
                ElectricalBomSnapshot = JsonSerializer.Serialize(package.ElectricalBomSnapshot, jsonOptions),
                package.StandardBomVersionId,
                package.NonStandardBomVersionId,
                package.ElectricalBomVersionId,
                package.StandardBomRevision,
                package.NonStandardBomRevision,
                StandardBomSnapshot = JsonSerializer.Serialize(package.StandardBomSnapshot, jsonOptions),
                NonStandardBomSnapshot = JsonSerializer.Serialize(package.NonStandardBomSnapshot, jsonOptions),
                package.ChangeNumber,
                package.ChangeReason,
                package.EffectiveSerialFrom,
                package.EffectiveSerialTo,
                CreatedAt = package.CreatedAt.UtcDateTime
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var task in package.ApprovalTasks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO approval_task(id, release_package_id, stage, step_order, step_name, assignee, decision_by, decision_value, decision_comment, decided_at, is_emergency_substitute, emergency_reason)
                VALUES (@Id, @ReleasePackageId, @Stage, @StepOrder, @StepName, @Assignee, NULL, NULL, NULL, NULL, 0, NULL)
                """,
                new { task.Id, task.ReleasePackageId, Stage = task.Stage.ToString(), task.StepOrder, task.StepName, task.Assignee },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var packageState = await connection.QuerySingleOrDefaultAsync<ReleaseSnapshotStateRow>(new CommandDefinition(
            """
            SELECT package.state,package.release_scope,package.locks_documents,snapshot.root_json
            FROM release_package package
            INNER JOIN reference_snapshot snapshot ON snapshot.id=package.reference_snapshot_id
            WHERE package.id=@PackageId
            FOR UPDATE
            """,
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (packageState.State is not ("Draft" or "Rejected" or "PublishFailed"))
            throw new PdmConflictException("只有草稿、已驳回或发布失败的发布包可以提交。");
        var documentIds = DeserializeDocumentIds(packageState.RootJson);
        if (packageState.LocksDocuments && documentIds.Length == 0) throw new PdmConflictException("发布包引用快照中没有可审批图档。");
        var blocked = packageState.LocksDocuments ? await connection.QuerySingleOrDefaultAsync<DocumentApprovalBlockRow>(new CommandDefinition(
            """
            SELECT drawing_number,checked_out_by,lifecycle_state
            FROM document
            WHERE id IN @DocumentIds AND (checked_out_by IS NOT NULL OR lifecycle_state='Obsolete')
            LIMIT 1
            FOR UPDATE
            """,
            new { DocumentIds = documentIds }, transaction, cancellationToken: cancellationToken)) : null;
        if (blocked is not null)
        {
            if (blocked.LifecycleState == DocumentLifecycleState.Obsolete.ToString())
                throw new PdmConflictException($"图档{blocked.DrawingNumber}已作废，不能提交审批。");
            throw new PdmConflictException($"图档{blocked.DrawingNumber}正在由{blocked.CheckedOutBy}编辑，不能提交审批。");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE approval_task SET decision_by=NULL,decision_value=NULL,decision_comment=NULL,decided_at=NULL,is_emergency_substitute=0,emergency_reason=NULL WHERE release_package_id=@PackageId",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (!string.Equals(packageState.ReleaseScope, ReleaseScope.LegacyCombined.ToString(), StringComparison.Ordinal))
        {
            var selfCheckTaskId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                "SELECT id FROM approval_task WHERE release_package_id=@PackageId ORDER BY step_order LIMIT 1",
                new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE approval_task SET decision_by=@Actor,decision_value='Approved',decision_comment='提交人自检',decided_at=@Now WHERE id=@TaskId",
                new { Actor = actor, Now = timeProvider.GetUtcNow().UtcDateTime, TaskId = selfCheckTaskId }, transaction, cancellationToken: cancellationToken));
        }
        var pendingTaskCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM approval_task WHERE release_package_id=@PackageId AND decision_value IS NULL",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        var submittedState = pendingTaskCount <= 1 ? ReleasePackageState.Approval : ReleasePackageState.ProcessReview;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state=@State,published_at=NULL,published_path=NULL,publish_error=NULL,row_version=row_version+1 WHERE id=@PackageId",
            new { State = submittedState.ToString(), PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (packageState.LocksDocuments)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET lifecycle_state='InReview',updated_at=@Now,row_version=row_version+1 WHERE id IN @DocumentIds",
                new { DocumentIds = documentIds, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var package = await FindReleasePackageAsync(connection, transaction, releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var packageState = await connection.QuerySingleOrDefaultAsync<ReleaseSnapshotStateRow>(new CommandDefinition(
            """
            SELECT package.state,package.release_scope,package.locks_documents,snapshot.root_json
            FROM release_package package
            INNER JOIN reference_snapshot snapshot ON snapshot.id=package.reference_snapshot_id
            WHERE package.id=@PackageId
            FOR UPDATE
            """,
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (packageState.State is not ("ProcessReview" or "Approval"))
            throw new PdmConflictException("只有审批中的发布包可以撤回。");
        var documentIds = DeserializeDocumentIds(packageState.RootJson);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE approval_task SET decision_by=NULL,decision_value=NULL,decision_comment=NULL,decided_at=NULL,is_emergency_substitute=0,emergency_reason=NULL WHERE release_package_id=@PackageId",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state='Draft',row_version=row_version+1 WHERE id=@PackageId",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (packageState.LocksDocuments && documentIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET lifecycle_state='Work',updated_at=@Now,row_version=row_version+1 WHERE id IN @DocumentIds AND lifecycle_state='InReview'",
                new { DocumentIds = documentIds, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        var package = await FindReleasePackageAsync(connection, transaction, releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, bool emergencySubstitute, string? emergencyReason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ApprovalDecisionRow>(new CommandDefinition(
            """
            SELECT t.id, t.release_package_id, t.stage, t.step_order, t.assignee, t.decision_value, p.state AS package_state, p.locks_documents
            FROM approval_task t
            INNER JOIN release_package p ON p.id = t.release_package_id
            WHERE t.id = @TaskId
            FOR UPDATE
            """,
            new { TaskId = taskId },
            transaction,
            cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("审批任务不存在。 ");

        if (row.DecisionValue is not null)
        {
            throw new PdmConflictException("审批任务已经处理。 ");
        }

        if (!emergencySubstitute && !string.Equals(row.Assignee, actor, StringComparison.OrdinalIgnoreCase) && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("只能处理分配给自己的审批任务。 ");
        }

        var currentTaskId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM approval_task WHERE release_package_id=@PackageId AND decision_value IS NULL ORDER BY step_order LIMIT 1",
            new { PackageId = row.ReleasePackageId }, transaction, cancellationToken: cancellationToken));
        if (currentTaskId != taskId || row.PackageState is not ("ProcessReview" or "Approval"))
        {
            throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE approval_task
            SET decision_by = @Actor, decision_value = @Decision, decision_comment = @Comment, decided_at = @Now,
                is_emergency_substitute = @EmergencySubstitute, emergency_reason = @EmergencyReason
            WHERE id = @TaskId
            """,
            new { TaskId = taskId, Actor = actor, Decision = decision.ToString(), Comment = comment, Now = now, EmergencySubstitute = emergencySubstitute, EmergencyReason = emergencyReason },
            transaction,
            cancellationToken: cancellationToken));

        var remainingTaskCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM approval_task WHERE release_package_id=@PackageId AND decision_value IS NULL",
            new { PackageId = row.ReleasePackageId }, transaction, cancellationToken: cancellationToken));
        var nextState = decision == ApprovalDecision.Rejected
            ? ReleasePackageState.Rejected
            : remainingTaskCount == 0 ? ReleasePackageState.Publishing
            : remainingTaskCount == 1 ? ReleasePackageState.Approval
            : ReleasePackageState.ProcessReview;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state = @State, row_version = row_version + 1 WHERE id = @PackageId",
            new { State = nextState.ToString(), PackageId = row.ReleasePackageId },
            transaction,
            cancellationToken: cancellationToken));

        if (nextState == ReleasePackageState.Rejected)
        {
            var rootJson = row.LocksDocuments ? await connection.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT snapshot.root_json FROM release_package package INNER JOIN reference_snapshot snapshot ON snapshot.id=package.reference_snapshot_id WHERE package.id=@PackageId",
                new { PackageId = row.ReleasePackageId }, transaction, cancellationToken: cancellationToken)) : null;
            var documentIds = rootJson is null ? [] : DeserializeDocumentIds(rootJson);
            if (documentIds.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE document SET lifecycle_state='Work',updated_at=@Now,row_version=row_version+1 WHERE id IN @DocumentIds AND lifecycle_state='InReview'",
                    new { DocumentIds = documentIds, Now = now }, transaction, cancellationToken: cancellationToken));
            }
        }

        if (nextState == ReleasePackageState.Publishing)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO integration_outbox(id, event_type, aggregate_type, aggregate_id, payload_json, occurred_at, retry_count)
                VALUES (@Id, 'ReleasePackageApproved', 'ReleasePackage', @AggregateId, @PayloadJson, @OccurredAt, 0)
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    AggregateId = row.ReleasePackageId.ToString(),
                    PayloadJson = JsonSerializer.Serialize(new { ReleasePackageId = row.ReleasePackageId }, jsonOptions),
                    OccurredAt = now
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        var package = await FindReleasePackageAsync(connection, transaction, row.ReleasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。 ");
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var document = await FindDocumentAsync(connection, transaction, documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckedOutBy is not null) throw new PdmConflictException("图档正在编辑，不能作废。");
        if (document.State == DocumentLifecycleState.InReview) throw new PdmConflictException("图档正在审批，不能作废。");
        if (document.State != DocumentLifecycleState.Obsolete)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET lifecycle_state='Obsolete',updated_at=@Now,row_version=row_version+1 WHERE id=@DocumentId",
                new { DocumentId = documentId, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        var obsolete = await FindDocumentAsync(connection, transaction, documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return obsolete;
    }

    private Guid[] DeserializeDocumentIds(string rootJson)
    {
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(rootJson, jsonOptions)
            ?? throw new InvalidDataException("引用树快照损坏。");
        return EnumerateDocumentIds(root).Distinct().ToArray();
    }

    public async Task MarkPublishedAsync(Guid releasePackageId, string publishedPath, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE release_package
            SET state = @State, published_at = @PublishedAt, published_path = @PublishedPath, publish_error = NULL, row_version = row_version + 1
            WHERE id = @PackageId AND state = @ExpectedState
            """,
            new
            {
                State = ReleasePackageState.Published.ToString(),
                PublishedAt = publishedAt.UtcDateTime,
                PublishedPath = publishedPath,
                PackageId = releasePackageId,
                ExpectedState = ReleasePackageState.Publishing.ToString()
            },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            throw new PdmConflictException("发布包状态已变化，不能标记为已发布。 ");
        }
        var package = await FindReleasePackageAsync(connection, transaction, releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var versionIds = new[] { package.StandardBomVersionId, package.NonStandardBomVersionId, package.ElectricalBomVersionId }
            .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        if (versionIds.Length > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE bom_version SET state='Released',updated_at=@PublishedAt,released_at=@PublishedAt,row_version=row_version+1 WHERE id IN @VersionIds AND state='InReview'",
                new { VersionIds = versionIds, PublishedAt = publishedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var eventType = package.Scope == ReleaseScope.StandardLongLead ? "LongLeadBomReleased" : "BomStreamReleased";
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_outbox(id,event_type,aggregate_type,aggregate_id,payload_json,occurred_at,retry_count)
            VALUES(@Id,@EventType,'ReleasePackage',@AggregateId,@PayloadJson,@OccurredAt,0)
            """,
            new
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                AggregateId = releasePackageId.ToString(),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    ReleasePackageId = releasePackageId,
                    package.ProjectId,
                    Scope = package.Scope.ToString(),
                    package.ChangeNumber,
                    package.StandardBomRevision,
                    package.SelectedBomItemIds,
                    PublishedPath = publishedPath
                }, jsonOptions),
                OccurredAt = publishedAt.UtcDateTime
            }, transaction, cancellationToken: cancellationToken));
        await EnqueueU9BomReleaseReadyAsync(connection, transaction, package, publishedAt, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkPublishFailedAsync(Guid releasePackageId, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state = @State, publish_error = @Error, row_version = row_version + 1 WHERE id = @PackageId AND state = @ExpectedState",
            new
            {
                State = ReleasePackageState.PublishFailed.ToString(),
                PackageId = releasePackageId,
                ExpectedState = ReleasePackageState.Publishing.ToString(),
                Error = error.Length <= 2000 ? error : error[..2000]
            },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_outbox(id, event_type, aggregate_type, aggregate_id, payload_json, occurred_at, retry_count, last_error)
            VALUES (@Id, 'ReleasePackagePublishFailed', 'ReleasePackage', @AggregateId, @PayloadJson, @OccurredAt, 0, @Error)
            """,
            new
            {
                Id = Guid.NewGuid(),
                AggregateId = releasePackageId.ToString(),
                PayloadJson = JsonSerializer.Serialize(new { ReleasePackageId = releasePackageId }, jsonOptions),
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                Error = error.Length <= 2000 ? error : error[..2000]
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pdm_user(id,username,display_name,password_hash,role,assigned_role_code,company_id,cross_company_view,is_active,row_version,created_at)
            VALUES (@Id,@Username,@DisplayName,@PasswordHash,@Role,@RoleCode,@CompanyId,@CrossCompanyView,@IsActive,1,@CreatedAt)
            """,
            new
            {
                user.Id,
                user.Username,
                user.DisplayName,
                user.PasswordHash,
                Role = user.Role.ToString(),
                RoleCode = user.EffectiveRoleCode,
                user.CompanyId,
                user.CrossCompanyView,
                user.IsActive,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime
            },
            cancellationToken: cancellationToken));
    }

    public async Task<UserAccount> UpdateUserAsync(string username, string displayName, UserRole role, string roleCode, bool isActive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_user SET display_name=@DisplayName,role=@Role,assigned_role_code=@RoleCode,is_active=@IsActive,token_version=token_version+1,row_version=row_version+1 WHERE username=@Username",
            new { Username = username, DisplayName = displayName, Role = role.ToString(), RoleCode = roleCode, IsActive = isActive },
            cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("用户不存在。");
        return await FindUserAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
    }

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_entry(id, occurred_at, actor, action_name, entity_type, entity_id, detail_json)
            VALUES (@Id, @OccurredAt, @Actor, @Action, @EntityType, @EntityId, @DetailJson)
            """,
            new
            {
                entry.Id,
                OccurredAt = entry.OccurredAt.UtcDateTime,
                entry.Actor,
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                DetailJson = JsonSerializer.Serialize(new { detail = entry.Detail }, jsonOptions)
            },
            cancellationToken: cancellationToken));
    }

    private sealed class ApprovalDecisionRow
    {
        public Guid Id { get; init; }
        public Guid ReleasePackageId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public int StepOrder { get; init; }
        public string Assignee { get; init; } = string.Empty;
        public string? DecisionValue { get; init; }
        public string PackageState { get; init; } = string.Empty;
        public bool LocksDocuments { get; init; }
    }

    private sealed class ReleaseSnapshotStateRow
    {
        public string State { get; init; } = string.Empty;
        public string ReleaseScope { get; init; } = Upton.Pdm.Domain.ReleaseScope.LegacyCombined.ToString();
        public bool LocksDocuments { get; init; }
        public string RootJson { get; init; } = string.Empty;
    }

    private sealed class DocumentApprovalBlockRow
    {
        public string DrawingNumber { get; init; } = string.Empty;
        public string? CheckedOutBy { get; init; }
        public string LifecycleState { get; init; } = string.Empty;
    }
}
