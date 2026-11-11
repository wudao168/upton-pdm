using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ArchiveRecycleBatch(Guid BatchId, DateTimeOffset CommittedAt, string Actor, int FileCount, string ProjectCode = "");
public sealed record ArchiveRecycleMember(PdmDocument Document, Guid VersionId);
public sealed record ArchiveRecyclePreview(Guid BatchId, IReadOnlyList<ArchiveRecycleMember> Members,
    IReadOnlyList<string> RetainedFiles, IReadOnlyList<string> Blockers, string ScopeHash)
{
    public bool CanRecycle => Members.Count > 0 && Blockers.Count == 0;
}
public sealed record ArchiveRecycleSet(Guid Id, Guid ProjectId, Guid BatchId, string Reason, string Actor,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, IReadOnlyList<ArchiveRecycleMember> Members,
    IReadOnlyList<Guid> BomItemIds, DateTimeOffset? RestoredAt = null);
public sealed record ArchiveRecycleDocument(PdmDocument Document, IReadOnlyList<Guid> VersionIds, bool HasReleasedVersion, bool UnderReview);
public sealed record ArchiveRecycleReference(Guid DocumentId, Guid ParentDocumentId, string ParentFileName);
public sealed record ArchiveRecycleInspection(ArchiveBatchReceipt Receipt, IReadOnlyList<ArchiveRecycleDocument> Documents,
    IReadOnlyList<DocumentModelDrawingRelation> Relations, IReadOnlyList<ArchiveRecycleReference> IncomingReferences, bool FollowupPending);

public interface IArchiveRecycleStore
{
    Task<IReadOnlyList<ArchiveRecycleBatch>> ListRecycleArchiveBatchesAsync(Guid projectId, CancellationToken ct);
    Task<ArchiveRecycleInspection> InspectRecycleArchiveAsync(Guid projectId, Guid batchId, CancellationToken ct);
    Task<IReadOnlyList<ArchiveRecycleSet>> ListArchiveRecycleSetsAsync(Guid projectId, CancellationToken ct);
    Task<bool> IsDocumentInArchiveRecycleSetAsync(Guid projectId, Guid documentId, CancellationToken ct);
    Task<ArchiveRecycleSet> RecycleArchiveSetAsync(Guid projectId, Guid batchId, string scopeHash, string reason, string actor, DateTimeOffset now, CancellationToken ct);
    Task<ArchiveRecycleSet> RestoreArchiveSetAsync(Guid projectId, Guid setId, string actor, DateTimeOffset now, CancellationToken ct);
}

