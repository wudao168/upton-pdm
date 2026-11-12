using System;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

// 查找已有工程图；保留已生成草稿的路径兼容，不创建或更新工程图。
internal static class RelatedDrawingPath
{
    internal static string ForSource(CadTreeNode node)
    {
        if (node == null || (node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly)
            || string.IsNullOrWhiteSpace(node.FullPath)) return string.Empty;
        var related = node.Children.FirstOrDefault(child => child.Kind == CadDocumentKind.Drawing
            && !string.IsNullOrWhiteSpace(child.FullPath) && File.Exists(child.FullPath));
        if (related != null) return related.FullPath;
        var adjacent = Path.ChangeExtension(node.FullPath, ".SLDDRW");
        if (File.Exists(adjacent) || !node.IsLatestReadOnlyPreview) return adjacent;
        return DrawingDraftPath.ForSource(node.FullPath, node.ProvenanceProjectId, node.DocumentId);
    }
}
