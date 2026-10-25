using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

internal sealed record PendingDrawingRelationSource(Guid DocumentId, Guid ProjectId, string FileName, DocumentReferenceNode Root);
internal sealed record DrawingRelationModelVersion(Guid DocumentId, Guid ProjectId, string FileName, string Kind, string RevisionLabel);

internal static class PendingDrawingRelations
{
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
            foreach (var node in root.Children)
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
