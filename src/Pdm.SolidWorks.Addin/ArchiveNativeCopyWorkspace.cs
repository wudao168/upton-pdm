using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Only owned copies are activated, rebuilt, saved or closed. The standalone
// reader preserves filenames: renaming a drawing can stall its native save.
internal sealed class ArchiveNativeCopyWorkspace : IDisposable
{
    internal sealed class FileCopy
    {
        internal string Source;
        internal string Path;
        internal string SourceSha256;
        internal bool ContentFrozen;
        internal string[] Dependencies = new string[0];
    }

    private readonly string directory;
    private readonly List<FileCopy> files = new List<FileCopy>();
    internal IReadOnlyList<FileCopy> Files => files;

    internal static void ValidateLoadedImports(ISldWorks app, IEnumerable<BatchOperationItem> items)
    {
        var assemblies = items.Where(item => item.Node.Kind == CadDocumentKind.Assembly && item.Node.RequiresModelUpdate)
            .Select(item => item.Node.FullPath);
        foreach (var path in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!(app.GetOpenDocumentByName(path) is IAssemblyDoc assembly)) continue;
            foreach (var component in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
            {
                var cached = component.GetPathName();
                if (!NativeArchiveManifest.IsInterconnectCachePath(cached)
                    || component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentFullyResolved || component.GetModelDoc2() != null
                    || File.Exists(cached) || string.IsNullOrWhiteSpace(component.GetImportedPath())) continue;
                PluginDiagnosticLog.Record("存档导入预检", "装配=" + path + "；实例=" + component.Name2
                    + "；缺失缓存=" + cached + "；原导入来源=" + component.GetImportedPath());
                throw new InvalidOperationException("装配“" + System.IO.Path.GetFileName(path) + "”的导入组件“" + component.Name2
                    + "”尚未载入内部模型，保存的导入缓存也已不存在，无法确认存档内容完整。已在准备上传副本前停止，未上传任何文件。"
                    + "请先恢复该组件的原生模型并确认结构；原导入来源和缺失缓存路径已记录在诊断日志中。");
            }
        }
    }

    internal ArchiveNativeCopyWorkspace(ISldWorks app, IEnumerable<string> inventory,
        IReadOnlyDictionary<string, string> requestedCopies, string stagingRoot = null, Action<string> report = null)
        : this(app, inventory, requestedCopies, stagingRoot, report, null) { }

