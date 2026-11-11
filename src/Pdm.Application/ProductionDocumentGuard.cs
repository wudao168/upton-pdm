using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>Recheck the live document lifecycle even when a PDA holds a cached scan.</summary>
public static class ProductionDocumentGuard
{
    public static async Task RequireAsync(IPdmRepository repository, ProductionTrackingState state,
        Guid lineId, CancellationToken ct)
    {
        var line = state.Lines.Single(item => item.Id == lineId);
        var package = await repository.FindReleasePackageAsync(state.FormalReleasePackageId ?? state.ReleasePackageId, ct);
        if (package?.State != ReleasePackageState.Published)
            throw new PdmConflictException("该批次的发布记录已失效，请重新扫码。");
        var documentIds = new HashSet<Guid>();
        if (line.SourceDocumentId is Guid sourceId)
        {
            documentIds.Add(sourceId);
            var relations = await repository.ListDocumentRelationsAsync(state.ProjectId, ct);
            foreach (var relation in relations.Where(item => item.ModelDocumentId == sourceId))
                if (package.VersionBindings?.Any(item => item.DocumentId == relation.DrawingDocumentId) == true)
                    documentIds.Add(relation.DrawingDocumentId);
        }
        foreach (var binding in package.VersionBindings ?? [])
        {
            var document = await repository.FindDocumentAsync(binding.DocumentId, ct);
            if (document is null) continue;
            if (document.Kind == DocumentKind.Drawing)
            {
                var version = await repository.FindDocumentVersionAsync(binding.DocumentId, binding.VersionId, ct);
                if (version is not null && (line.SourceDocumentId is Guid modelId
                        && ReleaseBoundSourceReader.ModelDocumentIds(package, binding.DocumentId, version.ReferenceSnapshot).Contains(modelId)
                    || string.Equals(document.DrawingNumber, line.DrawingNumber, StringComparison.OrdinalIgnoreCase)))
                    documentIds.Add(document.Id);
            }
        }
        foreach (var id in documentIds)
        {
            var document = await repository.FindDocumentAsync(id, ct);
            if (document is null || document.State == DocumentLifecycleState.Obsolete)
                throw new PdmConflictException("该批次的图档已作废或删除，不能扫码报工、打印或补打，请联系技术人员确认替代图纸。");
        }
    }
}
