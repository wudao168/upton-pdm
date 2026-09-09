using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public static class ProjectReferenceDocumentCounts
{
    public sealed record Document(Guid Id, Guid ProjectId, string FileName, DocumentKind Kind);

    // Match the workspace tree: resolve unique filenames, remove repeated sibling
    // instances, count each document once, and include current drawing relations.
    public static (int All, int Model, int Drawing) Count(DocumentReferenceNode? root, Guid projectId,
        IReadOnlyList<Document> documents, IReadOnlyList<DocumentModelDrawingRelation> relations)
    {
        static string FileKey(string name) => name.Trim().Replace('\\', '/').Split('/')[^1].ToLowerInvariant();
        var byId = documents.ToDictionary(document => document.Id);
        var byFile = documents.GroupBy(document => FileKey(document.FileName))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id);
        var drawingsByModel = relations.Where(relation => byId.TryGetValue(relation.DrawingDocumentId, out var drawing)
                && drawing.Kind == DocumentKind.Drawing)
            .ToLookup(relation => relation.ModelDocumentId, relation => relation.DrawingDocumentId);
        var kinds = new Dictionary<string, DocumentKind>(StringComparer.OrdinalIgnoreCase);
        void Visit(DocumentReferenceNode node)
        {
            var documentId = node.DocumentId;
            if (documentId is null && byFile.TryGetValue(FileKey(node.FileName), out var resolvedId)) documentId = resolvedId;
            var key = documentId?.ToString() ?? node.FileName.Trim().ToLowerInvariant();
            if (key.Length > 0) kinds.TryAdd(key, node.Kind);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in node.Children)
                if (string.IsNullOrWhiteSpace(child.InstancePath) || paths.Add(child.InstancePath.Trim())) Visit(child);
            if (documentId is Guid modelId && node.Kind != DocumentKind.Drawing)
                foreach (var drawingId in drawingsByModel[modelId]) kinds.TryAdd(drawingId.ToString(), DocumentKind.Drawing);
        }
        if (root is not null) Visit(root);
        foreach (var drawing in documents.Where(document => document.ProjectId == projectId && document.Kind == DocumentKind.Drawing))
            kinds.TryAdd(drawing.Id.ToString(), DocumentKind.Drawing);
        var drawingCount = kinds.Values.Count(kind => kind == DocumentKind.Drawing);
        return (kinds.Count, kinds.Count - drawingCount, drawingCount);
    }
}
