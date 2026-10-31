using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Native import features identify their saved parent; Pack and Go is a legacy fallback.
// Never classify by the IC~~ directory or imported-file name alone.
internal sealed class ImportedComponentStorage
{
    [ThreadStatic] private static int inventoryReadDepth;
    internal static bool IsReadingInventory => inventoryReadDepth > 0;
    private readonly ISldWorks application;
    private readonly Dictionary<string, string[]> inventories = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    // One storage reader belongs to one scan. Repeated descendants of the same
    // imported instance need the same native feature proof, not another feature lookup.
    private readonly Dictionary<string, bool> nativeFeatureProofs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    internal ImportedComponentStorage(ISldWorks application) => this.application = application ?? throw new ArgumentNullException(nameof(application));

    internal bool AllowInventory { get; set; } = true;

    internal string FindOwner(IComponent2 component, IModelDoc2 root)
    {
        var cache = (component.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? component.GetPathName();
        if (!NativeArchiveManifest.IsInterconnectCachePath(cache) || component.GetModelDoc2() == null) return null;
        var imported = component.GetImportedPath();
        if (string.IsNullOrWhiteSpace(imported)) return null;
        IModelDoc2 owner = null;
        string ownerPath = null;
        var importedRoot = component;
        for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
        {
            var model = parent.GetModelDoc2() as IModelDoc2;
            var path = model?.GetPathName() ?? parent.GetPathName();
            if (NativeArchiveManifest.IsInterconnectCachePath(path)) { importedRoot = parent; continue; }
            if (parent.IsVirtual || string.IsNullOrWhiteSpace(path) || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(path)) continue;
            owner = model;
            ownerPath = path;
            break;
        }
        // A lightweight native parent can have a persistent path without exposing
        // its ModelDoc2. Do not walk past that storage boundary to another assembly.
        if (ownerPath == null) { owner = root; ownerPath = owner?.GetPathName(); }
        if (!IsStableOwner(ownerPath) || owner != null && owner.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return null;
        // Native imported features carry their generated documents in the saved parent.
        // Pack and Go may still list those IC caches as associative references after a
        // reopen; their presence is not evidence that the native parent lacks the data.
        var featureKey = ownerPath + "\0" + importedRoot.Name2;
        if (!nativeFeatureProofs.TryGetValue(featureKey, out var nativeFeature))
        {
            nativeFeature = IsNativeImportedComponent(importedRoot, root);
            // Inventory reads can activate another document, making a previously
            // unavailable feature readable. Do not cache that negative strict result.
            if (nativeFeature || !AllowInventory) nativeFeatureProofs[featureKey] = nativeFeature;
        }
        if (nativeFeature) return ownerPath;
        if (!AllowInventory) return null;
        var rootPath = root?.GetPathName();
        if (!IsStableOwner(rootPath)) return null;
        if (!inventories.TryGetValue(rootPath, out var files))
        {
            // The whole-tree inventory defines which native containers travel together.
            // Their actual parent chain identifies the nearest persistent owner. Do not
            // activate every nested owner: that can invalidate loaded component contexts.
            files = ReadInventory(root);
            inventories.Add(rootPath, files);
        }
        var owns = InventoryOwns(ownerPath, cache, imported, files);
        if (!owns) PluginDiagnosticLog.Record("导入组件归属", "未确认存储归属；组件=" + cache + "；候选装配=" + ownerPath
            + "；打包根图档=" + files.FirstOrDefault() + "；清单项数=" + files.Length
            + "；缓存仍在清单内=" + files.Any(entry => (entry ?? string.Empty).Split('|').Contains(cache, StringComparer.OrdinalIgnoreCase)));
        return owns ? ownerPath : null;
    }

    internal static bool IsNativeImportedComponent(IComponent2 component, IModelDoc2 root)
    {
        var parent = component.GetParent();
        var host = parent?.GetModelDoc2() as IModelDoc2;
        var name = (component.Name2 ?? string.Empty).Split('/').Last();
        var feature = parent != null && host == null
            ? parent.FeatureByName(name) as IFeature
            : ((host ?? root) as IAssemblyDoc)?.FeatureByName(name) as IFeature;
        // This is the native instance identifier within its actual parent model, not
        // a filename search. GetSpecificFeature2 can be null for inactive parent models.
        return feature?.Is3DInterconnectFeature == true && string.Equals(feature.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    internal string[] ReadInventory(IModelDoc2 owner)
        => ReadNativeInventory(owner, false);

    internal string[] ReadNativeInventory(IModelDoc2 owner, bool includeDrawings)
    {
        var previous = application.ActiveDoc as IModelDoc2;
        var ownerPath = owner.GetPathName();
        IPackAndGo pack = null;
        inventoryReadDepth++;
        try
        {
            // GetPackAndGo can return the active document's inventory even when called
            // through another loaded model's Extension. Activate without rebuilding/saving.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    ReadInventoryStep(ownerPath, "激活需要核对的图档", () => { Activate(owner); return true; });
                    pack = ReadInventoryStep(ownerPath, "获取SolidWorks原生打包清单", () => owner.Extension.GetPackAndGo() as IPackAndGo);
                    if (pack == null) throw new InvalidOperationException("无法读取所属装配的原生打包清单：" + ownerPath);
                    ReadInventoryStep(ownerPath, includeDrawings ? "检索关联工程图（等待SolidWorks返回）" : "设置原生模型清单范围",
                        () => { pack.IncludeDrawings = includeDrawings; return true; });
                    ReadInventoryStep(ownerPath, "核对原生清单中的压缩组件", () => { pack.IncludeSuppressed = true; return true; });
                    var files = ReadInventoryStep(ownerPath, "枚举原生清单中的实际文件", () =>
                    {
                        if (!pack.GetDocumentNames(out var values) || !(values is Array names))
                            throw new InvalidOperationException("所属装配未返回可核对的原生打包清单：" + ownerPath);
                        return names.Cast<object>().Select(Convert.ToString).ToArray();
                    });
                    if (files.Length == 0 || !string.Equals(files[0], ownerPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原生打包清单与所属装配不一致，已保留本地文件。所属装配：" + ownerPath
                            + "；清单根图档：" + files.FirstOrDefault());
                    PluginDiagnosticLog.Record("原生文件清单", "所属图档=" + ownerPath + "；原活动图档=" + previous?.GetPathName()
                        + "；已核对清单根图档；包含工程图=" + includeDrawings + "；文件数=" + files.Length + "；未执行保存或重建。");
                    return files;
                }
                catch (Exception error) when (attempt == 0 && (error is COMException || error is InvalidOperationException))
                {
                    PluginDiagnosticLog.Record("原生文件清单", "重新激活目标图档后再读取一次：" + error);
                    if (pack != null && Marshal.IsComObject(pack)) Marshal.ReleaseComObject(pack);
                    pack = null;
                }
            }
        }
        finally
        {
            try
            {
                if (pack != null && Marshal.IsComObject(pack)) Marshal.ReleaseComObject(pack);
                if (previous != null && !string.Equals((application.ActiveDoc as IModelDoc2)?.GetPathName(), previous.GetPathName(), StringComparison.OrdinalIgnoreCase))
                    try { Activate(previous); }
                    catch (Exception error) { PluginDiagnosticLog.Record("图档恢复", "原生清单读取后恢复原活动文档失败，清单仍有效：" + error); }
            }
            finally { inventoryReadDepth--; }
        }
    }

    private static T ReadInventoryStep<T>(string ownerPath, string message, Func<T> read)
    {
        var session = ArchiveSession.Current;
        session?.Token.ThrowIfCancellationRequested();
        session?.ReportFile(0, 0, Path.GetFileName(ownerPath), "原生清单读取：" + message);
        PluginDiagnosticLog.Record("原生清单步骤", "开始：" + message + "；图档=" + ownerPath);
        var timer = Stopwatch.StartNew();
        var returned = false;
        try
        {
            var result = read();
            returned = true;
            session?.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            PluginDiagnosticLog.Record("原生清单步骤", (returned ? "接口已返回：" : "读取中断：") + message
                + "；耗时=" + timer.ElapsedMilliseconds + "毫秒；图档=" + ownerPath);
        }
    }

    private void Activate(IModelDoc2 document)
    {
        var errors = 0;
        var activated = application.ActivateDoc3(document.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
        if (activated == null || errors != 0 || !string.Equals(activated.GetPathName(), document.GetPathName(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("无法切换到需要核对的图档，已停止存档并保留本地文件：" + document.GetPathName() + "；状态码：" + errors);
    }

    internal static bool IsStableOwner(string path) => !string.IsNullOrWhiteSpace(path)
        && !NativeArchiveManifest.IsInterconnectCachePath(path) && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(path)
        && Path.GetExtension(path).Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path) && new FileInfo(path).Length > 0;

    internal static bool InventoryOwns(string owner, string cache, string imported, IEnumerable<string> files)
    {
        if (!IsStableOwner(owner) || !NativeArchiveManifest.IsInterconnectCachePath(cache) || string.IsNullOrWhiteSpace(imported)) return false;
        var entries = files.ToArray();
        if (entries.Length == 0 || !IsStableOwner(entries[0])) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) return false;
            foreach (var path in entry.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
                paths.Add(Path.GetFullPath(path));
            }
        }
        // An original STEP may also be listed for associative updates. It is outside
        // the managed tree and its presence must not change native storage ownership.
        return paths.Contains(Path.GetFullPath(owner)) && !paths.Contains(Path.GetFullPath(cache));
    }
}
