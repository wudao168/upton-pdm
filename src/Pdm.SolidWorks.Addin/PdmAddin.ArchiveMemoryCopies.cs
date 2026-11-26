using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Dictionary<string, ArchiveMemoryCopy> archiveMemoryCopies = new Dictionary<string, ArchiveMemoryCopy>(StringComparer.OrdinalIgnoreCase);
    private int archiveMemoryOperation;

    // Tracks the loaded document, not a persistent claim about the original disk
    // file. ModifyNotify also covers edits that GetUpdateStamp does not represent.
    private sealed class ArchiveMemoryCopy : IDisposable
    {
        internal readonly IModelDoc2 Model;
        internal readonly string SourcePath;
        internal string CopyPath, SourceHash, CopyHash;
        internal long SourceLength;
        internal int Revision, CapturedRevision, Stamp, Operation;
        internal bool Closed;
        internal bool SourceSavedToDisk;
        internal bool PreserveToolboxReferences;
        internal DocumentVersionDto CommittedVersion;
        internal Guid CommittedDocumentId;
        internal string CommittedUser, CommittedScope;
        private Action unsubscribe;
        private FileStream heldCopy;
        internal ArchiveMemoryCopy(IModelDoc2 model, string path)
        {
            Model = model; SourcePath = path;
            if (model.GetType() == 2 && model is DAssemblyDocEvents_Event assembly)
            {
                DAssemblyDocEvents_ModifyNotifyEventHandler changed = () => { Revision++; return 0; };
                DAssemblyDocEvents_DestroyNotify2EventHandler destroyed = reason => { if (reason == 0) Closed = true; return 0; };
                DAssemblyDocEvents_FileReloadNotifyEventHandler reloaded = () => { Closed = true; return 0; };
                assembly.ModifyNotify += changed; assembly.DestroyNotify2 += destroyed; assembly.FileReloadNotify += reloaded;
                unsubscribe = () => { assembly.ModifyNotify -= changed; assembly.DestroyNotify2 -= destroyed; assembly.FileReloadNotify -= reloaded; };
            }
            else if (model.GetType() == 1 && model is DPartDocEvents_Event part)
            {
                DPartDocEvents_ModifyNotifyEventHandler changed = () => { Revision++; return 0; };
                DPartDocEvents_DestroyNotify2EventHandler destroyed = reason => { if (reason == 0) Closed = true; return 0; };
                DPartDocEvents_FileReloadNotifyEventHandler reloaded = () => { Closed = true; return 0; };
                part.ModifyNotify += changed; part.DestroyNotify2 += destroyed; part.FileReloadNotify += reloaded;
                unsubscribe = () => { part.ModifyNotify -= changed; part.DestroyNotify2 -= destroyed; part.FileReloadNotify -= reloaded; };
            }
            else if (model.GetType() == 3 && model is DDrawingDocEvents_Event drawing)
            {
                DDrawingDocEvents_ModifyNotifyEventHandler changed = () => { Revision++; return 0; };
                DDrawingDocEvents_DestroyNotify2EventHandler destroyed = reason => { if (reason == 0) Closed = true; return 0; };
                DDrawingDocEvents_FileReloadNotifyEventHandler reloaded = () => { Closed = true; return 0; };
                drawing.ModifyNotify += changed; drawing.DestroyNotify2 += destroyed; drawing.FileReloadNotify += reloaded;
                unsubscribe = () => { drawing.ModifyNotify -= changed; drawing.DestroyNotify2 -= destroyed; drawing.FileReloadNotify -= reloaded; };
            }
            else throw new InvalidOperationException("无法跟踪当前图档的内存变化，未生成提交副本：" + Path.GetFileName(path));
        }
        internal bool MatchesMemory()
            => !string.IsNullOrWhiteSpace(CopyPath) && File.Exists(CopyPath) && MatchesNativeState();

        internal bool MatchesNativeState()
        {
            try { return !Closed && PathsEqual(Model.GetPathName(), SourcePath)
                && Revision == CapturedRevision && Model.GetUpdateStamp() == Stamp; }
            catch (COMException) { return false; }
            catch (InvalidComObjectException) { return false; }
        }
        internal bool CanUseCommittedBase()
        {
            try { return !Closed && PathsEqual(Model.GetPathName(), SourcePath) && (Model.GetSaveFlag() || MatchesMemory()); }
            catch (COMException) { return false; }
            catch (InvalidComObjectException) { return false; }
        }
        internal void ReplaceCopy(string path, string sourceHash, long sourceLength, string copyHash, int operation, int stamp, int revision)
        {
            var old = CopyPath;
            heldCopy?.Dispose();
            CopyPath = path; SourceHash = sourceHash; SourceLength = sourceLength; CopyHash = copyHash; Operation = operation;
            Stamp = stamp; CapturedRevision = revision;
            // The temporary-copy cleaner must not remove a snapshot used by this
            // still-open document or by an in-flight archive.
            heldCopy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.IsNullOrWhiteSpace(old) && !PathsEqual(old, path)) DeleteCheckInUploadCopy(old);
        }
        public void Dispose()
        {
            try { unsubscribe?.Invoke(); }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            finally
            {
                unsubscribe = null; heldCopy?.Dispose(); heldCopy = null;
                if (!SourceSavedToDisk && !string.IsNullOrWhiteSpace(CopyPath) && File.Exists(CopyPath)
                    && (CommittedVersion == null || !string.Equals(CommittedVersion.Sha256, CopyHash, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var record = new { sourcePath = SourcePath, copyPath = CopyPath, sourceSha256 = SourceHash, copySha256 = CopyHash, submitted = false };
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(CopyPath), "memory-source.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(record));
                        LogOperation("未提交的当前状态副本已保留；原图=" + SourcePath + "；恢复副本=" + CopyPath);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { LogDiagnostic("记录未提交副本位置，副本文件仍已保留", error); }
                }
                else DeleteCheckInUploadCopy(CopyPath);
                CopyPath = null;
            }
        }
    }

    private ArchiveMemoryCopy MemoryCopy(CadTreeNode node) => node != null && !string.IsNullOrWhiteSpace(node.FullPath)
        && archiveMemoryCopies.TryGetValue(node.FullPath, out var copy) ? copy : null;
    private string ArchiveFileReadPath(CadTreeNode node) => MemoryCopy(node)?.CopyPath ?? node.FullPath;
    private bool IsMemoryCopyPath(string path) => archiveMemoryCopies.Values.Any(copy => PathsEqual(copy.CopyPath, path));

    private void ForgetArchiveMemoryCopy(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !archiveMemoryCopies.TryGetValue(path, out var copy)) return;
        copy.Closed = true;
        if (Volatile.Read(ref batchDialogInProgress) == 0 && Volatile.Read(ref checkInOperationInProgress) == 0)
        { archiveMemoryCopies.Remove(path); copy.Dispose(); }
    }
    private void PruneArchiveMemoryCopies(bool all = false)
    {
        foreach (var pair in archiveMemoryCopies.Where(pair => all || pair.Value.Closed).ToArray())
        { archiveMemoryCopies.Remove(pair.Key); pair.Value.Dispose(); }
        foreach (var pair in unchangedCheckoutAssemblies.Where(pair => all || pair.Value.State.Closed).ToArray())
            ForgetUnchangedCheckout(pair.Key);
    }

    private static bool CanCaptureCurrentArchiveMemory(CadTreeNode node) =>
        (node.Kind is CadDocumentKind.Part or CadDocumentKind.Assembly or CadDocumentKind.Drawing)
        && node.HistoricalRepair == null && node.Role != CadReferenceRole.ExternalFileDependency
        && node.Status != CadReferenceStatus.Virtual && !node.IsStoredImport;

    private async Task<bool> PrepareCurrentMemoryCopiesAsync(IReadOnlyList<BatchOperationItem> selected, CancellationToken token,
        IReadOnlyDictionary<string, string> inspectedSourceHashes = null)
    {
        var changed = false;
        var reads = new ArchiveReadContext(this);
        foreach (var item in OrderBatchItemsForSave(DistinctBatchItems(selected)))
        {
            token.ThrowIfCancellationRequested();
            var node = item.Node;
            if (!CanCaptureCurrentArchiveMemory(node)) continue;
            // Lightweight/context-loaded documents can be returned by the exact-path
            // lookup before they appear in GetDocuments. They still own unsaved edits.
            var model = reads.FindLoaded(node.FullPath) ?? application.GetOpenDocumentByName(node.FullPath) as IModelDoc2;
            if (model != null && !PathsEqual(model.GetPathName(), node.FullPath))
                throw new InvalidOperationException("当前状态副本的图档路径不匹配，原窗口和修改已保留：" + node.FileName);
            if (model == null) { ForgetArchiveMemoryCopy(node.FullPath); continue; }
            if (node.IsRenamePendingSave)
                throw new InvalidOperationException("图档改名尚未保存，不能在存档时自动改写原文件，请先完成改名：" + node.FileName);
            var copy = MemoryCopy(node);
            if (!model.GetSaveFlag() && SavedArchiveSourceMatches(node)) continue;
            if (!model.GetSaveFlag() && !node.RequiresModelUpdate && copy == null) continue;
            var hash = await Task.Run(() => ComputeFileHash(node.FullPath), token);
            if (inspectedSourceHashes != null && inspectedSourceHashes.TryGetValue(node.FullPath, out var inspectedHash)
                && !string.Equals(inspectedHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("图档在检查后发生变化，本批尚未上传，请核对源文件后重试：" + node.FileName);
            if (copy != null && copy.Operation == archiveMemoryOperation && copy.SourceHash != null
                && !string.Equals(copy.SourceHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("源文件在存档期间发生变化，未覆盖原文件或继续提交：" + node.FileName);
            if (copy != null && ReferenceEquals(copy.Model, model) && copy.MatchesMemory() && string.Equals(copy.SourceHash, hash, StringComparison.OrdinalIgnoreCase))
            { copy.Operation = archiveMemoryOperation; continue; }
            if (!model.GetSaveFlag() && !node.RequiresModelUpdate && copy != null
                && (!ReferenceEquals(copy.Model, model) || !string.Equals(copy.SourceHash, hash, StringComparison.OrdinalIgnoreCase)))
            { archiveMemoryCopies.Remove(node.FullPath); copy.Dispose(); continue; }
            if (copy == null || !ReferenceEquals(copy.Model, model) || copy.Closed)
            {
                copy?.Dispose();
                copy = new ArchiveMemoryCopy(model, node.FullPath);
                archiveMemoryCopies[node.FullPath] = copy;
            }
            ArchiveSession.Current?.ReportFile(0, 0, node.FileName, "正在生成当前状态提交副本；不改写源文件…");
            var originalAttributes = File.GetAttributes(node.FullPath);
            var originalLength = new FileInfo(node.FullPath).Length;
            string path = null;
            try
            {
                if (Volatile.Read(ref nativeFileOpenInProgress) != 0)
                    throw new InvalidOperationException("仍有文件加载未结束，未开始生成提交副本：" + node.FileName);
                var startLoadEpoch = nativeLoadEpoch;
                var completedLoadEpoch = startLoadEpoch;
                var completedCommandSequence = nativeCommandSequence;
                var workingDirectory = application.GetCurrentWorkingDirectory();
                Interlocked.Increment(ref refreshSuppressionDepth);
                try
                {
                    // The working-directory query can pump a later load too.
                    if (nativeLoadEpoch != startLoadEpoch || Volatile.Read(ref nativeFileOpenInProgress) != 0)
                        throw new InvalidOperationException("生成提交副本前发生新的文件加载，请等待加载完成后重试：" + node.FileName);
                    path = CreateCheckInUploadCopy(model, node.FullPath, node.DocumentId ?? Guid.Empty);
                    completedLoadEpoch = nativeLoadEpoch;
                    completedCommandSequence = nativeCommandSequence;
                }
                finally
                {
                    try
                    {
                        if (!string.Equals(application.GetCurrentWorkingDirectory(), workingDirectory, StringComparison.OrdinalIgnoreCase)
                            && !application.SetCurrentWorkingDirectory(workingDirectory ?? string.Empty))
                            throw new InvalidOperationException("生成副本后未能恢复SolidWorks原工作目录，已停止提交：" + node.FileName);
                    }
                    finally { Interlocked.Decrement(ref refreshSuppressionDepth); }
                }
                await CompleteArchiveMemoryCopyLoadAsync(model, node.FullPath, startLoadEpoch, completedLoadEpoch,
                    completedCommandSequence, token);
                RestoreArchiveToolboxCopyReferences(node, path, token);
                var stamp = model.GetUpdateStamp();
                var revision = copy.Revision;
                if (!PathsEqual(model.GetPathName(), node.FullPath))
                    throw new InvalidOperationException("生成提交副本后图档路径异常，已停止提交，请保留当前窗口：" + node.FileName);
                await Task.Run(() => CheckInUploadHashes.VerifySource(node.FullPath, hash, token), token);
                if (File.GetAttributes(node.FullPath) != originalAttributes)
                    throw new InvalidOperationException("生成副本期间源文件属性发生变化，已停止提交：" + node.FileName);
                var copyHash = await Task.Run(() => ComputeFileHash(path), token);
                copy.ReplaceCopy(path, hash, originalLength, copyHash, archiveMemoryOperation, stamp, revision);
                copy.PreserveToolboxReferences = node.ToolboxEvidence.Count > 0;
                path = null;
                changed = true;
                ValidateCurrentMemoryCopy(node);
                LogOperation("当前状态副本已固定；原文件未改写；文件=" + node.FullPath + "；更新戳=" + copy.Stamp);
            }
            finally { DeleteCheckInUploadCopy(path); }
        }
        return changed;
    }

    private const int NativeOpenComponentFileCommand = 970; // swCommands_Open_Comp_File
    private static readonly TimeSpan ArchiveCopyLoadSettleLimit = TimeSpan.FromSeconds(10);

    private async Task CompleteArchiveMemoryCopyLoadAsync(IModelDoc2 model, string sourcePath, int startEpoch,
        int completedEpoch, int completedCommandSequence, CancellationToken token)
    {
        if (Volatile.Read(ref nativeFileOpenInProgress) == 0 && nativeLoadEpoch == completedEpoch) return;
        // SaveAs3 can resolve an imported component and emit only FileOpenPreNotify.
        // Scope any reconciliation to the notifications inside this successful call.
        try
        {
            if (completedEpoch == startEpoch || nativeLoadEpoch != completedEpoch || nativeCommandSequence != completedCommandSequence)
                throw new InvalidOperationException("副本调用以外发生了文件加载或命令");
            if (nativeFileOpenCommandId == NativeOpenComponentFileCommand && model is IAssemblyDoc)
            {
                var notifiedPaths = pendingNativeOpenPaths.ToArray();
                var idleAtReturn = nativeIdleSequence;
                await WaitForArchiveCopyComponentLoadAsync(model, sourcePath, completedEpoch,
                    completedCommandSequence, notifiedPaths, idleAtReturn, token);
                return;
            }
            application.GetRunningCommandInfo(out var command, out _, out var active);
            var ready = application.StartupProcessCompleted;
            var sameDocument = PathsEqual(model.GetPathName(), sourcePath);
            if (active || !ready || !sameDocument || disconnecting || nativeUiCommandId != 0
                || nativeFileOpenCommandId != 0 || nativeLoadEpoch != completedEpoch || nativeCommandSequence != completedCommandSequence)
                throw new InvalidOperationException("原生命令、图档或加载批次仍需核对，命令=" + command + "，活动=" + active
                    + "，记录命令=" + nativeUiCommandId + "，加载命令=" + nativeFileOpenCommandId
                    + "，宿主一致=" + sameDocument + "，启动完成=" + ready);
            CompleteNativeFileOpen(pendingOpenPath);
            LogOperation("当前状态副本生成完成，已核对并结束本次导入加载标记：" + sourcePath + "；epoch=" + completedEpoch);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LogDiagnostic("生成提交副本后核对原生加载状态", error);
            throw new InvalidOperationException("提交副本已生成，但SolidWorks加载状态尚未确认，已停止本次提交；原文件和当前修改保留。请等待加载完成后重试："
                + Path.GetFileName(sourcePath), error);
        }
    }

    private async Task WaitForArchiveCopyComponentLoadAsync(IModelDoc2 owner, string path, int epoch, int commandSequence,
        string[] notifiedPaths, int idleAtReturn, CancellationToken token)
    {
        if (notifiedPaths.Length == 0 || notifiedPaths.Any(source => !IsNativeModelReference(source) || PathsEqual(source, path)))
            throw new InvalidOperationException("副本调用未能确认组件文件通知");
        bool SameLoad() => !disconnecting && !ControlledOpenBoundary.IsActive && nativeFileOpenInProgress != 0
            && nativeCommandSequence == commandSequence && nativeLoadEpoch == epoch
            && nativeFileOpenCommandId == NativeOpenComponentFileCommand
            && (nativeUiCommandId == 0 || nativeUiCommandId == NativeOpenComponentFileCommand)
            && pendingNativeOpenPaths.SetEquals(notifiedPaths);

        ArchiveSession.Current?.SetStage("提交副本已生成，正在核对本次组件加载完成：" + Path.GetFileName(path));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        (int Count, int Stamp, IModelDoc2 Active)? previous = null;
        var sampledIdle = idleAtReturn;
        while (elapsed.Elapsed < ArchiveCopyLoadSettleLimit)
        {
            await Task.Delay(50, token);
            if (!disconnecting && nativeFileOpenInProgress == 0 && nativeLoadEpoch == unchecked(epoch + 1)
                && nativeCommandSequence == commandSequence && PathsEqual(owner.GetPathName(), path)
                && notifiedPaths.Any(source => PathsEqual(source, lastNativeOpenCompletionPath)))
                return; // Native FileOpenPostNotify completed the original load.
            if (!SameLoad()) throw new InvalidOperationException("等待组件期间发生了另一项文件加载或命令");
            if (pendingOpenIdleEpoch != epoch || nativeIdleSequence == sampledIdle) continue;
            sampledIdle = nativeIdleSequence;
            application.GetRunningCommandInfo(out _, out _, out var busy);
            var active = application.ActiveDoc as IModelDoc2;
            if (busy || application.CommandInProgress || !application.StartupProcessCompleted
                || active == null || active.GetBlockingState() != 0 || owner.GetBlockingState() != 0
                || !PathsEqual(owner.GetPathName(), path) || !ReferenceEquals(owner, application.GetOpenDocumentByName(path))
                || notifiedPaths.Any(source => application.GetOpenDocumentByName(source) is IModelDoc2 reference
                    && (!PathsEqual(reference.GetPathName(), source) || reference.GetBlockingState() != 0)))
            {
                previous = null;
                continue;
            }
            var count = application.GetDocumentCount();
            var stamp = owner.GetUpdateStamp();
            if (!SameLoad()) throw new InvalidOperationException("读取组件状态时发生了另一项文件加载或命令");
            if (previous.HasValue && previous.Value.Count == count && previous.Value.Stamp == stamp
                && count > 0 && ReferenceEquals(previous.Value.Active, active) && ReferenceEquals(application.ActiveDoc, active)
                && !application.CommandInProgress && SameLoad())
            {
                if (nativeUiCommandId == NativeOpenComponentFileCommand) nativeUiCommandId = 0;
                CompleteNativeFileOpen(pendingOpenPath);
                LogOperation("提交副本组件加载已核对完成，复用已生成副本：" + path
                    + "；组件数=" + notifiedPaths.Length + "；耗时毫秒=" + elapsed.ElapsedMilliseconds);
                return;
            }
            previous = (count, stamp, active);
        }
        throw new InvalidOperationException("本次组件打开通知在等待期内尚无连续空闲证据");
    }

    private void ValidateCurrentMemoryCopy(CadTreeNode node)
    {
        var copy = MemoryCopy(node);
        if (copy != null && !copy.MatchesMemory())
            throw new ArchiveMemoryChangedException(node.FileName);
    }

    private bool IsOwnCommittedMemoryCopy(ArchiveMemoryCopy copy, Guid? documentId) =>
        copy?.CommittedVersion != null && documentId == copy.CommittedDocumentId
        && copy.CommittedUser == authenticatedUsername && copy.CommittedScope == apiClient?.EditJournalScope;

    private Guid? PreparedMemoryArchiveBase(PreparedArchiveFile file, DocumentVersionDto latest)
    {
        if (!file.MemorySnapshot) return null;
        var copy = MemoryCopy(file.Node);
        if (copy == null || !copy.MatchesMemory() || !PathsEqual(file.UploadCopyPath, copy.CopyPath)
            || !string.Equals(file.SourceHash, copy.SourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.UploadCopySha256, copy.CopyHash, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveMemoryChangedException(file.Node.FileName);
        if (!IsOwnCommittedMemoryCopy(copy, file.Node.DocumentId)) return null;
        if (copy.CommittedVersion.Id != latest?.Id
            || !string.Equals(copy.CommittedVersion.Sha256, latest.Sha256, StringComparison.OrdinalIgnoreCase)
            || copy.CommittedVersion.FileLength != latest.FileLength)
            throw new InvalidOperationException("已提交的内存版本与服务器当前版本不一致，原文件和修改均已保留：" + file.Node.FileName);
        // The physical working file can intentionally still be the earlier
        // version. The verified copy and the in-session receipt establish this base.
        return copy.CommittedVersion.Id;
    }

    private sealed class ArchiveMemoryChangedException : InvalidOperationException
    {
        internal ArchiveMemoryChangedException(string fileName)
            : base("图档内存内容在生成副本后发生变化，请结束当前编辑后重新存档：" + fileName) { }
    }

    private IReadOnlyDictionary<string, string> ApplyCommittedMemoryState(IEnumerable<CadTreeNode> nodes, IReadOnlyDictionary<string, string> localHashes)
    {
        if (archiveMemoryCopies.Count == 0) return localHashes;
        var effectiveHashes = localHashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(node => !node.IsReadOnlyPreview && !node.IsHistoricalPreview))
        {
            var copy = MemoryCopy(node);
            if (!IsOwnCommittedMemoryCopy(copy, node.DocumentId) || !copy.CanUseCommittedBase()
                || !localHashes.TryGetValue(node.FullPath, out var diskHash)
                || !string.Equals(diskHash, copy.SourceHash, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(diskHash, copy.CopyHash, StringComparison.OrdinalIgnoreCase)) continue;
            // Only this still-open, unchanged model can use the committed memory
            // receipt. Never persist the older disk file as the new stored version.
            effectiveHashes[node.FullPath] = copy.CommittedVersion.Sha256;
            node.OpenedVersionId = copy.CommittedVersion.Id;
            node.OpenedRevision = copy.CommittedVersion.Revision?.Display ?? string.Empty;
            if (copy.MatchesMemory() && string.Equals(copy.CopyHash, copy.CommittedVersion.Sha256, StringComparison.OrdinalIgnoreCase))
                node.IsModifiedInSolidWorks = false;
        }
        return effectiveHashes;
    }

    private void RestoreCommittedSourceIdentity(CadTreeNode node, Guid projectId)
    {
        if (node.IsReadOnlyPreview || node.IsHistoricalPreview || node.IsStoredImport
            || PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)) return;
        var copy = MemoryCopy(node);
        if (copy?.CommittedVersion == null || copy.CommittedUser != authenticatedUsername
            || copy.CommittedScope != apiClient?.EditJournalScope || !copy.CanUseCommittedBase()
            || !PdmDocumentIdentityStore.TryReadArchiveMetadata(node.FullPath, projectId, out var metadata)
            || metadata.ProjectId != projectId || metadata.DocumentId != copy.CommittedDocumentId) return;
        var hash = ComputeFileHash(node.FullPath);
        if (!string.Equals(hash, copy.SourceHash, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hash, copy.CopyHash, StringComparison.OrdinalIgnoreCase)) return;
        node.DocumentId = node.ProvenanceDocumentId = copy.CommittedDocumentId;
        node.ProvenanceProjectId = projectId; node.IsExternalProvenance = false;
        node.OpenedVersionId = copy.CommittedVersion.Id;
        node.OpenedRevision = copy.CommittedVersion.Revision?.Display ?? string.Empty;
    }
}
