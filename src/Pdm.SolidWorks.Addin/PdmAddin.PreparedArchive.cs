using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static InvalidOperationException PreparedArchiveUncertain(Exception inner)
    {
        var error = new InvalidOperationException("整批提交结果待核对，已保留原提交清单和上传暂存。服务恢复后打开原图档并点击“存档”，可继续原批次。", inner);
        error.Data["ArchiveResultUncertain"] = true;
        return error;
    }

    private async Task<bool> TryResumePreparedArchiveAsync(string path, Guid projectId, IReadOnlyList<BatchOperationItem> selection, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var client = apiClient;
        var actor = authenticatedUsername;
        var batch = ArchiveBatchJournal.Recoverable(client.EditJournalScope, actor)
            .Where(item => item.Prepared != null && item.Prepared.ProjectId == projectId && PathsEqual(item.Prepared.RootPath, path))
            .OrderByDescending(item => item.Prepared.PreparedAtUtc).FirstOrDefault();
        if (batch == null) return false;
        using var batchLock = ArchiveBatchJournal.TryLock(batch.BatchId);
        if (batchLock == null)
            throw new InvalidOperationException("正在核对原存档批次，请稍后再试。");
        // A maintenance pass may have completed recovery before we took the lock.
        if (!ArchiveBatchJournal.Recoverable(client.EditJournalScope, actor).Any(item => item.BatchId == batch.BatchId))
        {
            ArchiveSession.Current?.Complete("原存档批次已核对，请刷新设计树查看结果。");
            return true;
        }
        var session = ArchiveSession.Current;
        var prepared = batch.Prepared;
        session?.SetStep(9, "正在核对原存档批次回执…", 1);
        var receipt = await client.GetArchiveBatchReceiptOrNullAsync(batch.BatchId, token);
        if (receipt == null)
        {
            // Only replay the immutable upload if the user confirmed that same set of source files.
            var original = prepared.ConfirmedSourcePaths ?? prepared.Files?.Select(file => file.SourcePath).ToArray();
            var confirmed = new HashSet<string>(selection.Select(item => item.Node.FullPath)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            if (original == null || !confirmed.SetEquals(original.Select(Path.GetFullPath)))
                throw new InvalidOperationException("原暂存批次和本次勾选的图档范围不同，暂存文件已保留。续提时请选用原范围：" +
                    string.Join("、", (original ?? Array.Empty<string>()).Select(Path.GetFileName).Take(12)));
            token.ThrowIfCancellationRequested();
            if (client != apiClient || actor != authenticatedUsername || client.EditJournalScope != batch.Scope)
                throw new InvalidOperationException("当前登录范围已变化，请在原账号和公司下核对批次。");
            if (!TryBeginWorkspaceOperation("正在继续原存档批次"))
            {
                ShowWorkspaceOperationBusy();
                return true;
            }
            try
            {
                string payload;
                try { payload = await Task.Run(() => ArchiveBatchJournal.ReadPayload(batch), token); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { throw new InvalidOperationException("无法读取或校验原批次提交清单。可在存档窗口结束原批次后按当前图档重新准备。", error); }
                if (client != apiClient || actor != authenticatedUsername || client.EditJournalScope != batch.Scope)
                    throw new InvalidOperationException("当前登录范围已变化，请在原账号和公司下核对批次。");
                session?.SetStep(9, "正在复核并提交原批次的暂存文件…", 1);
                try { receipt = await client.ResumeArchiveBatchAsync(batch, payload, token); }
                catch (Exception error) when (error.Data["ArchiveResultUncertain"] is bool uncertain && uncertain)
                { throw PreparedArchiveUncertain(error); }
            }
            finally { EndWorkspaceOperation(); }
        }
        await CompletePreparedArchiveRecoveryAsync(batch, receipt);
        ArchiveBatchJournal.Complete(batch.BatchId);
        archiveRecoveryNeedsReview = false;
        var message = "服务器已确认原批次整批存档，共 " + receipt.Results.Count + " 份图档。请在设计树核对版本。";
        if (session != null) session.Complete(message);
        else TopMostMessageBox.Show(taskPaneControl, message, "存档完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    private async Task CompletePreparedArchiveRecoveryAsync(InterruptedArchiveBatch batch, ArchiveBatchReceiptDto receipt)
    {
        var prepared = batch.Prepared;
        PdmApiClient.RequireCompleteArchiveReceipt(receipt, batch.BatchId, prepared.Files.Select(file => file.DocumentId).ToArray());
        if (batch.Scope != apiClient?.EditJournalScope || !string.Equals(batch.Actor, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前登录范围已变化，请在原账号和公司下刷新已提交的批次。");
        var results = receipt.Results.ToDictionary(result => result.Document.Id);
        var session = ArchiveSession.Current;
        session?.SetStep(10, "原批次已提交，正在刷新本地版本记录…", prepared.Files.Length);
        foreach (var file in prepared.Files) RememberExplicitProjectPath(file.SourcePath, prepared.ProjectId);
        await Task.Run(() =>
        {
            foreach (var file in prepared.Files)
            {
                var version = results[file.DocumentId].Version;
                if (!file.SyncIdentity || version == null) continue;
                try
                {
                    // Record only unchanged source files. A recovered receipt must never
                    // close a later edit session or overwrite a newer working copy.
                    if (string.IsNullOrWhiteSpace(file.SourceHash) || !File.Exists(file.SourcePath)
                        || !string.Equals(ComputeFileHash(file.SourcePath), file.SourceHash, StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.MemorySnapshot)
                        PdmDocumentIdentityStore.TryWriteAssociationOnly(file.SourcePath, file.DocumentId, prepared.ProjectId);
                    else
                        RememberControlledVersionIdentity(file.SourcePath, file.DocumentId, prepared.ProjectId, version);
                }
                catch (Exception error) { LogDiagnostic("核对续提图档的本地版本记录", error); }
            }
        });
        projectDocumentsCache.Remove(prepared.ProjectId);
        navigationRelations.Remove(prepared.ProjectId);
        ScheduleTreeRefresh();
    }
}
