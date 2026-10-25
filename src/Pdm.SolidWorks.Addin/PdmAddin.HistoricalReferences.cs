using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static void ValidateHistoricalReferenceScope(CadTreeNode tree, IReadOnlyList<BatchOperationItem> planned)
    {
        if (tree == null || planned == null || planned.Count == 0) return;
        var required = new HashSet<string>(planned.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Select(node => node.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        var pending = BuildBatchOperationItems(tree)
            .Where(item => required.Contains(item.Node.FullPath) && BatchOperationDialog.ExistingArchivedReferenceOwner(item) != null)
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (pending.Length == 0) return;

        throw new InvalidOperationException("本次实际存档范围需要以下历史遗漏引用，但它们尚未完成独立核对和补全；本次未登记或上传这些文件。\r\n"
            + string.Join("\r\n", pending.Select(item => item.Node.FileName + "（所属装配："
                + BatchOperationDialog.ExistingArchivedReferenceOwner(item).FileName + "）"))
            + "\r\n只修改其他零件时，可仅提交该零件及配套工程图；需要提交上述装配时，请先核对并补全这些历史引用。");
    }
}
