using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private EditDraftJournal editJournal;
    private string editJournalScope;
    private readonly Dictionary<Guid, LocalEditDraft> localEdits = new Dictionary<Guid, LocalEditDraft>();
    private readonly HashSet<Guid> pendingCloseDocumentIds = new HashSet<Guid>();
    private readonly HashSet<Guid> shownEditRequests = new HashSet<Guid>();
    private DateTime nextEditMaintenanceUtc = DateTime.MinValue;
    private int editMaintenanceInProgress;
    private string lastEditMaintenanceError;
    private int draftSnapshotInProgress;
    private Func<bool> draftIdleCheck = UserIdleForDraft;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastDraftInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastDraftInputInfo input);

    private static bool UserIdleForDraft()
    {
        var input = new LastDraftInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastDraftInputInfo)) };
        return GetLastInputInfo(ref input) && unchecked((uint)System.Environment.TickCount - input.Time) >= 5000;
    }

    private bool EnsureEditJournal()
    {
        if (apiClient == null || !apiClient.IsAuthenticated || string.IsNullOrWhiteSpace(authenticatedUsername)) return false;
        var scope = apiClient.EditJournalScope + "|" + authenticatedUsername.ToLowerInvariant();
        if (scope == editJournalScope) return true;
        editJournalScope = scope;
        editJournal = new EditDraftJournal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "edit-drafts"), scope);
        localEdits.Clear();
        foreach (var entry in editJournal.ReadAll().Where(entry => entry.SessionId == checkoutSessionId && !entry.Closed)) localEdits[entry.DocumentId] = entry;
        return true;
    }

    private void RememberLocalEdit(CadTreeNode node, DocumentDto document)
    {
        if (document.CheckoutSessionId != checkoutSessionId || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath)) return;
        try
        {
            if (!EnsureEditJournal()) return;
            if (localEdits.TryGetValue(document.Id, out var existing) && !existing.Closed && existing.CheckedOutAt == document.CheckedOutAt) return;
            PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata);
            using (var process = Process.GetCurrentProcess())
            {
                var entry = new LocalEditDraft
                {
                    DocumentId = document.Id, ProjectId = document.ProjectId, SessionId = checkoutSessionId, CheckedOutAt = document.CheckedOutAt ?? DateTime.MinValue,
                    SourcePath = Path.GetFullPath(node.FullPath), BaseVersionId = metadata?.VersionId,
                    BaseSha256 = !string.IsNullOrWhiteSpace(metadata?.ResolvedReferenceSha256) ? metadata.ResolvedReferenceSha256 : metadata?.Sha256,
                    ProcessId = process.Id, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks
                };
                editJournal.Save(entry);
                localEdits[document.Id] = entry;
                editIdleDeferredUntil[document.Id] = DateTime.UtcNow.AddMinutes(idleReleaseMinutes);
                lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(document.Id);
            }
        }
        catch (Exception exception) { ReportEditMaintenanceError(exception); }
    }

    // HTTP heartbeats do not depend on SolidWorks becoming idle. All COM and UI work remains on its UI thread.
    private async Task MonitorEditHeartbeatAsync()
    {
        var token = lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                TryStartCheckoutHeartbeat();
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { LogDiagnostic("Edit heartbeat monitor", exception); }
        }
    }

    private async void TryMaintainEdits()
    {
        if (disconnecting || DateTime.UtcNow < nextEditMaintenanceUtc || !CanMaintainEdits()
            || Interlocked.Exchange(ref editMaintenanceInProgress, 1) != 0) return;
        nextEditMaintenanceUtc = DateTime.UtcNow.AddSeconds(10);
        try
        {
            if (!EnsureEditJournal()) return;
            if (ArchiveBatchJournal.Recoverable(apiClient.EditJournalScope, authenticatedUsername).Any())
            {
                if (DateTime.UtcNow < nextArchiveBatchRecoveryUtc) return;
                nextArchiveBatchRecoveryUtc = DateTime.UtcNow.AddSeconds(30);
                if (!await ReconcileArchiveBatchesAsync()) return;
            }
            var pending = PrepareClosedEdits();
            TryOfferIdleRelease();
            await ClosePreservedEditsAsync(pending, lifetime.Token);
        }
        catch (Exception exception) { ReportEditMaintenanceError(exception); }
        finally { Interlocked.Exchange(ref editMaintenanceInProgress, 0); }
    }

    private bool CanMaintainEdits() => Volatile.Read(ref checkInOperationInProgress) == 0
        && Volatile.Read(ref workspaceOperationInProgress) == 0 && Volatile.Read(ref openOperationInProgress) == 0
        && Volatile.Read(ref controlledOpenInProgress) == 0 && Volatile.Read(ref refreshSuppressionDepth) == 0
        && Volatile.Read(ref automaticDrawingOperationInProgress) == 0
        && Volatile.Read(ref batchDialogInProgress) == 0;

    private IReadOnlyList<LocalEditDraft> PrepareClosedEdits()
    {
        if (IsSolidWorksCommandActive()) return Array.Empty<LocalEditDraft>();
        // Includes invisible component documents still used by an open assembly.
        var loaded = (application.GetDocuments() as Array)?.OfType<IModelDoc2>()
            .Where(model => !string.IsNullOrWhiteSpace(model.GetPathName()))
            .GroupBy(model => Path.GetFullPath(model.GetPathName()), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<LocalEditDraft>();
        foreach (var entry in localEdits.Values.ToArray())
        {
            var closing = false;
            try
            {
                if (entry.Closed) continue;
                if (loaded.TryGetValue(entry.SourcePath, out var model))
                {
                    if (entry.PendingClose) continue;
                    if (entry.RecoveryRequired)
                    {
                        EditDraftJournal.ClearMaintenanceFailure(entry);
                        editJournal.Save(entry);
                    }
                    var dirty = model.GetSaveFlag();
                    if (!entry.ObservedOpen || entry.UnsavedChanges != dirty)
                    {
                        entry.ObservedOpen = true;
                        entry.UnsavedChanges = dirty;
                        editJournal.Save(entry);
                    }
                    if (dirty && DateTime.UtcNow >= entry.NextSnapshotAttemptUtc
                        && DateTime.UtcNow - entry.SavedAtUtc >= TimeSpan.FromMinutes(1)
                        && Volatile.Read(ref pendingTreeRefresh) == 0 && Volatile.Read(ref treeRefreshInProgress) == 0 && draftIdleCheck())
                    {
                        string copy = null;
                        // Record the attempt before COM work: a locked source must not trigger
                        // SaveAs on every ten-second maintenance tick or immediately after a crash.
                        entry.NextSnapshotAttemptUtc = DateTime.UtcNow.AddMinutes(1);
                        editJournal.Save(entry);
                        try
                        {
                            Interlocked.Increment(ref refreshSuppressionDepth);
                            Interlocked.Increment(ref draftSnapshotInProgress);
                            copy = CreateCheckInUploadCopy(model, entry.SourcePath, entry.DocumentId);
                            editJournal.Preserve(entry, copy, true);
                            EditDraftJournal.ClearMaintenanceFailure(entry);
                            editJournal.Save(entry);
                            PluginDiagnosticLog.Record("草稿", "已保留本机自动草稿，尚未提交存档：" + Path.GetFileName(entry.SourcePath));
                        }
                        finally
                        {
                            try
                            {
                                // Preserve the dirty flag even when writing/verifying the backup failed.
                                model.SetSaveFlag();
                                DeleteCheckInUploadCopy(copy);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref draftSnapshotInProgress);
                                Interlocked.Decrement(ref refreshSuppressionDepth);
                            }
                        }
                    }
                    continue;
                }
                if (!entry.ObservedOpen && !entry.PendingClose) continue;
                if (DateTime.UtcNow < entry.NextMaintenanceAttemptUtc) continue;
                closing = true;
                PrepareClosedEdit(entry, false);
                pending.Add(entry);
            }
            catch (Exception exception) { ReportEditMaintenanceError(exception, entry, closing); }
        }
        foreach (var entry in editJournal.ReadAll().Where(entry => entry.SessionId != checkoutSessionId && !entry.Closed
            && DateTime.UtcNow >= entry.NextMaintenanceAttemptUtc))
        {
            try
            {
                if (EditDraftJournal.OwnerProcessRunning(entry) || loaded.ContainsKey(entry.SourcePath)) continue;
                // A crash may have left newer changes only in memory. Keep the draft and require recovery first.
                if (entry.UnsavedChanges && !entry.PendingClose) continue;
                PrepareClosedEdit(entry, true);
                pending.Add(entry);
            }
            catch (Exception exception) { ReportEditMaintenanceError(exception, entry, true); }
        }
        return pending;
    }

    private void PrepareClosedEdit(LocalEditDraft entry, bool recovered)
    {
        if (!entry.PendingClose)
        {
            var diskHash = EditDraftJournal.Hash(entry.SourcePath);
            if (!string.Equals(diskHash, entry.BaseSha256, StringComparison.OrdinalIgnoreCase))
                editJournal.Preserve(entry, entry.SourcePath, true);
            else
            {
                entry.DiskSha256 = diskHash;
                entry.RestoreOnOpen = false;
            }
            entry.UnsavedChanges = false;
            entry.PendingClose = true;
            editJournal.Save(entry); // The retry record is durable before removing the heartbeat.
        }
        lock (checkoutDocumentSync)
        {
            if (!recovered) pendingCloseDocumentIds.Add(entry.DocumentId);
            activeCheckoutDocumentIds.Remove(entry.DocumentId);
        }
    }

    private async Task ClosePreservedEditAsync(LocalEditDraft entry, CancellationToken token)
    {
        var current = (await apiClient.GetDocumentsAsync(entry.ProjectId, token).ConfigureAwait(false))
            .FirstOrDefault(document => document.Id == entry.DocumentId);
        await ClosePreservedEditWithDocumentAsync(entry, current, token).ConfigureAwait(false);
    }

    private async Task ClosePreservedEditsAsync(IReadOnlyList<LocalEditDraft> entries, CancellationToken token)
    {
        foreach (var project in entries.Where(entry => !entry.Closed && DateTime.UtcNow >= entry.NextMaintenanceAttemptUtc).GroupBy(entry => entry.ProjectId))
        {
            List<DocumentDto> documents;
            try { documents = await apiClient.GetDocumentsAsync(project.Key, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var groupEntries = project.ToArray();
                if (groupEntries.Length > 0)
                {
                    ReportEditMaintenanceError(exception, groupEntries[0], true);
                    var key = exception.GetType().FullName + ":" + exception.HResult + ":" + exception.Message;
                    var missing = exception is FileNotFoundException || exception is DirectoryNotFoundException;
                    foreach (var entry in groupEntries.Skip(1)) editJournal.DelayMaintenance(entry, key, missing, DateTime.UtcNow);
                }
                if (exception is System.Net.Http.HttpRequestException or TaskCanceledException or PdmApiClient.ServiceUnavailableException)
                    return;
                continue;
            }
            var byId = documents.ToDictionary(document => document.Id);
            foreach (var entry in project)
            {
                try
                {
                    byId.TryGetValue(entry.DocumentId, out var current);
                    await ClosePreservedEditWithDocumentAsync(entry, current, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { ReportEditMaintenanceError(exception, entry, true); }
            }
        }
    }

    private async Task ClosePreservedEditWithDocumentAsync(LocalEditDraft entry, DocumentDto current, CancellationToken token)
    {
        var journal = editJournal;
        var client = apiClient;
        var username = authenticatedUsername;
        var needsDraft = !string.Equals(entry.DiskSha256, entry.BaseSha256, StringComparison.OrdinalIgnoreCase);
        if (needsDraft && !journal.HasVerifiedDraft(entry)) throw new IOException("图档草稿未通过校验，已保留编辑状态。请保存图档后重试。");
        if (entry.UnsavedChanges && !journal.HasVerifiedDraft(entry))
            throw new IOException("图档仍有未保存修改且本机草稿未通过校验，已保留编辑状态。请打开原图档保存后重试。");
        if (current == null)
        {
            if (entry.BaseVersionId.HasValue)
                throw new InvalidOperationException("暂时无法读取该图档的服务器状态，已保留草稿和待办记录，稍后重试；请确认项目访问权限。");
            // A canceled first-archive reservation has no formal server document.
            // Its source and verified draft remain untouched and editable.
            entry.PendingClose = false;
            entry.Closed = true;
            EditDraftJournal.ClearMaintenanceFailure(entry);
            journal.Save(entry);
            return;
        }
        if (current.CheckedOutBy == null || current.CheckoutSessionId == entry.SessionId && current.CheckedOutAt?.ToUniversalTime() == entry.CheckedOutAt.ToUniversalTime() && string.Equals(current.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
            await client.CloseEditAsync(entry, needsDraft, token).ConfigureAwait(false);
        entry.PendingClose = false;
        entry.Closed = true;
        EditDraftJournal.ClearMaintenanceFailure(entry);
        journal.Save(entry);
        if (entry.BaseVersionId.HasValue)
        {
            try { SetFileReadOnly(entry.SourcePath, true); }
            catch (Exception error) { LogDiagnostic("结束编辑后设置本地只读状态：" + entry.SourcePath, error); }
        }
    }

    private void FinishClosedEditsBeforeDisconnect()
    {
        if (!CanMaintainEdits() || Volatile.Read(ref editMaintenanceInProgress) != 0 || !EnsureEditJournal()) return;
        try
        {
            var pending = PrepareClosedEdits();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                ClosePreservedEditsAsync(pending, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) { LogDiagnostic("Close edits on disconnect (will retry after login)", exception); }
    }

    private void ReportEditMaintenanceError(Exception exception, LocalEditDraft entry = null, bool delayRetry = false)
    {
        var errorKey = exception.GetType().FullName + ":" + exception.HResult + ":" + exception.Message;
        var missingSource = exception is FileNotFoundException || exception is DirectoryNotFoundException;
        if (entry != null)
        {
            var changed = !string.Equals(entry.MaintenanceErrorKey, errorKey, StringComparison.Ordinal);
            if (delayRetry) changed = editJournal.DelayMaintenance(entry, errorKey, missingSource, DateTime.UtcNow);
            else
            {
                entry.MaintenanceErrorKey = errorKey;
                editJournal.Save(entry);
            }
            if (!changed) return;
        }
        else
        {
            if (lastEditMaintenanceError == errorKey) return;
            lastEditMaintenanceError = errorKey;
        }
        LogDiagnostic("Edit draft/close", exception);
        var sharedFileBusy = exception is IOException && ((exception.HResult & 0xffff) == 32 || (exception.HResult & 0xffff) == 33);
        taskPaneControl?.SetCheckoutReminder(missingSource
            ? "原图档路径暂不可访问，已暂停频繁重试并保留编辑记录，未自动结束编辑。请找回原路径文件或检查本机草稿；系统稍后会重新检查。"
            : sharedFileBusy
            ? "图档暂时被占用，自动草稿已延后重试。原有草稿、未保存修改和编辑权限均保留，可继续编辑或手动保存。"
            : "自动保存草稿或结束编辑尚未完成，系统会重试。请保留本机文件，检查服务器连接和磁盘空间；仍需编辑可重新打开图档。", true);
    }

    private void FinishLocalEdit(Guid documentId)
    {
        shownEditRequests.Remove(documentId);
        try
        {
            if (!EnsureEditJournal()) return;
            foreach (var entry in editJournal.ReadAll().Where(entry => entry.DocumentId == documentId))
            {
                entry.Closed = true;
                entry.PendingClose = false;
                entry.RestoreOnOpen = false;
                entry.DiscardPending = false;
                EditDraftJournal.ClearMaintenanceFailure(entry);
                editJournal.Save(entry);
            }
            localEdits.Remove(documentId);
            lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(documentId);
        }
        catch (Exception exception) { ReportEditMaintenanceError(exception); }
    }

    private async Task FinishPendingCloseBeforeCheckoutAsync(Guid documentId, CancellationToken token)
    {
        if (plmAuthoritativeCheckouts.Contains(documentId)) return;
        if (!EnsureEditJournal()) return;
        if (editJournal.ReadAll().Any(item => item.DocumentId == documentId && item.DiscardPending))
            throw new InvalidOperationException("该图档已放弃编辑但工作文件尚未恢复，请从PLM重新打开后再获取权限；已放弃的修改不会重新提交。");
        if (!localEdits.TryGetValue(documentId, out var entry) || !entry.PendingClose) return;
        await ClosePreservedEditAsync(entry, token);
        if (!entry.Closed) throw new InvalidOperationException("上次结束编辑尚未确认，请检查服务器连接后重试；本机草稿已保留。");
        localEdits.Remove(documentId);
        lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(documentId);
    }

    private void RestoreEditDraftBeforeOpen(Guid documentId, string path, ControlledOpenFileDto latest)
    {
        if (!EnsureEditJournal()) return;
        var entry = editJournal.ReadAll().Where(item => item.DocumentId == documentId && item.RestoreOnOpen && !item.DiscardPending)
            .OrderByDescending(item => item.SavedAtUtc).FirstOrDefault();
        if (entry == null) return;
        if (latest.DocumentId != documentId) throw new InvalidOperationException("草稿与受控图档标识不一致，已停止恢复并保留文件。");
        var openDocuments = (application.GetDocuments() as Array)?.OfType<IModelDoc2>().ToArray() ?? Array.Empty<IModelDoc2>();
        var changedPath = !PathsEqual(entry.SourcePath, path);
        if (changedPath && openDocuments.Any(model => PathsEqual(model.GetPathName(), entry.SourcePath) && model.GetSaveFlag()))
            throw new InvalidOperationException("原工作文件仍有未保存修改，请先保存并关闭该工作文件再获取；文件及草稿均已保留。");
        var loaded = openDocuments.Any(model => PathsEqual(model.GetPathName(), path));
        if (loaded)
        {
            if (changedPath) throw new InvalidOperationException("目标工作文件已经打开，两处工作文件及草稿均已保留，请先核对并关闭目标文件后重试。");
            if (!EditDraftJournal.MatchesBase(entry, latest.VersionId, latest.Sha256)) throw new InvalidOperationException("服务器已有内容不同的新版本，本地图档和草稿已保留，请核对并合并修改。");
        }
        else
        {
            var verifiedBase = ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, latest, out var verifiedHash)
                ? verifiedHash : null;
            LogOperation("草稿恢复核对 document=" + documentId + " path=" + path + " currentHash=" + verifiedHash
                + " recordedDiskHash=" + entry.DiskSha256 + " draftHash=" + entry.DraftSha256
                + " archivedHash=" + latest.Sha256 + " verifiedControlledBase=" + (verifiedBase != null));
            if (editJournal.RestoreWithVerifiedBase(entry, path, latest.VersionId, latest.Sha256, verifiedBase))
                LogOperation("Recovered local edit draft document=" + documentId);
        }
        if (entry.BaseVersionId != latest.VersionId || changedPath)
        {
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(path, documentId, entry.ProjectId,
                latest.VersionId, latest.Revision, latest.Sha256, latest.FileLength))
                throw new IOException("草稿已保留，但未能更新等内容版本的基准记录，请稍后重试。");
            entry.BaseVersionId = latest.VersionId;
            entry.BaseSha256 = latest.Sha256;
            entry.SourcePath = Path.GetFullPath(path);
            entry.DiskSha256 = EditDraftJournal.Hash(path);
            editJournal.Save(entry);
            LogOperation("Draft base advanced without content changes document=" + documentId + " version=" + latest.VersionId);
        }
    }
}
