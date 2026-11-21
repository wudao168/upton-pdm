using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        DocumentVersionDto latest = null) => ReadSavedNativePropertiesCore(node, sourceHash, latest, requireExactPath: false);

    private IReadOnlyDictionary<string, string> ReadSavedNativePropertiesCore(CadTreeNode node, string sourceHash,
        DocumentVersionDto latest, bool requireExactPath)
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
                MemoryCopy(node)?.SourceHash ?? sourceHash ?? ComputeRegistrationFileHash(node), requireExactPath);
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

    private async Task<IReadOnlyDictionary<string, string>> ReadBatchArchivePreflightPropertiesAsync(CadTreeNode node,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards,
        IReadOnlyDictionary<string, IReadOnlyList<string>> referenceConfigurations, DocumentVersionDto latest,
        string sourceHash, CancellationToken token)
    {
        try
        {
            return ReadArchivePreflightPropertiesCore(node, nativePropertyCards, referenceConfigurations, latest, sourceHash);
        }
        catch (ArchiveSavedPropertyPathConflictException conflict) when (MemoryCopy(node) == null
            && node.Status != CadReferenceStatus.Virtual
            && (CanArchiveSavedModelBytes(node, false) || CanArchiveSavedDrawingBytes(node, false, sourceHash)))
        {
            ArchiveSession.Current?.ReportFile(0, 0, node.FileName, "发现不同路径的同名图档，正在独立会话中读取所选文件的属性…");
            try
            {
                var properties = await ArchiveNativeProcessWorkspace.ReadSavedPropertiesAsync(application, node.FullPath,
                    (int)node.Kind, SavedPropertyConfigurations(node).ToArray(), sourceHash, null, token);
                if (!HasReadCustomProperties(properties, node))
                    throw new InvalidOperationException("独立会话未返回完整的图档属性。");
                node.ReferenceWarnings.RemoveAll(value => value.StartsWith(DrawingPropertyIndexWarning, StringComparison.Ordinal));
                return properties;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                LogDiagnostic("独立读取所选图档的属性：" + node.FullPath, error);
                throw new InvalidOperationException(conflict.Message + " 独立读取未完成：" + ArchivePreflightError(error), error);
            }
        }
    }

    // Called by the archive worker after it verifies the isolated native process.
    public static IReadOnlyDictionary<string, string> ReadNativeArchiveSavedProperties(ISldWorks app, string source,
        int documentKind, string[] configurations, string sourceHash)
    {
        var kind = (CadDocumentKind)documentKind;
        var expectedExtension = kind switch
        {
            CadDocumentKind.Assembly => ".SLDASM",
            CadDocumentKind.Part => ".SLDPRT",
            CadDocumentKind.Drawing => ".SLDDRW",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source) || expectedExtension == null
            || !string.Equals(Path.GetExtension(source), expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("独立读取的图档路径或类型无效，尚未上传。");

        var path = Path.GetFullPath(source);
        CheckInUploadHashes.VerifySource(path, sourceHash, CancellationToken.None);
        if (app.GetDocumentCount() != 0)
            throw new InvalidOperationException("独立读取会话已有其他图档，无法保证读取所选路径，尚未上传。");
        if (!app.SetCurrentWorkingDirectory(Path.GetDirectoryName(path)))
            throw new InvalidOperationException("无法设置独立读取会话的引用查找目录，尚未上传。");

        var node = new CadTreeNode { FullPath = path, FileName = Path.GetFileName(path), Kind = kind };
        var addin = new PdmAddin { application = app };
        addin.archiveCurrentInstances = new Dictionary<string, CadTreeNode[]>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = (configurations ?? Array.Empty<string>()).Select(configuration => new CadTreeNode { Configuration = configuration }).ToArray()
        };
        var properties = addin.ReadSavedNativePropertiesCore(node, sourceHash, null, requireExactPath: true);
        CheckInUploadHashes.VerifySource(path, sourceHash, CancellationToken.None);
        return properties;
    }

    private bool ReadAvailableSavedPropertyIndex(CadTreeNode node, IDictionary<string, string> properties, string sourceHash,
        bool requireExactPath)
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
            else if (!requireExactPath) document = FindArchiveReadDocument(node);
            if (document == null && !requireExactPath)
            {
                // An archive's document index can precede a native load event. Never
                // mistake an existing user window for a temporary window we own.
                document = application.GetOpenDocumentByName(node.FullPath) as IModelDoc2;
                if (document != null && !PathsEqual(document.GetPathName(), node.FullPath))
                {
                    if (node.Kind != CadDocumentKind.Part) ValidateSavedPropertyOpenPath(node, document);
                    ValidateArchiveReadAlias(node, document);
                }
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
                    ValidateSavedPropertyOpenPath(node, document, errors);
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
            if (error is ArchiveSavedPropertyPathConflictException) throw;
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

    private static void ValidateSavedPropertyOpenPath(CadTreeNode node, IModelDoc2 document, int errors = 0)
    {
        var actual = document?.GetPathName();
        if (PathsEqual(actual, node.FullPath)) return;
        if ((errors & (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen) != 0
            || !string.IsNullOrWhiteSpace(actual) && string.Equals(Path.GetFileName(actual), node.FileName, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveSavedPropertyPathConflictException(node.FullPath, actual);
        throw new InvalidOperationException(document == null
            ? "无法只读加载图档的自定义属性。"
            : "SolidWorks 返回了不同路径的图档，已保留其窗口，尚未上传。");
    }

    private sealed class ArchiveSavedPropertyPathConflictException : InvalidOperationException
    {
        internal ArchiveSavedPropertyPathConflictException(string expected, string actual)
            : base("当前会话存在不同路径的同名图档。所选文件：" + expected
                + (string.IsNullOrWhiteSpace(actual) ? "。" : "；SolidWorks 返回的文件：" + actual + "。"))
        {
        }
    }

    private sealed class ArchiveUnsavedPropertySourceException : InvalidOperationException
    {
        internal ArchiveUnsavedPropertySourceException()
            : base("当前内存内容与提交文件尚未核对，未用内存属性代替已保存文件属性。") { }
    }

    private bool CanArchiveSavedDrawingBytes(CadTreeNode node, bool hasUnsavedChanges, string localHash) =>
        node.Kind == CadDocumentKind.Drawing && !hasUnsavedChanges
        && node.Role != CadReferenceRole.ExternalFileDependency && node.HistoricalRepair == null
        && ((!node.RequiresModelUpdate && !preloadedOwnedDrawings.Contains(node.FullPath))
            || SavedArchiveSourceMatches(node, localHash));

    private static bool CanArchiveSavedModelBytes(CadTreeNode node, bool hasUnsavedChanges) =>
        (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
        && !hasUnsavedChanges && !node.RequiresModelUpdate && node.Status != CadReferenceStatus.Virtual
        && node.Role != CadReferenceRole.ExternalFileDependency && node.HistoricalRepair == null;
}
