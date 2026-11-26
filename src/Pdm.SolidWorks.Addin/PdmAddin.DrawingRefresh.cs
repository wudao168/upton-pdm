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
    private bool IsClosedConfirmedDrawingSource(string path, Guid projectId, ControlledOpenFileDto file, string hash) =>
        controlledWorkspace != null && DocumentKindFromPath(path) == CadDocumentKind.Drawing
        && FindLoadedDocument(path) == null && (!localEdits.TryGetValue(file.DocumentId, out var draft) || draft.Closed)
        && ConfirmedArchiveSource.Matches(controlledWorkspace.WorkspaceRoot, apiClient.SourceReceiptScope, path, projectId, file, hash);

    private async Task RefreshClosedConfirmedDrawingsAsync(string directory, Guid projectId, ControlledOpenManifestDto manifest,
        ISet<Guid> drawingIds, CancellationToken token)
    {
        if (manifest.ProjectId != projectId || drawingIds.Count == 0) return;
        foreach (var file in manifest.Files.Where(file => drawingIds.Contains(file.DocumentId)))
        {
            token.ThrowIfCancellationRequested();
            var path = ControlledReferenceMaterializer.LocalPath(directory, file);
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
                || binding.ProjectId != projectId || binding.DocumentId != file.DocumentId || binding.VersionId == file.VersionId
                || !IsClosedConfirmedDrawingSource(path, projectId, file, ComputeFileHash(path))) continue;
            var latest = (await apiClient.GetVersionsAsync(file.DocumentId, token, 1)).FirstOrDefault();
            if (latest?.Id != file.VersionId) throw new InvalidOperationException("核对存档结果期间工程图版本已变化，请重新获取：" + file.FileName);
            var node = new CadTreeNode { DocumentId = file.DocumentId, FullPath = path, FileName = file.FileName, Kind = CadDocumentKind.Drawing };
            await RefreshDrawingForCheckoutAsync(node, projectId, latest, binding);
        }
    }

    private async Task RefreshLinkedDrawingBeforeArchiveAsync(CadTreeNode drawing, Guid projectId, CancellationToken token)
    {
        if (!drawing.DocumentId.HasValue || !PdmDocumentIdentityStore.TryReadLocalMetadata(drawing.FullPath, out var binding)
            || binding.DocumentId != drawing.DocumentId || binding.ProjectId != projectId) return;
        var latest = (await apiClient.GetVersionsAsync(drawing.DocumentId.Value, token, 1)).FirstOrDefault();
        if (latest == null || latest.Id == binding.VersionId) return;
        // A just-committed in-session drawing can still have the preceding disk binding.
        if (WorkingReuseCommittedMemoryMatches(drawing.FullPath, new ControlledOpenFileDto
        {
            DocumentId = drawing.DocumentId.Value, VersionId = latest.Id, Sha256 = latest.Sha256, FileLength = latest.FileLength
        })) return;
        await RefreshDrawingForCheckoutAsync(drawing, projectId, latest, binding);
    }

    private async Task RefreshDrawingForCheckoutAsync(CadTreeNode node, Guid projectId,
        DocumentVersionDto latest, PdmControlledFileMetadata binding)
    {
        var path = node.FullPath;
        var id = node.DocumentId.Value;
        var installed = false;
        var requireClosed = false;
        var baseline = (await apiClient.GetVersionsAsync(id, lifetime.Token)).FirstOrDefault(version => version.Id == binding.VersionId);
        InvalidOperationException Conflict(string reason) => new InvalidOperationException(node.FileName
            + "：本地图纸版本 " + (baseline?.DrawingVersion ?? "待核对") + "，PLM最新图纸版本 " + (latest.DrawingVersion ?? "待核对") + "；" + reason
            + (installed ? "。最新版已取回为只读，原副本备份已保留，尚未获取编辑权限。" : "。当前窗口、文件和修改均已保留，未覆盖文件或获取编辑权限。")
            + "未切换、保存上层装配体。");
        void RequireUnchanged(string expectedHash)
        {
            if (requireClosed && FindLoadedDocument(path) != null)
                throw Conflict("核对期间旧工程图已重新打开，请关闭后再获取");
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var current)
                || current.DocumentId != id || current.ProjectId != projectId || current.VersionId != binding.VersionId)
                throw Conflict("等待期间本地版本关联发生变化，请刷新后重试");
            var hashMatches = string.Equals(ComputeFileHash(path), expectedHash, StringComparison.OrdinalIgnoreCase);
            var dirty = FindLoadedDocument(path)?.GetSaveFlag() == true;
            var hasDraft = localEdits.TryGetValue(id, out var draft) && !draft.Closed;
            if (hashMatches && !dirty && !hasDraft) return;
            if (hashMatches && dirty && !hasDraft && WorkingReuseCommittedMemoryMatches(path, new ControlledOpenFileDto
                { DocumentId = id, VersionId = latest.Id, Sha256 = latest.Sha256, FileLength = latest.FileLength }))
                throw Conflict("当前窗口内容已存档，仍保留上次存档的保存标记。请关闭工程图，再从工作区打开最新并获取。若关闭时有保存提示，请在确认没有新增修改后选择“不保存”");
            throw Conflict("存在本地或内存待保存状态，请先另存副本保留修改，再从工作区打开最新工程图");
        }
        var hash = ComputeFileHash(path);
        RequireUnchanged(hash);
        requireClosed = IsClosedConfirmedDrawingSource(path, projectId, new ControlledOpenFileDto
            { DocumentId = id, VersionId = latest.Id, Sha256 = latest.Sha256, FileLength = latest.FileLength }, hash);
        if (!requireClosed && (baseline == null || !VersionMatchesLocalFile(baseline, path, hash)))
            throw Conflict("磁盘内容与原存档基准不一致，请先另存副本保留修改，再从工作区打开最新工程图");
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId
            && pair.Value.Files.Any(file => file.DocumentId == id
                && PathsEqual(path, Path.Combine(pair.Key, file.RelativePath ?? file.FileName))))
            .OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
        if (context.Value == null) throw Conflict("当前工程图的受控引用清单尚未就绪，请从工作区打开后重试");
        if (!PathsEqual(NormalizeDirectory(Path.GetDirectoryName(path)), NormalizeDirectory(context.Key)))
            throw Conflict("当前工程图的引用目录尚未定位到其工作文件夹，请刷新工作区后重试");
        var requested = await apiClient.CreateControlledOpenManifestAsync(id, latest.Id, false, false, lifetime.Token,
            allowMissingComponents: true);
        var drawing = requested.Files.SingleOrDefault(file => file.DocumentId == id);
        if (requested.ProjectId != projectId || requested.RootDocumentId != id || requested.RootVersionId != latest.Id
            || drawing == null || drawing.VersionId != latest.Id || !string.Equals(drawing.FileName, node.FileName, StringComparison.OrdinalIgnoreCase))
            throw Conflict("服务器返回的工程图版本或引用清单不一致");
        var unavailable = new HashSet<Guid>();
        foreach (var reference in requested.Files.Where(file => file.DocumentId != id))
        {
            var local = context.Value.Files.FirstOrDefault(file => file.DocumentId == reference.DocumentId);
            if (context.Value.Files.Any(file => file.DocumentId != reference.DocumentId
                && string.Equals(file.FileName, reference.FileName, StringComparison.OrdinalIgnoreCase))
                || local != null && (!string.Equals(local.FileName, reference.FileName, StringComparison.OrdinalIgnoreCase)
                    || !WorkingConfigurationMatches(local, reference)))
                throw Conflict("当前工作区存在与工程图引用身份或配置冲突的文件：" + reference.FileName);
            if (local != null && File.Exists(ControlledReferenceMaterializer.ManifestPath(context.Key, local))) continue;
            var loadedByName = application.GetOpenDocumentByName(reference.FileName) as IModelDoc2;
            if (loadedByName != null) throw Conflict("已打开的同名模型无法核对来源：" + reference.FileName);
            unavailable.Add(reference.DocumentId);
            RecordMissingDrawingModel(requested, reference);
        }
        drawing.RelativePath = context.Value.Files.First(file => file.DocumentId == id
            && PathsEqual(path, ControlledReferenceMaterializer.ManifestPath(context.Key, file))).RelativePath ?? node.FileName;
        var files = context.Value.Files.Where(file => file.DocumentId != id && !unavailable.Contains(file.DocumentId)
            && DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing).Concat(new[] { drawing }).ToList();
        var manifest = new ControlledOpenManifestDto { ProjectId = projectId, RootDocumentId = id,
            RootVersionId = latest.Id, RootRelativePath = drawing.RelativePath, RootRevision = latest.Revision?.Display, Files = files,
            Warnings = requested.Warnings, ReferenceIssues = OpenReferenceFindings.Merge(requested.ReferenceIssues) };
        ControlledOpenBoundary.RequireLoadedSources(application, path, manifest);
        var stagingId = Guid.NewGuid().ToString("N");
        const string backupName = "previous.bak";
        var staging = ManagedCadPaths.ChooseStagingDirectory(
            Path.Combine(controlledWorkspace.WorkspaceRoot, ".uplm", "Staging", "drawing-get-" + stagingId),
            stagingId, new[] { drawing.RelativePath, backupName });
        var downloaded = await apiClient.DownloadControlledOpenFileAsync(drawing, staging, lifetime.Token);
        var backup = ManagedCadPaths.Resolve(staging, backupName);
        var attributes = File.GetAttributes(path);
        Exception preparationError = null;
        void Install()
        {
            if (requireClosed) RequireUnchanged(hash);
            if (!string.Equals(ComputeFileHash(path), hash, StringComparison.OrdinalIgnoreCase)) throw Conflict("取回期间磁盘内容发生变化");
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            if (!string.Equals(ComputeFileHash(backup), hash, StringComparison.OrdinalIgnoreCase)) throw Conflict("原副本备份未通过校验");
            try
            {
                SetFileReadOnly(path, false);
                File.Copy(downloaded, path, true);
                if (!PdmDocumentIdentityStore.TryWriteControlledVersion(path, id, projectId, latest.Id, drawing.Revision, drawing.Sha256, drawing.FileLength))
                    throw new IOException("工程图版本记录未写入。");
                if ((drawing.NativeIsolation != null || drawing.IsNativeIsolated)
                    && (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var isolated)
                        || !PdmDocumentIdentityStore.TryRecordNativeIsolation(path, isolated,
                            drawing.NativeIsolation?.ReferenceSnapshotId ?? latest.Id, ComputeFileHash(path))))
                    throw new IOException("工程图已有的独立副本来源记录未写入。");
                SetFileReadOnly(path, true);
                ControlledReferenceMaterializer.PrepareDrawingOnly(application, path, manifest);
                installed = true;
            }
            catch
            {
                SetFileReadOnly(path, false);
                File.Copy(backup, path, true);
                File.SetAttributes(path, attributes);
                PdmDocumentIdentityStore.RestoreLocalMetadata(path, binding);
                throw;
            }
        }
        RequireUnchanged(hash);
        var loaded = FindLoadedDocument(path);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var search = new ControlledReferenceSearchScope(application, path, manifest))
        {
            if (loaded == null) Install();
            else
            {
                var events = application as DSldWorksEvents_Event;
                if (events == null) throw Conflict("SolidWorks工程图重载事件接口暂不可用，请稍后重试");
                DSldWorksEvents_FileCloseNotifyEventHandler prepare = (fileName, reason) =>
                {
                    if (installed || preparationError != null || reason != (int)swFileCloseNotifyReason_e.swFileCloseNotifyReason_CloseForReload || !PathsEqual(fileName, path)) return 0;
                    try { Install(); } catch (Exception error) { preparationError = error; }
                    return 0;
                };
                events.FileCloseNotify += prepare;
                try
                {
                    // No DiscardChanges flag: SolidWorks must refuse a newly modified drawing.
                    var error = application.CloseAndReopen2(loaded,
                        (int)(swCloseReopenOption_e.swCloseReopenOption_ReadOnly | swCloseReopenOption_e.swCloseReopenOption_MatchSheet), out var reopenedObject);
                    var reopened = reopenedObject as IModelDoc2;
                    if (preparationError != null)
                    {
                        LogDiagnostic("工程图取回准备失败；备份位置=" + backup, preparationError);
                        throw new IOException("工程图最新版准备未完成，未获取编辑权限。请保留当前文件，核对诊断记录后重试。", preparationError);
                    }
                    if (error != 0 || !installed || reopened == null || !PathsEqual(reopened.GetPathName(), path))
                        throw Conflict("工程图最新版重载尚未完成，原副本备份已保留");
                    if (!(reopened is IDrawingDoc)) throw new IOException("SolidWorks返回的文件不是工程图，未获取权限。");
                    ValidateControlledLoadedSources(reopened, path, manifest, application, missing, allowMissingDrawingModels: true);
                }
                finally { events.FileCloseNotify -= prepare; }
            }
            search.VerifyLoadedSources(missing);
        }
        if (!ControlledReferenceMaterializer.Matches(path, drawing)) throw Conflict("工程图最新版的本地内容尚未核验完成");
        foreach (var pair in controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId))
        {
            var index = pair.Value.Files.FindIndex(file => file.DocumentId == id
                && PathsEqual(path, Path.Combine(pair.Key, file.RelativePath ?? file.FileName)));
            if (index < 0) continue;
            pair.Value.Files[index] = drawing;
            if (pair.Value.RootDocumentId == id)
            {
                pair.Value.RootVersionId = latest.Id;
                pair.Value.RootRevision = latest.Revision?.Display;
                pair.Value.ReferenceIssues = OpenReferenceFindings.Merge(manifest.ReferenceIssues);
                pair.Value.Warnings = manifest.Warnings;
            }
        }
        node.OpenedVersionId = latest.Id;
        node.CurrentRevision = latest.Revision?.Display;
        node.IsModifiedInSolidWorks = false;
        ControlledReferenceMaterializer.RememberVerifiedOpen(path, manifest);
        try
        {
            File.Delete(backup);
            File.Delete(downloaded);
            if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging, false);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        { LogDiagnostic("清理工程图取回临时副本", error); }
        LogOperation("工程图取回最新版完成；document=" + id + "；old=" + binding.Revision + "；latest=" + latest.Revision?.Display + "；模型保存=0；模型切换=0");
        foreach (var issue in manifest.ReferenceIssues.Where(issue => !OpenReferenceFindings.IsToolboxDiagnostic(issue) && !string.IsNullOrWhiteSpace(issue.FileName)))
            missing.Add(issue.FileName);
        if (manifest.Warnings?.Count > 0) LogOperation("工程图取回提醒：" + string.Join("；", manifest.Warnings));
        ReportMissingComponentsOnOpen(missing, "已取回工程图", path);
    }
}
