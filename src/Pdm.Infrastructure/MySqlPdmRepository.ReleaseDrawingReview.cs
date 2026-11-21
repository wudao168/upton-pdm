using System.Data.Common;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<Guid>> ListReleasePackagesWithDrawingReworkAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM release_package
            WHERE state IN ('ProcessReview','Approval')
              AND JSON_SEARCH(drawing_review_json,'one','Issue',NULL,'$.items[*].notes[*].status') IS NOT NULL
            """, cancellationToken: ct));
        return ids.ToArray();
    }

    public async Task<ReleasePackage> SaveReleaseDrawingReviewAsync(Guid packageId, ReleaseDrawingReview review,
        int expectedRevision, Guid? approvalTaskId, CancellationToken ct, Guid? documentId = null)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        // Match the archive path: lock the document before updating its release package.
        var drawing = documentId is Guid id ? await LockDocumentAsync(connection, transaction, id, ct) : null;
        if (drawing?.LifecycleState == "Obsolete")
            throw new PdmConflictException("送审图纸已失效，请核对当前发布包。");
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE release_package p SET drawing_review_json=@Review,row_version=row_version+1
            WHERE p.id=@Id AND COALESCE(JSON_EXTRACT(drawing_review_json,'$.revision'),0)=@Expected
              AND ((@TaskId IS NULL AND state IN ('Draft','Rejected')) OR
                (@TaskId IS NOT NULL AND state IN ('ProcessReview','Approval') AND EXISTS
                  (SELECT 1 FROM approval_task t WHERE t.id=@TaskId AND t.release_package_id=p.id AND t.decision_value IS NULL
                    AND NOT EXISTS (SELECT 1 FROM approval_task earlier WHERE earlier.release_package_id=p.id
                      AND earlier.step_order<t.step_order AND earlier.decision_value IS NULL))))
            """, new { Id=packageId, Review=JsonSerializer.Serialize(review,jsonOptions), Expected=expectedRevision, TaskId=approvalTaskId },
            transaction, cancellationToken:ct));
        if (affected != 1) throw new PdmConflictException("图纸审核资料或审批节点已变化，请刷新后重试。");
        var package = await FindReleasePackageAsync(connection, transaction, packageId, ct)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (drawing is not null && approvalTaskId is Guid taskId && package.LocksDocuments
            && package.VersionBindings?.Any(binding => binding.DocumentId == drawing.Id && binding.Publish && binding.RequiresPromotion) == true)
        {
            var item = review.Items.Single(value => value.DocumentId == drawing.Id);
            var hasIssue = ReleaseDrawingReview.HasIssue(review, item, taskId);
            if (!hasIssue && drawing.CheckedOutBy is not null)
                throw new PdmConflictException("图纸正在编辑，请先结束编辑，再撤回退回。");
            await UpdateDrawingReviewLockAsync(connection, transaction, drawing.Id, !hasIssue, ct);
        }
        await transaction.CommitAsync(ct);
        return package;
    }

    private static Task UpdateDrawingReviewLockAsync(DbConnection connection, DbTransaction transaction, Guid id, bool locked, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE document SET lifecycle_state=@State,row_version=row_version+1
            WHERE id=@Id AND lifecycle_state IN ('Work','InReview','Released')
            """, new { Id = id, State = locked ? "InReview" : "Work" }, transaction, cancellationToken:ct));

    private async Task<PdmDocument> AdoptArchivedReviewDrawingAsync(DbConnection connection, DbTransaction transaction,
        PdmDocument document, DocumentVersion version, CancellationToken ct)
    {
        // The source file and all release bindings change in one transaction.
        var packageIds = await connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM release_package
            WHERE project_id=@ProjectId AND state IN ('ProcessReview','Approval') AND drawing_review_json IS NOT NULL
              AND JSON_SEARCH(drawing_review_json,'one',@DocumentId,NULL,'$.items[*].documentId') IS NOT NULL
            ORDER BY id FOR UPDATE
            """, new { document.ProjectId, DocumentId = document.Id.ToString() }, transaction, cancellationToken:ct));
        var lockDrawing = false;
        foreach (var packageId in packageIds)
        {
            var package = await FindReleasePackageAsync(connection, transaction, packageId, ct)
                ?? throw new PdmNotFoundException("发布包不存在。");
            var item = package.DrawingReview?.Items.SingleOrDefault(value => value.DocumentId == document.Id);
            if (item is null || ReleaseDrawingRevision.ActiveReturn(package, item) is not { } issue) continue;
            var task = package.ApprovalTasks.Single(step => step.Id == issue.ApprovalTaskId);
            if (document.CheckedOutBy is not null)
                throw new PdmRuleException("退回图纸须结束编辑并提交存档，才能自动进入复审。");
            var previous = await FindDocumentVersionAsync(connection, transaction, document.Id, item.VersionId, ct)
                ?? throw new PdmConflictException("发布包缺少原送审图纸版本。");
            var designer = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT display_name FROM pdm_user WHERE username=@Actor", new { Actor = version.CreatedBy }, transaction, cancellationToken:ct));
            var updated = ReleaseDrawingRevision.Replace(package, task, item, previous, version, designer);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE release_package SET state=@State,drawing_review_json=@Review,version_bindings_json=@Bindings,
                    pdf_title_block_snapshot_json=@TitleBlock,row_version=row_version+1 WHERE id=@Id
                """, new
                {
                    Id = packageId,
                    State = updated.State.ToString(),
                    Review = JsonSerializer.Serialize(updated.DrawingReview, jsonOptions),
                    Bindings = JsonSerializer.Serialize(updated.VersionBindings, jsonOptions),
                    TitleBlock = updated.PdfTitleBlockSnapshot is { } titleBlock ? JsonSerializer.Serialize(titleBlock, jsonOptions) : null
                }, transaction, cancellationToken:ct));
            var resetTasks = updated.ApprovalTasks.Where((step, index) => !ReferenceEquals(step, package.ApprovalTasks[index]));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE approval_task SET decision_by=NULL,decision_value=NULL,decision_comment=NULL,decided_at=NULL,
                    is_emergency_substitute=0,emergency_reason=NULL,participants_json=@Participants
                WHERE id=@Id AND release_package_id=@PackageId
                """, resetTasks.Select(step => new
                {
                    step.Id, PackageId = packageId, Participants = ApprovalGroupJson.Write(step.Participants)
                }), transaction, cancellationToken:ct));
            lockDrawing |= package.LocksDocuments && updated.VersionBindings!.Any(binding => binding.DocumentId == document.Id && binding.Publish);
        }
        if (!lockDrawing) return document;
        await UpdateDrawingReviewLockAsync(connection, transaction, document.Id, true, ct);
        return await FindDocumentAsync(connection, transaction, document.Id, ct) ?? throw new PdmNotFoundException("图档不存在。");
    }
}
