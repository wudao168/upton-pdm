using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

internal sealed record PendingDrawingRelationSource(Guid DocumentId, Guid ProjectId, string FileName, DocumentReferenceNode Root);
internal sealed record DrawingRelationModelVersion(Guid DocumentId, Guid ProjectId, string FileName, string Kind, string RevisionLabel);

internal static class PendingDrawingRelations
{
    internal static IReadOnlyList<DocumentModelDrawingRelation> Reconcile(
        IEnumerable<DocumentModelDrawingRelation> stored, IEnumerable<PendingDrawingRelationSource> evidence,
        IEnumerable<DrawingRelationModelVersion> versions)
    {
        var sources = evidence.Where(source => source.Root.Kind == DocumentKind.Drawing
            && source.Root.DocumentId == source.DocumentId
            && string.Equals(source.Root.FileName, source.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var disproved = sources.ToDictionary(source => source.DocumentId,
            source => DrawingModelRelations.DependencyOnlyModelIds(source.Root));
        var complete = sources.Where(source => DrawingModelRelations.HasCompleteModelIndex(source.Root))
            .ToDictionary(source => source.DocumentId,
                source => DrawingModelRelations.KnownModels(source.Root).Select(node => node.DocumentId!.Value).ToHashSet());
        // Never turn a missing/partial read into evidence of removal. Explicitly
        // classified dependencies, however, cannot remain paired-model relations.
        return stored.Where(row => (!disproved.TryGetValue(row.DrawingDocumentId, out var ids)
                || !ids.Contains(row.ModelDocumentId)) && (!complete.TryGetValue(row.DrawingDocumentId, out var direct)
                || direct.Contains(row.ModelDocumentId)))
            .Concat(Recover(sources, versions)).Distinct()
            .OrderBy(row => row.ModelDocumentId).ThenBy(row => row.DrawingDocumentId).ToArray();
    }

    // Read-only compatibility for versions saved before partial drawings kept their known relations.
    // Callers supply only current drawing snapshots and versions of live, registered model documents.
    internal static IReadOnlyList<DocumentModelDrawingRelation> Recover(
        IEnumerable<PendingDrawingRelationSource> sources, IEnumerable<DrawingRelationModelVersion> versions)
    {
        var byId = versions.ToLookup(version => version.DocumentId);
        var result = new HashSet<DocumentModelDrawingRelation>();
        foreach (var source in sources)
        {
            var root = source.Root;
            if (!root.StructureIndexPending || root.Kind != DocumentKind.Drawing || root.DocumentId != source.DocumentId
                || !string.Equals(root.FileName, source.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var node in DrawingModelRelations.KnownModels(root))
            {
                if (node.Kind is not (DocumentKind.Assembly or DocumentKind.Part)
                    || node.Status is ReferenceNodeStatus.Missing or ReferenceNodeStatus.Virtual
                    || node.DocumentId is not Guid id || node.Revision is not RevisionLabel revision) continue;
                if (byId[id].Any(version => version.ProjectId == source.ProjectId
                    && version.Kind == node.Kind.ToString() && version.RevisionLabel == revision.Display
                    && string.Equals(version.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new(id, source.DocumentId));
            }
        }
        return result.ToArray();
    }
}
