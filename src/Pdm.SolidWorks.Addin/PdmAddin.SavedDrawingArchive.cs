using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private const string DrawingPropertyIndexWarning = "属性索引待补齐：";
    private const string CustomPropertiesReadMarker = "UPLM.CustomPropertiesRead";
    private const string CustomPropertyConfigurationsMarker = "UPLM.CustomPropertyConfigurations";

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
        var inspected = node.ArchiveInspectedProperties;
        if (!string.IsNullOrWhiteSpace(sourceHash) && HasReadCustomProperties(inspected, node)
            && string.Equals(node.ArchiveInspectedPropertiesSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var property in inspected) properties[property.Key] = property.Value;
        }
        else if (!HasReadCustomProperties(latest?.PropertySnapshot, node)
            || latest.ReferenceSnapshot?.ReferenceWarnings?.Any(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal)) == true
            || !VersionMatchesLocalFile(latest, node.FullPath, sourceHash ?? ComputeRegistrationFileHash(node)))
            ReadAvailableSavedPropertyIndex(node, properties,
                MemoryCopy(node)?.SourceHash ?? sourceHash ?? ComputeRegistrationFileHash(node));
        node.ReferenceWarnings.RemoveAll(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal));
        properties["FileName"] = node.FileName;
        properties["Extension"] = Path.GetExtension(node.FullPath);
        properties["LastWriteTimeUtc"] = new FileInfo(node.FullPath).LastWriteTimeUtc.ToString("o");
        return properties;
    }

    private bool HasReadCustomProperties(IReadOnlyDictionary<string, string> properties, CadTreeNode node)
    {
        if (properties == null || !properties.TryGetValue(CustomPropertiesReadMarker, out var value) || value != "1"
            || !properties.TryGetValue(CustomPropertyConfigurationsMarker, out var stored)) return false;
        try
        {
            var configurations = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string[]>(stored);
            return configurations != null && SavedPropertyConfigurations(node).All(configuration =>
                configurations.Contains(configuration, StringComparer.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
    }

    private HashSet<string> SavedPropertyConfigurations(CadTreeNode node)
    {
        var configurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node.Kind == CadDocumentKind.Drawing) return configurations;
        if (!string.IsNullOrWhiteSpace(node.Configuration)) configurations.Add(node.Configuration);
        if (archiveCurrentInstances != null && archiveCurrentInstances.TryGetValue(node.FullPath, out var instances))
            configurations.UnionWith(instances.Select(instance => instance.Configuration).Where(value => !string.IsNullOrWhiteSpace(value)));
        return configurations;
    }

    private bool ReadAvailableSavedPropertyIndex(CadTreeNode node, IDictionary<string, string> properties, string sourceHash)
    {
        IModelDoc2 document = null;
        var opened = false;
        var source = new FileInfo(node.FullPath);
        var length = source.Length;
        var lastWrite = source.LastWriteTimeUtc;
        try
        {
            var memory = MemoryCopy(node);
            if (memory != null)
            {
                ValidateCurrentMemoryCopy(node);
                document = memory.Model;
            }
            else document = FindArchiveReadDocument(node);
            if (document == null)
            {
                // An archive's document index can precede a native load event. Never
                // mistake an existing user window for a temporary window we own.
                document = application.GetOpenDocumentByName(node.FullPath) as IModelDoc2;
                if (document != null && !PathsEqual(document.GetPathName(), node.FullPath)) ValidateArchiveReadAlias(node, document);
            }
            if (document == null)
            {
                // Use the existing authorized CAD process for encrypted/shared files.
                // Open only this missing metadata source, never save it or resolve its tree.
                var type = ToSolidWorksDocumentType(node.Kind);
                var visible = application.GetDocumentVisible(type);
                var options = swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly;
                if (node.Kind == CadDocumentKind.Assembly)
                    options |= swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight | swOpenDocOptions_e.swOpenDocOptions_LoadLightweight;
                if (node.Kind == CadDocumentKind.Drawing) options |= swOpenDocOptions_e.swOpenDocOptions_OpenDetailingMode;
                using (ArchiveReadContext.Measure(node, "只读加载自定义属性"))
                try
                {
                    application.DocumentVisible(false, type);
                    var errors = 0; var warnings = 0;
                    document = application.OpenDoc6(node.FullPath, type, (int)options, string.Empty, ref errors, ref warnings);
                    PluginDiagnosticLog.Record("图档属性读取", "只读打开；图档=" + node.FullPath + "；错误码=" + errors + "；警告码=" + warnings);
                    if (document == null) throw new InvalidOperationException("无法只读加载图档的自定义属性。");
                    if (!PathsEqual(document.GetPathName(), node.FullPath))
                        throw new InvalidOperationException("SolidWorks返回了其他路径的同名图档，未读取或关闭该窗口。");
                    opened = true;
                    archiveBatchReads?.Invalidate();
                    if (!document.IsOpenedReadOnly()) throw new InvalidOperationException("属性读取窗口未处于只读状态。");
                }
                finally { application.DocumentVisible(visible, type); }
            }
            // OpenDoc6 can set the save flag while loading a saved assembly. Only
            // this hidden, newly opened, exact-path read-only window is ours.
            var dirtyOnOpen = opened && document.GetSaveFlag();
            var openStamp = dirtyOnOpen ? document.GetUpdateStamp() : 0;
            if (memory == null && !opened && document.GetSaveFlag())
                throw new ArchiveUnsavedPropertySourceException();
            var configurations = SavedPropertyConfigurations(node);
            if (node.Kind != CadDocumentKind.Drawing)
            {
                if (configurations.Count == 0 && document.ConfigurationManager?.ActiveConfiguration?.Name is string active && !string.IsNullOrWhiteSpace(active))
                    configurations.Add(active);
            }
            var available = configurations.Count > 0 ? document.GetConfigurationNames() as Array : null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var configuration in new[] { string.Empty }.Concat(configurations))
            {
                var scope = string.IsNullOrEmpty(configuration) ? "全局" : "配置:" + configuration;
                // A configuration warning must not prevent reading actual global
                // properties. The earlier configuration check owns missing names.
                if (!string.IsNullOrEmpty(configuration)
                    && available?.Length > 0
                    && !available.Cast<object>().Any(value => string.Equals(Convert.ToString(value), configuration, StringComparison.OrdinalIgnoreCase))) continue;
                ReadPropertyManager(document.Extension.CustomPropertyManager[configuration], scope, values, useCached: true, requireComplete: true);
            }
            if (memory != null) ValidateCurrentMemoryCopy(node);
            else if (opened && (dirtyOnOpen != document.GetSaveFlag()
                || dirtyOnOpen && (document.GetUpdateStamp() != openStamp || !document.IsOpenedReadOnly())))
                throw new InvalidOperationException("临时只读图档在读取属性期间发生变化，请重新检查。");
            else if (!opened && document.GetSaveFlag()) throw new ArchiveUnsavedPropertySourceException();
            source.Refresh();
            if (dirtyOnOpen || !source.Exists || source.Length != length || source.LastWriteTimeUtc != lastWrite)
            {
                // Metadata may change as a network/encryption filter opens a native
                // document. Verify the submitted content, not just that metadata.
                var afterLength = source.Exists ? (long?)source.Length : null;
                var afterWrite = source.Exists ? source.LastWriteTimeUtc.ToString("o") : "无法读取";
                var verifiedHash = ComputeFileHash(source.FullName);
                var matches = string.Equals(sourceHash, verifiedHash, StringComparison.OrdinalIgnoreCase);
                PluginDiagnosticLog.Record("属性读取源文件核验", "图档=" + source.FullName
                    + "；读取前大小=" + length + "；读取后大小=" + afterLength
                    + "；读取前时间=" + lastWrite.ToString("o") + "；读取后时间=" + afterWrite
                    + "；内容指纹一致=" + matches);
                if (!matches) throw new InvalidOperationException("读取自定义属性期间源文件内容发生变化。");
            }
            foreach (var key in properties.Keys.Where(key => (key.StartsWith("全局/", StringComparison.OrdinalIgnoreCase)
                || configurations.Any(configuration => key.StartsWith("配置:" + configuration + "/", StringComparison.OrdinalIgnoreCase)))
                && !values.ContainsKey(key)).ToArray()) properties[key] = string.Empty;
            foreach (var value in values) properties[value.Key] = value.Value;
            properties[CustomPropertiesReadMarker] = "1";
            properties[CustomPropertyConfigurationsMarker] = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(configurations.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
            PluginDiagnosticLog.Record("图档属性读取", "已完整读取；图档=" + node.FullPath + "；属性数=" + values.Count + "；只读加载=" + opened);
            return true;
        }
        catch (Exception error)
        {
            LogDiagnostic("自定义属性读取失败，未提交原生文件：" + node.FullPath, error);
            if (error is ArchiveUnsavedPropertySourceException)
                throw new InvalidOperationException("图档存在尚未固定到提交副本的内存修改：" + node.FileName
                    + "。本批尚未上传，当前窗口和修改已保留。请结束当前编辑后重新检查；只读文件无需强行保存回原目录，"
                    + "存档须先生成包含当前修改的提交副本。", error);
            var contentChanged = error is InvalidOperationException && error.Message == "读取自定义属性期间源文件内容发生变化。";
            throw new InvalidOperationException((contentChanged ? "读取属性期间源文件内容发生变化，请等待文件稳定后重试：" : "未能完整读取图档自定义属性：") + node.FileName
                + "。本批尚未上传，原文件、窗口及修改已保留。请检查该文件能否在当前SolidWorks中正常读取属性；"
                + "使用加密软件时请核对其SolidWorks读取授权。技术原因已记入诊断日志。", error);
        }
        finally
        {
            if (opened && document != null && !CloseArchiveReadDocument(document))
                throw new InvalidOperationException("自定义属性的临时读取窗口未能关闭，已停止存档并保留原文件：" + node.FileName);
        }
    }

    private sealed class ArchiveUnsavedPropertySourceException : InvalidOperationException
    {
        internal ArchiveUnsavedPropertySourceException()
            : base("当前内存内容与提交文件尚未核对，未用内存属性代替已保存文件属性。") { }
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
