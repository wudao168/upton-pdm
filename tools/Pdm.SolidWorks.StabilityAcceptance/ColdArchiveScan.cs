using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
    private static object[] ColdScanNodeEvidence(object root) => Flatten(root).Select(node => (object)new
    {
        path = Get(node, "FullPath"), fileName = Get(node, "FileName"), kind = Get(node, "Kind").ToString(),
        documentId = Get(node, "DocumentId"), modified = Get(node, "IsModifiedInSolidWorks"),
        outsideNativeFileScope = Get(node, "IsOutsideNativeFileScope"), status = Get(node, "Status").ToString()
    }).ToArray();

    private static void NormalizeColdArchiveScan(Assembly plugin, object root)
    {
        Report["coldScanBeforeNormalization"] = ColdScanNodeEvidence(root);
        var aliases = (IReadOnlyDictionary<string, string>)plugin.GetType("Upton.Pdm.SolidWorks.ArchiveFileAliases", true)
            .GetMethod("Normalize", Members).Invoke(null, new[] { root });
        Report["coldScanAliases"] = aliases.Select(pair => new { source = pair.Key, canonical = pair.Value }).ToArray();
        Report["coldScanRedirectedAliases"] = aliases.Count(pair => !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase));
        Report["coldScanAfterNormalization"] = ColdScanNodeEvidence(root);
        var directory = Path.GetFullPath((string)Report["copies"]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var drawings = Flatten(root).Where(node => Get(node, "Kind").ToString() == "Drawing")
            .Select(node => (string)Get(node, "FullPath")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var outside = drawings.Where(path => !Path.GetFullPath(path).StartsWith(directory, StringComparison.OrdinalIgnoreCase)).ToArray();
        Report["coldScanDrawingPathsOutsideCurrentDirectory"] = outside;
        Report["coldScanProductionNormalizationApplied"] = true;
        Report["coldOpenTestBoundary"] = "API取回字节的独立只读OpenDoc6冷打开及生产扫描身份/归一化校验；不等同于客户端按钮完整Prepare流程。";
        if (outside.Length != 0) throw new InvalidOperationException("冷打开扫描混入当前取回目录之外的工程图。");
    }
}
