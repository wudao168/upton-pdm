using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private bool idleReleaseEnabled;
    private int idleReleaseMinutes = 30;
    private int idleReleaseCountdownSeconds = 60;
    private readonly Dictionary<Guid, DateTime> editIdleDeferredUntil = new Dictionary<Guid, DateTime>();
    private EditAutoReleaseDialog idleReleaseDialog;
    private Func<int, bool> editReleaseIdleCheck = UserIdleForEditRelease;

    private static bool UserIdleForEditRelease(int minutes)
    {
        var input = new LastDraftInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(LastDraftInputInfo)) };
        return GetLastInputInfo(ref input) && unchecked((uint)System.Environment.TickCount - input.Time) >= minutes * 60_000L;
    }

    private sealed class IdleReleaseCandidate
    {
        public LocalEditDraft Entry;
        public long Length;
        public DateTime LastWrite;
    }

    private bool CanAutomaticallyReleaseEdits() => idleReleaseEnabled && !disconnecting && apiClient?.IsAuthenticated == true
        && CanMaintainEdits() && !IsSolidWorksCommandActive() && editReleaseIdleCheck(idleReleaseMinutes);

    private bool NoLoadedDocumentHasUnsavedChanges()
    {
        try { var loaded = application.GetDocuments() as Array; return loaded != null && !loaded.OfType<IModelDoc2>().Any(model => model.GetSaveFlag()); }
        catch { return false; }
    }

    private bool StillIdle(IdleReleaseCandidate candidate, bool verifyHash)
    {
        var entry = candidate.Entry;
        if (!localEdits.TryGetValue(entry.DocumentId, out var current) || !ReferenceEquals(current, entry) || entry.Closed || entry.PendingClose
            || entry.UnsavedChanges || entry.SessionId != checkoutSessionId || !entry.BaseVersionId.HasValue) return false;
        lock (checkoutDocumentSync)
            if (!activeCheckoutDocumentIds.Contains(entry.DocumentId) || pendingArchiveDocumentIds.Contains(entry.DocumentId)) return false;
        var model = FindLoadedDocument(entry.SourcePath);
        if (model == null || model.GetSaveFlag()) return false;
        var file = new FileInfo(entry.SourcePath);
        return file.Exists && file.Length == candidate.Length && file.LastWriteTimeUtc == candidate.LastWrite
            && (!verifyHash || string.Equals(EditDraftJournal.Hash(entry.SourcePath), entry.BaseSha256, StringComparison.OrdinalIgnoreCase));
    }

    private void TryOfferIdleRelease()
    {
        if (idleReleaseDialog != null || !CanAutomaticallyReleaseEdits() || !NoLoadedDocumentHasUnsavedChanges()) return;
        var now = DateTime.UtcNow;
        var candidates = new List<IdleReleaseCandidate>();
        foreach (var entry in localEdits.Values.ToArray())
        {
            if (now - entry.CheckedOutAt.ToUniversalTime() < TimeSpan.FromMinutes(idleReleaseMinutes)
                || editIdleDeferredUntil.TryGetValue(entry.DocumentId, out var defer) && now < defer || !File.Exists(entry.SourcePath)) continue;
            var file = new FileInfo(entry.SourcePath);
            var candidate = new IdleReleaseCandidate { Entry = entry, Length = file.Length, LastWrite = file.LastWriteTimeUtc };
            if (StillIdle(candidate, true)) candidates.Add(candidate);
        }
        if (candidates.Count == 0) return;
        void Postpone()
        {
            foreach (var candidate in candidates) editIdleDeferredUntil[candidate.Entry.DocumentId] = DateTime.UtcNow.AddMinutes(idleReleaseMinutes);
        }
        idleReleaseDialog = new EditAutoReleaseDialog(candidates.Count, idleReleaseCountdownSeconds,
            () => CanAutomaticallyReleaseEdits() && NoLoadedDocumentHasUnsavedChanges() && candidates.All(candidate => StillIdle(candidate, false)),
            async () => { try { await ReleaseIdleEditsAsync(candidates); } finally { Postpone(); } }, Postpone);
        idleReleaseDialog.FormClosed += (_, _) => idleReleaseDialog = null;
        idleReleaseDialog.Show(taskPaneControl);
    }

    private async Task ReleaseIdleEditsAsync(IReadOnlyList<IdleReleaseCandidate> candidates)
    {
        if (!CanAutomaticallyReleaseEdits() || !NoLoadedDocumentHasUnsavedChanges() || !candidates.All(candidate => StillIdle(candidate, true))) return;
        if (!TryBeginWorkspaceOperation("正在安全释放闲置图档权限")) return;
        var released = 0;
        try
        {
            foreach (var candidate in candidates)
            {
                var entry = candidate.Entry;
                var model = FindLoadedDocument(entry.SourcePath);
                var wasReadOnly = model?.IsOpenedReadOnly() == true;
                try
                {
                    if (model == null || !StillIdle(candidate, true)) continue;
                    var version = (await apiClient.GetVersionsAsync(entry.DocumentId, lifetime.Token)).FirstOrDefault();
                    if (version?.Id != entry.BaseVersionId || model.GetSaveFlag()) continue;
                    // 先冻结本机编辑，再发服务器释放请求，避免等待HTTP期间出现新修改。
                    if (!model.SetReadOnlyState(true) || !model.IsOpenedReadOnly()) throw new IOException("图档未能切换为只读，已保留编辑权限。");
                    SetFileReadOnly(entry.SourcePath, true);
                    if (model.GetSaveFlag() || !StillIdle(candidate, true)) throw new IOException("图档状态已变化，已停止自动释放。");
                    var document = await apiClient.AutoReleaseEditAsync(entry, version.Id, version.Sha256, lifetime.Token);
                    FinishLocalEdit(entry.DocumentId);
                    foreach (var node in EnumerateTree(currentTree).Where(node => node.DocumentId == entry.DocumentId))
                    {
                        ApplyCheckoutDocument(node, document); ClearCheckoutState(node); node.WorkState = CadWorkState.None;
                    }
                    lock (checkoutDocumentSync) activeCheckoutDocumentIds.Remove(entry.DocumentId);
                    released++;
                }
                catch (Exception error)
                {
                    LogDiagnostic("闲置图档自动释放", error);
                    // 只在服务器仍确认原会话持有权限时恢复可写，不能恢复已被接管的旧会话。
                    try
                    {
                        var current = (await apiClient.GetDocumentsAsync(entry.ProjectId, lifetime.Token)).FirstOrDefault(document => document.Id == entry.DocumentId);
                        if (!wasReadOnly && current?.CheckoutSessionId == entry.SessionId && current.CheckedOutAt?.ToUniversalTime() == entry.CheckedOutAt.ToUniversalTime())
                        {
                            SetFileReadOnly(entry.SourcePath, false); model?.SetReadOnlyState(false);
                        }
                    }
                    catch (Exception verifyError) { LogDiagnostic("核对自动释放结果", verifyError); }
                }
            }
            taskPaneControl.SetCheckoutReminder(released > 0 ? $"已自动释放 {released} 个无修改图档，本地文件保留；需要修改时重新获取权限。" : "暂未能确认安全释放，已保留占用，请检查连接后重试。", false);
            taskPaneControl.SetTree(currentTree);
        }
        finally { EndWorkspaceOperation(); }
    }
}
