using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

internal static class WorkspaceDocumentRead
{
    internal static async Task<object> ReadAsync(Guid projectId, IReadOnlySet<Guid> visibleFolders, IReadOnlySet<Guid> readableProjects,
        string actor, UserRole role, IPdmRepository repository, CancellationToken ct)
    {
        var currentDocuments = (await repository.ListDocumentsAsync(projectId, ct))
            .Where(item => item.FolderId.HasValue && visibleFolders.Contains(item.FolderId.Value)).ToArray();
        var referenceRoot = await repository.GetReferenceTreeAsync(projectId, ct);
        var ids = currentDocuments.Select(item => item.Id).ToHashSet();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (referenceRoot is not null)
        {
            var pending = new Stack<DocumentReferenceNode>();
            pending.Push(referenceRoot);
            while (pending.TryPop(out var node))
            {
                if (node.DocumentId.HasValue) ids.Add(node.DocumentId.Value);
                if (!string.IsNullOrWhiteSpace(node.FileName)) names.Add(node.FileName.Trim().Replace('\\', '/').Split('/')[^1]);
                foreach (var child in node.Children) pending.Push(child);
            }
        }
        var indexed = await repository.ListWorkspaceDocumentsAsync(projectId, ids, names, ct);
        // 同名候选必须保留以判断歧义，但不能因此扩展它们各自的全部工程图关系。
        ids.UnionWith(indexed.GroupBy(item => item.FileName.Trim().Replace('\\', '/').Split('/')[^1], StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1 && names.Contains(group.Key)).Select(group => group.Single().Id));
        var relations = await repository.ListWorkspaceDocumentRelationsAsync(projectId, ids, ct);
        var indexedIds = indexed.Select(item => item.Id).ToHashSet();
        var missing = relations.SelectMany(item => new[] { item.ModelDocumentId, item.DrawingDocumentId }).Where(id => !indexedIds.Contains(id)).Distinct().ToArray();
        if (missing.Length > 0) indexed = indexed.Concat(await repository.ListWorkspaceDocumentsAsync(projectId, missing, [], ct)).DistinctBy(item => item.Id).ToArray();
        var readable = readableProjects.ToDictionary(id => id, _ => true);
        var documents = new List<PdmDocument>();
        foreach (var document in indexed)
        {
            if (document.FolderId is not Guid folderId || !visibleFolders.Contains(folderId)) continue;
            if (!readable.TryGetValue(document.ProjectId, out var allowed))
                readable[document.ProjectId] = allowed = await repository.HasProjectReadAccessAsync(document.ProjectId, actor, role, ct);
            if (allowed) documents.Add(document);
        }
        var locked = await repository.ListActiveDrawingReviewDocumentIdsAsync(projectId, ct);
        currentDocuments = currentDocuments.Select(item => item with { DrawingReviewLocked = locked.Contains(item.Id) }).ToArray();
        var currentById = currentDocuments.ToDictionary(item => item.Id);
        var libraryDocuments = documents.Select(item => currentById.GetValueOrDefault(item.Id) ?? item).ToArray();
        var visibleIds = libraryDocuments.Select(item => item.Id).ToHashSet();
        return new
        {
            currentDocuments,
            libraryDocuments,
            documentRelations = relations.Where(item => visibleIds.Contains(item.ModelDocumentId) && visibleIds.Contains(item.DrawingDocumentId)).ToArray(),
            referenceRoot,
        };
    }
}
