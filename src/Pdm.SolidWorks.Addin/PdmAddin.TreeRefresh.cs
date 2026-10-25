using System.Threading;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private void SchedulePermissionStateRefresh()
    {
        CancelMetadataRefresh();
        if (currentProjectId.HasValue)
            lock (metadataCacheSync) projectDocumentsCache.Remove(currentProjectId.Value);
        // Permission changes update the existing nodes. Structural events still
        // invalidate the cached tree and retain their pending forced scan.
        ScheduleTreeRefresh(forceScan: false);
    }

    private bool DeferBackgroundTreeRefresh()
    {
        if (Volatile.Read(ref batchDialogInProgress) == 0
            && Volatile.Read(ref refreshSuppressionDepth) == 0
            && Volatile.Read(ref workspaceOperationInProgress) == 0
            && Volatile.Read(ref openOperationInProgress) == 0
            && Volatile.Read(ref checkInOperationInProgress) == 0
            && Volatile.Read(ref controlledOpenInProgress) == 0) return false;

        ScheduleTreeRefresh(false, false);
        return true;
    }
}
