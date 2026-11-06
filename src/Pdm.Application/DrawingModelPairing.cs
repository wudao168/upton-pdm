using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record DrawingModelPair(Guid DrawingDocumentId, Guid? ModelDocumentId, IReadOnlySet<Guid> ReferencedModelIds, string? Issue);

/// <summary>工程图发布对象与加载依赖分开；装配图中的子模型不是独立发布对象。</summary>
public static class DrawingModelPairing
{
    public static async Task<IReadOnlyList<DrawingModelPair>> ReadAsync(IPdmRepository repository, Guid projectId, CancellationToken ct)
    {
        var documents = (await repository.ListDocumentsAsync(projectId, ct)).ToDictionary(item => item.Id);
        var relations = await repository.ListDocumentRelationsAsync(projectId, ct);
        var result = new List<DrawingModelPair>();
        foreach (var group in relations.GroupBy(item => item.DrawingDocumentId))
        {
            if (!documents.TryGetValue(group.Key, out var drawing)) continue;
            var models = group.Select(item => item.ModelDocumentId)
                .Where(id => documents.TryGetValue(id, out var model) && model.Kind is DocumentKind.Part or DocumentKind.Assembly).ToHashSet();
            if (models.Count == 1) { result.Add(new(group.Key, models.Single(), models, null)); continue; }
            var drawingVersion = (await repository.ListDocumentVersionsAsync(group.Key, ct)).FirstOrDefault();
            var roots = new List<Guid>();
            foreach (var id in models.Where(id => documents[id].Kind == DocumentKind.Assembly))
            {
                var revisions = drawingVersion is null ? [] : Nodes(drawingVersion.ReferenceSnapshot)
                    .Where(node => node.DocumentId == id && node.Revision.HasValue).Select(node => node.Revision!.Value).Distinct().ToArray();
                if (revisions.Length > 1) continue;
                var history = await repository.ListDocumentVersionsAsync(id, ct);
                var version = revisions.Length == 1 ? history.FirstOrDefault(item => item.Revision == revisions[0]) : history.FirstOrDefault();
                if (version is null || version.ReferenceSnapshot.HasPendingStructureIndex) continue;
                var contained = Nodes(version.ReferenceSnapshot).Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId!.Value).ToHashSet();
                if (models.All(contained.Contains)) roots.Add(id);
            }
            var primary = roots.Count == 1 ? roots[0] : (Guid?)null;
            result.Add(new(group.Key, primary, models, primary.HasValue ? null
                : $"工程图{drawing.FileName}对应多个模型，无法唯一确定发布对象。请核对工程图视图与主模型关联后重新存档；系统不会将全部引用模型自动发布。"));
        }
        return result;
    }

    public static HashSet<Guid> ReferenceOnlyModelIds(IReadOnlyList<DrawingModelPair> pairs)
    {
        var primary = pairs.Where(item => item.ModelDocumentId.HasValue).Select(item => item.ModelDocumentId!.Value).ToHashSet();
        var ambiguous = pairs.Where(item => !item.ModelDocumentId.HasValue).SelectMany(item => item.ReferencedModelIds).ToHashSet();
        return pairs.Where(item => item.ModelDocumentId.HasValue).SelectMany(item => item.ReferencedModelIds)
            .Where(id => !primary.Contains(id) && !ambiguous.Contains(id)).ToHashSet();
    }

    private static IEnumerable<DocumentReferenceNode> Nodes(DocumentReferenceNode node)
    {
        if (node.Role == ReferenceRole.ExternalFileDependency) yield break;
        yield return node;
        foreach (var child in node.Children) foreach (var descendant in Nodes(child)) yield return descendant;
    }
}
