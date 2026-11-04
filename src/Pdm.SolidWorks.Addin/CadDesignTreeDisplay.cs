using System;

namespace Upton.Pdm.SolidWorks;

// Display membership is separate from the dependency closure required for check-in.
internal static class CadDesignTreeDisplay
{
    internal static bool IsStructureNode(CadTreeNode node) => node != null
        && node.Role != CadReferenceRole.ExternalFileDependency
        && !node.IsContextReference && !node.ReferenceTypeUnverified;

    internal static string Identity(CadTreeNode node)
    {
        var document = node.DocumentId.HasValue ? "document:" + node.DocumentId.Value.ToString("N")
            : !string.IsNullOrWhiteSpace(node.FullPath) ? "file:" + node.FullPath.Replace('/', '\\') + "|" + node.FileName
            : "node:" + node.NodeId.ToString("N");
        return document + "\n" + (node.Configuration ?? string.Empty);
    }
}
