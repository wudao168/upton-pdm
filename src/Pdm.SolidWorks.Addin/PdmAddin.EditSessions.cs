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
                    BaseSha256 = metadata?.Sha256, ProcessId = process.Id, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks
                };
                editJournal.Save(entry);
                localEdits[document.Id] = entry;
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
            var pending = PrepareClosedEdits();
            await ClosePreservedEditsAsync(pending, lifetime.Token);
        }
        catch (Exception exception) { ReportEditMaintenanceError(exception); }
        finally { Interlocked.Exchange(ref editMaintenanceInProgress, 0); }
    }

    private bool CanMaintainEdits() => Volatile.Read(ref checkInOperationInProgress) == 0
        && Volatile.Read(ref workspaceOperationInProgress) == 0 && Volatile.Read(ref openOperationInProgress) == 0
        && Volatile.Read(ref controlledOpenInProgress) == 0 && Volatile.Read(ref refreshSuppressionDepth) == 0
        && Volatile.Read(ref drawingQrSyncInProgress) == 0 && Volatile.Read(ref automaticDrawingOperationInProgress) == 0
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
            SetFileReadOnly(entry.SourcePath, true);
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
                foreach (var entry in project) ReportEditMaintenanceError(exception, entry, true);
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
        if (current == null) throw new InvalidOperationException("暂时无法读取该图档的服务器状态，已保留草稿和待办记录，稍后重试；请确认项目访问权限。");
        var needsDraft = !string.Equals(entry.DiskSha256, entry.BaseSha256, StringComparison.OrdinalIgnoreCase);
        if (needsDraft && !journal.HasVerifiedDraft(entry)) throw new IOException("图档草稿未通过校验，已保留编辑状态。请保存图档后重试。");
        if (current.CheckedOutBy == null || current.CheckoutSessionId == entry.SessionId && current.CheckedOutAt?.ToUniversalTime() == entry.CheckedOutAt.ToUniversalTime() && string.Equals(current.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
            await client.CloseEditAsync(entry, needsDraft, token).ConfigureAwait(false);
        entry.PendingClose = false;
        entry.Closed = true;
        EditDraftJournal.ClearMaintenanceFailure(entry);
        journal.Save(entry);
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
        try
        {
            if (!EnsureEditJournal()) return;
            foreach (var entry in editJournal.ReadAll().Where(entry => entry.DocumentId == documentId))
            {
                entry.Closed = true;
                entry.PendingClose = false;
                entry.RestoreOnOpen = false;
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
        if (!EnsureEditJournal()) return;
        if (!localEdits.TryGetValue(documentId, out var entry) || !entry.PendingClose) return;
        await ClosePreservedEditAsync(entry, token);
        if (!entry.Closed) throw new InvalidOperationException("上次结束编辑尚未确认，请检查服务器连接后重试；本机草稿已保留。");
        localEdits.Remove(documentId);
        lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(documentId);
    }

    private void RestoreEditDraftBeforeOpen(Guid documentId, string path, Guid? latestVersionId)
    {
        if (!EnsureEditJournal()) return;
        var entry = editJournal.ReadAll().Where(item => item.DocumentId == documentId && item.RestoreOnOpen)
            .OrderByDescending(item => item.SavedAtUtc).FirstOrDefault();
        if (entry == null) return;
        var loaded = (application.GetDocuments() as Array)?.OfType<IModelDoc2>().Any(model => PathsEqual(model.GetPathName(), path)) == true;
        if (loaded)
        {
            if (entry.BaseVersionId != latestVersionId) throw new InvalidOperationException("服务器已有新版本，本地图档和草稿已保留，请关闭图档后核对并合并修改。");
            return;
        }
        if (editJournal.Restore(entry, path, latestVersionId))
            LogOperation("Recovered local edit draft document=" + documentId);
    }
}
