using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static bool IsCurrentProjectWorkingPath(string path, string projectCode)
    {
        if (string.IsNullOrWhiteSpace(path) || IsReadOnlyPreviewPath(path) || IsHistoricalDerivedPath(path)) return false;
        var configuredRoot = PdmDocumentIdentityStore.CurrentWorkspaceRoot;
        var managedRoot = PdmDocumentIdentityStore.FindManagedWorkspaceRoot(configuredRoot, path);
        if (managedRoot == null) return false;
        // Existing View checkouts can retain an older project folder name. The manifest
        // and file bindings still identify the project. Compact entries encode its code.
        return !PdmDocumentIdentityStore.IsMachineCompactRoot(managedRoot)
            && ManagedCadPaths.IsWithin(path, Path.Combine(managedRoot, "View"))
            || PdmDocumentIdentityStore.IsProjectWorkspacePath(configuredRoot, projectCode, path);
    }

    private IModelDoc2 OpenControlledReadOnlyDocument(string path, int documentType, string configuration,
        ControlledOpenManifestDto manifest)
        => OpenControlledReadOnlyDocumentCore(path, documentType, configuration, manifest, false, allowMissingComponents: true);

    private void PreloadControlledNativeSources(string path, ControlledOpenManifestDto manifest) =>
        ControlledReferenceMaterializer.PreloadVerifiedNativeSources(application, path, manifest,
            (source, kind) => OpenDocumentInvisiblyForBatch(source, kind, string.Empty, readOnly: true));

    private IModelDoc2 OpenControlledReadOnlyDocumentCore(string path, int documentType, string configuration,
        ControlledOpenManifestDto manifest, bool preserveLoadedContext, bool preserveWorkingEdits = false,
        bool allowMissingComponents = true, bool requireDrawingModels = false)
    {
        using var retainedWorking = new ControlledWorkingCopyScope(path, manifest);
        var resumedPaths = new HashSet<string>(manifest.Files.Where(file => manifest.ResumedDocumentIds.Contains(file.DocumentId))
            .Select(file => ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(path), file)), StringComparer.OrdinalIgnoreCase);
        resumedPaths.UnionWith(manifest.RetainedLocalReferences.Values.Where(record => !record.Managed || record.Editable).SelectMany(record => record.Paths));
        ControlledOpenBoundary.RequireLoadedSources(application, path, manifest);
        var beforeOpen = new HashSet<string>(((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var preserved = new HashSet<string>(preserveLoadedContext
            ? ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value))
            : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var nativeFirst = documentType == (int)swDocumentTypes_e.swDocASSEMBLY && !manifest.ForEdit
            && IsCurrentProjectWorkingPath(path, manifest.ProjectCode);
        var wasLoaded = FindLoadedDocument(path) != null;
        if (nativeFirst && !wasLoaded)
        {
            try
            {
                var saved = application.GetDocumentDependencies2(path, true, false, false) as Array;
                if (saved != null)
                    for (var index = 1; index < saved.Length; index += 2)
                        foreach (var source in (Convert.ToString(saved.GetValue(index)) ?? string.Empty).Split('|'))
                        {
                            var file = manifest.Files.FirstOrDefault(candidate => string.Equals(candidate.FileName, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase));
                            if (file != null && !PathsEqual(source, ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(path), file)))
                                nativeFirst = false;
                            if (file == null && ControlledReferenceMaterializer.IsLinkedResource(source)
                                && (File.Exists(source) || File.Exists(Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(source)))))
                                nativeFirst = false;
                        }
            }
            catch (Exception error)
            {
                nativeFirst = false;
                PluginDiagnosticLog.Record("受控引用核对", "原生依赖预检查未完成，将先准备受控引用：" + error);
            }
        }
        for (var attempt = 0; attempt < (nativeFirst ? 2 : 1); attempt++)
        {
            var prepareReferences = !nativeFirst || attempt > 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            IModelDoc2 opened;
            Action<ISet<string>> verifyBoundary;
            var missingComponents = allowMissingComponents
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            using (var search = new ControlledReferenceSearchScope(application, path, manifest))
            {
                if (prepareReferences)
                {
                    if (preserveLoadedContext && preserveWorkingEdits) ControlledReferenceMaterializer.PrepareEditableDrawingOnly(application, path, manifest);
                    else if (preserveLoadedContext) ControlledReferenceMaterializer.PrepareDrawingOnly(application, path, manifest);
                    else ControlledReferenceMaterializer.Prepare(application, path, manifest);
                    if (documentType == (int)swDocumentTypes_e.swDocDRAWING && preserveLoadedContext && preserveWorkingEdits)
                        PrepareWorkingDrawingReferencePreloads(path, manifest);
                    PreloadControlledNativeSources(path, manifest);
                }
                opened = OpenOrActivateDocumentOnSolidWorksThread(path, documentType, configuration,
                    !preserved.Contains(path) && !resumedPaths.Contains(path));
                verifyBoundary = search.VerifyLoadedSources;
            }
            if (opened == null) { verifyBoundary(null); return null; }
            try
            {
                if (nativeFirst && attempt == 0)
                {
                    foreach (var file in manifest.Files)
                        if (!ControlledReferenceMaterializer.Matches(ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(path), file), file))
                            throw new IOException("受控文件在打开期间发生变化，未认定打开完成：" + file.FileName);
                }
                var embeddedSources = ValidateHistoricalControlledOpenReferences(opened, path, manifest, application, missingComponents)
                    ?? ValidateControlledLoadedSources(opened, path, manifest, application, missingComponents, !requireDrawingModels);
                verifyBoundary(missingComponents);
                var expected = new HashSet<string>(manifest.Files.Select(file =>
                    ControlledReferenceMaterializer.ManifestPath(Path.GetDirectoryName(path), file)), StringComparer.OrdinalIgnoreCase);
                expected.UnionWith(manifest.RetainedLocalReferences.Values.SelectMany(record => record.Paths));
                foreach (var loaded in (application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                {
                    var actual = loaded.GetPathName();
                    if (!string.IsNullOrWhiteSpace(actual) && !beforeOpen.Contains(actual)
                        && !expected.Contains(Path.GetFullPath(actual)) && !embeddedSources.Contains(actual)
                        && !manifest.UnverifiedToolboxSources.Contains(actual))
                        throw new InvalidOperationException("本次打开加载了受控范围之外的上下文文件，尚未认定打开成功：" + actual);
                    if (!string.IsNullOrWhiteSpace(actual) && expected.Contains(Path.GetFullPath(actual)) && !preserved.Contains(actual)
                        && !resumedPaths.Contains(actual))
                        EnsureDocumentOpenedReadOnly(loaded, actual);
                }
                LogOperation("Controlled read-only open completed path=" + path + " referencePrepass=" + prepareReferences
                    + " exactSources=" + (nativeFirst && attempt == 0) + " elapsedMs=" + timer.ElapsedMilliseconds);
                foreach (var file in manifest.Files.Where(file => manifest.ResumedDocumentIds.Contains(file.DocumentId)))
                {
                    var editablePath = ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(path), file);
                    SetFileReadOnly(editablePath, false);
                    EnsureLoadedDocumentEditable(editablePath);
                }
                try { if (manifest.RetainedLocalReferences.Count == 0 && manifest.UnverifiedToolboxSources.Count == 0
                    && !(missingComponents?.Count > 0)) ControlledReferenceMaterializer.RememberVerifiedOpen(path, manifest); }
                catch (Exception cacheError) { LogDiagnostic("保存已核对的引用准备记录，本次打开已完成", cacheError); }
                ReportMissingComponentsOnOpen(missingComponents, "已打开", path);
                if (documentType == (int)swDocumentTypes_e.swDocASSEMBLY && !wasLoaded && !beforeOpen.Contains(path)
                    && !IsReadOnlyPreviewPath(path) && !IsHistoricalDerivedPath(path))
                    RememberReadOnlyAssemblyOpen(opened, path, manifest);
                return opened;
            }
            catch (Exception error)
            {
                if (wasLoaded) throw;
                var retry = nativeFirst && attempt == 0 && error is InvalidOperationException;
                CloseRejectedControlledOpen(opened, path, beforeOpen, error, retry);
                if (!retry) throw;
                LogDiagnostic("原生打开的引用来源未通过，回退到逐文件定位后重试", error);
            }
        }
        throw new IOException("受控装配体打开未完成，请检查引用图档后重试。");
    }

    private void CloseRejectedControlledOpen(IModelDoc2 opened, string path, ISet<string> beforeOpen, Exception error,
        bool closeNewDependencies)
    {
        var title = opened.GetTitle();
        if (beforeOpen.Contains(path) || !PathsEqual(opened.GetPathName(), path)
            || ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Any(other => string.Equals(other.GetTitle(), title, StringComparison.OrdinalIgnoreCase)
                    && !PathsEqual(other.GetPathName(), path)))
            throw new IOException("实际加载来源尚未核对，已保留原有窗口，请核对图档所在目录后重试。", error);
        application.CloseDoc(title);
        if (FindLoadedDocument(path) != null)
            throw new IOException("本次打开的只读窗口尚未关闭，请核对引用占用后重试。", error);
        if (!closeNewDependencies) return;
        var remaining = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray();
        foreach (var model in remaining.Where(model => !beforeOpen.Contains(model.GetPathName())))
        {
            if (model.GetSaveFlag())
                throw new IOException("新加载的文件出现未保存内容，已保留窗口及内容，请核对后重试。", error);
            var titleToClose = model.GetTitle();
            if (remaining.Any(other => !ReferenceEquals(other, model)
                && string.Equals(other.GetTitle(), titleToClose, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("新加载的文件存在同名窗口，已保留文件和窗口，请核对所在目录后重试。", error);
            application.CloseDoc(titleToClose);
        }
    }

    private void ReportMissingComponentsOnOpen(ISet<string> missingComponents, string completed = "已打开", string rootPath = null)
    {
        rootPath = rootPath ?? (application?.ActiveDoc as IModelDoc2)?.GetPathName() ?? currentTree?.FullPath;
        var context = controlledOpenManifests.FirstOrDefault(pair => pair.Value.Files.Any(file =>
            PathsEqual(ControlledReferenceMaterializer.ManifestPath(pair.Key, file), rootPath)));
        if (context.Value != null)
        {
            var manifest = context.Value;
            var issues = (manifest.ReferenceIssues ?? new List<OpenReferenceIssueDto>()).ToList();
            foreach (var path in missingComponents ?? new HashSet<string>())
                if (!issues.Any(issue => string.Equals(issue.OriginalPath, path, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(issue.OriginalPath) && string.Equals(issue.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new OpenReferenceIssueDto { InstancePath = manifest.RootRelativePath, FileName = Path.GetFileName(path),
                        OriginalPath = path, Reason = "缺失引用已压缩，未加载。" });
            manifest.ReferenceIssues = OpenReferenceFindings.Merge(issues);
            try { OpenReferenceFindings.Save(ManagedCadPaths.Resolve(context.Key, manifest.RootRelativePath), manifest.RootVersionId, manifest.ReferenceIssues); }
            catch (Exception error) { LogDiagnostic("保留缺件诊断清单", error); }
            if (currentTree != null && PathsEqual(currentTree.FullPath, rootPath)) currentTree.OpenReferenceIssues = manifest.ReferenceIssues;
        }
        var names = (missingComponents ?? new HashSet<string>()).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var toolbox = context.Value?.UnverifiedToolboxSources.Count ?? 0;
        if (names.Length == 0 && toolbox == 0) return;
        var details = new List<string>();
        if (names.Length > 0)
        {
            PluginDiagnosticLog.Record("缺件打开", "缺失引用未加载：" + string.Join("、", names));
            details.Add(names.Length + "项缺失引用未加载，" + string.Join("、", names.Take(2)) + (names.Length > 2 ? "等" : ""));
        }
        if (toolbox > 0) details.Add(toolbox + "项Toolbox来源待核对，详见引用诊断");
        taskPaneControl?.SetCheckoutReminder(completed + "，" + string.Join("；", details), false);
    }

    private async Task RestoreMissingControlledArchiveFilesAsync(CadTreeNode root, Guid projectId, CancellationToken token)
    {
        var groups = EnumerateArchiveNodes(root).Where(node => node.DocumentId.HasValue
            && (node.Kind != CadDocumentKind.Drawing || root.Kind == CadDocumentKind.Drawing)
            && node.Status != CadReferenceStatus.Virtual && !File.Exists(node.FullPath))
            .GroupBy(node => new { Id = node.DocumentId.Value, node.OpenedVersionId, node.CurrentRevision }).ToArray();
        if (groups.Length == 0) return;
        var documents = await apiClient.GetDocumentsAsync(projectId, token);
        foreach (var group in groups)
        {
            var node = group.First();
            if (!documents.Any(document => document.Id == group.Key.Id && document.Kind == (int)node.Kind
                && string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase))) continue;
            var history = await apiClient.GetVersionsAsync(group.Key.Id, token);
            var version = history.FirstOrDefault(item => item.Id == node.OpenedVersionId)
                ?? history.FirstOrDefault(item => item.Revision?.Display == node.CurrentRevision);
            if (version == null) continue; // Never substitute the latest version for an unknown saved reference.
            var workspace = WorkspaceSettingsStore.GetWorkspaceRoot();
            var copyId = Guid.NewGuid().ToString("N");
            var identity = projectId.ToString("N") + ":" + copyId;
            var leaf = Path.GetFileName(node.FileName);
            var directory = ManagedCadPaths.ChooseDirectory(Path.Combine(workspace, ".uplm", "ArchiveReferences", "plm-" + copyId),
                new[] { ManagedCadPaths.CompactDirectory(workspace, "a", identity),
                    ManagedCadPaths.CompactDirectory(ManagedCadPaths.CompactWorkspaceRoot, "a", identity) },
                new[] { leaf });
            var target = ManagedCadPaths.Resolve(directory, leaf);
            Directory.CreateDirectory(directory);
            if (!File.Exists(target))
            {
                var stage = await apiClient.DownloadVersionToWorkspaceStageAsync(group.Key.Id, version.Id,
                    node.FileName, version.Sha256, token);
                try { File.Copy(stage, target, false); ControlledReferenceMaterializer.CopyResolvedBinding(stage, target); }
                finally { DeleteWorkspaceStage(stage); }
            }
            if (!VersionMatchesLocalFile(version, target, ComputeFileHash(target)))
                throw new IOException("补齐的受控引用文件校验不一致，已保留文件，请重新获取：" + node.FileName);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, group.Key.Id, projectId, version.Id,
                version.Revision?.Display, version.Sha256, version.FileLength))
                throw new IOException("补齐引用后无法保存版本记录：" + node.FileName);
            SetFileReadOnly(target, true);
            foreach (var occurrence in group)
            {
                occurrence.FullPath = target;
                occurrence.OpenedVersionId = version.Id;
                occurrence.OpenedRevision = occurrence.CurrentRevision = occurrence.Revision = version.Revision?.Display;
                if (occurrence.Status == CadReferenceStatus.Missing) occurrence.Status = CadReferenceStatus.Normal;
            }
            PluginDiagnosticLog.Record("引用补齐", node.FileName + "：已从PLM补齐原引用版本 " + version.Revision?.Display);
        }
    }

    private static void ValidateControlledLoadedPaths(IModelDoc2 opened, string rootPath,
        ControlledOpenManifestDto manifest, ISldWorks app)
        => ValidateControlledLoadedPathsCore(opened, rootPath, manifest, app, false);

    private static bool IsAssemblyInLargeDesignReview(IModelDoc2 model)
        => model is IAssemblyDoc && model.IsOpenedViewOnly();

    private static void RequireFullAssemblyForControlledOpen(IModelDoc2 model)
    {
        if (IsAssemblyInLargeDesignReview(model))
            throw new InvalidOperationException("装配体正以大型设计审阅模式打开，无法核对完整的模型引用。请在“大型设计审阅”选项卡选择“设定所有为还原”，等待组件加载完成后重试。图档：" + model.GetPathName());
    }

    private static void ValidateControlledLoadedPathsCore(IModelDoc2 opened, string rootPath,
        ControlledOpenManifestDto manifest, ISldWorks app, bool requireExactSources)
        => ValidateControlledLoadedSources(opened, rootPath, manifest, app);

    private static HashSet<string> ValidateControlledLoadedSources(IModelDoc2 opened, string rootPath,
        ControlledOpenManifestDto manifest, ISldWorks app, ISet<string> missingComponents = null, bool allowMissingDrawingModels = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var expected = ControlledOpenBoundary.Paths(directory, manifest);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recoveredViewConfigurations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var validatedDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var embeddedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolboxBrowser = new Lazy<string>(() => SolidWorksToolboxPaths.BrowserDirectory(app));
        var toolboxOwners = new Dictionary<string, IReadOnlyList<KeyValuePair<string, string[]>>>(StringComparer.OrdinalIgnoreCase);
        bool WarnUnverifiedToolboxSource(IComponent2 component, IModelDoc2 root, string actual, bool embedded)
        {
            if (embedded || missingComponents == null || !SolidWorksToolboxPaths.IsBrowserPart(actual, toolboxBrowser.Value)
                || expected.TryGetValue(Path.GetFileName(actual), out var controlled) && PathsEqual(controlled, actual)) return false;
            var ownerPath = (component.GetParent()?.GetModelDoc2() as IModelDoc2 ?? root).GetPathName();
            if (!toolboxOwners.TryGetValue(ownerPath, out var saved))
            {
                try { saved = PdmDependencyEntries(app.GetDocumentDependencies2(ownerPath, false, false, false) as Array, ownerPath); }
                catch (Exception error)
                {
                    PluginDiagnosticLog.Record("Toolbox引用核对", ownerPath + "：" + error);
                    saved = Array.Empty<KeyValuePair<string, string[]>>();
                }
                toolboxOwners[ownerPath] = saved;
            }
            var instance = component.Name2.Split('/').Last();
            var original = FindSavedToolboxReferencePaths(instance, component.ReferencedConfiguration, saved).FirstOrDefault();
            var reason = "SolidWorks已临时加载本机Toolbox文件，来源待核对。实际来源：" + ArchiveReferencePath.Visible(actual);
            manifest.UnverifiedToolboxSources.Add(actual);
            var issue = new OpenReferenceIssueDto { InstancePath = component.Name2, OriginalPath = original,
                FileName = Path.GetFileName(original ?? actual), Reason = reason };
            manifest.ReferenceIssues ??= new List<OpenReferenceIssueDto>();
            if (!manifest.ReferenceIssues.Any(existing => string.Equals(existing.Key, issue.Key, StringComparison.OrdinalIgnoreCase)))
                manifest.ReferenceIssues.Add(issue);
            PluginDiagnosticLog.Record("Toolbox引用提醒", issue.ToString());
            return true;
        }
        bool SavedImportedOwner(string ownerPath, string configuration)
        {
            // A closed imported container can retain a cache address from another
            // session. Verify native import ownership instead of classifying by name.
            var before = new HashSet<string>(((app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Select(model => model.GetPathName()), StringComparer.OrdinalIgnoreCase);
            var previous = app.ActiveDoc as IModelDoc2;
            var visible = app.GetDocumentVisible((int)swDocumentTypes_e.swDocASSEMBLY);
            IModelDoc2 probe = null;
            try
            {
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocASSEMBLY);
                var errors = 0; var warnings = 0;
                probe = app.OpenDoc6(ownerPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly
                        | swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight), configuration, ref errors, ref warnings);
                if (probe == null || errors != 0 || !PathsEqual(probe.GetPathName(), ownerPath) || !(probe is IAssemblyDoc assembly)) return false;
                var imported = new ImportedComponentStorage(app);
                foreach (var component in ((assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>()))
                    if (PathsEqual(imported.FindOwner(component, probe), ownerPath))
                    {
                        PluginDiagnosticLog.Record("嵌入归属", "未加载导入容器已只读核对原生保存归属：" + ownerPath);
                        return true;
                    }
                return false;
            }
            catch (Exception error) { PluginDiagnosticLog.Record("嵌入归属", ownerPath + "：" + error); return false; }
            finally
            {
                if (probe != null && PathsEqual(probe.GetPathName(), ownerPath) && !before.Contains(ownerPath)) app.CloseDoc(probe.GetTitle());
                app.DocumentVisible(visible, (int)swDocumentTypes_e.swDocASSEMBLY);
                if (previous != null && !PathsEqual((app.ActiveDoc as IModelDoc2)?.GetPathName(), previous.GetPathName()))
                {
                    var errors = 0;
                    app.ActivateDoc3(previous.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
            }
        }
        bool ReportMissingComponent(string path)
        {
            if (missingComponents == null || NativeArchiveManifest.IsInterconnectCachePath(path)
                || !IsNativeModelReference(path)) return false;
            missingComponents.Add(path);
            return true;
        }
        void CheckDeferred(string path, string name, bool embedded, bool mayBeMissing = false, bool requireExactPath = false)
        {
            if (embedded) return;
            var fileName = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
            if (!expected.TryGetValue(fileName, out var local))
            {
                if (mayBeMissing && ReportMissingComponent(path)) return;
                failures.Add(name + "：未加载组件缺少服务器受控文件；原引用：" + path);
                return;
            }
            var file = manifest.Files.FirstOrDefault(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            var isLocal = manifest.RetainedLocalReferences.TryGetValue(fileName, out var pending);
            if (isLocal || requireExactPath)
            {
                var actual = ArchiveReferencePath.Resolve(path, directory, "核对未加载组件引用", rootPath);
                if (isLocal && pending.Matches(actual)) local = actual;
                else if ((requireExactPath || isLocal && pending.RequireExactDeferredPath) && !PathsEqual(actual, local))
                    failures.Add(name + "：关联模型尚未定位到当前受控工作区：" + fileName);
            }
            // Unloaded occurrences expose a saved path. Verify the controlled bytes here
            // and compare actual paths only after the native model has loaded.
            if (verifiedCopies.Add(local) && (isLocal ? !pending.Matches(local) : !ControlledReferenceMaterializer.Matches(local, file)))
                failures.Add(name + "：未加载组件的受控文件内容校验失败");
            if (!Path.GetExtension(local).Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)
                || !deferredAssemblies.Add(local)) return;
            Array dependencies;
            try { dependencies = app.GetDocumentDependencies2(local, false, false, false) as Array; }
            catch (Exception error)
            {
                PluginDiagnosticLog.Record("受控引用核对", local + "：" + error);
                failures.Add(name + "：未加载子装配的原生依赖未能读取"); return;
            }
            if (dependencies == null) return; // A physically empty assembly has no file dependencies.
            if (dependencies.Length % 2 != 0) { failures.Add(name + "：原生依赖清单不完整"); return; }
            for (var index = 1; index < dependencies.Length; index += 2)
            {
                var source = (Convert.ToString(dependencies.GetValue(index)) ?? string.Empty).Split('|');
                var native = source[0];
                // These generated files are embedded in the already verified owner.
                // Ordinary disk components never use this virtual-component storage path.
                if (IsSolidWorksTemporaryVirtualComponentPath(native)) continue;
                if (source.Length == 2 && NativeArchiveManifest.IsInterconnectCachePath(native)) continue;
                if (!IsNativeModelReference(native)) continue;
                if (!expected.TryGetValue(Path.GetFileName(native), out var target))
                {
                    if (NativeArchiveManifest.IsInterconnectCachePath(native)
                        && ImportedComponentStorage.IsStableOwner(local) && SavedImportedOwner(local, file?.Configuration ?? string.Empty)) continue;
                    if (!ReportMissingComponent(native))
                        failures.Add(name + "：内部引用缺少服务器受控文件：" + Path.GetFileName(native));
                    PluginDiagnosticLog.Record("受控引用核对", "未确认的内部引用；所属=" + local + "；原始=" + string.Join("|", source));
                }
                else
                {
                    var localChild = manifest.RetainedLocalReferences.TryGetValue(Path.GetFileName(native), out var child) && child.Matches(native);
                    CheckDeferred(localChild ? native : target, name + "/" + Path.GetFileName(native), false);
                }
            }
        }
        void Check(string path, string name, bool embedded)
        {
            if (embedded || string.IsNullOrWhiteSpace(path)) return;
            var full = ArchiveReferencePath.Resolve(path, directory, "核对PLM实际加载来源", rootPath);
            if (manifest.RetainedLocalReferences.TryGetValue(Path.GetFileName(full), out var pending))
            {
                if (!pending.Matches(full)) failures.Add(name + "：实际加载的文件路径或内容与已核对副本不一致：" + full);
                return;
            }
            if (!expected.TryGetValue(Path.GetFileName(full), out var local)
                || !string.Equals(full, local, StringComparison.OrdinalIgnoreCase))
                failures.Add(name + "；实际来源：" + full);
        }
        void ValidateDependencies(IModelDoc2 model)
        {
            if (model != null && validatedDependencies.Add(model.GetPathName() ?? model.GetTitle()))
                NativeDesignDependencies.Validate(model, expected);
        }
        void Visit(IModelDoc2 model, IView drawingView = null)
        {
            if (model == null) return;
            var modelKey = model.GetPathName() ?? model.GetTitle();
            if (!visited.Add(modelKey) && (drawingView == null
                || !recoveredViewConfigurations.TryGetValue(modelKey, out var configurations)
                || !configurations.Add(drawingView.ReferencedConfiguration ?? string.Empty))) return;
            RequireFullAssemblyForControlledOpen(model);
            ValidateDependencies(model);
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY && model is IAssemblyDoc assembly)
            {
                var imported = new ImportedComponentStorage(app);
                var drawingOccurrences = new Lazy<DrawingAssemblyOccurrences>(() =>
                    DrawingAssemblyOccurrences.FromControlledRoot(app, manifest, directory, model, drawingView, expected));
                foreach (var original in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                {
                    var component = original;
                    var loaded = component.GetModelDoc2() as IModelDoc2;
                    var path = loaded?.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) path = component.GetPathName();
                    if (drawingView != null && loaded == null && string.IsNullOrWhiteSpace(path) && !component.IsVirtual)
                    {
                        if (!recoveredViewConfigurations.TryGetValue(modelKey, out var seen))
                            recoveredViewConfigurations.Add(modelKey, seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        seen.Add(drawingView.ReferencedConfiguration ?? string.Empty);
                        IComponent2 corresponding = null;
                        try { corresponding = drawingOccurrences.Value?.Find(component); }
                        catch (System.Runtime.InteropServices.COMException error)
                        { PluginDiagnosticLog.Record("工程图实例来源核对", modelKey + "；" + component.Name2 + "；" + error); }
                        if (corresponding == null)
                        {
                            failures.Add(Path.GetFileName(modelKey) + "（视图配置 " + drawingView.ReferencedConfiguration + "）中的 " + component.Name2
                                + "：工程图视图没有返回组件路径，也未能在当前受控总装的相同配置中确认该实例。");
                            continue;
                        }
                        component = corresponding;
                        loaded = component.GetModelDoc2() as IModelDoc2;
                        path = loaded?.GetPathName();
                        if (string.IsNullOrWhiteSpace(path)) path = component.GetPathName();
                    }
                    var embedded = component.IsVirtual;
                    if (!embedded && IsSolidWorksTemporaryVirtualComponentPath(path))
                        for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
                            if (parent.IsVirtual) { embedded = true; break; }
                    if (!embedded && NativeArchiveManifest.IsInterconnectCachePath(path))
                    {
                        var owner = imported.FindOwner(component, model);
                        embedded = !string.IsNullOrWhiteSpace(owner) && expected.Values.Contains(owner, StringComparer.OrdinalIgnoreCase);
                    }
                    if (loaded != null)
                    {
                        if (WarnUnverifiedToolboxSource(component, model, path, embedded)) continue;
                        Check(path, component.Name2, embedded);
                    }
                    else CheckDeferred(path, component.Name2, embedded, missingComponents != null
                        && !expected.ContainsKey(Path.GetFileName(path) ?? string.Empty)
                        && (CanOmitUnloadedComponent(component, path, embedded)
                            || SolidWorksToolboxPaths.IsBrowserPart(path, toolboxBrowser.Value)),
                        requireExactPath: !ReferenceEquals(original, component));
                    if (embedded && !string.IsNullOrWhiteSpace(path)) embeddedSources.Add(path);
                    // GetComponents(false) already contains every occurrence in this
                    // root's configuration. Re-rooting each loaded child inspects that
                    // document's active configuration instead of the assembly instance,
                    // and repeats the subtree scan. Check its design sources only.
                    ValidateDependencies(loaded);
                }
            }
            if (model.GetType() == (int)swDocumentTypes_e.swDocDRAWING && model is IDrawingDoc drawing)
                foreach (var view in ((drawing.GetViews() as object[]) ?? Array.Empty<object>())
                    .OfType<object[]>().SelectMany(sheet => sheet.Skip(1)).OfType<IView>())
                {
                    var loaded = view.ReferencedDocument as IModelDoc2;
                    var path = loaded?.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) path = view.GetReferencedModelName();
                    if (loaded != null) Check(path, "工程图模型", false);
                    else CheckDeferred(path, "工程图模型", false, mayBeMissing: allowMissingDrawingModels,
                        requireExactPath: !allowMissingDrawingModels);
                    Visit(loaded, view);
                }
        }
        Check(opened?.GetPathName(), "当前图档", false);
        Visit(opened);
        // Unloaded missing components may be reported in a read-only open. A wrong
        // loaded source or an invalid controlled copy still fails verification.
        if (failures.Count > 0)
            throw new InvalidOperationException("PLM图档未能仅使用本次受控副本打开，已停止使用外部文件。请补齐存档引用后重新打开：\r\n"
                + string.Join("\r\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
        return embeddedSources;
    }

    private static bool IsNativeModelReference(string path)
        => string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase);

    private static bool CanOmitUnloadedComponent(IComponent2 component, string path, bool embedded)
        => !embedded && IsNativeModelReference(path) && !NativeArchiveManifest.IsInterconnectCachePath(path)
            && !IsSolidWorksTemporaryVirtualComponentPath(path)
            && component.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed;
}
