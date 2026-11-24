using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private int nativeUiCommandId;
    private int nativeCommandSequence;
    private int nativeFileOpenCommandId;
    private int nativeLoadEpoch;
    private int unknownAddedEpoch;
    private int unknownSavedEpoch;
    private bool unknownRemovalObserved;
    private IModelDoc2 unknownLoadOwner;
    private string unknownLoadOwnerPath;
    private string pendingOpenPath;
    private IModelDoc2 pendingOpenHost;
    private string pendingOpenHostPath;
    private long pendingOpenStarted;
    private int pendingOpenIdleEpoch = -1;
    private int nativeOpenCompletionEpoch = -1;
    private int nativeDocumentLoadedEpoch = -1;
    private int nativeActivationCompletedEpoch = -1;
    private int nativeIdleSequence;
    private ReferenceActivationObservation referenceActivationObservation;

    private sealed class ReferenceActivationObservation
    {
        internal int Epoch, IdleSequence, Count, AssemblyStamp, ReferenceStamp;
        internal IModelDoc2 Assembly, Reference;
    }

    // swCommands_Circle can remain as the last UI command after a sketch has
    // closed. It is not an open, insert, or assembly structure command.
    private const int SketchCircleCommand = 46;
    // swCommands_Open_Comp_File can remain as the last command when rebuilding
    // an assembly loads another reference without a matching FileOpenPostNotify.
    private const int OpenComponentFileCommand = 970;

    private int OnNativeDocumentLoaded(string title, string path)
    {
        // Unlike FileOpenPostNotify, this event also covers lazily loaded references.
        // Record evidence only; never traverse or resolve CAD in the callback.
        var load = assemblyReferenceLoad;
        if (load != null && (load.ForReuse || load.DraftSnapshot) && load.Observed.Contains(path)) load.Loaded.Add(path);
        if (savedAssemblyActivation?.Observed.Contains(path) == true) savedAssemblyActivation.OwnerReferenceProbeFailed = false;
        if (nativeFileOpenInProgress != 0 && PathsEqual(path, pendingOpenPath))
            nativeDocumentLoadedEpoch = nativeLoadEpoch;
        return 0;
    }

    private int OnActiveModelDocumentChanged()
    {
        if (nativeDocumentLoadedEpoch == nativeLoadEpoch)
            nativeActivationCompletedEpoch = nativeLoadEpoch;
        return OnActiveDocumentChanged();
    }

    private bool NativeInsertionEvidenceAllowed() => !disconnecting && !ControlledOpenBoundary.IsActive
        && Volatile.Read(ref ownedReferenceResolutionDepth) == 0
        && Volatile.Read(ref openOperationInProgress) == 0 && Volatile.Read(ref controlledOpenInProgress) == 0
        && Volatile.Read(ref workspaceOperationInProgress) == 0 && Volatile.Read(ref batchDialogInProgress) == 0
        && Volatile.Read(ref checkInOperationInProgress) == 0 && Volatile.Read(ref draftSnapshotInProgress) == 0
        && Volatile.Read(ref refreshSuppressionDepth) == 0
        && Volatile.Read(ref treeRefreshInProgress) == 0;

    private void ObserveNativeFileOpening(string fileName)
    {
        savedAssemblyActivation = null;
        nativeLoadEpoch++;
        ObserveAssemblyReferenceLoad(fileName);
        referenceActivationObservation = null;
        pendingOpenPath = fileName;
        pendingOpenHost = nativeFileOpenCommandId == 2 ? assemblyEventDocument : null;
        pendingOpenHostPath = assemblyEventDocumentPath;
        pendingOpenStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        pendingOpenIdleEpoch = -1;
        nativeOpenCompletionEpoch = -1;
        nativeDocumentLoadedEpoch = nativeActivationCompletedEpoch = -1;
        unknownAddedEpoch = unknownSavedEpoch = 0; unknownRemovalObserved = false;
        unknownLoadOwner = null; unknownLoadOwnerPath = null;
        if (nativeFileOpenCommandId != 0 || !NativeInsertionEvidenceAllowed()
            || assemblyEventDocument == null || string.IsNullOrWhiteSpace(assemblyEventDocumentPath)
            || PathsEqual(fileName, assemblyEventDocumentPath)) return;
        // Event callbacks retain only cached owner identity, never inspect components.
        unknownLoadOwner = assemblyEventDocument;
        unknownLoadOwnerPath = assemblyEventDocumentPath;
    }

    private int OnAssemblyComponentAdded(int entityType, string itemName)
    {
        if (entityType == 2) savedAssemblyActivation = null;
        if (entityType == 2 && unknownLoadOwner != null && !NativeInsertionEvidenceAllowed())
            unknownAddedEpoch = unknownSavedEpoch = 0;
        if (entityType == 2 && unknownLoadOwner != null && !unknownRemovalObserved && NativeInsertionEvidenceAllowed()
            && ReferenceEquals(unknownLoadOwner, assemblyEventDocument) && PathsEqual(unknownLoadOwnerPath, assemblyEventDocumentPath))
        {
            unknownAddedEpoch = nativeLoadEpoch; unknownSavedEpoch = 0;
            LogOperation("本次未知来源加载已观察到所属装配加入组件 epoch=" + nativeLoadEpoch + " item=" + itemName);
        }
        return OnAssemblyItemChanged(entityType, itemName);
    }

    private int OnAssemblyComponentDeleted(int entityType, string itemName)
    {
        if (entityType == 2) savedAssemblyActivation = null;
        if (entityType == 2) { unknownRemovalObserved = true; unknownAddedEpoch = unknownSavedEpoch = 0; }
        return OnAssemblyItemChanged(entityType, itemName);
    }

    private void ObserveNativeOwnerSaved(string fileName)
    {
        ObserveSavedAssemblyReferenceLoads(fileName);
        if (unknownLoadOwner != null && ReferenceEquals(unknownLoadOwner, assemblyEventDocument)
            && PathsEqual(fileName, unknownLoadOwnerPath)) unknownSavedEpoch = 0;
        if (unknownLoadOwner != null && unknownAddedEpoch == nativeLoadEpoch && !unknownRemovalObserved
            && NativeInsertionEvidenceAllowed() && ReferenceEquals(unknownLoadOwner, assemblyEventDocument)
            && PathsEqual(unknownLoadOwnerPath, assemblyEventDocumentPath) && PathsEqual(fileName, unknownLoadOwnerPath))
        {
            unknownSavedEpoch = nativeLoadEpoch;
            LogOperation("本次插入的所属装配已保存，等待显式存档入口核对 epoch=" + nativeLoadEpoch + " owner=" + fileName);
        }
    }

    private bool TryCompleteSavedInsertionForArchive()
    {
        var epoch = nativeLoadEpoch; var owner = unknownLoadOwner; var path = unknownLoadOwnerPath;
        if (!SavedInsertionProofMatches(epoch, owner, path)) return false;
        try
        {
            if (application == null || !application.StartupProcessCompleted) return false;
            application.GetRunningCommandInfo(out var command, out var title, out var active);
            if (active || !PathsEqual(owner.GetPathName(), path) || owner.GetSaveFlag()
                || !ReferenceEquals(application.ActiveDoc, owner)) return false;
            // COM calls may pump events. A later load or internal operation voids the proof.
            if (!SavedInsertionProofMatches(epoch, owner, path)) return false;
            Interlocked.Exchange(ref nativeFileOpenInProgress, 0);
            unknownLoadOwner = null; unknownAddedEpoch = unknownSavedEpoch = 0;
            ScheduleTreeRefresh();
            LogOperation("显式存档入口已核对本次组件加入及宿主保存，结束残留加载标记 epoch=" + epoch + " owner=" + path);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对已保存插入的加载状态，原窗口和文件保留", error); return false; }
    }

    private bool SavedInsertionProofMatches(int epoch, IModelDoc2 owner, string path) =>
        epoch == nativeLoadEpoch && nativeFileOpenCommandId == 0 && Volatile.Read(ref nativeFileOpenInProgress) != 0
        && owner != null && ReferenceEquals(owner, unknownLoadOwner) && ReferenceEquals(owner, assemblyEventDocument)
        && PathsEqual(path, unknownLoadOwnerPath) && PathsEqual(path, assemblyEventDocumentPath)
        && unknownAddedEpoch == epoch && unknownSavedEpoch == epoch && !unknownRemovalObserved && NativeInsertionEvidenceAllowed();

    private bool RunTrackedAssemblyCommand(IModelDoc2 document, int command)
    {
        var previous = nativeUiCommandId;
        unknownSavedEpoch = 0;
        nativeUiCommandId = command;
        try
        {
            var started = document.Extension.RunCommand(command, string.Empty);
            if (!started && nativeUiCommandId == command) nativeUiCommandId = previous;
            return started;
        }
        catch { if (nativeUiCommandId == command) nativeUiCommandId = previous; throw; }
    }

    private int OnNativeCommandClosed(int command, int reason)
    {
        if (nativeUiCommandId == command) nativeUiCommandId = 0;
        if (command == 2 && reason == -2 && command == nativeFileOpenCommandId && nativeFileOpenInProgress != 0)
            nativeOpenCompletionEpoch = nativeLoadEpoch;
        // swCommands_PmCancel=-1, swCommands_PmOK=-2. A successful File > Open
        // still needs FileOpenPostNotify, the documented end of native loading.
        var completed = reason == -1 || reason == -2 && IsAssemblyInsertionCommand(command);
        if (!completed || command != nativeFileOpenCommandId
            || command != 2 && !IsAssemblyInsertionCommand(command)
            || Volatile.Read(ref nativeFileOpenInProgress) == 0
            || ControlledOpenBoundary.IsActive || Volatile.Read(ref openOperationInProgress) != 0
            || Volatile.Read(ref controlledOpenInProgress) != 0) return 0;
        Interlocked.Exchange(ref nativeFileOpenInProgress, 0);
        nativeFileOpenCommandId = 0;
        ScheduleTreeRefresh();
        LogOperation("文件打开所属命令已结束，清除本次加载标记 command=" + command + " reason=" + reason);
        return 0;
    }

    private const string ArchiveBusyReminder =
        "SolidWorks当前命令或文件加载尚未结束，请先完成或取消插入、配合等操作，再点击“存档”。"
        + "本次尚未开始存档，文件和未保存修改均已保留。";

    private const string ArchiveLoadReminder = "SolidWorks 正在加载图档或引用，请等待加载完成后再点击“存档”。本次尚未开始存档。";
    private const string ArchiveDraftReminder = "正在备份本机草稿，请等待备份和引用加载完成后再点击“存档”。本次尚未开始存档。";
    private const string ArchiveSaveReminder = "已有本地保存请求，请等待保存完成后再点击“存档”。本次尚未开始存档。";

    private string ArchiveRequestBusyReason()
    {
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return ArchiveDraftReminder;
        if (Volatile.Read(ref nativeFileOpenInProgress) != 0) return ArchiveLoadReminder;
        if (pendingManagedSave != null || managedSaveInProgress) return ArchiveSaveReminder;
        return IsSolidWorksCommandActive() ? ArchiveBusyReminder : null;
    }

    private void ClearArchiveBusyReminders()
    {
        taskPaneControl?.ClearCheckoutReminder(ArchiveBusyReminder);
        taskPaneControl?.ClearCheckoutReminder(ArchiveLoadReminder);
        taskPaneControl?.ClearCheckoutReminder(ArchiveDraftReminder);
    }

    private bool CanStartArchiveRequest()
    {
        ReconcileNativeCommandBeforeUserOperation();
        var reason = ArchiveRequestBusyReason();
        if (reason == null) { ClearArchiveBusyReminders(); return true; }
        taskPaneControl?.SetCheckoutReminder(reason, true);
        LogOperation("存档窗口尚未创建，等待当前命令或文件加载结束 nativeOpen="
            + Volatile.Read(ref nativeFileOpenInProgress) + " sourceCommand=" + nativeFileOpenCommandId);
        if (reason == ArchiveBusyReminder)
            TopMostMessageBox.Show(taskPaneControl, reason, "存档准备提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void ReconcileNativeCommandBeforeUserOperation()
    {
        if (savedAssemblyActivation != null) savedAssemblyActivation.OwnerReferenceProbeFailed = false;
        TryCompleteFinishedNativeOpen();
        TryCompleteReturnedFileOpen();
        TryCompleteSavedInsertionForArchive();
    }

    // Reconcile a missing FileOpenPostNotify only with a matching command-complete
    // event plus an idle, stable native document. Time alone is never completion.
    private bool TryCompleteFinishedNativeOpen()
    {
        if (TryCompleteAssemblyReferenceLoad()) return true;
        if (TryCompleteLoadedReferenceActivation()) return true;
        var epoch = nativeLoadEpoch;
        bool Proof() => nativeFileOpenInProgress != 0 && nativeFileOpenCommandId == 2
            && nativeOpenCompletionEpoch == epoch && pendingOpenIdleEpoch == epoch
            && nativeLoadEpoch == epoch && NativeInsertionEvidenceAllowed();
        if (!Proof()) return false;
        try
        {
            if (!application.StartupProcessCompleted || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out var active);
            if (active) return false;
            var model = application.GetOpenDocumentByName(pendingOpenPath) as IModelDoc2;
            if (model == null || !ReferenceEquals(application.ActiveDoc, model)
                || !PathsEqual(model.GetPathName(), pendingOpenPath) || model.GetBlockingState() != 0) return false;
            var count = application.GetDocumentCount();
            var stamp = model.GetUpdateStamp();
            application.GetRunningCommandInfo(out _, out _, out active);
            if (active || application.CommandInProgress || model.GetBlockingState() != 0 || model.GetUpdateStamp() != stamp
                || application.GetDocumentCount() != count || !ReferenceEquals(application.ActiveDoc, model) || !Proof()) return false;
            OnFileOpened(pendingOpenPath);
            LogOperation("已核对打开命令完成及稳定文档，恢复结构读取：" + pendingOpenPath);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对原生打开结束状态", error); return false; }
    }

    private bool TryCompleteLoadedReferenceActivation()
    {
        var epoch = nativeLoadEpoch;
        var sourceCommand = nativeFileOpenCommandId;
        var confirmAcrossIdle = sourceCommand == SketchCircleCommand || sourceCommand == OpenComponentFileCommand;
        var activationNotified = nativeDocumentLoadedEpoch == epoch && nativeActivationCompletedEpoch == epoch;
        bool Proof() => nativeFileOpenInProgress != 0
            && nativeFileOpenCommandId == sourceCommand
            && (confirmAcrossIdle || sourceCommand == 0 && activationNotified)
            && pendingOpenIdleEpoch == epoch && nativeLoadEpoch == epoch && unknownAddedEpoch == 0 && !unknownRemovalObserved
            && NativeInsertionEvidenceAllowed();
        if (!Proof()) return false;
        try
        {
            if (application == null || !application.StartupProcessCompleted || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out var busy);
            if (busy) return false;
            var active = application.ActiveDoc as IModelDoc2;
            var path = active?.GetPathName();
            if (active == null || string.IsNullOrWhiteSpace(path) || PathsEqual(path, currentDocumentIdentity)
                || active.GetBlockingState() != 0) return false;
            lock (activeTreeCacheSync)
                if (!activeTreeCache.Values.Any(entry => PathsEqual(DocumentIdentity(entry.Tree), path)
                    && (!confirmAcrossIdle || entry.Tree.Kind == CadDocumentKind.Assembly))) return false;
            if (!controlledOpenManifests.Any(context => context.Value.Files.Any(file => file.IsRoot
                    && PathsEqual(ControlledReferenceMaterializer.LocalPath(context.Key, file), path))
                && context.Value.Files.Any(file => PathsEqual(ControlledReferenceMaterializer.LocalPath(context.Key, file), pendingOpenPath)))) return false;
            var loaded = application.GetOpenDocumentByName(pendingOpenPath) as IModelDoc2;
            if (loaded == null || !PathsEqual(loaded.GetPathName(), pendingOpenPath) || loaded.GetBlockingState() != 0) return false;
            var stamp = active.GetUpdateStamp();
            var referenceStamp = confirmAcrossIdle ? loaded.GetUpdateStamp() : 0;
            var count = application.GetDocumentCount();
            application.GetRunningCommandInfo(out _, out _, out busy);
            if (busy || application.CommandInProgress || active.GetBlockingState() != 0
                || loaded.GetBlockingState() != 0 || active.GetUpdateStamp() != stamp || application.GetDocumentCount() != count
                || !ReferenceEquals(application.ActiveDoc, active) || !Proof()) return false;
            if (confirmAcrossIdle)
            {
                if (loaded.GetUpdateStamp() != referenceStamp || !Proof()) return false;
                // Some versions load nested references without a complete pair of
                // document-load/activation events. Confirm the same objects, stamps
                // and document count on two separate native idle notifications.
                var prior = referenceActivationObservation;
                if (prior == null || prior.Epoch != epoch || prior.Count != count
                    || prior.AssemblyStamp != stamp || prior.ReferenceStamp != referenceStamp
                    || !ReferenceEquals(prior.Assembly, active) || !ReferenceEquals(prior.Reference, loaded))
                {
                    referenceActivationObservation = new ReferenceActivationObservation
                    {
                        Epoch = epoch, IdleSequence = nativeIdleSequence, Count = count,
                        Assembly = active, AssemblyStamp = stamp, Reference = loaded, ReferenceStamp = referenceStamp
                    };
                    return false;
                }
                if (prior.IdleSequence == nativeIdleSequence) return false;
                referenceActivationObservation = null;
                if (nativeUiCommandId == sourceCommand) nativeUiCommandId = 0;
            }
            OnFileOpened(pendingOpenPath);
            LogOperation("已核对引用加载完成和总装激活，恢复设计树：" + path);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对总装切换后的引用加载状态", error); return false; }
    }

    // File > Open on a STEP-derived child can return to the original assembly
    // without a post-open notification or an open document for the child.
    // Only reconcile on a foreground operation. Idle during a native load is
    // not sufficient evidence to start an assembly scan.
    private bool TryCompleteReturnedFileOpen()
    {
        var epoch = nativeLoadEpoch;
        var host = pendingOpenHost;
        var path = pendingOpenPath;
        bool SameRequest() => nativeFileOpenInProgress != 0 && nativeFileOpenCommandId == 2
            && (nativeUiCommandId == 0 || nativeUiCommandId == 2) && epoch == nativeLoadEpoch
            && pendingOpenIdleEpoch == epoch && host != null && ReferenceEquals(host, assemblyEventDocument)
            && NativeInsertionEvidenceAllowed();
        if (!SameRequest() || string.IsNullOrWhiteSpace(path)
            || !(path.EndsWith(".stp.SLDASM", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".step.SLDASM", StringComparison.OrdinalIgnoreCase))
            || System.Diagnostics.Stopwatch.GetTimestamp() - pendingOpenStarted < 5L * System.Diagnostics.Stopwatch.Frequency)
            return false;
        try
        {
            if (!application.StartupProcessCompleted || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out var active);
            if (active || !ReferenceEquals(application.ActiveDoc, host) || host.GetBlockingState() != 0
                || !PathsEqual(host.GetPathName(), pendingOpenHostPath) || PathsEqual(path, assemblyEventDocumentPath))
                return false;
            var count = application.GetDocumentCount();
            var stamp = host.GetUpdateStamp();
            if (application.GetOpenDocumentByName(path) != null || application.GetDocumentCount() != count
                || host.GetUpdateStamp() != stamp || !ReferenceEquals(application.ActiveDoc, host)
                || host.GetBlockingState() != 0 || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out active);
            if (active || !SameRequest()) return false;
            Interlocked.Exchange(ref nativeFileOpenInProgress, 0);
            nativeFileOpenCommandId = nativeUiCommandId = 0;
            pendingOpenHost = null;
            nativeLoadEpoch++;
            ScheduleTreeRefresh(forceScan: false);
            LogOperation("显式操作已核对打开命令返回原装配，恢复结构读取：图档=" + path + "；宿主=" + assemblyEventDocumentPath);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对原生打开状态", error); return false; }
    }
}
