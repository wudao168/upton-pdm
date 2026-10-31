using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private const string DrawingPropertyIndexWarning = "属性索引待补齐：";

    private IReadOnlyDictionary<string, string> ReadSavedDrawingProperties(CadTreeNode node, string sourceHash = null,
        DocumentVersionDto latest = null)
        => ReadSavedNativeProperties(node, sourceHash, latest);

    private IReadOnlyDictionary<string, string> ReadSavedNativeProperties(CadTreeNode node, string sourceHash = null,
        DocumentVersionDto latest = null)
    {
        if (!string.IsNullOrWhiteSpace(sourceHash) && node.ArchiveInspectedProperties != null
            && string.Equals(node.ArchiveInspectedPropertiesSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
            return node.ArchiveInspectedProperties;
        if (latest == null)
            lock (metadataCacheSync)
                if (documentVersionsCache.TryGetValue(node.DocumentId ?? Guid.Empty, out var cached))
                    latest = cached.Versions.FirstOrDefault();
        var properties = new Dictionary<string, string>(latest?.PropertySnapshot ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var pending = latest == null || latest.PropertySnapshot == null
            || !VersionMatchesLocalFile(latest, node.FullPath, sourceHash ?? ComputeRegistrationFileHash(node))
            || latest.ReferenceSnapshot?.ReferenceWarnings?.Any(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal)) == true;
        node.ReferenceWarnings.RemoveAll(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal));
        if (pending)
        {
            var warning = DrawingPropertyIndexWarning + "已完整保留已保存的" + (node.Kind == CadDocumentKind.Drawing ? "工程图" : "3D图档") + "原文件，未为提取属性完整打开或重存图纸；"
                + (latest == null ? "PLM属性索引尚未完整提取。" : "已保留原有属性索引，尚未确认本地属性是否变化。");
            node.ReferenceWarnings.Add(warning);
            ArchiveWarningGate.Check("PropertyIndexPending", node.FileName + "：" + warning);
            archiveWarnings.Add(node.FileName + "：" + warning);
            PluginDiagnosticLog.Record("图档属性索引", node.FullPath + "；" + warning);
        }
        properties["FileName"] = node.FileName;
        properties["Extension"] = Path.GetExtension(node.FullPath);
        properties["LastWriteTimeUtc"] = new FileInfo(node.FullPath).LastWriteTimeUtc.ToString("o");
        return properties;
    }

    private bool CanArchiveSavedDrawingBytes(CadTreeNode node, bool hasUnsavedChanges) =>
        node.Kind == CadDocumentKind.Drawing && !hasUnsavedChanges && !node.RequiresModelUpdate
        && node.Role != CadReferenceRole.ExternalFileDependency && node.HistoricalRepair == null
        && !preloadedOwnedDrawings.Contains(node.FullPath);

    private static bool CanArchiveSavedModelBytes(CadTreeNode node, bool hasUnsavedChanges) =>
        (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
        && !hasUnsavedChanges && !node.RequiresModelUpdate && node.Status != CadReferenceStatus.Virtual
        && node.Role != CadReferenceRole.ExternalFileDependency && node.HistoricalRepair == null;
}
