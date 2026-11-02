using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Built after the final receipt. Preserve the distinct current-tree, drawing
    // reference and cached-preview matching rules of FindMatchingDocumentInstances.
    private sealed class ArchiveCommittedInstanceIndex
    {
        private readonly bool hasTree;
        private readonly Dictionary<string, CadTreeNode[]> primaryPaths, referencePaths, cachedPaths, operationPaths;
        private readonly Dictionary<Guid, CadTreeNode[]> primaryIds;
        internal ArchiveCommittedInstanceIndex(CadTreeNode root, IEnumerable<CadTreeNode> cached, IEnumerable<CadTreeNode> operations)
        {
            hasTree = root != null;
            var primary = EnumerateCadNodes(root).ToArray();
            primaryPaths = Paths(primary);
            primaryIds = primary.Where(node => node.DocumentId.HasValue).GroupBy(node => node.DocumentId.Value).ToDictionary(group => group.Key, group => group.ToArray());
            referencePaths = Paths(EnumerateArchiveNodes(root));
            cachedPaths = Paths(cached.Where(node => !node.IsHistoricalPreview));
            operationPaths = Paths(operations);
        }
        private static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { return path; }
        }
        private static Dictionary<string, CadTreeNode[]> Paths(IEnumerable<CadTreeNode> nodes) => nodes.Where(node => node != null && Key(node.FullPath) != null)
            .GroupBy(node => Key(node.FullPath), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        private static CadTreeNode[] At(Dictionary<string, CadTreeNode[]> index, string path) => Key(path) is string key && index.TryGetValue(key, out var found) ? found : Array.Empty<CadTreeNode>();
        internal IReadOnlyList<CadTreeNode> Matches(CadTreeNode source, Guid id)
        {
            if (source == null) return Array.Empty<CadTreeNode>();
            if (!hasTree) return new[] { source };
            var byId = primaryIds.TryGetValue(id, out var found) ? found : Array.Empty<CadTreeNode>();
            return byId.Concat(At(primaryPaths, source.FullPath)).Where(node => node.DocumentId == id || PathsEqual(node.FullPath, source.FullPath))
                .Concat(At(referencePaths, source.FullPath).Where(node => node.DocumentId == id))
                .Concat(At(cachedPaths, source.FullPath).Where(node => !node.IsHistoricalPreview))
                .Concat(new[] { source }).Distinct().ToArray();
        }
        internal IReadOnlyList<CadTreeNode> Operations(CadTreeNode source) => At(operationPaths, source.FullPath);
    }
}
