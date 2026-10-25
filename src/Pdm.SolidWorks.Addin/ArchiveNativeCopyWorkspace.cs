using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// GUID-prefixed copies can coexist with the engineer's open documents. Only
// these copies are activated, rebuilt, saved or closed by this operation.
internal sealed class ArchiveNativeCopyWorkspace : IDisposable
{
    internal sealed class FileCopy
    {
        internal string Source;
        internal string Path;
        internal string SourceSha256;
        internal bool ContentFrozen;
    }

    private readonly string directory;
    private readonly List<FileCopy> files = new List<FileCopy>();
    internal IReadOnlyList<FileCopy> Files => files;

    internal ArchiveNativeCopyWorkspace(ISldWorks app, IEnumerable<string> inventory,
        IReadOnlyDictionary<string, string> requestedCopies, string stagingRoot = null, Action<string> report = null)
    {
        directory = System.IO.Path.Combine(stagingRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UPTON-PDM", "native-isolation"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var prefix = "uplm_" + Guid.NewGuid().ToString("N") + "_";
        var originalDocuments = (app.GetDocuments() as Array)?.OfType<IModelDoc2>().ToArray() ?? new IModelDoc2[0];
        var before = originalDocuments.Select(model => model.GetPathName()).ToArray();
        var originalTitles = new HashSet<string>(originalDocuments.Select(model => model.GetTitle()), StringComparer.OrdinalIgnoreCase);
        var previous = app.ActiveDoc as IModelDoc2;
        var previousPath = previous?.GetPathName();
        var previousTitle = previous?.GetTitle();
        var visibility = new Dictionary<int, bool>();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in inventory.Concat(requestedCopies.Keys).Where(File.Exists).GroupBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length > 1 && candidates.Select(path => ArchiveFileFingerprint.Read(path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new IOException("隔离归档范围存在内容不同的同名文件，未自动选择来源：" + group.Key);
            sources.Add(group.Key, candidates[0]);
        }
        var pending = new Queue<string>(requestedCopies.Keys);
        var byName = new Dictionary<string, FileCopy>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var copiesPrepared = false;
        var cleanupFailures = new List<string>();
        try
        {
            while (pending.Count > 0)
            {
                var source = pending.Dequeue();
                var name = System.IO.Path.GetFileName(source);
                if (byName.ContainsKey(name)) continue;
                report?.Invoke(name);
                var input = requestedCopies.TryGetValue(source, out var prepared) ? prepared : source;
                if (!File.Exists(input)) throw new IOException("独立归档副本缺少源文件：" + name);
                var copy = new FileCopy { Source = source, Path = System.IO.Path.Combine(directory, prefix + name), SourceSha256 = ArchiveFileFingerprint.Read(source) };
                using (var reader = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new FileStream(copy.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) reader.CopyTo(writer);
                File.SetAttributes(copy.Path, FileAttributes.Normal);
                files.Add(copy); byName.Add(name, copy);
                var values = Native(input) ? app.GetDocumentDependencies2(input, false, false, false) as Array : null;
                if (values != null && values.Length % 2 != 0) throw new IOException("原生引用清单未完整读取：" + name);
                var references = values == null ? new string[0] : Enumerable.Range(0, values.Length / 2)
                    .SelectMany(index => (Convert.ToString(values.GetValue(index * 2 + 1)) ?? "").Split('|'))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                dependencies[copy.Path] = references;
                foreach (var reference in references)
                    if (!NativeArchiveManifest.IsInterconnectCachePath(reference)
                        && sources.TryGetValue(System.IO.Path.GetFileName(reference), out var dependency)) pending.Enqueue(dependency);
            }
            foreach (var copy in files.Where(file => Native(file.Path)))
                foreach (var reference in dependencies[copy.Path].Distinct(StringComparer.OrdinalIgnoreCase))
                    // STEP/import and parameter links are frozen from their saved
                    // content below. They are not native assembly/drawing references.
                    if (Native(reference) && byName.TryGetValue(System.IO.Path.GetFileName(reference), out var target)
                        && !app.ReplaceReferencedDocument(copy.Path, reference, target.Path))
                        throw ReferenceRelocationFailure(copy, reference, target.Path);

            IModelDoc2 Open(FileCopy copy)
            {
                var loaded = app.GetOpenDocumentByName(copy.Path) as IModelDoc2;
                if (loaded == null)
                {
                    var type = Type(copy.Path);
                    if (!visibility.ContainsKey(type)) visibility[type] = app.GetDocumentVisible(type);
                    app.DocumentVisible(false, type);
                    int error = 0, warning = 0;
                    loaded = app.OpenDoc6(copy.Path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning);
                    if (loaded == null || error != 0 || !string.Equals(loaded.GetPathName(), copy.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        PluginDiagnosticLog.Record("归档副本打开", "图档=" + copy.Source + "；错误码=" + error + "；警告码=" + warning);
                        throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source)
                            + "”无法完整打开，未开始上传，原窗口已保留。请核对图档兼容性及缺失引用；原生错误已记录在过程日志中。");
                    }
                }
                if (loaded is IAssemblyDoc assembly)
                {
                    assembly.ResolveAllLightWeightComponents(false);
                    if (!loaded.EditRebuild3()) throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source)
                        + "”的隔离装配副本未能完成重建，未开始上传，原窗口已保留。");
                    var storage = new ImportedComponentStorage(app);
                    foreach (var component in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                    {
                        var actual = (component.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? component.GetPathName();
                        if (component.IsVirtual || files.Any(file => string.Equals(actual, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                        if (NativeArchiveManifest.IsInterconnectCachePath(actual)
                            && files.Any(file => string.Equals(storage.FindOwner(component, loaded), file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                        PluginDiagnosticLog.Record("归档组件来源", "图档=" + copy.Source + "；实例=" + component.Name2
                            + "；实际文件=" + actual + "；导入来源=" + component.GetImportedPath()
                            + "；模型已加载=" + (component.GetModelDoc2() != null)
                            + "；原生导入特征=" + ImportedComponentStorage.IsNativeImportedComponent(component, loaded)
                            + "；直接父装配=" + (component.GetParent()?.GetModelDoc2() as IModelDoc2)?.GetPathName());
                        throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source)
                            + "”的组件“" + component.Name2 + "”尚未确认属于隔离副本，未开始上传，原窗口已保留。实际来源已记录在诊断日志中。");
                    }
                }
                if (loaded is IDrawingDoc drawing)
                {
                    var previousSheet = (drawing.GetCurrentSheet() as ISheet)?.GetName();
                    try
                    {
                        foreach (var sheet in (drawing.GetSheetNames() as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>())
                        {
                            if (!drawing.ActivateSheet(sheet)) throw new IOException("无法核对工程图图页：" + sheet);
                            for (var view = drawing.GetFirstView() as IView; view != null; view = view.GetNextView() as IView)
                            {
                                var reference = view.GetReferencedModelName();
                                if (string.IsNullOrWhiteSpace(reference)) continue;
                                var actual = (view.ReferencedDocument as IModelDoc2)?.GetPathName();
                                if (string.IsNullOrWhiteSpace(actual) || !files.Any(file => string.Equals(file.Path, actual, StringComparison.OrdinalIgnoreCase)))
                                    throw new InvalidOperationException("工程图“" + System.IO.Path.GetFileName(copy.Source) + "”的图页“" + sheet
                                        + "”缺少已核对的完整模型，未开始上传：" + System.IO.Path.GetFileName(reference));
                            }
                        }
                    }
                    finally
                    {
                        if (previousSheet != null && (drawing.GetCurrentSheet() as ISheet)?.GetName() != previousSheet)
                            drawing.ActivateSheet(previousSheet);
                    }
                }
                return loaded;
            }

            // Load requested assemblies first, so converted dependent parts retain
            // their native assembly context while the saved caches are rebuilt.
            foreach (var copy in files.Where(file => requestedCopies.ContainsKey(file.Source) && Type(file.Path) == 2))
                copy.ContentFrozen = NativeDesignDependencies.Read(Open(copy)).Count > 0;
            foreach (var copy in files.Where(file => Native(file.Path)).OrderBy(file => Type(file.Path) == 1 ? 0 : Type(file.Path) == 2 ? 1 : 2))
            {
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                var model = Open(copy);
                int activation = 0;
                app.ActivateDoc3(model.GetTitle(), false, 0, ref activation);
                if (activation != 0 || !string.Equals((app.ActiveDoc as IModelDoc2)?.GetPathName(), copy.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("无法安全切换到归档隔离副本。");
                try { copy.ContentFrozen |= model is IDrawingDoc
                    ? ArchiveResourceIsolation.Detach(model) : ArchiveReferenceIsolation.Detach(model); }
                catch (IOException error)
                { throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source) + "”不能安全固化，未开始上传，原图档和窗口已保留。" + error.Message, error); }
                if (model.GetSaveFlag() || copy.ContentFrozen)
                {
                    int error = 0, warning = 0;
                    if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_IncludeVirtualSubAsmComps), ref error, ref warning) || error != 0)
                        throw new IOException("独立归档副本保存失败：" + System.IO.Path.GetFileName(copy.Source));
                }
            }
            copiesPrepared = true;
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("归档隔离副本", error.ToString());
            throw;
        }
        finally
        {
            var closing = new List<Tuple<string, string>>();
            foreach (var model in (app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                try
                {
                    var path = model.GetPathName(); var title = model.GetTitle();
                    if (files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (originalTitles.Contains(title)) throw new IOException("隔离副本与原窗口同名，已保留原窗口，未继续关闭或上传。");
                        closing.Add(Tuple.Create(path, title));
                    }
                }
                catch (System.Runtime.InteropServices.COMException error) when (error.ErrorCode == unchecked((int)0x80010108))
                { PluginDiagnosticLog.Record("归档副本清理", "原生缓存对象已释放，未将其当作文档关闭。"); }
            // Release referring drawings/assemblies before their dependent parts.
            foreach (var item in closing.OrderBy(item => Type(item.Item1) == 3 ? 0 : Type(item.Item1) == 2 ? 1 : 2)
                .ThenBy(item => files.FindIndex(file => string.Equals(file.Path, item.Item1, StringComparison.OrdinalIgnoreCase))))
                try { if (app.GetOpenDocumentByName(item.Item1) != null) app.CloseDoc(item.Item2); }
                catch (System.Runtime.InteropServices.COMException error)
                {
                    cleanupFailures.Add(System.IO.Path.GetFileName(item.Item1));
                    PluginDiagnosticLog.Record("归档副本清理", "隔离副本关闭失败，保留原始存档失败原因：" + item.Item1 + "；" + error);
                }
            foreach (var pair in visibility) app.DocumentVisible(pair.Value, pair.Key);
            if (previousPath != null && app.GetOpenDocumentByName(previousPath) != null)
            {
                int error = 0;
                app.ActivateDoc3(previousTitle, false, 0, ref error);
            }
            if (!copiesPrepared) Dispose();
        }
        try
        {
        if (cleanupFailures.Count > 0)
            throw new InvalidOperationException("归档隔离副本关闭未完成，未开始上传。原文件已保留；请检查诊断日志中的副本清理记录。");
        foreach (var copy in files.Where(file => Native(file.Path)))
        {
            // Read the saved result: design links removed by solidification must
            // not be restored or sent to ReplaceReferencedDocument afterwards.
            var saved = app.GetDocumentDependencies2(copy.Path, false, false, false) as Array;
            if (saved != null && saved.Length % 2 != 0)
                throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source) + "”固化后的引用清单未完整读取，未开始上传。");
            var references = saved == null ? new string[0] : Enumerable.Range(0, saved.Length / 2)
                .SelectMany(index => (Convert.ToString(saved.GetValue(index * 2 + 1)) ?? "").Split('|')).ToArray();
            foreach (var target in files.Where(file => Native(file.Path)))
                if (references.Contains(target.Path, StringComparer.OrdinalIgnoreCase)
                    && !app.ReplaceReferencedDocument(copy.Path, target.Path, target.Source))
                    throw ReferenceRelocationFailure(copy, target.Path, target.Source);
        }
        var remaining = (app.GetDocuments() as Array)?.OfType<IModelDoc2>().Select(model => model.GetPathName()).ToArray() ?? new string[0];
        if (before.Any(path => !remaining.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw new IOException("原有文档的加载状态发生变化，未认定独立归档副本准备完成。");
        }
        catch { Dispose(); throw; }
    }

    private static InvalidOperationException ReferenceRelocationFailure(FileCopy owner, string source, string target)
    {
        PluginDiagnosticLog.Record("归档引用定位", "原生替换返回失败；图档=" + owner.Source + "；旧引用=" + source + "；目标=" + target);
        return new InvalidOperationException("图档“" + System.IO.Path.GetFileName(owner.Source) + "”的原生引用“"
            + System.IO.Path.GetFileName(source) + "”无法定位到归档副本，未开始上传。原文件和窗口已保留；请检查引用来源，完整路径已记录在诊断日志中。");
    }

    private static int Type(string path) => System.IO.Path.GetExtension(path).Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) ? 1
        : System.IO.Path.GetExtension(path).Equals(".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2
        : System.IO.Path.GetExtension(path).Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    private static bool Native(string path) => Type(path) != 0;
    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException error) { PluginDiagnosticLog.Record("归档副本清理", "临时副本暂未清理：" + directory + "；" + error); }
    }
}
