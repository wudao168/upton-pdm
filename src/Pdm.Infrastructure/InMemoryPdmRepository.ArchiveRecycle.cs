using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository : IArchiveRecycleStore
{
    private readonly Dictionary<Guid, ArchiveRecycleSet> archiveRecycleSets = new();
    public Task<IReadOnlyList<ArchiveRecycleBatch>> ListRecycleArchiveBatchesAsync(Guid projectId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ArchiveRecycleBatch>>(archiveBatchReceipts.Values.Where(item => item.ProjectId == projectId)
            .OrderByDescending(item => item.Results.Select(result => result.Version?.CreatedAt ?? result.Document.UpdatedAt).Max()).Take(100)
            .Select(item => new ArchiveRecycleBatch(item.BatchId, item.Results.Select(result => result.Version?.CreatedAt ?? result.Document.UpdatedAt).Max(), item.Actor, item.Results.Count)).ToArray());
    }
    private ArchiveRecycleInspection InspectRecycleArchive(Guid projectId, Guid batchId)
    {
        if (!archiveBatchReceipts.TryGetValue(batchId, out var receipt) || receipt.ProjectId != projectId) throw new PdmNotFoundException("该项目中没有已成功提交的对应存档批次。");
        var ids = receipt.Results.Select(item => item.Document.Id).ToHashSet();
        var current = documents.Values.Where(document => ids.Contains(document.Id) && document.ProjectId == projectId).Select(document =>
            new ArchiveRecycleDocument(WithStoredVersionCount(document), versions.Values.Where(version => version.DocumentId == document.Id).Select(version => version.Id).ToArray(),
                versions.Values.Any(version => version.DocumentId == document.Id && (version.Status == DocumentVersionStatus.Released || version.ReleasePackageId.HasValue)),
                IsDocumentUnderActiveDrawingReview(document.Id))).ToArray();
        var relations = ListDocumentRelationsAsync(projectId, default).GetAwaiter().GetResult().Where(item => (ids.Contains(item.ModelDocumentId) || ids.Contains(item.DrawingDocumentId))
            && documents.TryGetValue(item.ModelDocumentId, out var model) && !model.DeletedAt.HasValue && !model.PurgedAt.HasValue
            && documents.TryGetValue(item.DrawingDocumentId, out var drawing) && !drawing.DeletedAt.HasValue && !drawing.PurgedAt.HasValue).ToArray();
        var extraIds = relations.SelectMany(item => new[] { item.ModelDocumentId, item.DrawingDocumentId }).Where(id => !ids.Contains(id)).ToHashSet();
        current = current.Concat(documents.Values.Where(document => extraIds.Contains(document.Id)).Select(document => new ArchiveRecycleDocument(document, [], false, false))).ToArray();
        return new(receipt, current, relations, ArchiveRecycleRules.References(referenceSnapshots.Values.Select(item => item.Root)), archiveBatchFollowups.ContainsKey(batchId));
    }
    public Task<ArchiveRecycleInspection> InspectRecycleArchiveAsync(Guid projectId, Guid batchId, CancellationToken ct)
    { lock (gate) return Task.FromResult(InspectRecycleArchive(projectId, batchId)); }
    public Task<IReadOnlyList<ArchiveRecycleSet>> ListArchiveRecycleSetsAsync(Guid projectId, CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<ArchiveRecycleSet>>(archiveRecycleSets.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.CreatedAt).Take(100).ToArray()); }
    public Task<bool> IsDocumentInArchiveRecycleSetAsync(Guid projectId, Guid documentId, CancellationToken ct)
    { lock (gate) return Task.FromResult(archiveRecycleSets.Values.Any(item => item.ProjectId == projectId && !item.RestoredAt.HasValue && item.Members.Any(member => member.Document.Id == documentId))); }

    public Task<ArchiveRecycleSet> RecycleArchiveSetAsync(Guid projectId, Guid batchId, string scopeHash, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            var preview = ArchiveRecycleRules.Preview(InspectRecycleArchive(projectId, batchId)); ArchiveRecycleRules.RequirePreview(preview, scopeHash);
            var ids = preview.Members.Select(item => item.Document.Id).ToHashSet();
            var bomIds = bomItems.Where(item => item.ProjectId == projectId && item.SourceDocumentId.HasValue && ids.Contains(item.SourceDocumentId.Value)
                && !item.DeletedAt.HasValue && item.Source != "Manual" && !item.IsManuallyOverridden && !item.IsManuallyRetained).Select(item => item.Id).ToArray();
            var set = new ArchiveRecycleSet(Guid.NewGuid(), projectId, batchId, reason, actor, now, now.AddDays(30), preview.Members, bomIds);
            foreach (var member in set.Members) documents[member.Document.Id] = member.Document with
            { DeletedAt = now, DeletedBy = actor, DeleteReason = reason, RowVersion = member.Document.RowVersion + 1, UpdatedAt = now };
            for (var i = 0; i < bomItems.Count; i++) if (bomIds.Contains(bomItems[i].Id)) bomItems[i] = bomItems[i] with
            { DeletedAt = now, DeletedBy = actor, DeleteReason = "存档批次回收：" + set.Id };
            RebuildRecycleForest(projectId); archiveRecycleSets[set.Id] = set;
            audits.Enqueue(new(Guid.NewGuid(), now, actor, "document.recycle.set", "PdmDocument", set.Id.ToString(), reason));
            return Task.FromResult(set);
        }
    }
    public Task<ArchiveRecycleSet> RestoreArchiveSetAsync(Guid projectId, Guid setId, string actor, DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!archiveRecycleSets.TryGetValue(setId, out var set) || set.ProjectId != projectId || set.RestoredAt.HasValue || now >= set.ExpiresAt)
                throw new PdmRuleException("回收批次不存在、已恢复或已超过30天恢复期限。");
            var ids = set.Members.Select(item => item.Document.Id).ToHashSet(); var names = set.Members.Select(item => item.Document.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (documents.Values.Any(item => item.ProjectId == projectId && !ids.Contains(item.Id) && names.Contains(item.FileName) && !item.DeletedAt.HasValue && !item.PurgedAt.HasValue))
                throw new PdmConflictException("项目中已有同名图档，整批未恢复，请先核对冲突。");
            if (set.Members.Any(member => !documents.TryGetValue(member.Document.Id, out var document) || document.RowVersion != member.Document.RowVersion + 1
                || document.DeletedAt != set.CreatedAt || document.PurgedAt.HasValue)) throw new PdmConflictException("回收图档已被单独恢复或状态已变化，整批未恢复，请刷新后核对。");
            foreach (var member in set.Members) documents[member.Document.Id] = documents[member.Document.Id] with
            { DeletedAt = null, DeletedBy = null, DeleteReason = null, RowVersion = member.Document.RowVersion + 2, UpdatedAt = now };
            for (var i = 0; i < bomItems.Count; i++) if (set.BomItemIds.Contains(bomItems[i].Id) && bomItems[i].DeletedAt == set.CreatedAt
                && bomItems[i].DeleteReason == "存档批次回收：" + set.Id) bomItems[i] = bomItems[i] with { DeletedAt = null, DeletedBy = null, DeleteReason = null };
            RebuildRecycleForest(projectId); set = set with { RestoredAt = now }; archiveRecycleSets[set.Id] = set;
            audits.Enqueue(new(Guid.NewGuid(), now, actor, "document.restore.set", "PdmDocument", set.Id.ToString(), set.Reason));
            return Task.FromResult(set);
        }
    }
    private void RebuildRecycleForest(Guid projectId)
    {
        CadReferenceSnapshot? aggregate = null;
        foreach (var root in referenceRoots.Where(item => item.Key.Item1 == projectId && documents.TryGetValue(item.Key.Item2, out var doc)
            && !doc.DeletedAt.HasValue && !doc.PurgedAt.HasValue).OrderBy(item => item.Value.Root.FileName, StringComparer.OrdinalIgnoreCase))
            aggregate = ProjectReferenceForest.Merge(aggregate, root.Value);
        if (aggregate is null) referenceSnapshots.TryRemove(projectId, out _);
        else { referenceSnapshots[projectId] = aggregate; referenceSnapshotHistory[aggregate.SnapshotId] = aggregate; }
    }
}
