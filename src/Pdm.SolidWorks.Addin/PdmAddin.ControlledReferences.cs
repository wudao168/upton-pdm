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
    private async Task RestoreMissingControlledArchiveFilesAsync(CadTreeNode root, Guid projectId, CancellationToken token)
    {
        var groups = EnumerateArchiveNodes(root).Where(node => node.DocumentId.HasValue
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
                projectId.ToString("N"), group.Key.Id.ToString("N"), version.Id.ToString("N"));
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
        void Visit(IModelDoc2 model)
        {
            if (model == null || !visited.Add(model.GetPathName() ?? model.GetTitle())) return;
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY && model is IAssemblyDoc assembly)
            {
                var imported = new ImportedComponentStorage(app);
                foreach (var component in ((assembly.GetComponents(false) as Array)?.Cast<object>() ?? Enumerable.Empty<object>()).OfType<IComponent2>())
                {
                    var loaded = component.GetModelDoc2() as IModelDoc2;
                    var path = loaded?.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) path = component.GetPathName();
                    var embedded = component.IsVirtual;
                    if (!embedded && IsSolidWorksTemporaryVirtualComponentPath(path))
                        for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
                            if (parent.IsVirtual) { embedded = true; break; }
                    if (!embedded && NativeArchiveManifest.IsInterconnectCachePath(path))
                    {
                        var owner = imported.FindOwner(component, model);
                        embedded = !string.IsNullOrWhiteSpace(owner) && expected.Values.Contains(owner, StringComparer.OrdinalIgnoreCase);
                    }
                    Check(path, component.Name2, embedded);
                }
            }
            if (model.GetType() == (int)swDocumentTypes_e.swDocDRAWING && model is IDrawingDoc drawing)
                foreach (var view in ((drawing.GetViews() as object[]) ?? Array.Empty<object>())
                    .OfType<object[]>().SelectMany(sheet => sheet).OfType<IView>())
                {
                    var loaded = view.ReferencedDocument as IModelDoc2;
                    var path = loaded?.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) path = view.GetReferencedModelName();
                    Check(path, "工程图模型", false);
                    Visit(loaded);
                }
        }
        Check(opened?.GetPathName(), "当前图档", false);
        Visit(opened);
        if (failures.Count > 0)
            throw new InvalidOperationException("PLM图档未能仅使用本次受控副本打开，已停止使用外部文件。请补齐存档引用后重新打开：\r\n"
                + string.Join("\r\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
    }
}
