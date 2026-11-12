using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task RefreshDrawingForCheckoutAsync(CadTreeNode node, Guid projectId,
        DocumentVersionDto latest, PdmControlledFileMetadata binding)
    {
        var path = node.FullPath;
        var id = node.DocumentId.Value;
        var installed = false;
        InvalidOperationException Conflict(string reason) => new InvalidOperationException(node.FileName
            + "：本地版本 " + binding.Revision + "，PLM最新版本 " + latest.Revision?.Display + "；" + reason
            + (installed ? "。最新版已取回为只读，原副本备份已保留，尚未获取编辑权限。" : "。当前窗口、文件和修改均已保留，未覆盖文件或获取编辑权限。")
            + "未切换、保存上层装配体。");
        void RequireUnchanged(string expectedHash)
        {
            if (!string.Equals(ComputeFileHash(path), expectedHash, StringComparison.OrdinalIgnoreCase)
                || FindLoadedDocument(path)?.GetSaveFlag() == true
                || localEdits.TryGetValue(id, out var draft) && !draft.Closed)
                throw Conflict("存在本地或内存待保存状态，请先另存副本保留修改，再从工作区打开最新工程图");
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var current)
                || current.DocumentId != id || current.ProjectId != projectId || current.VersionId != binding.VersionId)
                throw Conflict("等待期间本地版本关联发生变化，请刷新后重试");
        }
        var hash = ComputeFileHash(path);
        RequireUnchanged(hash);
        var baseline = (await apiClient.GetVersionsAsync(id, lifetime.Token)).FirstOrDefault(version => version.Id == binding.VersionId);
        if (baseline == null || !VersionMatchesLocalFile(baseline, path, hash)) throw Conflict("磁盘内容与原存档基准不一致，请先另存副本保留修改，再从工作区打开最新工程图");
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId
            && pair.Value.Files.Any(file => file.DocumentId == id
                && PathsEqual(path, Path.Combine(pair.Key, file.RelativePath ?? file.FileName))))
            .OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
        if (context.Value == null) throw Conflict("当前工程图的受控引用清单尚未就绪，请从工作区打开后重试");
        if (!PathsEqual(Path.GetDirectoryName(path), context.Key)) throw Conflict("当前工程图的引用目录尚未定位到其工作文件夹，请刷新工作区后重试");
        var requested = await apiClient.CreateControlledOpenManifestAsync(id, latest.Id, false, false, lifetime.Token,
            allowMissingComponents: true);
        var drawing = requested.Files.SingleOrDefault(file => file.DocumentId == id);
        if (requested.ProjectId != projectId || requested.RootDocumentId != id || requested.RootVersionId != latest.Id
            || drawing == null || drawing.VersionId != latest.Id || !string.Equals(drawing.FileName, node.FileName, StringComparison.OrdinalIgnoreCase))
            throw Conflict("服务器返回的工程图版本或引用清单不一致");
        foreach (var reference in requested.Files.Where(file => file.DocumentId != id))
            if (!context.Value.Files.Any(file => file.DocumentId == reference.DocumentId
                && string.Equals(file.FileName, reference.FileName, StringComparison.OrdinalIgnoreCase)
                && WorkingConfigurationMatches(file, reference)))
                throw Conflict("新版工程图所需的配套图档尚未在当前工作区核对：" + reference.FileName);
        drawing.RelativePath = context.Value.Files.First(file => file.DocumentId == id
            && PathsEqual(path, Path.Combine(context.Key, file.RelativePath ?? file.FileName))).RelativePath ?? node.FileName;
        var files = context.Value.Files.Where(file => file.DocumentId != id
            && DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing).Concat(new[] { drawing }).ToList();
        var manifest = new ControlledOpenManifestDto { ProjectId = projectId, RootDocumentId = id,
            RootVersionId = latest.Id, RootRelativePath = drawing.RelativePath, RootRevision = latest.Revision?.Display, Files = files };
        ControlledOpenBoundary.RequireLoadedSources(application, path, manifest);
        var staging = Path.Combine(controlledWorkspace.WorkspaceRoot, ".uplm", "Staging", "drawing-get-" + Guid.NewGuid().ToString("N"));
        var downloaded = await apiClient.DownloadControlledOpenFileAsync(drawing, staging, lifetime.Token);
        var backup = Path.Combine(staging, "previous.bak");
        var attributes = File.GetAttributes(path);
        Exception preparationError = null;
        void Install()
        {
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
                    ValidateControlledLoadedSources(reopened, path, manifest, application, missing, allowMissingDrawingModels: false);
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
            if (pair.Value.RootDocumentId == id) { pair.Value.RootVersionId = latest.Id; pair.Value.RootRevision = latest.Revision?.Display; }
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
        ReportMissingComponentsOnOpen(missing, "已取回工程图");
    }
}
