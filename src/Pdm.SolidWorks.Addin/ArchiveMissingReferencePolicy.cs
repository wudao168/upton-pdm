using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

// A reference warning never supplies bytes, a document identity or a version.
internal static class ArchiveMissingReferencePolicy
{
    internal const string WarningPrefix = "存档引用提示：";
    internal static bool BlockMissingReferences { get; set; }

    internal static IEnumerable<CadTreeNode> Walk(CadTreeNode root)
    {
        var visited = new HashSet<CadTreeNode>();
        var pending = new Stack<CadTreeNode>();
        if (root != null) pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node)) continue;
            yield return node;
            foreach (var child in node.Children.Concat(node.ReferencedModels)) pending.Push(child);
        }
    }

    internal static void ReportEarly(CadTreeNode root)
    {
        var missing = Walk(root).Where(node => node.Status == CadReferenceStatus.Missing)
            .GroupBy(node => node.FullPath ?? node.FileName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (missing.Length == 0) return;
        ArchiveSession.Current?.SetStage("发现" + missing.Length + "项引用路径待核对；"
            + (BlockMissingReferences ? "已启用缺失引用阻止选项，确认范围后优先检查。" : "默认仅提示，将按勾选范围存档。"));
        foreach (var node in missing)
            ArchiveSession.Current?.ReportMissingReference(node.FullPath, node.FileName);
    }

    internal static void Prepare(CadTreeNode context, IReadOnlyList<CadTreeNode> selected)
    {
        var paths = new HashSet<string>(selected.Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var node in Walk(context)) node.ConfirmedArchivePaths = paths;
        var embedded = new HashSet<CadTreeNode>(NativeArchiveManifest.EmbeddedVirtualNodes(context, false));
        foreach (var owner in selected)
        {
            if (!File.Exists(owner.FullPath))
                throw new IOException("已勾选图档的本地文件不存在或不可访问，尚未上传：" + owner.FileName + "；路径：" + owner.FullPath);
            owner.IsOutsideNativeFileScope = false;
            foreach (var node in Walk(owner))
            {
                if (node == owner || paths.Contains(node.FullPath) || embedded.Contains(node) || node.Status == CadReferenceStatus.Virtual || node.IsStoredImport) continue;
                if (!string.IsNullOrWhiteSpace(node.FullPath) && !File.Exists(node.FullPath))
                    TryOmitMissing(owner, node.FullPath, node);
                else if (!node.DocumentId.HasValue && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath))
                    OmitUnselected(owner, node);
            }
        }
    }

    internal static bool TryOmitMissing(CadTreeNode owner, string path, CadTreeNode reference = null)
    {
        if (owner?.ConfirmedArchivePaths == null || owner.ConfirmedArchivePaths.Contains(path) || File.Exists(path)) return false;
        if (BlockMissingReferences)
            throw new ArchiveWarningBlockedException("管理员已启用“引用缺失时阻止存档”：" + owner.FileName + " → " + Path.GetFileName(path)
                + "；路径：" + path + "。尚未上传；请联系管理员核对策略。");
        Record(owner, path, "引用文件缺失，未勾选，本次不上传");
        foreach (var node in Walk(owner).Where(node => node != owner && string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            node.IsOutsideNativeFileScope = true;
        if (reference != null) reference.IsOutsideNativeFileScope = true;
        return true;
    }

    internal static void OmitUnselected(CadTreeNode owner, CadTreeNode reference)
    {
        if (owner?.ConfirmedArchivePaths == null || owner.ConfirmedArchivePaths.Contains(reference.FullPath)) return;
        ArchiveWarningGate.Check("OutsideVault", owner.FileName + " → " + reference.FileName + "：引用未纳入本次勾选；请调整提交范围或联系管理员。");
        reference.IsOutsideNativeFileScope = true;
        foreach (var node in Walk(owner).Where(node => node != owner && string.Equals(node.FullPath, reference.FullPath, StringComparison.OrdinalIgnoreCase)))
            node.IsOutsideNativeFileScope = true;
        Record(owner, reference.FullPath, "引用未纳入本次勾选，未上传其内容或修改");
    }

    private static void Record(CadTreeNode owner, string path, string reason)
    {
        var prefix = WarningPrefix + path + "；";
        owner.StructureIndexPending = true;
        if (owner.ReferenceWarnings.Any(message => message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return;
        var message = prefix + reason + "；原有关联保留，结构待补全";
        owner.ReferenceWarnings.Add(message);
        PluginDiagnosticLog.Record("引用提示", owner.FileName + "：" + message);
        ArchiveSession.Current?.SetStage(owner.FileName + "：" + message);
    }
}
