using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Missing design sources can be archived as notices. Actual model occurrences,
    // drawing models and unverified embedded storage still require their existing checks.
    private static bool CanArchiveMissingSource(CadTreeNode owner, string path, IEnumerable<string> designSources = null)
    {
        if (owner == null || owner.Kind == CadDocumentKind.Drawing
            || NativeArchiveManifest.IsInterconnectCachePath(path)
            || IsSolidWorksTemporaryVirtualComponentPath(path)) return false;
        if (EnumerateArchiveNodes(owner).Any(node => node != owner
            && node.Role != CadReferenceRole.ExternalFileDependency
            && (PathsEqual(node.FullPath, path) || PathsEqual(node.SourceReferencePath, path)))) return false;
        if (owner.Kind == CadDocumentKind.Part) return true;
        return !IsPdmNativeFile(path) || owner.EmbeddedDesignDependencies.Any(source => PathsEqual(source, path))
            || (designSources ?? Array.Empty<string>()).Any(source => PathsEqual(source, path));
    }

    private static void RecordReferenceWarning(CadTreeNode owner, string source, string reason,
        IEnumerable<string> searched = null)
    {
        var message = "来源：" + source + "；" + reason + "；原有关联保留";
        var locations = (searched ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (locations.Length > 0) message += "；已查找：" + string.Join("、", locations);
        // Later validation repeats the same dependency, without discarding search evidence.
        if (owner.ReferenceWarnings.Any(value => value.StartsWith("来源：" + source + "；", StringComparison.OrdinalIgnoreCase))) return;
        owner.ReferenceWarnings.Add(message);
        PluginDiagnosticLog.Record("引用提示", owner.FileName + "：" + message + "。继续存档，提示随版本保存。");
    }

    private bool RecordMissingSourceWarning(CadTreeNode owner, string path, IEnumerable<string> searched = null,
        IEnumerable<string> designSources = null)
    {
        if (!CanArchiveMissingSource(owner, path, designSources)) return false;
        // Unregistered missing sources have no file to upload or version to bind.
        // Keep their original CAD link and persist the notice instead of a phantom file node.
        owner.Children.RemoveAll(node => node.Role == CadReferenceRole.ExternalFileDependency && !node.DocumentId.HasValue
            && (PathsEqual(node.FullPath, path) || PathsEqual(node.SourceReferencePath, path)) && !File.Exists(node.FullPath));
        RecordReferenceWarning(owner, path, "未找到原始来源文件，未计为新增图档", searched);
        return true;
    }
}
