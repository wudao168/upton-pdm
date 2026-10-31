using System;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private int nativeUiCommandId;
    private int nativeFileOpenCommandId;
    private int nativeLoadEpoch;
    private int unknownAddedEpoch;
    private int unknownSavedEpoch;
    private bool unknownRemovalObserved;
    private IModelDoc2 unknownLoadOwner;
    private string unknownLoadOwnerPath;

    private bool NativeInsertionEvidenceAllowed() => !disconnecting && !ControlledOpenBoundary.IsActive
        && Volatile.Read(ref ownedReferenceResolutionDepth) == 0
        && Volatile.Read(ref openOperationInProgress) == 0 && Volatile.Read(ref controlledOpenInProgress) == 0
        && Volatile.Read(ref workspaceOperationInProgress) == 0 && Volatile.Read(ref batchDialogInProgress) == 0
        && Volatile.Read(ref checkInOperationInProgress) == 0 && Volatile.Read(ref draftSnapshotInProgress) == 0
        && Volatile.Read(ref refreshSuppressionDepth) == 0 && Volatile.Read(ref automaticDrawingOperationInProgress) == 0
        && Volatile.Read(ref treeRefreshInProgress) == 0;

    private void ObserveNativeFileOpening(string fileName)
    {
        nativeLoadEpoch++;
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
        if (entityType == 2) { unknownRemovalObserved = true; unknownAddedEpoch = unknownSavedEpoch = 0; }
        return OnAssemblyItemChanged(entityType, itemName);
    }

    private void ObserveNativeOwnerSaved(string fileName)
    {
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

    private string ArchiveRequestBusyReason()
    {
        if (!IsSolidWorksCommandActive()) return null;
        return "SolidWorks当前命令或文件加载尚未结束，请先完成或取消插入、配合等操作，再点击“存档”。"
            + "本次尚未开始存档，文件和未保存修改均已保留。";
    }

    private bool CanStartArchiveRequest()
    {
        TryCompleteSavedInsertionForArchive();
        var reason = ArchiveRequestBusyReason();
        if (reason == null) return true;
        taskPaneControl?.SetCheckoutReminder(reason, true);
        LogOperation("存档窗口尚未创建，等待当前命令或文件加载结束 nativeOpen="
            + Volatile.Read(ref nativeFileOpenInProgress) + " sourceCommand=" + nativeFileOpenCommandId);
        TopMostMessageBox.Show(taskPaneControl, reason, "存档准备提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
