using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, EditHandoff> editHandoffs = new();

    private void RequireHandoffRecipient(Guid documentId, string actor)
    {
        if (editHandoffs.TryGetValue(documentId, out var handoff) && handoff.ExpiresAt > timeProvider.GetUtcNow() && !handoff.ToUser.Equals(actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException($"图档已定向交接给{handoff.ToUser}，预留5分钟接手，请稍后重试或联系交接人员。");
    }

    private PdmDocument ApplyArchiveEditDisposition(PdmDocument document, DocumentVersionCommit commit, string actor, Guid versionId, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(commit.HandoffTo)) editHandoffs[document.Id] = new(document.Id, document.ProjectId, actor, commit.HandoffTo, versionId, now.AddMinutes(5)) { WaitSourceKey = EditHandoff.WaitingKey(document) };
        return commit.KeepEditing
            ? document with { CheckedOutAt = document.CheckedOutAt >= now ? document.CheckedOutAt.Value.AddTicks(10) : now, CheckoutLastHeartbeatAt = now, CheckoutLeaseExpiresAt = now.AddMinutes(systemSettings.CheckoutLeaseMinutes), UpdatedAt = now }
            : ClearEditLock(document, now);
    }

    public Task<PdmDocument> ReclaimCheckoutAsync(Guid documentId, Guid expectedSessionId, DateTimeOffset expectedCheckoutAt, DateTimeOffset? heartbeatBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = documents.GetValueOrDefault(documentId) ?? throw new PdmNotFoundException("图档不存在。");
            if (current.CheckoutSessionId != expectedSessionId || current.CheckedOutAt != expectedCheckoutAt || string.IsNullOrWhiteSpace(current.CheckedOutBy))
                throw new PdmConflictException("编辑会话已经变化，原接管请求已取消，请刷新后重试。");
            if (heartbeatBefore.HasValue && ((current.CheckoutLastHeartbeatAt ?? current.CheckedOutAt ?? current.UpdatedAt) >= heartbeatBefore.Value || current.CheckoutLeaseExpiresAt >= now))
                throw new PdmConflictException("编辑人员已恢复连接，已取消离线回收。");
            if (current.State is DocumentLifecycleState.InReview or DocumentLifecycleState.Obsolete || IsDocumentUnderActiveDrawingReview(documentId))
                throw new PdmConflictException("审核、审批或作废图档不能通过普通编辑接管处理。");
            if (archiveUploads.Values.Any(upload => upload.State == "Staged" && upload.Session.ExpiresAt > now
                && upload.Session.ArchiveContext?.DocumentId == documentId && upload.Session.ArchiveContext.CheckoutSessionId == expectedSessionId
                && !versions.Values.Any(version => version.StorageRelativePath == upload.Session.TargetRelativePath)))
                throw new PdmConflictException("图档存在尚未结束的存档上传，请先核对该批次结果，暂不接管。");
            var result = ClearEditLock(current, now);
            documents[documentId] = result;
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<EditHandoff>> ListEditHandoffsAsync(CancellationToken cancellationToken)
    { lock (gate) return Task.FromResult<IReadOnlyList<EditHandoff>>(editHandoffs.Values.ToArray()); }

    public Task<bool> ExpireEditHandoffAsync(EditHandoff handoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(editHandoffs.TryGetValue(handoff.DocumentId, out var current) && current == handoff && current.ExpiresAt <= now && editHandoffs.Remove(handoff.DocumentId));
    }
}
