using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class ConfirmedArchiveInventory
{
    // This is the known file membership, not proof that every occurrence was indexed.
    // Saved references are still read/validated by the dependency stages before upload.
    internal static string[] Read(CadTreeNode root)
    {
        if (root?.ConfirmedArchivePaths == null || !root.ConfirmedArchivePaths.Contains(root.FullPath))
            throw new InvalidOperationException("尚未确认当前图档的存档范围，请先选择图档。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<CadTreeNode>();
        var pending = new Stack<CadTreeNode>(); pending.Push(root);
        while (pending.Count > 0)
        {
            ArchiveSession.Current?.Token.ThrowIfCancellationRequested();
            var node = pending.Pop();
            if (!visited.Add(node) || node.IsOutsideNativeFileScope) continue;
            if (node.Kind == CadDocumentKind.Drawing && node != root && !root.ConfirmedArchivePaths.Contains(node.FullPath)) continue;
            foreach (var child in node.Children.Concat(node.ReferencedModels)) pending.Push(child);
            if (node.StructureIndexPending || !string.IsNullOrWhiteSpace(node.ArchiveReadError)
                || SuppressedAssemblyArchive.IsUnexpanded(node) && node.SuppressedArchiveVersion == null)
                root.StructureIndexPending = true;
            if (node.Status == CadReferenceStatus.Virtual || node.IsStoredImport
                || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
                || NativeArchiveManifest.IsInterconnectCachePath(node.FullPath)) continue;
            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                if (!ArchiveMissingReferencePolicy.TryOmitMissing(root, node.FullPath, node))
                    throw new IOException("已勾选图档的文件不存在或不可访问，尚未上传：" + node.FileName);
                continue;
            }
            if (node.Kind != CadDocumentKind.Drawing || node == root || root.ConfirmedArchivePaths.Contains(node.FullPath))
                paths.Add(node.FullPath);
        }
        if (!paths.Contains(root.FullPath)) throw new IOException("已勾选根图档没有可提交的本地文件：" + root.FileName);
        PluginDiagnosticLog.Record("确认文件清单", root.FileName + "：已按确认范围收集" + paths.Count
            + "项已知文件；未调用完整Pack and Go；未读取完整的结构保留待补全标记。");
        return new[] { root.FullPath }.Concat(paths.Where(path => !string.Equals(path, root.FullPath, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
