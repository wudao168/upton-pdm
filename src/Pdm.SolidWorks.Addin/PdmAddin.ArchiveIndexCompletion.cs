using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static bool ArchivedIndexPending(DocumentReferenceNodeDto node) => node != null
        && (node.StructureIndexPending || (node.Children ?? new List<DocumentReferenceNodeDto>()).Any(ArchivedIndexPending));

    private static bool CurrentArchiveIndexPending(CadTreeNode root)
    {
        var pending = new Stack<CadTreeNode>();
        var visited = new HashSet<CadTreeNode>();
        if (root != null) pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node == null || node.IsOutsideNativeFileScope || !visited.Add(node)) continue;
            if (node.StructureIndexPending || node.HasUnverifiedDrawingIndex) return true;
            // Reusing an unopened suppressed assembly does not re-read its index.
            // Pending state in the reused snapshot must survive that reuse.
            if (ArchivedIndexPending(node.SuppressedArchiveVersion?.ReferenceSnapshot)
                && SuppressedAssemblyArchive.CanReuse(node)) return true;
            foreach (var child in node.Children.Where(child => child.Kind != CadDocumentKind.Drawing)) pending.Push(child);
            if (node.Kind == CadDocumentKind.Drawing)
                foreach (var model in node.ReferencedModels) pending.Push(model);
        }
        return false;
    }

    private static bool ArchiveIndexWasCompleted(DocumentReferenceNodeDto archived, CadTreeNode current) =>
        archived != null && current != null && archived.DocumentId == current.DocumentId
        && (current.Kind != CadDocumentKind.Drawing || current.DrawingViewsRead)
        && ArchivedIndexPending(archived) && !CurrentArchiveIndexPending(current);
}