public static class ArchiveRecycleRules
{
    public static ArchiveRecyclePreview Preview(ArchiveRecycleInspection state)
    {
        var members = new List<ArchiveRecycleMember>(); var retained = new List<string>(); var blockers = new List<string>();
        var documents = state.Documents.ToDictionary(item => item.Document.Id);
        foreach (var result in state.Receipt.Results)
        {
            if (!result.VersionCreated || result.Version is null) { retained.Add(result.Document.FileName + "：本批未新增版本，保留原图档"); continue; }
            if (!documents.TryGetValue(result.Document.Id, out var current) || current.Document.DeletedAt.HasValue || current.Document.PurgedAt.HasValue)
            { retained.Add(result.Document.FileName + "：已不在当前可回收范围"); continue; }
            if (current.VersionIds.Count != 1 || current.VersionIds[0] != result.Version.Id)
            { retained.Add(result.Document.FileName + "：已有其他版本，保留图档和历史；可使用历史版本恢复"); continue; }
            var document = current.Document;
            members.Add(new(document, result.Version.Id));
            if (document.State != DocumentLifecycleState.Work || current.HasReleasedVersion)
                blockers.Add(document.FileName + "：已发布、审批中或已作废，不能回收");
            if (!string.IsNullOrWhiteSpace(document.CheckedOutBy)) blockers.Add(document.FileName + "：仍在编辑，请先完成或释放签出");
            if (current.UnderReview) blockers.Add(document.FileName + "：图纸审核或写回尚未结束");
        }
        var ids = members.Select(item => item.Document.Id).ToHashSet();
        foreach (var relation in state.Relations.Where(item => ids.Contains(item.ModelDocumentId) != ids.Contains(item.DrawingDocumentId)))
            blockers.Add("存在本次回收范围之外的3D/2D关联：" + (documents.GetValueOrDefault(relation.ModelDocumentId)?.Document.FileName ?? "范围外模型")
                + " → " + (documents.GetValueOrDefault(relation.DrawingDocumentId)?.Document.FileName ?? "范围外工程图"));
        foreach (var reference in state.IncomingReferences.Where(item => ids.Contains(item.DocumentId) && !ids.Contains(item.ParentDocumentId)))
            blockers.Add("仍被本次范围之外的图档引用：" + reference.ParentFileName);
        if (state.FollowupPending) blockers.Add("服务器仍在更新本批BOM及通知，请完成后再回收");
        if (members.Count == 0) blockers.Add("本批没有可回收的首次存档图档");
        var distinctBlockers = blockers.Distinct().Order(StringComparer.Ordinal).ToArray();
        var scope = JsonSerializer.Serialize(new { state.Receipt.BatchId,
            Members = members.OrderBy(item => item.Document.Id).Select(item => new { item.Document.Id, item.Document.RowVersion, item.VersionId }),
            Blockers = distinctBlockers });
        return new(state.Receipt.BatchId, members, retained, distinctBlockers,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope))));
    }

    public static void RequirePreview(ArchiveRecyclePreview preview, string expectedHash)
    {
        if (!preview.CanRecycle) throw new PdmRuleException(string.Join("；", preview.Blockers));
        if (!string.Equals(preview.ScopeHash, expectedHash, StringComparison.Ordinal))
            throw new PdmConflictException("回收范围或图档状态已变化，请重新预览后确认；本次未处理任何图档。");
    }

    public static IReadOnlyList<ArchiveRecycleReference> References(IEnumerable<DocumentReferenceNode> roots)
    {
        var found = new List<ArchiveRecycleReference>();
        void Walk(DocumentReferenceNode node, Guid? parent, string parentName)
        {
            if (node.DocumentId is Guid id && parent is Guid owner && id != owner) found.Add(new(id, owner, parentName));
            foreach (var child in node.Children) Walk(child, node.DocumentId ?? parent, node.DocumentId.HasValue ? node.FileName : parentName);
        }
        foreach (var root in roots) Walk(root, null, "");
        return found.Distinct().ToArray();
    }
}

public sealed class ArchiveRecycleService(IPdmRepository repository, IArchiveRecycleStore store, TimeProvider clock)
{
    private async Task Access(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        if (!await AuthorizationPolicy.CanAsync(repository, projectId, actor, PermissionCodes.DocumentRecycle, ct))
            throw new UnauthorizedAccessException("当前账号没有此项目的图档回收权限。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("没有该项目的访问权限。");
    }
    public async Task<IReadOnlyList<ArchiveRecycleBatch>> ListBatchesAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        await Access(projectId, actor, role, ct);
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        return (await store.ListRecycleArchiveBatchesAsync(projectId, ct)).Select(item => item with { ProjectCode = project.Code }).ToArray();
    }
    public async Task<IReadOnlyList<ArchiveRecycleSet>> ListSetsAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    { await Access(projectId, actor, role, ct); return await store.ListArchiveRecycleSetsAsync(projectId, ct); }
    public async Task<ArchiveRecyclePreview> PreviewAsync(Guid projectId, Guid batchId, string actor, UserRole role, CancellationToken ct)
    {
        await Access(projectId, actor, role, ct);
        var preview = ArchiveRecycleRules.Preview(await store.InspectRecycleArchiveAsync(projectId, batchId, ct));
        foreach (var member in preview.Members)
            if (!await repository.HasDocumentAccessAsync(member.Document.Id, actor, role, FolderAccess.View | FolderAccess.Delete, ct))
                return preview with { Blockers = preview.Blockers.Append(member.Document.FileName + "：没有删除权限").ToArray() };
        return preview;
    }
    public async Task<ArchiveRecycleSet> RecycleAsync(Guid projectId, Guid batchId, string scopeHash, string reason, string actor, UserRole role, CancellationToken ct)
    {
        var preview = await PreviewAsync(projectId, batchId, actor, role, ct);
        ArchiveRecycleRules.RequirePreview(preview, scopeHash);
        reason = reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 500) throw new PdmRuleException("请填写1至500字的回收原因。");
        return await store.RecycleArchiveSetAsync(projectId, batchId, scopeHash, reason, actor, clock.GetUtcNow(), ct);
    }
    public async Task<ArchiveRecycleSet> RestoreAsync(Guid projectId, Guid setId, string actor, UserRole role, CancellationToken ct)
    { await Access(projectId, actor, role, ct); return await store.RestoreArchiveSetAsync(projectId, setId, actor, clock.GetUtcNow(), ct); }
}