    internal ArchiveNativeCopyWorkspace(ISldWorks app, IEnumerable<string> inventory,
        IReadOnlyDictionary<string, string> requestedCopies, string stagingRoot, Action<string> report, Func<ISldWorks> freshReader)
    {
        directory = System.IO.Path.Combine(stagingRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UPTON-PDM", "native-isolation"), Guid.NewGuid().ToString("N"));
        var originalDocuments = (app.GetDocuments() as Array)?.OfType<IModelDoc2>().ToArray() ?? new IModelDoc2[0];
        if (originalDocuments.Length != 0)
            throw new InvalidOperationException("独立归档进程已载入其他图档，未继续操作，原窗口已保留。");
        Directory.CreateDirectory(directory);
        var inputDirectory = System.IO.Path.Combine(directory, "input");
        var packageDirectory = System.IO.Path.Combine(directory, "package");
        var baselineDirectory = System.IO.Path.Combine(directory, "baseline");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(baselineDirectory);
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
                throw new InvalidOperationException("隔离归档范围存在内容不同的同名文件，未自动选择来源：" + group.Key);
            sources.Add(group.Key, candidates[0]);
        }
        var pending = new Queue<string>(requestedCopies.Keys);
        var byName = new Dictionary<string, FileCopy>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var checkedEmbeddedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedIndependentLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var designTableBaselines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> inputMap = null;
        void CloseOwnedDocuments()
        {
            // Native child interfaces can retain pointers into their document.
            // Drain their finalizers while that document is still open; releasing
            // them after CloseDoc can dereference already-freed native state.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            PluginDiagnosticLog.Record("专用会话生命周期", "关闭前：控制=" + app.UserControl + "；后台控制=" + app.UserControlBackground + "；文档=" + app.GetDocumentCount());
            var closing = ((app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Select(model => new { Path = model.GetPathName(), Title = model.GetTitle(), Kind = model.GetType() })
                .OrderByDescending(model => model.Kind).ToArray();
            foreach (var model in closing)
                if (string.IsNullOrWhiteSpace(model.Path) || app.GetOpenDocumentByName(model.Path) != null)
                    app.CloseDoc(model.Title);
            if (app.GetDocumentCount() != 0)
                throw new IOException("专用归档会话的暂存图档尚未完全关闭，未继续处理；原工作窗口已保留。");
            System.Windows.Forms.Application.DoEvents();
        }
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
                if (!File.Exists(input)) throw new InvalidOperationException("独立归档副本缺少源文件，未开始上传：" + name);
                var copy = new FileCopy { Source = source, Path = System.IO.Path.Combine(inputDirectory, name), SourceSha256 = ArchiveFileFingerprint.Read(source) };
                using (var reader = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new FileStream(copy.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) reader.CopyTo(writer);
                File.SetAttributes(copy.Path, FileAttributes.Normal);
                files.Add(copy); byName.Add(name, copy);
                var values = Native(input) ? app.GetDocumentDependencies2(input, false, false, false) as Array : null;
                if (values != null && values.Length % 2 != 0) throw new InvalidOperationException("原生引用清单未完整读取，未开始上传：" + name);
                var references = values == null ? new string[0] : Enumerable.Range(0, values.Length / 2)
                    .SelectMany(index => (Convert.ToString(values.GetValue(index * 2 + 1)) ?? "").Split('|'))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                dependencies[copy.Path] = references;
                foreach (var reference in references)
                {
                    if (NativeArchiveManifest.IsInterconnectCachePath(reference)
                        || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(reference)) continue;
                    var dependencyName = System.IO.Path.GetFileName(reference);
                    if (!sources.TryGetValue(dependencyName, out var dependency))
                    {
                        var peer = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source), dependencyName);
                        dependency = File.Exists(reference) ? reference : File.Exists(peer) ? peer : null;
                        if (dependency != null) sources.Add(dependencyName, dependency);
                    }
                    if (dependency != null) pending.Enqueue(dependency);
                }
            }
            foreach (var copy in files.Where(file => Native(file.Path)))
                foreach (var reference in dependencies[copy.Path].Distinct(StringComparer.OrdinalIgnoreCase))
                    // Native parent/child and derived-part pointers are redirected
                    // on owned inputs before any same-name document is opened.
                    if (Native(reference) && byName.TryGetValue(System.IO.Path.GetFileName(reference), out var target)
                        && !app.ReplaceReferencedDocument(copy.Path, reference, target.Path))
                        throw ReferenceRelocationFailure(copy, reference, target.Path);
            foreach (var copy in files.Where(file => Native(file.Path)))
                copy.Dependencies = dependencies[copy.Path].Where(reference => !NativeArchiveManifest.IsInterconnectCachePath(reference)
                    && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(reference))
                    .Select(reference => byName.TryGetValue(System.IO.Path.GetFileName(reference), out var dependency) ? dependency.Source : null)
                    .Where(source => source != null && !string.Equals(source, copy.Source, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            void ValidateAssembly(FileCopy copy, IModelDoc2 loaded, bool deferSuppressed)
            {
                if (Type(copy.Path) != 2 || !(loaded is IAssemblyDoc assembly)) return;
                assembly.ResolveAllLightWeightComponents(false);
                var storage = new ImportedComponentStorage(app);
                foreach (var component in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                {
                    if (deferSuppressed && component.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    var actual = (component.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? component.GetPathName();
                    if (component.IsVirtual || files.Any(file => string.Equals(actual, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                    if (NativeArchiveManifest.IsInterconnectCachePath(actual)
                        && files.Any(file => string.Equals(storage.FindOwner(component, loaded), file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                    PluginDiagnosticLog.Record("归档组件来源", "图档=" + copy.Source + "；实例=" + component.Name2
                        + "；实际文件=" + actual + "；导入来源=" + component.GetImportedPath());
                    throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source)
                        + "”的组件“" + component.Name2 + "”尚未确认属于隔离副本，未开始上传。实际来源已记录在诊断日志中。");
                }
            }

            IModelDoc2 Open(FileCopy copy)
            {
                var loaded = app.GetOpenDocumentByName(copy.Path) as IModelDoc2;
                if (loaded == null && Type(copy.Path) == 1)
                    foreach (var source in copy.Dependencies)
                    {
                        var dependency = files.FirstOrDefault(file => string.Equals(file.Source, source, StringComparison.OrdinalIgnoreCase));
                        if (dependency == null || !Native(dependency.Path) || Type(dependency.Path) == 3
                            || app.GetOpenDocumentByName(dependency.Path) != null) continue;
                        var dependencyType = Type(dependency.Path);
                        if (!visibility.ContainsKey(dependencyType)) visibility[dependencyType] = app.GetDocumentVisible(dependencyType);
                        app.DocumentVisible(false, dependencyType);
                        int dependencyError = 0, dependencyWarning = 0;
                        var parent = app.OpenDoc6(dependency.Path, dependencyType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref dependencyError, ref dependencyWarning);
                        if (parent == null || dependencyError != 0 || !string.Equals(parent.GetPathName(), dependency.Path, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("受控设计来源未能完整打开：" + System.IO.Path.GetFileName(dependency.Source));
                    }
                loaded = app.GetOpenDocumentByName(copy.Path) as IModelDoc2;
                if (loaded == null)
                {
                    var type = Type(copy.Path);
                    if (!visibility.ContainsKey(type)) visibility[type] = app.GetDocumentVisible(type);
                    app.DocumentVisible(false, type);
                    int error = 0, warning = 0;
                    PluginDiagnosticLog.Record("归档副本打开", "开始：" + copy.Source);
                    loaded = app.OpenDoc6(copy.Path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning);
                    PluginDiagnosticLog.Record("归档副本打开", "返回：" + copy.Source + "；错误码=" + error + "；警告码=" + warning);
                    if (loaded == null || error != 0 || !string.Equals(loaded.GetPathName(), copy.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        PluginDiagnosticLog.Record("归档副本打开", "图档=" + copy.Source + "；错误码=" + error + "；警告码=" + warning);
                        throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source)
                            + "”无法完整打开，未开始上传，原窗口已保留。请核对图档兼容性及缺失引用；原生错误已记录在过程日志中。");
                    }
                }
                int activation = 0;
                app.ActivateDoc3(loaded.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation);
                if ((activation != 0 && activation != (int)swActivateDocError_e.swDocNeedsRebuildWarning)
                    || !string.Equals((app.ActiveDoc as IModelDoc2)?.GetPathName(), copy.Path, StringComparison.OrdinalIgnoreCase))
                {
                    PluginDiagnosticLog.Record("归档副本激活", "图档=" + copy.Source + "；状态码=" + activation
                        + "；实际活动文件=" + (app.ActiveDoc as IModelDoc2)?.GetPathName());
                    throw new InvalidOperationException("无法安全切换到归档隔离副本，未开始上传：" + System.IO.Path.GetFileName(copy.Source));
                }
                ValidateAssembly(copy, loaded, true);
                foreach (var input in (app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                    if (files.Any(file => string.Equals(file.Path, input.GetPathName(), StringComparison.OrdinalIgnoreCase))
                        && checkedEmbeddedPaths.Add(input.GetPathName()))
                    {
                        try
                        {
                            var baseline = ArchiveDesignTableLinks.PrepareAndCapture(app, input, directory);
                            if (baseline != null)
                            {
                                designTableBaselines[files.Single(file => string.Equals(file.Path, input.GetPathName(), StringComparison.OrdinalIgnoreCase)).Source] = baseline;
                                int error = 0, warning = 0;
                                if (!input.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref error, ref warning) || error != 0)
                                    throw new IOException("设计表断链后的隔离副本未保存。");
                            }
                            ArchiveResourceIsolation.ValidateEmbeddedObjects(input);
                        }
                        catch (IOException error) { throw new IOException("图档“" + System.IO.Path.GetFileName(input.GetPathName()) + "”的嵌入内容未通过预检查：" + error.Message, error); }
                    }
                if (loaded is IDrawingDoc drawing)
                {
                    var previousSheet = (drawing.GetCurrentSheet() as ISheet)?.GetName();
                    try
                    {
                        foreach (var sheet in (drawing.GetSheetNames() as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>())
                        {
                            if (!drawing.ActivateSheet(sheet)) throw new InvalidOperationException("无法核对工程图图页，未开始上传：" + sheet);
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
                if (string.Equals(System.IO.Path.GetDirectoryName(copy.Path), inputDirectory, StringComparison.OrdinalIgnoreCase))
                    foreach (var reference in NativeDesignDependencies.ReadManaged(loaded))
                    {
                        // The closed native manifest can omit a live Interconnect
                        // STEP or parameter source. Collect the actual feature's
                        // source before relocating it, keeping its original name.
                        var name = System.IO.Path.GetFileName(reference);
                        if (Native(reference)) continue;
                        if (!byName.TryGetValue(name, out var dependency))
                        {
                            if (!sources.TryGetValue(name, out var source))
                            {
                                var peer = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(copy.Source), name);
                                source = File.Exists(reference) ? reference : File.Exists(peer) ? peer : null;
                            }
                            if (source == null) continue;
                            if (!NativeDesignDependencies.IsParameterFile(source)
                                && !new[] { ".step", ".stp" }.Contains(System.IO.Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
                                throw new IOException("设计来源格式尚不能完整核对，已在上传前停止：" + name);
                            dependency = new FileCopy { Source = source, Path = System.IO.Path.Combine(inputDirectory, name), SourceSha256 = ArchiveFileFingerprint.Read(source) };
                            File.Copy(source, dependency.Path);
                            File.SetAttributes(dependency.Path, FileAttributes.Normal);
                            dependency.Dependencies = new string[0];
                            files.Add(dependency); byName.Add(name, dependency); inputMap.Add(name, dependency.Path);
                            dependencies.Add(dependency.Path, new string[0]);
                            PluginDiagnosticLog.Record("实际设计来源补齐", copy.Source + "；来源=" + reference + "；同名受控副本=" + dependency.Path);
                        }
                        dependencies[copy.Path] = dependencies[copy.Path].Concat(new[] { reference }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        copy.Dependencies = copy.Dependencies.Concat(new[] { dependency.Source }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                return loaded;
            }

            var baselines = new Dictionary<string, IReadOnlyDictionary<string, ArchiveReferenceIsolation.State>>(StringComparer.OrdinalIgnoreCase);
            inputMap = files.ToDictionary(file => System.IO.Path.GetFileName(file.Source), file => file.Path, StringComparer.OrdinalIgnoreCase);
            var inactiveReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var copy in files.Where(file => Type(file.Path) == 1 && dependencies[file.Path].Any(reference => !File.Exists(reference)
                && !inputMap.ContainsKey(System.IO.Path.GetFileName(reference)) && !NativeArchiveManifest.IsInterconnectCachePath(reference))).ToArray())
            {
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                var model = Open(copy);
                var changed = ArchiveReferenceIsolation.PrepareMissingImportSources(model, inputMap, out var inactive);
                if (!changed) inactive = ArchiveSmartComponentLinks.Prepare(model, inputMap, out changed);
                inactiveReferences.UnionWith(inactive);
                dependencies[copy.Path] = dependencies[copy.Path].Where(reference => !inactive.Contains(reference, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (changed)
                {
                    int error = 0, warning = 0;
                    if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref error, ref warning) || error != 0)
                        throw new InvalidOperationException("智能关联处理后的受控副本未保存，尚未上传：" + System.IO.Path.GetFileName(copy.Source));
                }
                CloseOwnedDocuments();
            }
            // Complete embedded-table preparation on inputs, including standalone
            // leaves not loaded by an assembly. Output checks must only verify the
            // saved package and its complete original configuration baseline.
            foreach (var copy in files.Where(file => Type(file.Path) == 1 && dependencies[file.Path].Length == 0).ToArray())
                if (!checkedEmbeddedPaths.Contains(copy.Path))
                {
                    report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                    var model = Open(copy);
                    if (dependencies[copy.Path].Length != 0) { CloseOwnedDocuments(); continue; }
                    NativeDesignDependencies.ValidateManaged(model, inputMap);
                    if (NativeDesignDependencies.ReadManaged(model).Count == 0
                        && !ArchiveResourceIsolation.PrepareManaged(model, inputMap)
                        && !designTableBaselines.ContainsKey(copy.Source)) checkedIndependentLeaves.Add(copy.Source);
                    CloseOwnedDocuments();
                }
            var inputHashes = files.ToDictionary(file => file.Source, file => ArchiveFileFingerprint.Read(file.Path), StringComparer.OrdinalIgnoreCase);
            var referenceFree = new HashSet<string>(files.Where(file => Type(file.Path) == 1 && dependencies[file.Path].Length == 0)
                .Select(file => file.Source), StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(file => referenceFree.Contains(file.Source)))
                File.Copy(file.Path, System.IO.Path.Combine(baselineDirectory, System.IO.Path.GetFileName(file.Source)));
            foreach (var copy in files.Where(file => Native(file.Path) && Type(file.Path) != 3)
                .Where(file => !referenceFree.Contains(file.Source))
                .OrderByDescending(file => dependencies[file.Path].Length))
            {
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                var model = Open(copy);
                baselines.Add(copy.Source, ArchiveReferenceIsolation.CaptureConfigurations(model));
                var importEmbedded = ArchiveReferenceIsolation.EmbedImportedStepAssemblies(model);
                if (NativeDesignDependencies.Relocate(model, inputMap) || importEmbedded)
                {
                    int error = 0, warning = 0;
                    if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_IncludeVirtualSubAsmComps), ref error, ref warning) || error != 0)
                        throw new IOException("受控来源重定位后的隔离输入未保存：" + System.IO.Path.GetFileName(copy.Source));
                }
                CloseOwnedDocuments();
            }
            var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var copy in files.Where(file => Native(file.Path)).OrderByDescending(file => dependencies[file.Path].Length).ToArray())
            {
                if (packed.Contains(copy.Source)) continue;
                if (referenceFree.Contains(copy.Source))
                {
                    File.Copy(copy.Path, System.IO.Path.Combine(packageDirectory, System.IO.Path.GetFileName(copy.Source)), true);
                    packed.Add(copy.Source);
                    continue;
                }
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                var model = Open(copy);
                foreach (var loaded in (app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                {
                    var owned = files.FirstOrDefault(file => string.Equals(file.Path, loaded.GetPathName(), StringComparison.OrdinalIgnoreCase));
                    if (owned == null)
                    {
                        if (NativeArchiveManifest.IsInterconnectCachePath(loaded.GetPathName())
                            || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(loaded.GetPathName())) continue;
                        throw new InvalidOperationException("原生打包载入了隔离输入之外的图档，已停止，原文件未保存：" + System.IO.Path.GetFileName(loaded.GetPathName()));
                    }
                    if (!(loaded is IDrawingDoc) && !referenceFree.Contains(owned.Source) && !baselines.ContainsKey(owned.Source))
                        baselines.Add(owned.Source, ArchiveReferenceIsolation.CaptureConfigurations(loaded));
                    NativeDesignDependencies.Relocate(loaded, inputMap);
                }
                int activation = 0;
                app.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation);
                if (!string.Equals((app.ActiveDoc as IModelDoc2)?.GetPathName(), copy.Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("原生打包根图档不一致，已停止存档。");
                var pack = model.Extension.GetPackAndGo() as IPackAndGo;
                if (pack == null) throw new InvalidOperationException("无法创建原生存档清单：" + System.IO.Path.GetFileName(copy.Source));
                try
                {
                    pack.IncludeDrawings = true; pack.IncludeSuppressed = true; pack.IncludeToolboxComponents = true;
                    pack.FlattenToSingleFolder = false; pack.AddPrefix = ""; pack.AddSuffix = "";
                    if (!pack.GetDocumentNames(out var entries) || !(entries is Array names) || names.Length == 0
                        || !string.Equals(Convert.ToString(names.GetValue(0)), copy.Path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原生打包未返回完整且一致的根图档清单。");
                    var sourcePaths = names.Cast<object>().Select(Convert.ToString).ToArray();
                    var destinations = new string[sourcePaths.Length];
                    for (var index = 0; index < sourcePaths.Length; index++)
                    {
                        var sourcePath = sourcePaths[index];
                        var owned = files.FirstOrDefault(file => string.Equals(file.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
                        if (owned == null)
                        {
                            if (NativeArchiveManifest.IsInterconnectCachePath(sourcePath)
                                || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(sourcePath)
                                || inactiveReferences.Contains(sourcePath)) { destinations[index] = ""; continue; }
                            throw new InvalidOperationException("原生存档仍有未准备的真实依赖，已在上传前停止：" + System.IO.Path.GetFileName(sourcePath));
                        }
                        destinations[index] = System.IO.Path.Combine(packageDirectory, System.IO.Path.GetFileName(owned.Source));
                    }
                    if (!pack.SetDocumentSaveToNames(destinations)) throw new InvalidOperationException("无法设置保持原名的原生存档目标。");
                    var statuses = model.Extension.SavePackAndGo((PackAndGo)pack) as Array;
                    if (statuses == null || statuses.Length != sourcePaths.Length)
                        throw new InvalidOperationException("原生存档未返回完整的逐文件结果。");
                    for (var index = 0; index < sourcePaths.Length; index++)
                    {
                        if (string.IsNullOrEmpty(destinations[index])) continue;
                        if (Convert.ToInt32(statuses.GetValue(index)) != 0 || !File.Exists(destinations[index]))
                            throw new InvalidOperationException("原生存档副本保存失败，尚未上传：" + System.IO.Path.GetFileName(sourcePaths[index]));
                        packed.Add(files.Single(file => string.Equals(file.Path, sourcePaths[index], StringComparison.OrdinalIgnoreCase)).Source);
                    }
                }
                finally { if (System.Runtime.InteropServices.Marshal.IsComObject(pack)) System.Runtime.InteropServices.Marshal.ReleaseComObject(pack); }
                CloseOwnedDocuments();
            }
            // Input and output files deliberately retain the same names. Close only
            // this private process's owned inputs before validating the saved package.
            CloseOwnedDocuments();
            foreach (var copy in files)
            {
                var output = System.IO.Path.Combine(packageDirectory, System.IO.Path.GetFileName(copy.Source));
                if (!File.Exists(output)) File.Copy(copy.Path, output);
                copy.ContentFrozen = ArchiveFileFingerprint.Read(output) != copy.SourceSha256;
                copy.Path = output;
            }
            var controlled = files.ToDictionary(file => System.IO.Path.GetFileName(file.Source), file => file.Path, StringComparer.OrdinalIgnoreCase);
            // Overlapping Pack and Go roots can retain an input pointer in a
            // suppressed nested component. Repoint every saved native file while
            // all documents are closed, then validate it in the fresh reader.
            foreach (var copy in files.Where(file => Native(file.Path)))
            {
                var values = app.GetDocumentDependencies2(copy.Path, false, false, false) as Array;
                if (values != null && values.Length % 2 != 0)
                    throw new IOException("打包后的原生引用清单未完整读取：" + System.IO.Path.GetFileName(copy.Source));
                foreach (var reference in values == null ? Enumerable.Empty<string>() : Enumerable.Range(0, values.Length / 2)
                    .SelectMany(index => (Convert.ToString(values.GetValue(index * 2 + 1)) ?? "").Split('|')).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (Native(reference) && controlled.TryGetValue(System.IO.Path.GetFileName(reference), out var target)
                        && !string.Equals(reference, target, StringComparison.OrdinalIgnoreCase)
                        && !app.ReplaceReferencedDocument(copy.Path, reference, target))
                        throw ReferenceRelocationFailure(copy, reference, target);
                copy.ContentFrozen |= ArchiveFileFingerprint.Read(copy.Path) != copy.SourceSha256;
            }
            var bytePreserved = new HashSet<string>(files.Where(file => referenceFree.Contains(file.Source)
                && string.Equals(inputHashes[file.Source], ArchiveFileFingerprint.Read(file.Path), StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Source), StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, ArchiveReferenceIsolation.State> ReadLeafBaseline(FileCopy file)
            {
                // No user documents exist in this private process. Reference-free
                // leaves can be inspected independently without an assembly context.
                CloseOwnedDocuments();
                var path = System.IO.Path.Combine(baselineDirectory, System.IO.Path.GetFileName(file.Source));
                int error = 0, warning = 0;
                var source = app.OpenDoc6(path, 1, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning);
                if (source == null || error != 0 || !string.Equals(source.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法核对原生叶子文件的完整基准：" + System.IO.Path.GetFileName(file.Source));
                try { return ArchiveReferenceIsolation.CaptureConfigurations(source); }
                finally { CloseOwnedDocuments(); }
            }
            foreach (var copy in files.Where(file => referenceFree.Contains(file.Source) && !bytePreserved.Contains(file.Source)))
            {
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                baselines.Add(copy.Source, ReadLeafBaseline(copy));
            }
            if (freshReader != null)
            {
                app = freshReader();
                app.UserControlBackground = true;
                app.CommandInProgress = true;
            }
            foreach (var copy in files.Where(file => Native(file.Path)).OrderBy(file => Type(file.Path) == 1 ? 0 : Type(file.Path) == 2 ? 1 : 2)
                .ThenBy(file => file.Dependencies.Length))
            {
                report?.Invoke(System.IO.Path.GetFileName(copy.Source));
                if (checkedIndependentLeaves.Contains(copy.Source) && bytePreserved.Contains(copy.Source))
                {
                    if (!string.Equals(inputHashes[copy.Source], ArchiveFileFingerprint.Read(copy.Path), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("已核对的独立原生文件在打包后发生变化，已停止存档。");
                    PluginDiagnosticLog.Record("原生内容复用", copy.Source + "：来源、嵌入内容和资源已完整核对，输出字节全部一致，复用全配置原生内容。");
                    continue;
                }
                var model = Open(copy);
                try
                {
                    if (designTableBaselines.TryGetValue(copy.Source, out var parameters)
                        && parameters != ArchiveSmartComponentLinks.ConfigurationParameters(model))
                        throw new IOException("存档副本重开后设计表的全部配置、参数、特征或注释状态不一致，已停止存档：" + System.IO.Path.GetFileName(copy.Source));
                    if (bytePreserved.Contains(copy.Source))
                    {
                        NativeDesignDependencies.ValidateManaged(model, controlled);
                        if (!ArchiveResourceIsolation.PrepareManaged(model, controlled))
                        {
                            // Exact native bytes retain every configuration, feature
                            // and saved body. Do not regenerate unused standard sizes.
                            if (!string.Equals(inputHashes[copy.Source], ArchiveFileFingerprint.Read(copy.Path), StringComparison.OrdinalIgnoreCase))
                                throw new IOException("原生叶子文件在核对时发生变化。");
                            PluginDiagnosticLog.Record("原生内容复用", copy.Source + "：原生字节全部一致，完整保留所有配置，未逐配置重建或重存。");
                            CloseOwnedDocuments();
                            continue;
                        }
                        CloseOwnedDocuments();
                        File.Copy(System.IO.Path.Combine(baselineDirectory, System.IO.Path.GetFileName(copy.Source)), copy.Path, true);
                        baselines[copy.Source] = ReadLeafBaseline(copy);
                        model = Open(copy);
                    }
                    if (!(model is IDrawingDoc))
                    {
                        copy.ContentFrozen |= NativeDesignDependencies.Relocate(model, controlled);
                        if (!baselines.TryGetValue(copy.Source, out var originalGeometry))
                            throw new IOException("缺少原生副本生成前的配置和几何核对记录。");
                        ArchiveReferenceIsolation.VerifyManagedCopy(model, originalGeometry, controlled);
                    }
                    copy.ContentFrozen |= ArchiveResourceIsolation.PrepareManaged(model, controlled);
                }
                catch (IOException error)
                { throw new InvalidOperationException("图档“" + System.IO.Path.GetFileName(copy.Source) + "”不能安全固化，未开始上传，原图档和窗口已保留。" + error.Message, error); }
                if (Type(copy.Path) == 2)
                {
                    var originalConfiguration = model.ConfigurationManager.ActiveConfiguration.Name;
                    try
                    {
                        foreach (var configuration in (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>())
                        {
                            if (model.ConfigurationManager.ActiveConfiguration.Name != configuration && !model.ShowConfiguration2(configuration))
                                throw new InvalidOperationException("固化后无法核对装配配置：" + System.IO.Path.GetFileName(copy.Source) + "；" + configuration);
                            ValidateAssembly(copy, model, false);
                        }
                    }
                    finally
                    {
                        if (model.ConfigurationManager.ActiveConfiguration.Name != originalConfiguration && !model.ShowConfiguration2(originalConfiguration))
                            throw new InvalidOperationException("固化后无法恢复装配配置：" + originalConfiguration);
                    }
                }
                PluginDiagnosticLog.Record("归档副本固化", "已完成内容核对：" + copy.Source);
                if (model.GetSaveFlag() || copy.ContentFrozen)
                {
                    int error = 0, warning = 0;
                    PluginDiagnosticLog.Record("归档副本保存", "开始：" + copy.Source);
                    if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_IncludeVirtualSubAsmComps), ref error, ref warning) || error != 0)
                    {
                        PluginDiagnosticLog.Record("归档副本保存", "图档=" + copy.Source + "；错误码=" + error + "；警告码=" + warning);
                        throw new InvalidOperationException("独立归档副本原生保存失败，未开始上传：" + System.IO.Path.GetFileName(copy.Source)
                            + "。原生错误已记录在诊断日志中，原文件已保留。");
                    }
                    PluginDiagnosticLog.Record("归档副本保存", "完成：" + copy.Source);
                }
                copy.ContentFrozen |= ArchiveFileFingerprint.Read(copy.Path) != copy.SourceSha256;
                CloseOwnedDocuments();
            }
            copiesPrepared = true;
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("归档隔离副本", error.ToString());
            if (error is IOException) throw new InvalidOperationException("受控存档副本准备未完成，尚未上传，原文件和窗口已保留。" + error.Message, error);
            throw;
        }
        finally
        {
            var closing = new List<Tuple<string, string>>();
            var loaded = new IModelDoc2[0];
            try { loaded = (app.GetDocuments() as Array)?.OfType<IModelDoc2>().ToArray() ?? loaded; }
            catch (System.Runtime.InteropServices.COMException error)
            { cleanupFailures.Add("独立会话连接"); PluginDiagnosticLog.Record("归档副本清理", "独立会话已退出，保留原始失败原因：" + error); }
            foreach (var model in loaded)
                try
                {
                    var path = model.GetPathName(); var title = model.GetTitle();
                    if (files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (originalTitles.Contains(title)) throw new InvalidOperationException("隔离副本与原窗口同名，已保留原窗口，未继续关闭或上传。");
                        closing.Add(Tuple.Create(path, title));
                    }
                }
                catch (System.Runtime.InteropServices.COMException error) when (error.ErrorCode == unchecked((int)0x80010108))
                { PluginDiagnosticLog.Record("归档副本清理", "原生缓存对象已释放，未将其当作文档关闭。"); }
            // Release referring drawings/assemblies before their dependent parts.
            foreach (var item in closing.OrderBy(item => Type(item.Item1) == 3 ? 0 : Type(item.Item1) == 2 ? 1 : 2)
                .ThenBy(item => files.FindIndex(file => string.Equals(file.Path, item.Item1, StringComparison.OrdinalIgnoreCase))))
                try
                {
                    PluginDiagnosticLog.Record("归档副本关闭", "开始：" + item.Item1);
                    if (app.GetOpenDocumentByName(item.Item1) != null) app.CloseDoc(item.Item2);
                    PluginDiagnosticLog.Record("归档副本关闭", "完成：" + item.Item1);
                }
                catch (System.Runtime.InteropServices.COMException error)
                {
                    cleanupFailures.Add(System.IO.Path.GetFileName(item.Item1));
                    PluginDiagnosticLog.Record("归档副本清理", "隔离副本关闭失败，保留原始存档失败原因：" + item.Item1 + "；" + error);
                }
            foreach (var pair in visibility)
                try { app.DocumentVisible(pair.Value, pair.Key); }
                catch (System.Runtime.InteropServices.COMException error)
                { cleanupFailures.Add("窗口显示设置"); PluginDiagnosticLog.Record("归档副本清理", error.ToString()); }
            try
            {
                if (previousPath != null && app.GetOpenDocumentByName(previousPath) != null)
                {
                    int error = 0;
                    app.ActivateDoc3(previousTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref error);
                    if (error != 0 && error != (int)swActivateDocError_e.swDocNeedsRebuildWarning) cleanupFailures.Add("原活动窗口");
                }
            }
            catch (System.Runtime.InteropServices.COMException error)
            { cleanupFailures.Add("原活动窗口"); PluginDiagnosticLog.Record("归档副本清理", error.ToString()); }
            if (!copiesPrepared) Dispose();
        }
        try
        {
        if (cleanupFailures.Count > 0)
            throw new InvalidOperationException("归档隔离副本关闭未完成，未开始上传。原文件已保留；请检查诊断日志中的副本清理记录。");
        // Keep the saved references inside the package. Controlled download will
        // remap these names to its exact manifest; never restore original disk paths.
        var remaining = (app.GetDocuments() as Array)?.OfType<IModelDoc2>().Select(model => model.GetPathName()).ToArray() ?? new string[0];
        if (before.Any(path => !remaining.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("原有文档的加载状态发生变化，未认定独立归档副本准备完成，未开始上传。");
        }
        catch { Dispose(); throw; }
    }

    internal static ArchiveNativeCopyWorkspace Attach(string ownedDirectory, IEnumerable<FileCopy> completed)
        => new ArchiveNativeCopyWorkspace(ownedDirectory, completed);

    private ArchiveNativeCopyWorkspace(string ownedDirectory, IEnumerable<FileCopy> completed)
    { directory = ownedDirectory; files.AddRange(completed); }

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
