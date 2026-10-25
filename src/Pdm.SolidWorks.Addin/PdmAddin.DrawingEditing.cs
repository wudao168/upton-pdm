using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private KeyValuePair<string, ControlledOpenManifestDto> FindLoadedEditContext(ControlledOpenManifestDto requested)
    {
        var active = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        if (string.IsNullOrWhiteSpace(active)) return default;
        return controlledOpenManifests.Where(pair => pair.Value.ProjectId == requested.ProjectId
            && pair.Value.RootDocumentId != requested.RootDocumentId && IsPathWithinDirectory(active, pair.Key)
            && pair.Value.Files.Any(file => file.DocumentId == requested.RootDocumentId)
            && FindLoadedDocument(Path.Combine(pair.Key, pair.Value.RootRelativePath)) != null)
            .OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
    }

    private static ControlledOpenManifestDto MergeEditContext(ControlledOpenManifestDto owner, ControlledOpenManifestDto requested)
    {
        var files = owner.Files.Concat(requested.Files).GroupBy(file => file.DocumentId).Select(group => group.Last()).ToArray();
        if (files.GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("编辑上下文包含不同图档的同名文件，原有窗口已保留，请核对文件名后重试。");
        return new ControlledOpenManifestDto { Id = Guid.NewGuid(), ProjectId = owner.ProjectId, ProjectCode = owner.ProjectCode,
            RootDocumentId = owner.RootDocumentId, RootVersionId = owner.RootVersionId, RootRevision = owner.RootRevision,
            RootRelativePath = owner.RootRelativePath, ForEdit = true, Warnings = requested.Warnings,
            Files = files.Select(file => new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = file.VersionId,
                Revision = file.Revision, FileName = file.FileName, RelativePath = file.RelativePath, FileLength = file.FileLength,
                Sha256 = file.Sha256, Configuration = file.Configuration, IsRoot = file.DocumentId == owner.RootDocumentId }).ToList() };
    }

    private void ValidateEditContextSwitch(ControlledOpenManifestDto manifest, string directory)
    {
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var all = CaptureLoadedDocumentSnapshots();
        var affected = all.Where(document => files.ContainsKey(Path.GetFileName(document.Path))
            && !PathsEqual(document.Path, Path.Combine(directory, files[Path.GetFileName(document.Path)].RelativePath))).ToArray();
        foreach (var document in affected)
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(document.Path, out var metadata)
                && (metadata.ProjectId != manifest.ProjectId || metadata.DocumentId != files[Path.GetFileName(document.Path)].DocumentId))
                throw new InvalidOperationException("另一个图档的同名文件仍打开，已保留原窗口，请核对后重试：" + document.Title);
        var loaded = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Where(model => !string.IsNullOrWhiteSpace(model.GetPathName())).ToArray();
        var references = loaded.GroupBy(model => model.GetPathName(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => LoadedReferencePaths(group.First()).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var readOnly = new HashSet<string>(loaded.Where(model => model.IsOpenedReadOnly()).Select(model => model.GetPathName()), StringComparer.OrdinalIgnoreCase);
        affected = ExpandReadOnlyReferenceOwners(all, affected, references, readOnly);
        EnsureNoUnselectedReferenceOwners(affected.Select(document => document.Path).ToArray());
        var dirty = affected.Where(document => document.HasUnsavedChanges && !document.HasDiscardableReadOnlySaveFlag).ToArray();
        if (dirty.Length > 0) throw new InvalidOperationException("需要切换的图档仍有未保存修改，原有窗口和内容已保留："
            + string.Join("、", dirty.Select(document => document.Title)) + "。请先保存这些实际修改后重试；无需给未修改的上层装配获取权限。");
    }

    private void PreserveReadOnlyContextStates(IEnumerable<LoadedDocumentSnapshot> documents)
    {
        var pending = documents.Where(document => document.HasUnsavedChanges && document.HasDiscardableReadOnlySaveFlag).ToArray();
        if (pending.Length == 0) return;
        var directory = Path.Combine(WorkspaceSettingsStore.GetWorkspaceRoot(), "Recovery", "编辑切换", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var snapshot in pending)
        {
            var document = FindLoadedDocument(snapshot.Path);
            if (document == null || !document.GetSaveFlag()) continue;
            var temporary = CreateCheckInUploadCopy(document, snapshot.Path, Guid.NewGuid());
            try { File.Copy(temporary, Path.Combine(directory, Path.GetFileName(snapshot.Path))); }
            finally { DeleteCheckInUploadCopy(temporary); }
        }
        PluginDiagnosticLog.Record("编辑切换", "只读窗口切换前的未保存状态已保留为恢复副本，未提交或升版：" + directory);
    }

    private void EnsureNoUnselectedReferenceOwners(IReadOnlyCollection<string> switchingPaths)
    {
        if (switchingPaths.Count == 0) return;
        var targets = new HashSet<string>(switchingPaths, StringComparer.OrdinalIgnoreCase);
        var owners = new List<string>();
        foreach (var loaded in (application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
        {
            if (targets.Contains(loaded.GetPathName())) continue;
            if (LoadedReferencePaths(loaded).Any(path => !string.IsNullOrWhiteSpace(path) && targets.Contains(path)))
                owners.Add(loaded.GetTitle());
        }
        if (owners.Count > 0)
            throw new InvalidOperationException("所选模型仍被已打开的图档引用：" + string.Join("、", owners.Distinct())
                + "。原文件及修改均保留，请保存并关闭这些图档后再切换模型编辑。若只新增工程图，可直接出图，无需获取模型编辑权限。");
    }

    private static IEnumerable<string> LoadedReferencePaths(IModelDoc2 loaded)
    {
        if (loaded is IDrawingDoc drawing)
            foreach (var sheet in (drawing.GetViews() as object[] ?? Array.Empty<object>()).OfType<object[]>())
                foreach (var view in sheet.Skip(1).OfType<IView>())
                    yield return (view.ReferencedDocument as IModelDoc2)?.GetPathName() ?? view.GetReferencedModelName();
        if (loaded is IAssemblyDoc assembly)
            foreach (var component in (assembly.GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<IComponent2>())
                yield return (component.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? component.GetPathName();
    }

    private static LoadedDocumentSnapshot[] ExpandReadOnlyReferenceOwners(
        LoadedDocumentSnapshot[] all, LoadedDocumentSnapshot[] conflicts,
        IReadOnlyDictionary<string, string[]> references, ISet<string> readOnlyPaths)
    {
        var selected = new HashSet<string>(conflicts.Select(document => document.Path), StringComparer.OrdinalIgnoreCase);
        bool ReferencesSelected(LoadedDocumentSnapshot document) => references.TryGetValue(document.Path, out var paths) && paths.Any(selected.Contains);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var document in all)
            {
                if (selected.Contains(document.Path) || !ReferencesSelected(document)) continue;
                if (!readOnlyPaths.Contains(document.Path)
                    || document.HasUnsavedChanges && !document.HasDiscardableReadOnlySaveFlag)
                    throw new InvalidOperationException("打开所选受控版本需要切换同名模型，但引用它的图档正在编辑或有未保存修改："
                        + document.Title + "。当前窗口和修改已保留，请保存并关闭该图档后重试。");
                expanded |= selected.Add(document.Path);
            }
        }
        var ordered = new List<LoadedDocumentSnapshot>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Order(LoadedDocumentSnapshot document)
        {
            if (!visited.Add(document.Path)) return;
            foreach (var owner in all.Where(candidate => selected.Contains(candidate.Path)
                && references.TryGetValue(candidate.Path, out var paths) && paths.Contains(document.Path, StringComparer.OrdinalIgnoreCase))) Order(owner);
            ordered.Add(document);
        }
        foreach (var document in all.Where(document => selected.Contains(document.Path))) Order(document);
        return ordered.ToArray();
    }

    // A newly saved drawing beside a preview is not part of that immutable download.
    // Missing metadata alone is not sufficient: the downloaded manifest must exclude it.
    private bool IsNewDrawingInLatestPreview(CadTreeNode node)
    {
        if (node?.Kind != CadDocumentKind.Drawing || node.DocumentId.HasValue
            || !IsLatestReadOnlyPreviewPath(node.FullPath)
            || PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out _)) return false;
        var context = controlledOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(node.FullPath, pair.Key));
        return context.Value?.Files != null && !context.Value.Files.Any(file =>
            PathsEqual(node.FullPath, Path.Combine(context.Key, file.RelativePath ?? file.FileName)));
    }

    private CadTreeNode PrepareDrawingCheckInContext(CadTreeNode node)
    {
        var projectId = currentProjectId;
        var modelId = node.RelatedModelDocumentId
            ?? node.ReferencedModels.FirstOrDefault(reference => reference.DocumentId.HasValue)?.DocumentId
            ?? EnumerateCadNodes(currentTree).FirstOrDefault(parent => parent.Kind != CadDocumentKind.Drawing
                && parent.Children.Any(child => PathsEqual(child.FullPath, node.FullPath)))?.DocumentId;
        var model = OpenOrActivateDocumentOnSolidWorksThread(node.FullPath, (int)swDocumentTypes_e.swDocDRAWING, string.Empty);
        if (model == null) throw new InvalidOperationException("未能打开待存档工程图，原文件已保留。");
        if (IsNewDrawingInLatestPreview(node))
        {
            var originalHash = ComputeFileHash(node.FullPath);
            var destination = DrawingDraftPath.ForSource(node.FullPath, projectId, modelId);
            if (File.Exists(destination))
                throw new InvalidOperationException("工程图草稿目录已有同名文件，已保留两份文件，请核对后存档：" + destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var errors = 0;
            var warnings = 0;
            // Switch only this new drawing, then preserve the old disk copy outside the
            // preview tree so it cannot be rediscovered as another new drawing.
            if (!model.Extension.SaveAs(destination, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings) || errors != 0)
                throw new IOException("工程图未能保存到可编辑工作区，原图及当前修改已保留。保存错误码：" + errors);
            if (!PathsEqual(model.GetPathName(), destination) || !File.Exists(destination))
                throw new IOException("工程图未完成工作路径切换，原文件已保留，请核对后重试。");
            var recovery = DrawingDraftPath.PreservePreviousCopy(node.FullPath, originalHash);
            LogOperation("New drawing moved to writable draft source=" + node.FullPath + " target=" + destination);
            LogOperation("New drawing previous disk copy preserved at=" + recovery);
            if (projectId.HasValue) RememberExplicitProjectPath(destination, projectId.Value);
        }
        RefreshTree(true);
        currentProjectId = projectId;
        taskPaneControl.SelectProject(projectId);
        var drawing = currentTree;
        if (drawing?.Kind != CadDocumentKind.Drawing) throw new InvalidOperationException("当前工程图尚未就绪，请稍后重试。");
        if (!drawing.RelatedModelDocumentId.HasValue) drawing.RelatedModelDocumentId = modelId;
        return drawing;
    }
}
