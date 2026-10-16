using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal static class SuppressedAssemblyLocalStructure
{
    public static Task ExpandAsync(CadTreeNode root, Func<CadTreeNode, Task<CadTreeNode>> read, CancellationToken token)
    {
        return Visit(root, read, new HashSet<string>(StringComparer.OrdinalIgnoreCase), token);
    }

    private static async Task Visit(CadTreeNode node, Func<CadTreeNode, Task<CadTreeNode>> read, HashSet<string> ancestors, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node == null || node.Kind == CadDocumentKind.Drawing) return;
        var key = node.Kind == CadDocumentKind.Assembly && !string.IsNullOrWhiteSpace(node.FullPath)
            ? Path.GetFullPath(node.FullPath) + "|" + node.Configuration : null;
        if (key != null && !ancestors.Add(key))
            throw new InvalidOperationException("发现循环装配体引用，已停止存档：" + node.FileName
                + "；实例：" + node.InstancePath + "；配置：" + node.Configuration + "；路径：" + node.FullPath
                + "。请核对该实例与上层装配的引用关系后重试。");
        try
        {
            if (!string.IsNullOrWhiteSpace(node.SuppressedLocalStructureSha256) && !CanUse(node))
            {
                node.Children.RemoveAll(child => child.Kind != CadDocumentKind.Drawing);
                node.SuppressedLocalStructureSha256 = null;
            }
            if (SuppressedAssemblyArchive.IsUnexpanded(node) && !SuppressedAssemblyArchive.CanReuse(node) && !CanUse(node))
            {
                var before = Hash(node.FullPath);
                var structure = await read(node);
                if (structure == null || structure.Kind != CadDocumentKind.Assembly
                    || !string.Equals(Path.GetFullPath(structure.FullPath), Path.GetFullPath(node.FullPath), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(structure.Configuration, node.Configuration, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(before, Hash(node.FullPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("压缩子装配体的文件或配置在读取期间发生变化，请保存并刷新后重试：" + node.FileName);
                foreach (var child in structure.Children.Where(child => child.Kind != CadDocumentKind.Drawing))
                {
                    Rebase(child, structure.InstancePath, node.InstancePath);
                    node.Children.Add(child);
                }
                node.SuppressedLocalStructureSha256 = before;
            }
            foreach (var child in node.Children) await Visit(child, read, ancestors, token);
        }
        finally { if (key != null) ancestors.Remove(key); }
    }

    public static bool CanUse(CadTreeNode node) => !string.IsNullOrWhiteSpace(node?.SuppressedLocalStructureSha256)
        && !node.IsModifiedInSolidWorks && File.Exists(node.FullPath)
        && string.Equals(node.SuppressedLocalStructureSha256, Hash(node.FullPath), StringComparison.OrdinalIgnoreCase);

    private static void Rebase(CadTreeNode node, string from, string to)
    {
        if (!string.IsNullOrEmpty(from) && node.InstancePath.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            node.InstancePath = to + node.InstancePath.Substring(from.Length);
        node.ComponentSelectionName = string.Empty;
        foreach (var child in node.Children) Rebase(child, from, to);
    }

    private static string Hash(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("压缩子装配体文件不存在，无法读取内部结构：" + path);
        return ArchiveFileFingerprint.Read(path);
    }
}
