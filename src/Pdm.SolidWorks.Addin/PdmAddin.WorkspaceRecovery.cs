using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<IReadOnlyList<DocumentDto>> ResumeWorkspaceEditsForOpenAsync(ControlledOpenManifestDto manifest)
    {
        try { return await ResumeRetainedWorkingCopyAsync(manifest); }
        catch (InvalidDataException)
        {
            var conflict = await Task.Run(() => controlledWorkspace.FindWorkingConflicts(manifest, Array.Empty<Guid>()), lifetime.Token);
            if (conflict == null) throw;
            controlledWorkspace.ProtectedPaths = CaptureLoadedCachePaths();
            var action = await taskPaneControl.ResolveWorkspaceCacheConflictAsync(conflict, false, !ConflictFilesAreLoaded(conflict),
                "原编辑会话或基准版本尚未核对，暂不能直接恢复；本地文件和草稿均已保留。", lifetime.Token);
            if (action == WorkspaceConflictAction.BackupAndGetLatest)
            {
                manifest.CacheBackup = await PreserveWorkspaceConflictAsync(manifest, conflict);
                manifest.RetainedWorkingHashes.Clear(); manifest.ResumedDocumentIds.Clear();
            }
            else manifest.WorkspaceOpenCanceled = true;
            return Array.Empty<DocumentDto>();
        }
    }

    private bool ConflictFilesAreLoaded(WorkspaceCacheConflictException conflict) => CaptureLoadedCachePaths()
        .Any(path => conflict.Files.Any(file => PathsEqual(file.Path, path)));

    private bool CanResumeWorkspaceConflict(ControlledOpenManifestDto manifest, WorkspaceCacheConflictException conflict)
        => !ConflictFilesAreLoaded(conflict) && conflict.Files.All(file =>
            file.Binding?.DocumentId == file.DocumentId && file.Binding.ProjectId == manifest.ProjectId
            && file.Binding.VersionId == file.RequiredVersionId
            && manifest.Files.Any(required => required.DocumentId == file.DocumentId && WorkingReuseBindingMatches(file.Path, manifest.ProjectId, required))
            && (editJournal == null || !editJournal.ReadDocument(file.DocumentId).Any(entry => !entry.Closed && !PathsEqual(entry.SourcePath, file.Path)))
            && (!localEdits.TryGetValue(file.DocumentId, out var edit) || edit.Closed || PathsEqual(edit.SourcePath, file.Path)));

    private async Task<WorkspaceCacheBackup> PreserveWorkspaceConflictAsync(ControlledOpenManifestDto manifest, WorkspaceCacheConflictException conflict)
    {
        controlledWorkspace.ProtectedPaths = CaptureLoadedCachePaths();
        var records = editJournal == null ? Array.Empty<LocalEditDraft>() : conflict.Files
            .SelectMany(file => editJournal.ReadDocument(file.DocumentId)).ToArray();
        var backup = await Task.Run(() =>
        {
            var result = controlledWorkspace.BackupWorkingConflicts(manifest, conflict, lifetime.Token);
            var drafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var draftHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in records.Where(entry => !string.IsNullOrWhiteSpace(entry.DraftPath)))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!File.Exists(entry.DraftPath)) continue; // Keep its record; do not claim the absent draft was recovered.
                var expected = ComputeFileHash(entry.DraftPath);
                var relative = "drafts/" + entry.SessionId.ToString("N") + "-" + entry.DocumentId.ToString("N") + Path.GetExtension(entry.DraftPath);
                var path = Path.Combine(result.RecoveryDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var input = new FileStream(entry.DraftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                if (ComputeFileHash(path) != expected || ComputeFileHash(entry.DraftPath) != expected)
                    throw new IOException("草稿备份内容发生变化，原文件和草稿均已保留，未更新工作区。");
                drafts[entry.DraftPath] = relative;
                draftHashes[entry.DraftPath] = expected;
            }
            File.WriteAllText(Path.Combine(result.RecoveryDirectory, "edit-recovery.json"),
                new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(new { records, drafts, draftHashes }), System.Text.Encoding.UTF8);
            result.PreservedEdits = records;
            return result;
        });
        LogOperation("工作区差异文件及编辑记录已备份，尚未更新原缓存：" + backup.RecoveryDirectory);
        return backup;
    }

    private void FinishBackedUpWorkspaceEdits(WorkspaceCacheBackup backup)
    {
        if (editJournal == null) return;
        foreach (var previous in backup.PreservedEdits.Where(entry => backup.Files.Any(file => PathsEqual(file.Path, entry.SourcePath))))
        {
            var current = editJournal.ReadDocument(previous.DocumentId).FirstOrDefault(entry => entry.SessionId == previous.SessionId
                && PathsEqual(entry.SourcePath, previous.SourcePath));
            if (current == null) continue;
            if (current.SavedAtUtc != previous.SavedAtUtc || current.DraftSha256 != previous.DraftSha256
                || current.BaseVersionId != previous.BaseVersionId || current.CheckedOutAt != previous.CheckedOutAt)
                throw new IOException("PLM缓存已取回，但期间产生了新的编辑恢复记录；新记录和原备份均已保留，请核对后继续。");
            current.Closed = true;
            current.RestoreOnOpen = current.RecoveryRequired = current.PendingClose = current.DiscardPending = false;
            editJournal.Save(current);
            if (localEdits.TryGetValue(current.DocumentId, out var local) && local.SessionId == current.SessionId && PathsEqual(local.SourcePath, current.SourcePath))
                localEdits.Remove(current.DocumentId);
        }
    }

    private async Task<IReadOnlyCollection<string>> RefreshWorkspaceProtectedPathsAsync()
    {
        var uiContext = taskPaneSynchronizationContext;
        if (uiContext == null || uiContext.IsCurrentThread) return CaptureLoadedCachePaths();
        var result = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.Token.Register(() => result.TrySetCanceled()))
        {
            uiContext.Post(_ =>
            {
                try { result.TrySetResult(CaptureLoadedCachePaths()); } catch (Exception error) { result.TrySetException(error); }
            }, null);
            return await result.Task;
        }
    }

    private async Task ResumeWorkspaceConflictAsync(ControlledOpenManifestDto manifest, WorkspaceCacheConflictException conflict,
        HashSet<Guid> editable, List<Guid> newCheckouts)
    {
        if (!CanResumeWorkspaceConflict(manifest, conflict)) throw new IOException("本地修改的基准或打开状态发生变化，已保留文件，请重新选择处理方式。");
        var documents = (await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token)).ToDictionary(document => document.Id);
        foreach (var file in conflict.Files)
        {
            var version = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token, 1)).FirstOrDefault();
            if (version?.Id != file.RequiredVersionId || ComputeFileHash(file.Path) != file.ActualSha256
                || !documents.TryGetValue(file.DocumentId, out var previous) || previous.ProjectId != manifest.ProjectId)
                throw new IOException("恢复期间服务器版本或本地文件发生变化，原文件已保留，未继续打开。");
            if (!string.IsNullOrWhiteSpace(previous.CheckedOutBy) && (previous.CheckoutSessionId != checkoutSessionId
                || !string.Equals(previous.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("该图档仍由其他编辑会话持有权限，原文件和编辑记录已保留，未接管该会话。");
            var created = string.IsNullOrWhiteSpace(previous.CheckedOutBy);
            plmAuthoritativeCheckouts.Add(file.DocumentId);
            DocumentDto document;
            try { document = await apiClient.CheckoutAsync(file.DocumentId, checkoutSessionId, checkoutMachineName, lifetime.Token); }
            finally { plmAuthoritativeCheckouts.Remove(file.DocumentId); }
            if (created) newCheckouts.Add(file.DocumentId);
            if (document.Id != file.DocumentId || document.ProjectId != manifest.ProjectId
                || document.CheckoutSessionId != checkoutSessionId || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                throw new IOException("服务器尚未确认恢复编辑权限，文件和原记录均已保留。");
            TrackCheckoutDocument(document);
            var node = new CadTreeNode { DocumentId = file.DocumentId, FullPath = file.Path,
                FileName = file.FileName, Kind = DocumentKindFromPath(file.Path), OpenedVersionId = file.RequiredVersionId };
            RememberLocalEdit(node, document);
            if (!localEdits.TryGetValue(file.DocumentId, out var entry) || !PathsEqual(entry.SourcePath, file.Path))
                throw new IOException("编辑恢复记录未对应当前路径，原文件已保留。");
            editJournal.Preserve(entry, file.Path, false);
            if (ComputeFileHash(file.Path) != file.ActualSha256) throw new IOException("恢复时本地修改发生变化，原文件和权限已保留，请重试。");
            manifest.RetainedWorkingHashes[file.DocumentId] = file.ActualSha256;
            manifest.ResumedDocumentIds.Add(file.DocumentId);
            editable.Add(file.DocumentId);
        }
        await RequireRetainedManifestUnchangedAsync(manifest);
    }

    private async Task<string> PrepareWorkspaceWithRecoveryAsync(ControlledOpenManifestDto manifest, HashSet<Guid> editable, List<Guid> newCheckouts)
    {
        GetTaskPaneSynchronizationContext();
        controlledWorkspace.PendingLocalEdits = editJournal == null ? Array.Empty<Guid>() : editJournal.ReadAll()
            .Where(entry => !entry.Closed || entry.RecoveryRequired || entry.RestoreOnOpen || entry.DiscardPending).Select(entry => entry.DocumentId).Distinct().ToArray();
        controlledWorkspace.RefreshProtectedPaths = RefreshWorkspaceProtectedPathsAsync;
        var choiceMade = manifest.CacheBackup != null;
        try
        {
            while (true)
            {
                try
                {
                    var path = manifest.CacheBackup != null
                        ? await controlledWorkspace.PrepareBackedUpWorkingCopyAsync(manifest, editable, manifest.CacheBackup, lifetime.Token)
                        : await controlledWorkspace.PreparePlmWorkingCopyAsync(manifest, editable, lifetime.Token);
                    if (manifest.CacheBackup != null)
                    {
                        FinishBackedUpWorkspaceEdits(manifest.CacheBackup);
                        taskPaneControl.SetProjectLoadingStatus(manifest.ProjectId,
                            "已保留原工作文件备份，并打开PLM版本；备份位置：" + manifest.CacheBackup.RecoveryDirectory);
                    }
                    return path;
                }
                catch (WorkspaceCacheConflictException conflict)
                {
                    if (choiceMade) throw new IOException("处理期间又检测到本地差异，原文件和已有备份均已保留，请重新从工作区打开后核对。");
                    var presented = manifest.RetainedWorkingHashes.Count == 0 ? conflict
                        : await Task.Run(() => controlledWorkspace.FindWorkingConflicts(manifest, editable), lifetime.Token) ?? conflict;
                    var canRestore = CanResumeWorkspaceConflict(manifest, presented);
                    var action = await taskPaneControl.ResolveWorkspaceCacheConflictAsync(presented, canRestore, !ConflictFilesAreLoaded(presented),
                        "本机基准与PLM版本不同或归属尚未确认，不能直接续接旧版本编辑。", lifetime.Token);
                    if (action == WorkspaceConflictAction.Cancel) { LogOperation("已取消工作区打开，原文件、草稿和权限均已保留。"); return null; }
                    choiceMade = true;
                    if (action == WorkspaceConflictAction.RestoreEditing) await ResumeWorkspaceConflictAsync(manifest, presented, editable, newCheckouts);
                    else
                    {
                        manifest.CacheBackup = await PreserveWorkspaceConflictAsync(manifest, presented);
                        foreach (var id in manifest.ResumedDocumentIds) editable.Remove(id);
                        manifest.RetainedWorkingHashes.Clear(); manifest.ResumedDocumentIds.Clear();
                    }
                }
            }
        }
        finally { controlledWorkspace.RefreshProtectedPaths = null; controlledWorkspace.PendingLocalEdits = Array.Empty<Guid>(); }
    }
}
