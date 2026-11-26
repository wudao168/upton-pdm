using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private Task<bool> ConfirmReferenceVersionsAsync(string reason)
    {
        var result = new TaskCompletionSource<bool>();
        GetTaskPaneSynchronizationContext().Post(_ =>
        {
            try
            {
                if (disconnecting) { result.TrySetResult(false); return; }
                result.TrySetResult(TopMostMessageBox.Show(taskPaneControl, reason
                    + "\r\n\r\n是否采用最新受控子件？确认后请核对装配及配合，并通过存档记录引用更新。",
                    "确认引用版本", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes);
            }
            catch (Exception error) { result.TrySetException(error); }
        }, null);
        return result.Task;
    }

    private async Task<DocumentDto> AcquireRecordedCheckoutAsync(Guid id, Guid projectId, string path, bool newlyAcquired)
    {
        LocalEditDraft entry = null;
        if (newlyAcquired)
        {
            if (!EnsureEditJournal()) throw new IOException("编辑恢复记录尚未准备完成，未申请编辑权限。");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata);
                entry = new LocalEditDraft { DocumentId = id, ProjectId = projectId, SessionId = checkoutSessionId,
                    SourcePath = path, BaseVersionId = metadata?.VersionId,
                    BaseSha256 = !string.IsNullOrWhiteSpace(metadata?.ResolvedReferenceSha256) ? metadata.ResolvedReferenceSha256 : metadata?.Sha256,
                    AcquisitionBaseSha256 = EditDraftJournal.Hash(path), AcquisitionRollbackPending = true,
                    ProcessId = process.Id, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks };
                editJournal.Save(entry); // Write intent before the request: a lost response is recoverable.
                localEdits[id] = entry;
            }
        }
        var receiptTimer = System.Diagnostics.Stopwatch.StartNew();
        var document = await apiClient.CheckoutAsync(id, checkoutSessionId, checkoutMachineName, lifetime.Token);
        LogOperation("获取权限耗时，回执已返回主线程，图档=" + id + "，耗时毫秒=" + receiptTimer.ElapsedMilliseconds);
        receiptTimer.Restart();
        if (entry != null)
        {
            entry.CheckedOutAt = document.CheckedOutAt ?? DateTime.MinValue;
            editJournal.Save(entry);
        }
        TrackCheckoutDocument(document);
        LogOperation("获取权限耗时，回执及本机编辑记录已登记，图档=" + id + "，耗时毫秒=" + receiptTimer.ElapsedMilliseconds);
        return document;
    }

    private void CompleteRecordedAcquisitions(IEnumerable<Guid> ids)
    {
        foreach (var id in ids.Distinct())
            if (localEdits.TryGetValue(id, out var entry) && entry.AcquisitionRollbackPending)
            {
                entry.AcquisitionRollbackPending = false;
                editJournal.Save(entry);
            }
    }

    private async Task EnsureCheckedOutDocumentEditableAsync(string path, Guid id, Guid projectId, DateTime? checkedOutAt)
    {
        var model = FindLoadedDocument(path);
        if (TryUnlockAndObserveCheckout(model, path, id, projectId)) return;

        var session = checkoutSessionId;
        var account = authenticatedUsername;
        var client = apiClient;
        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var original)
            || original.ProjectId != projectId || original.DocumentId != id || !original.VersionId.HasValue)
            throw new IOException("编辑权限已取得，但本地工作副本的版本关联需要核对：" + Path.GetFileName(path));

        // Yield the SolidWorks UI thread before retrying its native write lock.
        // This does not reload the document or clear any in-memory changes.
        await Task.Delay(350, lifetime.Token);
        var current = await client.GetDocumentAsync(id, lifetime.Token);
        if (disconnecting || !ReferenceEquals(client, apiClient) || session != checkoutSessionId
            || !string.Equals(account, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("登录或编辑会话已变化，当前图档和修改已保留，请重新核对权限。");
        if (current == null || current.Id != id || current.ProjectId != projectId
            || current.CheckoutSessionId != session || !string.Equals(current.CheckedOutBy, account, StringComparison.OrdinalIgnoreCase)
            || checkedOutAt.HasValue && current.CheckedOutAt != checkedOutAt
            || current.CheckoutLeaseExpiresAt.HasValue && current.CheckoutLeaseExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("服务器编辑权限已变化，当前图档和修改已保留，请刷新后重新获取。");
        if (!ReferenceEquals(model, FindLoadedDocument(path)) || !PathsEqual(path, model.GetPathName())
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
            || binding.ProjectId != projectId || binding.DocumentId != id || binding.VersionId != original.VersionId)
            throw new IOException("获取期间本地图档或版本关联已变化，当前窗口和修改已保留，请核对后重试。");
        if (IsSolidWorksCommandActive())
            throw new IOException("SolidWorks 正在执行命令，请完成当前操作后再核对图档编辑权限。");
        if (!TryUnlockAndObserveCheckout(model, path, id, projectId))
            throw new IOException(Path.GetFileName(path) + " 已取得 PLM 编辑权限，但 SolidWorks 仍未能切换为可写。"
                + "当前窗口和修改已保留，请先保留需要的修改，并核对该文件是否仍被其他窗口或程序占用。");
        LogOperation("已复核当前编辑会话并在原窗口恢复可写：" + Path.GetFileName(path));
    }

    private async Task<string> RecoverRecordedAcquisitionsAsync()
    {
        if (!EnsureEditJournal()) return "";
        var released = 0; var retained = 0; var pending = 0;
        // Records from a stopped process are retried using their original session,
        // never releasing a checkout belonging to a newer session or acquisition.
        foreach (var entry in editJournal.ReadAll().Where(item => item.AcquisitionRollbackPending && !item.Closed))
        {
            if (entry.SessionId != checkoutSessionId)
            {
                if (EditDraftJournal.OwnerProcessRunning(entry)) continue;
            }
            try
            {
                var document = (await apiClient.GetDocumentsAsync(entry.ProjectId, lifetime.Token)).FirstOrDefault(item => item.Id == entry.DocumentId);
                if (document == null) throw new IOException("当前账号无法核对图档权限，恢复记录已保留。");
                if (document.CheckoutSessionId != entry.SessionId
                    || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                    || entry.CheckedOutAt != DateTime.MinValue && document.CheckedOutAt != entry.CheckedOutAt)
                {
                    entry.AcquisitionRollbackPending = false;
                    entry.CheckoutSessionLost = true;
                    editJournal.Save(entry);
                    TrackCheckoutDocument(document);
                    ApplyCheckoutDocumentToMatchingInstances(CheckoutContextNodes().Where(node => node.DocumentId == entry.DocumentId).ToArray(), document);
                    if (document.CheckoutSessionId != checkoutSessionId
                        || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                        ProtectLoadedDocument(entry.SourcePath);
                    continue;
                }
                if (FindLoadedDocument(entry.SourcePath)?.GetSaveFlag() == true || !File.Exists(entry.SourcePath)
                    || !string.Equals(EditDraftJournal.Hash(entry.SourcePath), entry.AcquisitionBaseSha256, StringComparison.OrdinalIgnoreCase))
                {
                    entry.AcquisitionRollbackPending = false;
                    entry.RecoveryRequired = true;
                    editJournal.Save(entry);
                    if (entry.SessionId == checkoutSessionId) localEdits[entry.DocumentId] = entry;
                    TrackCheckoutDocument(document);
                    retained++;
                    continue;
                }
                var result = await apiClient.DiscardCheckoutAsync(entry.DocumentId, entry.SessionId, lifetime.Token);
                ApplyCheckoutDocumentToMatchingInstances(CheckoutContextNodes().Where(node => node.DocumentId == entry.DocumentId).ToArray(), result);
                TrackCheckoutDocument(result);
                ProtectLoadedDocument(entry.SourcePath);
                entry.Closed = true;
                entry.AcquisitionRollbackPending = false;
                editJournal.Save(entry);
                if (entry.SessionId == checkoutSessionId) localEdits.Remove(entry.DocumentId);
                released++;
            }
            catch (Exception error) { pending++; LogDiagnostic("获取未完成的权限恢复待重试：" + entry.DocumentId, error); }
        }
        if (released + retained + pending == 0) return "";
        var message = "本次新增权限：已释放" + released + "项，因本地修改保留" + retained + "项，待联网核对或重试" + pending + "项。原有权限与文件保留。";
        taskPaneControl?.SetCheckoutReminder(message, retained + pending > 0);
        LogOperation(message);
        return message;
    }

    private CadTreeNode[] CheckoutContextNodes()
    {
        lock (activeTreeCacheSync)
            return EnumerateTree(currentTree).Concat(activeTreeCache.Values.SelectMany(entry => EnumerateTree(entry.Tree)))
                .Distinct().ToArray();
    }

    private HashSet<Guid> ApplyLostCheckoutSessions(ISet<Guid> lost, DateTime serverTime)
    {
        var applied = new HashSet<Guid>();
        var nodes = CheckoutContextNodes();
        foreach (var id in lost)
        {
            localEdits.TryGetValue(id, out var draft);
            var matching = nodes.Where(node => node.DocumentId == id).ToArray();
            // An in-flight response must never revoke a newer acquisition.
            if (draft?.CheckedOutAt > serverTime || matching.Any(node => node.CheckedOutAt > serverTime
                || node.CheckoutLastHeartbeatAt > serverTime)) continue;
            foreach (var node in matching) ApplyLostCheckoutSession(node, serverTime);
            if (draft != null && draft.SessionId == checkoutSessionId)
            {
                draft.CheckoutSessionLost = true;
                draft.PendingClose = false;
                draft.AcquisitionRollbackPending = false;
                try { editJournal?.Save(draft); }
                catch (Exception error) { LogDiagnostic("保存失权恢复记录", error); }
                ProtectLoadedDocument(draft.SourcePath);
            }
            // Include invisible children and windows evicted from the tree cache.
            foreach (var model in (application?.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            {
                var path = model.GetPathName();
                if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata) && metadata.DocumentId == id)
                    ProtectLoadedDocument(path);
            }
            applied.Add(id);
        }
        return applied;
    }
}
