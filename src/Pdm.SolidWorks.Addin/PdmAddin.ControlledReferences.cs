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
    private IModelDoc2 OpenControlledReadOnlyDocument(string path, int documentType, string configuration,
        ControlledOpenManifestDto manifest)
        => OpenControlledReadOnlyDocumentCore(path, documentType, configuration, manifest, false);

    private IModelDoc2 OpenControlledReadOnlyDocumentCore(string path, int documentType, string configuration,
        ControlledOpenManifestDto manifest, bool preserveLoadedContext)
    {
        var beforeOpen = new HashSet<string>(((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var preserved = new HashSet<string>(preserveLoadedContext
            ? ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value))
            : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var nativeFirst = documentType == (int)swDocumentTypes_e.swDocASSEMBLY && !manifest.ForEdit
            && !IsReadOnlyPreviewPath(path) && !IsHistoricalDerivedPath(path)
            && PdmDocumentIdentityStore.IsControlledWorkspacePath(path)
            && path.IndexOf(Path.DirectorySeparatorChar + "View" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
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
            if (prepareReferences)
            {
                if (preserveLoadedContext) ControlledReferenceMaterializer.PrepareDrawingOnly(application, path, manifest);
                else ControlledReferenceMaterializer.Prepare(application, path, manifest);
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            IModelDoc2 opened;
            using (new ControlledReferenceSearchScope(application, path, manifest))
                opened = OpenOrActivateDocumentOnSolidWorksThread(path, documentType, configuration, !preserved.Contains(path));
            if (opened == null) return null;
            try
            {
                if (nativeFirst && attempt == 0)
                {
                    foreach (var file in manifest.Files)
                        if (!ControlledReferenceMaterializer.Matches(ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(path), file), file))
                            throw new IOException("受控文件在打开期间发生变化，未认定打开完成：" + file.FileName);
                }
                var embeddedSources = ValidateHistoricalControlledOpenReferences(opened, path, manifest, application)
                    ?? ValidateControlledLoadedSources(opened, path, manifest, application);
                var expected = new HashSet<string>(manifest.Files.Select(file =>
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), file.RelativePath ?? file.FileName))), StringComparer.OrdinalIgnoreCase);
                foreach (var loaded in (application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                {
                    var actual = loaded.GetPathName();
                    if (!string.IsNullOrWhiteSpace(actual) && !beforeOpen.Contains(actual)
                        && !expected.Contains(Path.GetFullPath(actual)) && !embeddedSources.Contains(actual))
                        throw new InvalidOperationException("本次打开加载了受控范围之外的上下文文件，尚未认定打开成功：" + actual);
                    if (!string.IsNullOrWhiteSpace(actual) && expected.Contains(Path.GetFullPath(actual)) && !preserved.Contains(actual))
                        EnsureDocumentOpenedReadOnly(loaded, actual);
                }
                LogOperation("Controlled read-only open completed path=" + path + " referencePrepass=" + prepareReferences
                    + " exactSources=" + (nativeFirst && attempt == 0) + " elapsedMs=" + timer.ElapsedMilliseconds);
                return opened;
            }
            catch (Exception error)
            {
                // Only this attempt's new root can be closed. A failed check must
                // never close a pre-existing user window or another same-name file.
                if (wasLoaded) throw;
                var title = opened.GetTitle();
                if (!PathsEqual(opened.GetPathName(), path)
                    || ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                        .Any(other => string.Equals(other.GetTitle(), title, StringComparison.OrdinalIgnoreCase)
                            && !PathsEqual(other.GetPathName(), path)))
                    throw new IOException("实际加载来源尚未核对，已保留同名窗口，请核对图档所在目录后重试。", error);
                application.CloseDoc(title);
                if (FindLoadedDocument(path) != null)
                    throw new IOException("本次打开的只读窗口尚未关闭，未重试引用定位。", error);
                if (!nativeFirst || attempt > 0 || !(error is InvalidOperationException)) throw;
                // Release only models loaded by this failed attempt, so the reference
                // API can repair closed copies. Every pre-existing user document stays open.
                var remaining = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray();
                foreach (var model in remaining.Where(model => !beforeOpen.Contains(model.GetPathName())))
                {
                    if (model.GetSaveFlag())
                        throw new IOException("新加载的文件出现未保存内容，已保留窗口及内容，未自动关闭重试。", error);
                    var titleToClose = model.GetTitle();
                    if (remaining.Any(other => beforeOpen.Contains(other.GetPathName())
                        && string.Equals(other.GetTitle(), titleToClose, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("新加载文件与原有窗口同名，原窗口及文件均已保留，未继续自动重试。", error);
                    application.CloseDoc(titleToClose);
                }
                LogDiagnostic("原生打开的引用来源未通过，回退到逐文件定位后重试", error);
            }
        }
        throw new IOException("受控装配体打开未完成，请检查引用图档后重试。");
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
            var directory = Path.Combine(WorkspaceSettingsStore.GetWorkspaceRoot(), ".uplm", "ArchiveReferences",
                "plm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(node.FileName));
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

    private static void ValidateControlledLoadedPathsCore(IModelDoc2 opened, string rootPath,
        ControlledOpenManifestDto manifest, ISldWorks app, bool requireExactSources)
        => ValidateControlledLoadedSources(opened, rootPath, manifest, app);

    private static HashSet<string> ValidateControlledLoadedSources(IModelDoc2 opened, string rootPath,
        ControlledOpenManifestDto manifest, ISldWorks app)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var expected = manifest.Files.ToDictionary(file => file.FileName,
            file => ControlledReferenceMaterializer.LocalPath(directory, file), StringComparer.OrdinalIgnoreCase);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var embeddedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        void CheckDeferred(string path, string name, bool embedded)
        {
            if (embedded) return;
            var fileName = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
            if (!expected.TryGetValue(fileName, out var local))
            {
                failures.Add(name + "：未加载组件缺少服务器受控文件；原引用：" + path);
                return;
            }
            var file = manifest.Files.First(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (!PathsEqual(ArchiveReferencePath.Resolve(path, directory, "核对未加载组件引用", rootPath), local))
                failures.Add(name + "：未加载组件仍指向原目录，尚未完成受控引用定位");
            if (verifiedCopies.Add(local) && !ControlledReferenceMaterializer.Matches(local, file))
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
                if (!new[] { ".SLDPRT", ".SLDASM" }.Contains(Path.GetExtension(native), StringComparer.OrdinalIgnoreCase)) continue;
                if (!expected.TryGetValue(Path.GetFileName(native), out var target))
                {
                    if (NativeArchiveManifest.IsInterconnectCachePath(native)
                        && ImportedComponentStorage.IsStableOwner(local) && SavedImportedOwner(local, file.Configuration)) continue;
                    failures.Add(name + "：内部引用缺少服务器受控文件：" + Path.GetFileName(native));
                    PluginDiagnosticLog.Record("受控引用核对", "未确认的内部引用；所属=" + local + "；原始=" + string.Join("|", source));
                }
                else
                {
                    if (!PathsEqual(native, target)) failures.Add(name + "：内部引用尚未定位到受控工作区：" + Path.GetFileName(native));
                    CheckDeferred(target, name + "/" + Path.GetFileName(native), false);
                }
            }
        }
        void Check(string path, string name, bool embedded)
        {
            if (embedded || string.IsNullOrWhiteSpace(path)) return;
            var full = ArchiveReferencePath.Resolve(path, directory, "核对PLM实际加载来源", rootPath);
            if (!expected.TryGetValue(Path.GetFileName(full), out var local)
                || !string.Equals(full, local, StringComparison.OrdinalIgnoreCase))
                failures.Add(name + "；实际来源：" + full);
        }
        void Visit(IModelDoc2 model, string importedOwner = null)
        {
            if (model == null || !visited.Add(model.GetPathName() ?? model.GetTitle())) return;
            NativeDesignDependencies.Validate(model, expected);
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY && model is IAssemblyDoc assembly)
            {
                var imported = new ImportedComponentStorage(app);
                foreach (var component in ((assembly.GetComponents(false) as Array)?.Cast<object>() ?? Enumerable.Empty<object>()).OfType<IComponent2>())
                {
                    var loaded = component.GetModelDoc2() as IModelDoc2;
                    var path = loaded?.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) path = component.GetPathName();
                    var embedded = component.IsVirtual;
                    string componentOwner = null;
                    if (!embedded && IsSolidWorksTemporaryVirtualComponentPath(path))
                        for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
                            if (parent.IsVirtual) { embedded = true; break; }
                    if (!embedded && NativeArchiveManifest.IsInterconnectCachePath(path))
                    {
                        var owner = imported.FindOwner(component, model);
                        embedded = !string.IsNullOrWhiteSpace(owner) && expected.Values.Contains(owner, StringComparer.OrdinalIgnoreCase);
                        if (embedded) componentOwner = owner;
                        else if (!string.IsNullOrWhiteSpace(importedOwner) && NativeArchiveManifest.IsInterconnectCachePath(model.GetPathName()))
                        {
                            // Re-rooting an imported container loses its outer instance
                            // parent. Its verified native owner still carries its IC children.
                            embedded = true;
                            componentOwner = importedOwner;
                        }
                    }
                    if (loaded != null) Check(path, component.Name2, embedded);
                    else CheckDeferred(path, component.Name2, embedded);
                    if (embedded && !string.IsNullOrWhiteSpace(path)) embeddedSources.Add(path);
                    Visit(loaded, componentOwner);
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
                    else CheckDeferred(path, "工程图模型", false);
                    Visit(loaded);
                }
        }
        Check(opened?.GetPathName(), "当前图档", false);
        Visit(opened);
        // A real missing file or wrong loaded source cannot be downgraded to a warning.
        if (failures.Count > 0)
            throw new InvalidOperationException("PLM图档未能仅使用本次受控副本打开，已停止使用外部文件。请补齐存档引用后重新打开：\r\n"
                + string.Join("\r\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
        return embeddedSources;
    }
}
