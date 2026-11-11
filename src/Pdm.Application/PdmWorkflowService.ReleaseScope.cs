using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    private static void RequireCompleteReleaseIndex(CadReferenceSnapshot? snapshot)
    {
        if (snapshot?.Root.HasPendingStructureIndex == true)
            throw new PdmRuleException("原生图档已存档，所选范围的结构或图模关联仍待补全；已有BOM保留，补全并核对后才能正式发布。");
    }
    private async Task<IReadOnlyList<Guid>> ValidateRelatedOplIssuesAsync(Guid projectId, ReleaseScope scope,
        IReadOnlyList<Guid>? ids, string actor, UserRole role, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return [];
        if (!IsSupplementScope(scope)) throw new PdmRuleException("只有增补/变更发布可以关联OPL问题。");
        if (projectOplService is null) throw new PdmRuleException("OPL关联服务尚未就绪，请联系管理员。");
        return await projectOplService.ValidateReleaseLinksAsync(projectId, ids, actor, role, ct);
    }

    private async Task<ReleasePackage> CaptureReleaseVersionsAsync(ReleasePackage package, CancellationToken ct)
    {
        // 仅首次冻结本次标准件长交期范围；其他BOM的历史发布快照不可按当前标准库补写。
        if (bomFiles is not null && package.Scope == ReleaseScope.StandardLongLead)
            package = package with
            {
                StandardBomSnapshot = await bomFiles.CaptureAsync(package.StandardBomSnapshot, ct),
                MechanicalBomSnapshot = await bomFiles.CaptureAsync(package.MechanicalBomSnapshot, ct)
            };
        if (package.Scope == ReleaseScope.LegacyCombined) return package;
        var drawingBomRelease = IsDrawingBomRelease(package.Scope);
        var selected = package.SelectedBomItemIds.ToHashSet();
        var models = IsNonStandardScope(package.Scope)
            ? package.NonStandardBomSnapshot.Where(item => selected.Contains(item.Id) && item.SourceDocumentId.HasValue)
                .Select(item => item.SourceDocumentId!.Value).Distinct().ToArray() : [];
        var drawingPairs = await DrawingModelPairing.ReadAsync(repository, package.ProjectId, ct);
        var uncertain = drawingPairs.FirstOrDefault(pair => pair.Issue is not null && pair.ReferencedModelIds.Any(models.Contains));
        if (!drawingBomRelease && !IsLongLeadScope(package.Scope) && uncertain is not null) throw new PdmRuleException(uncertain.Issue!);
        var relations = drawingPairs.Where(pair => pair.ModelDocumentId.HasValue)
            .Select(pair => new DocumentModelDrawingRelation(pair.ModelDocumentId!.Value, pair.DrawingDocumentId)).ToArray();
        var drawings = relations.Where(item => models.Contains(item.ModelDocumentId)).Select(item => item.DrawingDocumentId).ToHashSet();
        var publishIds = drawingBomRelease ? drawings : models.Concat(drawings).ToHashSet();
        var bindings = new Dictionary<Guid, ReleaseVersionBinding>();
        var drawingIds = new HashSet<Guid>();
        var reviewedItems = drawingBomRelease
            ? (await repository.ListDrawingReviewPackagesAsync(package.ProjectId, ct)).Where(review => IsDrawingReviewApproved(review.State))
                .SelectMany(review => review.Items).Where(item => IsDrawingReviewed(item.DrawingState)).ToArray() : [];
        var reference = package.ReferenceSnapshotId.HasValue ? await repository.FindReferenceSnapshotAsync(package.ReferenceSnapshotId.Value, ct) : null;
        await RequireCompletePackageReleaseIndexAsync(package, reference, ct);
        static IEnumerable<DocumentReferenceNode> Tree(DocumentReferenceNode node)
        {
            if (node.Role == ReferenceRole.ExternalFileDependency) yield break;
            yield return node;
            foreach (var child in node.Children) foreach (var descendant in Tree(child)) yield return descendant;
        }

        async Task AddAsync(Guid id, RevisionLabel? revision = null, Guid? reviewedVersionId = null)
        {
            var referenceOnly = drawingBomRelease && !publishIds.Contains(id);
            var document = await repository.FindDocumentAsync(id, ct);
            if (referenceOnly && (document is null || document.ProjectId != package.ProjectId || revision is null && reviewedVersionId is null)) return;
            if (document is null) throw new PdmRuleException("发布范围中的图档不存在。");
            if (document.ProjectId != package.ProjectId) throw new PdmRuleException("发布引用不属于当前项目。");
            var history = await repository.ListDocumentVersionsAsync(id, ct);
            var version = reviewedVersionId.HasValue ? history.FirstOrDefault(value => value.Id == reviewedVersionId)
                : revision is null ? history.FirstOrDefault() : history.FirstOrDefault(value => value.Revision == revision);
            if (referenceOnly && version is null) return;
            if (version is null) throw new PdmRuleException($"图档{document.FileName}的发布引用版本不存在，请核对后重试。");
            if (!drawingBomRelease && !IsLongLeadScope(package.Scope) && version.ReferenceSnapshot.HasPendingStructureIndex)
                throw new PdmRuleException($"图档{document.FileName}已保存，但结构或图模关联仍待补全，暂不能用于正式发布。");
            // A formal promotion preserves this source design; reuse it instead of promoting the same work file again.
            if (version.Status == DocumentVersionStatus.Work)
                version = history.FirstOrDefault(value => value.Status == DocumentVersionStatus.Released && value.SourceVersionId == version.Id) ?? version;
            // 引用模型冻结到当前使用版本，只有工程图对应的选中模型才升正式版。
            if (bindings.TryGetValue(id, out var bound))
            {
                if (!referenceOnly && bound.VersionId != version.Id && !MatchesReviewedVersion(history.First(value => value.Id == bound.VersionId), version.Id, history))
                    throw new PdmConflictException($"图档{document.FileName}在本次发布中引用了不同版本，请核对模型及工程图的使用版本。");
                return;
            }
            var selectedForPublication = publishIds.Contains(id) && !IsLongLeadScope(package.Scope);
            if (selectedForPublication && document.Kind == DocumentKind.Drawing)
            {
                var targetRevision = version.Status == DocumentVersionStatus.Work ? version.Revision.Release().Display : version.Revision.Display;
                BomDrawingRevision.ValidateDrawingProperties(version, targetRevision, document.FileName);
            }
            if (selectedForPublication && document.Kind == DocumentKind.Drawing) drawingIds.Add(id);
            var storedName = ReleaseBoundSourceReader.FileName(version.ReferenceSnapshot.DocumentId == id ? version.ReferenceSnapshot.FileName : document.FileName);
            bindings[id] = new(id, version.Id, version.Revision.Display, version.Sha256, selectedForPublication, storedName,
                selectedForPublication && version.Status == DocumentVersionStatus.Work,
                history.FirstOrDefault(value => value.Status == DocumentVersionStatus.Released)?.Revision.Display ?? "",
                selectedForPublication ? version.Status == DocumentVersionStatus.Work ? version.Revision.Release().Display : version.Revision.Display : "",
                document.Kind == DocumentKind.Drawing ? drawingPairs.SingleOrDefault(pair => pair.DrawingDocumentId == id)?.ModelDocumentId : null);
            if (drawingBomRelease && selectedForPublication && bindings[id].DrawingModelDocumentId is Guid modelId)
            {
                var review = reviewedItems.FirstOrDefault(item => item.ModelDocumentId == modelId && item.DrawingDocumentId == id
                    && item.EffectiveDrawingVersionId is Guid reviewed && MatchesReviewedVersion(version, reviewed, history));
                if (review is not null) await AddAsync(modelId, reviewedVersionId: review.EffectiveModelVersionId);
            }
            async Task VisitAsync(DocumentReferenceNode node)
            {
                if (drawingBomRelease && (node.IsSuppressed || node.Role == ReferenceRole.ExternalFileDependency || node.Status is ReferenceNodeStatus.Suppressed or ReferenceNodeStatus.Missing)) return;
                if (node.DocumentId is Guid childId && childId != id) await AddAsync(childId, node.Revision);
                else foreach (var child in node.Children) await VisitAsync(child);
            }
            foreach (var child in version.ReferenceSnapshot.Children) await VisitAsync(child);
        }

        // Capture selected models first: an associated drawing must use that exact saved version.
        foreach (var id in models.Where(_ => !drawingBomRelease))
        {
            var revisions = reference is null ? [] : Tree(reference.Root).Where(node => node.DocumentId == id && node.Revision.HasValue).Select(node => node.Revision!.Value).Distinct().ToArray();
            if (revisions.Length != 1) throw new PdmConflictException("所选模型在当前发布结构中没有唯一的使用版本，请选择对应装配范围并核对后再发布。");
            await AddAsync(id, revisions[0]);
        }
        foreach (var id in publishIds.Except(models)) await AddAsync(id);
        var captured = package with { VersionBindings = bindings.Values.ToArray(), DrawingDocumentCount = drawingIds.Count, LocksDocuments = !IsLongLeadScope(package.Scope)
            && IsNonStandardScope(package.Scope) && (bindings.Values.Any(item => item.Publish) || selected.Count > 0) };
        return captured;
    }

    private async Task ValidateReleaseVersionsAsync(ReleasePackage package, CancellationToken ct)
    {
        await RequireCompletePackageReleaseIndexAsync(package, package.ReferenceSnapshotId.HasValue
            ? await repository.FindReferenceSnapshotAsync(package.ReferenceSnapshotId.Value, ct) : null, ct);
        if (package.VersionBindings is null) return; // Existing in-flight packages keep their original rules.
        foreach (var binding in package.VersionBindings.Where(item => item.Publish))
        {
            var history = await repository.ListDocumentVersionsAsync(binding.DocumentId, ct);
            var bound = history.FirstOrDefault(version => version.Id == binding.VersionId);
            if (!IsDrawingBomRelease(package.Scope) && bound?.ReferenceSnapshot.HasPendingStructureIndex == true)
                throw new PdmRuleException("本次发布图档的结构或图模关联仍待补全，原生文件已保留，请补全后再发布。");
            var latest = history.FirstOrDefault();
            if (IsDrawingBomRelease(package.Scope) && binding.DrawingModelDocumentId.HasValue && !binding.RequiresPromotion
                && (bound is null || latest is null || latest.Id != bound.Id && !MatchesReviewedVersion(bound, latest.Id, history)))
                throw new PdmConflictException("本次发布绑定的2D图纸版本已变化，请刷新草稿后核对当前图纸的审核记录。");
            if (binding.RequiresPromotion && (latest?.Id != binding.VersionId || latest.Sha256 != binding.Sha256))
                throw new PdmConflictException("本次发布绑定的图纸版本已变化，请刷新发布草稿并重新核对；系统不会自动切换到新版本。");
        }
    }
}
