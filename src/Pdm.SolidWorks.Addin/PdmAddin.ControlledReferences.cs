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
        var preserved = new HashSet<string>(preserveLoadedContext
            ? ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value))
            : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var nativeFirst = documentType == (int)swDocumentTypes_e.swDocASSEMBLY && !manifest.ForEdit
            && !IsReadOnlyPreviewPath(path) && !IsHistoricalDerivedPath(path)
            && PdmDocumentIdentityStore.IsControlledWorkspacePath(path)
            && path.IndexOf(Path.DirectorySeparatorChar + "View" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        var wasLoaded = FindLoadedDocument(path) != null;
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
                    ValidateControlledLoadedPathsCore(opened, path, manifest, application, true);
                }
                ValidateHistoricalControlledOpenReferences(opened, path, manifest, application);
                if (documentType == (int)swDocumentTypes_e.swDocDRAWING)
                    ValidateControlledLoadedPathsCore(opened, path, manifest, application, true);
                var expected = new HashSet<string>(manifest.Files.Select(file =>
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), file.RelativePath ?? file.FileName))), StringComparer.OrdinalIgnoreCase);
                foreach (var loaded in (application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                {
                    var actual = loaded.GetPathName();
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
                try { File.Copy(stage, target, false); }
                finally { DeleteWorkspaceStage(stage); }
            }
            if (!string.Equals(ComputeFileHash(target), version.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("补齐的受控引用文件校验不一致，已保留文件，请重新获取：" + node.FileName);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, group.Key.Id, projectId, version.Id,
                version.Revision?.Display, version.Sha256, new FileInfo(target).Length))
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
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var expected = manifest.Files.ToDictionary(file => file.FileName,
            file => ControlledReferenceMaterializer.LocalPath(directory, file), StringComparer.OrdinalIgnoreCase);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    // F21/F23: an unloaded component's stored address is not its loaded source.
                    if (loaded != null) Check(path, component.Name2, embedded);
                    else PluginDiagnosticLog.Record("组件加载", component.Name2 + "：当前未加载，旧地址不作为实际来源。");
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
                    Visit(loaded);
                }
        }
        Check(opened?.GetPathName(), "当前图档", false);
        Visit(opened);
        if (failures.Count > 0 && (requireExactSources || DocumentRuleGate.ShouldBlock("F20", string.Join("；", failures))))
            throw new InvalidOperationException("PLM图档未能仅使用本次受控副本打开，已停止使用外部文件。请补齐存档引用后重新打开：\r\n"
                + string.Join("\r\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
    }
}
