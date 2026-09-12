using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ControlledDocumentRecycleReadiness(
    PdmDocument Document,
    bool CanRecycle,
    IReadOnlyList<string> Blockers,
    int StoredVersionCount,
    int WhereUsedCount,
    DateTimeOffset? RestoreDeadline);

public sealed class ControlledDocumentRecycleService(IPdmRepository repository, TimeProvider timeProvider)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<PdmDocument>> ListAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, cancellationToken);
        return await repository.ListDeletedDocumentsAsync(projectId, cancellationToken);
    }

    public async Task<ControlledDocumentRecycleReadiness> GetReadinessAsync(Guid projectId, Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, cancellationToken);
        var document = await repository.FindDocumentIncludingDeletedAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        if (document.ProjectId != projectId) throw new PdmNotFoundException("图档不属于当前项目。");
        if (!await repository.HasDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Delete, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该图档的删除权限。");

        var blockers = new List<string>();
        if (document.PurgedAt is not null) blockers.Add("该图档已超过30天恢复期限。");
        if (document.DeletedAt is not null) blockers.Add("该图档已经在回收站中。");
        if (document.State == DocumentLifecycleState.Released) blockers.Add("已发布图档不能删除，请使用“作废图档”。");
        else if (document.State == DocumentLifecycleState.InReview) blockers.Add("审批中的图档不能删除，请先撤回审批。");
        else if (document.State == DocumentLifecycleState.Obsolete) blockers.Add("已作废图档不能删除，需保留发布审计链。");
        if (!string.IsNullOrWhiteSpace(document.CheckedOutBy)) blockers.Add($"图档正由 {document.CheckedOutBy} 签出，请先完成或释放签出。");
        if (await repository.IsDocumentUnderActiveDrawingReviewAsync(documentId, cancellationToken)) blockers.Add("图档正在图纸审核中，请先结束或撤回审核。");

        var relations = await repository.ListDocumentRelationsAsync(projectId, cancellationToken);
        if (relations.Any(item => item.ModelDocumentId == documentId || item.DrawingDocumentId == documentId))
            blockers.Add("图档存在受控的3D/2D关联，请先解除关联。");

        var whereUsed = await repository.ListWhereUsedAsync(documentId, cancellationToken);
        if (whereUsed.Count > 0) blockers.Add($"图档仍被 {whereUsed.Count} 处装配结构引用，请先解除引用并重新存档。");
        var root = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken);
        if (root?.RootDocumentId == documentId) blockers.Add("图档是当前装配结构根节点，不能删除。");

        var versions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken);
        if (versions.Any(version => version.ReleasePackageId is not null || version.Status == DocumentVersionStatus.Released))
            blockers.Add("图档存在已发布版本，只能作废，不能进入回收站。");

        return new ControlledDocumentRecycleReadiness(
            document,
            blockers.Count == 0,
            blockers,
            document.StoredVersionCount ?? versions.Count,
            whereUsed.Count,
            document.DeletedAt?.Add(Retention));
    }

    public async Task<PdmDocument> RecycleAsync(Guid projectId, Guid documentId, long expectedRowVersion, string? reason, string? confirmation, string actor, UserRole role, CancellationToken cancellationToken)
    {
        reason = reason?.Trim();
        confirmation = confirmation?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) throw new PdmRuleException("删除原因不能为空。");
        if (reason.Length > 500) throw new PdmRuleException("删除原因不能超过500个字符。");
        var readiness = await GetReadinessAsync(projectId, documentId, actor, role, cancellationToken);
        if (!readiness.CanRecycle) throw new PdmRuleException(string.Join("；", readiness.Blockers));
        if (!string.Equals(confirmation, readiness.Document.DrawingNumber, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(confirmation, readiness.Document.FileName, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("确认文字必须与图号或文件名完全一致。");

        var now = timeProvider.GetUtcNow();
        var updated = await repository.SetDocumentDeletedAsync(documentId, true, expectedRowVersion, actor, reason, now, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "document.recycle", nameof(PdmDocument), documentId.ToString(), $"{updated.DrawingNumber} · 原因：{reason} · 30天内可恢复"), cancellationToken);
        return updated;
    }

    public async Task<PdmDocument> RestoreAsync(Guid projectId, Guid documentId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, cancellationToken);
        var document = await repository.FindDocumentIncludingDeletedAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        if (document.ProjectId != projectId || document.DeletedAt is null || document.PurgedAt is not null)
            throw new PdmRuleException("该图档不在可恢复的回收站中。");
        var now = timeProvider.GetUtcNow();
        var updated = await repository.SetDocumentDeletedAsync(documentId, false, expectedRowVersion, actor, null, now, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "document.restore", nameof(PdmDocument), documentId.ToString(), $"{updated.DrawingNumber} · 从受控回收站恢复"), cancellationToken);
        return updated;
    }

    private async Task RequireAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role != UserRole.Administrator
            || !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentRecycle, cancellationToken))
            throw new UnauthorizedAccessException("仅管理员可管理受控图档回收站。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目内容的访问权限。");
    }
}
