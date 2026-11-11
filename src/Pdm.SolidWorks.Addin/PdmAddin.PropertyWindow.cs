using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private Func<string, bool> CreatePropertyDrawingScope(CadTreeNode root)
    {
        if (root.Kind == CadDocumentKind.Drawing || string.IsNullOrWhiteSpace(root.FullPath)) return null;
        var directory = Path.GetDirectoryName(root.FullPath);
        var localModels = new HashSet<string>(EnumerateCadNodes(root)
            .Where(node => node.Kind != CadDocumentKind.Drawing && !node.IsOutsideNativeFileScope
                && !string.IsNullOrWhiteSpace(node.FullPath) && IsPathWithinDirectory(node.FullPath, directory))
            .Select(node => Path.GetFullPath(node.FullPath)), StringComparer.OrdinalIgnoreCase);

        return drawingPath =>
        {
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath)) return false;
            if (IsPathWithinDirectory(drawingPath, directory)) return true;

            // Search paths and copied PLM identities are discovery hints. For an
            // outside drawing, require a saved reference to a model in this copy.
            try { return ReadRenameReferences(drawingPath, false).Any(localModels.Contains); }
            catch (Exception error)
            {
                LogDiagnostic("核对属性窗口外部工程图引用：" + drawingPath, error);
                return false;
            }
        };
    }

    private static bool PropertyPathsMatch(string left, string right) => PathsEqual(left, right)
        || (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right));

    private Func<string, bool> EnsurePropertyWindowContext(IModelDoc2 document, CadTreeNode root, Guid? projectId,
        IEnumerable<BatchPropertyEditItem> items)
    {
        if (Volatile.Read(ref workspaceOperationInProgress) != 0 || IsSolidWorksCommandActive())
            throw new InvalidOperationException("请先完成当前 SolidWorks 或 UPLM 操作，再继续处理属性。");
        if (!ReferenceEquals(application.ActiveDoc, document) || !PropertyPathsMatch(document.GetPathName(), root.FullPath)
            || currentTree == null || currentTree.IsReadOnlyPreview || !PropertyPathsMatch(currentTree.FullPath, root.FullPath)
            || (currentProjectId ?? GetExplicitProjectId(root.FullPath)) != projectId)
            throw new InvalidOperationException("当前工作图档或项目已切换。请切回打开属性窗口时的图档和项目，或关闭后重新打开属性。");
        if (root.Kind != CadDocumentKind.Drawing && !string.IsNullOrWhiteSpace(root.Configuration)
            && !string.Equals(document.ConfigurationManager?.ActiveConfiguration?.Name, root.Configuration, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前配置已切换，请切回原配置或重新打开属性窗口。");

        EnsureNativeTreeForOperation(null);
        var includeDrawing = CreatePropertyDrawingScope(currentTree);
        var paths = new HashSet<string>(BuildBatchOperationItems(currentTree).Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        if (items.Select(item => item.OperationItem.Node).Any(node => !paths.Contains(node.FullPath)
            || (node.Kind == CadDocumentKind.Drawing && includeDrawing != null && !includeDrawing(node.FullPath))))
            throw new InvalidOperationException("部分图档已不在当前结构中，请关闭后重新打开属性窗口。");
        return includeDrawing;
    }
}
