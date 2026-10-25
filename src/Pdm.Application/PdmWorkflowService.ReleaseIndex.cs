using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    private static void RequireCompleteSelectedReleaseIndex(CadReferenceSnapshot? snapshot,
        IReadOnlyList<BomItem> selected, IReadOnlyList<BomItem> removed, Guid? explicitRootId)
    {
        var sources = selected.Select(item => (Item: item, Removed: false))
            .Concat(removed.Select(item => (Item: item, Removed: true)))
            .Where(source => source.Item.SourceDocumentId.HasValue || !string.IsNullOrWhiteSpace(source.Item.SourceInstancePath)).ToArray();
        if (sources.Length == 0) return; // Explicitly manual BOM rows have no CAD index dependency.
        if (snapshot is null) throw new PdmRuleException("所选物料的CAD来源尚未核实，请核对所属装配范围后再发布。");
        var roots = ProjectReferenceForest.Roots(snapshot);
        var requiredRoots = new HashSet<Guid>();
        static string PathKey(string? path) => (path ?? "").Replace('\\', '/').Trim('/');
        static string RelativePath(string path)
        {
            var parts = path.Split('/');
            return parts.Length >= 3 && parts[0].Equals("roots", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(parts[1], out _) ? string.Join('/', parts.Skip(2)) : path;
        }

        foreach (var source in sources)
        {
            var item = source.Item;
            var path = PathKey(item.SourceInstancePath);
            Guid? pathRoot = null;
            if (path.StartsWith("roots/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                if (parts.Length < 3 || !Guid.TryParse(parts[1], out var id))
                    throw new PdmRuleException($"物料{item.DrawingNumber}的CAD来源路径无法确定所属装配，请核对后再发布。");
                pathRoot = id;
            }
            if (explicitRootId.HasValue && pathRoot.HasValue && explicitRootId != pathRoot)
                throw new PdmRuleException($"物料{item.DrawingNumber}的CAD来源不属于所选装配范围，请核对后再发布。");
            var candidates = roots.Where(root => root.DocumentId.HasValue
                && (!pathRoot.HasValue || root.DocumentId == pathRoot)
                && (!explicitRootId.HasValue || root.DocumentId == explicitRootId)).ToArray();
            if (!source.Removed)
            {
                candidates = candidates.Where(root => FlattenOpenNodes(root).Any(node =>
                    (!item.SourceDocumentId.HasValue || node.DocumentId == item.SourceDocumentId)
                    && (string.IsNullOrWhiteSpace(item.SourceConfiguration)
                        || string.Equals(node.Configuration, item.SourceConfiguration, StringComparison.OrdinalIgnoreCase))
                    && (path.Length == 0 || string.Equals(RelativePath(PathKey(node.InstancePath)), RelativePath(path), StringComparison.OrdinalIgnoreCase)))).ToArray();
            }
            // Deleted occurrences are absent from the current tree by definition. Their source comes
            // only from the bound BOM's immutable base version; never infer it from a filename.
            if (candidates.Length != 1)
                throw new PdmRuleException($"物料{item.DrawingNumber}的CAD来源无法唯一对应到已存档装配，请核对所属范围后再发布。");
            requiredRoots.Add(candidates[0].DocumentId!.Value);
        }
        if (snapshot.Root.StructureIndexPending || roots.Any(root => root.DocumentId.HasValue
            && requiredRoots.Contains(root.DocumentId.Value) && root.HasPendingStructureIndex))
            throw new PdmRuleException("原生图档已存档，所选范围的结构或图模关联仍待补全；已有BOM保留，补全并核对后才能正式发布。");
    }

    private async Task RequireCompleteScopedReleaseIndexAsync(CadReferenceSnapshot? snapshot, ReleaseScope scope,
        IReadOnlyList<BomItem> selected, IReadOnlyList<Guid>? confirmedRemovedIds, Guid? explicitRootId,
        Guid projectId, CancellationToken ct, Guid? boundBomVersionId = null)
    {
        if (IsLongLeadScope(scope) || ReleaseScopeBomKind(scope) == BomKind.Electrical) return;
        if (snapshot?.Root.HasPendingStructureIndex != true) return;
        BomItem[] removed = [];
        if (confirmedRemovedIds is { Count: > 0 })
        {
            var kind = ReleaseScopeBomKind(scope);
            BomVersion? baseline;
            if (boundBomVersionId.HasValue)
            {
                var bound = await repository.FindBomVersionAsync(projectId, boundBomVersionId.Value, ct);
                baseline = bound?.BaseVersionId is Guid baseId
                    ? await repository.FindBomVersionAsync(projectId, baseId, ct) : null;
            }
            else baseline = (await repository.ListBomVersionsAsync(projectId, kind, ct))
                .FirstOrDefault(version => version.State == BomVersionState.Released);
            var ids = confirmedRemovedIds.ToHashSet();
            removed = baseline is not null && baseline.ProjectId == projectId && baseline.Kind == kind && baseline.State == BomVersionState.Released
                ? baseline.Items.Where(item => ids.Contains(item.Id)).ToArray() : [];
            if (removed.Select(item => item.Id).Distinct().Count() != ids.Count)
                throw new PdmRuleException("确认删除项的CAD来源无法从本次绑定的正式BOM基线核实，请刷新变更范围后重试。");
        }
        RequireCompleteSelectedReleaseIndex(snapshot, selected, removed, explicitRootId);
    }

    private async Task RequireCompletePackageReleaseIndexAsync(ReleasePackage package, CadReferenceSnapshot? snapshot, CancellationToken ct)
    {
        if (package.Scope == ReleaseScope.LegacyCombined) { RequireCompleteReleaseIndex(snapshot); return; }
        if (IsLongLeadScope(package.Scope) || ReleaseScopeBomKind(package.Scope) == BomKind.Electrical) return;
        if (snapshot?.Root.HasPendingStructureIndex != true) return;
        var kind = ReleaseScopeBomKind(package.Scope);
        var ids = package.SelectedBomItemIds.ToHashSet();
        var selected = PackageBomSnapshot(package, kind).Where(item => ids.Contains(item.Id)).ToArray();
        if (selected.Select(item => item.Id).Distinct().Count() != ids.Count)
            throw new PdmRuleException("所选物料的CAD来源未完整保存在发布快照中，请刷新发布草稿后重试。");
        var versionId = kind == BomKind.Standard ? package.StandardBomVersionId : package.NonStandardBomVersionId;
        await RequireCompleteScopedReleaseIndexAsync(snapshot, package.Scope, selected, package.ConfirmedRemovedBomItemIds,
            package.RootDocumentId, package.ProjectId, ct, versionId);
    }
}
