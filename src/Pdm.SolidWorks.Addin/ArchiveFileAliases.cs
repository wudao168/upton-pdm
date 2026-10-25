using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveFileAliases
{
    internal static IReadOnlyDictionary<string, string> Normalize(CadTreeNode root)
    {
        var nodes = new List<CadTreeNode>();
        var visited = new HashSet<CadTreeNode>();
        void Visit(CadTreeNode node)
        {
            if (node == null || !visited.Add(node) || node.IsOutsideNativeFileScope) return;
            nodes.Add(node);
            foreach (var child in node.Children) Visit(child);
            foreach (var model in node.ReferencedModels) Visit(model);
        }
        Visit(root);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in nodes.Where(node => node.Status != CadReferenceStatus.Virtual && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length < 2) continue;
            if (group.Any(node => node.IsModifiedInSolidWorks) || group.Where(node => node.DocumentId.HasValue)
                .Select(node => node.DocumentId.Value).Distinct().Count() > 1 || paths.Any(path => !File.Exists(path))) continue;
            var hashes = paths.Select(path => ArchiveFileFingerprint.Read(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hashes.Length != 1) continue;
            var canonical = group.OrderByDescending(node => node.DocumentId.HasValue).First().FullPath;
            foreach (var path in paths) aliases[path] = canonical;
            foreach (var node in group) node.FullPath = canonical;
            PluginDiagnosticLog.Record("同名文件归并", group.Key + "：已核对相同内容，使用一个存档副本；原文件均保留。");
        }
        return aliases;
    }
}
