using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.swconst;

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
        if (latest == null)
            lock (metadataCacheSync)
                if (documentVersionsCache.TryGetValue(node.DocumentId ?? Guid.Empty, out var cached))
                    latest = cached.Versions.FirstOrDefault();
        var properties = new Dictionary<string, string>(latest?.PropertySnapshot ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourceHash) && node.ArchiveInspectedProperties != null
            && string.Equals(node.ArchiveInspectedPropertiesSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
            foreach (var property in node.ArchiveInspectedProperties) properties[property.Key] = property.Value;
        var readComplete = ReadAvailableSavedPropertyIndex(node, properties);
        var pending = !readComplete && (latest == null || latest.PropertySnapshot == null
            || !VersionMatchesLocalFile(latest, node.FullPath, sourceHash ?? ComputeRegistrationFileHash(node))
            || latest.ReferenceSnapshot?.ReferenceWarnings?.Any(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal)) == true);
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

    private bool ReadAvailableSavedPropertyIndex(CadTreeNode node, IDictionary<string, string> properties)
    {
        // Reading cached managers on an exact already-loaded document needs no
        // document open, configuration activation, rebuild, save or write access.
        try
        {
            var document = FindLoadedDocument(node.FullPath);
            if (document == null || !PathsEqual(document.GetPathName(), node.FullPath)) return false;
            var memory = MemoryCopy(node);
            if (memory != null) ValidateCurrentMemoryCopy(node);
            else if (document.GetSaveFlag()) return false;
            var configurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node.Kind != CadDocumentKind.Drawing)
            {
                if (!string.IsNullOrWhiteSpace(node.Configuration)) configurations.Add(node.Configuration);
                if (archiveCurrentInstances != null && archiveCurrentInstances.TryGetValue(node.FullPath, out var instances))
                    configurations.UnionWith(instances.Select(instance => instance.Configuration).Where(value => !string.IsNullOrWhiteSpace(value)));
                if (configurations.Count == 0 && document.ConfigurationManager?.ActiveConfiguration?.Name is string active && !string.IsNullOrWhiteSpace(active))
                    configurations.Add(active);
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var completedScopes = new List<string>();
            var complete = true;
            foreach (var configuration in new[] { string.Empty }.Concat(configurations))
            {
                var scope = string.IsNullOrEmpty(configuration) ? "全局" : "配置:" + configuration;
                var scopeComplete = true;
                try
                {
                    var manager = document.Extension.CustomPropertyManager[configuration];
                    if (manager == null) { complete = false; continue; }
                    var names = (manager.GetNames() as Array)?.Cast<object>().OfType<string>().ToArray() ?? Array.Empty<string>();
                    if (manager.Count != names.Length) scopeComplete = false;
                    foreach (var name in names)
                        try
                        {
                            var result = manager.Get6(name, true, out var raw, out var resolved, out var wasResolved, out var linked);
                            if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent) { scopeComplete = false; continue; }
                            values[scope + "/" + name] = wasResolved || !string.IsNullOrEmpty(resolved) ? resolved : raw;
                        }
                        catch { scopeComplete = false; }
                }
                catch { scopeComplete = false; }
                if (scopeComplete) completedScopes.Add(scope + "/");
                complete &= scopeComplete;
            }
            if (memory != null) ValidateCurrentMemoryCopy(node);
            else if (document.GetSaveFlag()) return false;
            // Only a completely read scope proves that a previously indexed field
            // was removed. Failed reads preserve the earlier values.
            foreach (var key in properties.Keys.Where(key => completedScopes.Any(scope => key.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                && !values.ContainsKey(key)).ToArray()) properties[key] = string.Empty;
            foreach (var value in values) properties[value.Key] = value.Value;
            return complete;
        }
        catch (Exception error)
        {
            LogDiagnostic("缓存属性暂未完整读取，保留原生文件及已有索引：" + node.FileName, error);
            return false;
        }
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
