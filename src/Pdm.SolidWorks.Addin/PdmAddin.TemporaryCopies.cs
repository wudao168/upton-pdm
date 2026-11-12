using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private Task<ArchiveTemporaryCopyCleanupResult> CleanArchiveTemporaryCopiesAsync() =>
        Task.Run(() => ArchiveTemporaryCopyCleaner.Clean(ArchiveTemporaryCopyCleanupBusy, HasUnconfirmedCacheInstances));

    private bool ArchiveTemporaryCopyCleanupBusy()
    {
        if (Volatile.Read(ref checkInOperationInProgress) != 0 || Volatile.Read(ref workspaceOperationInProgress) != 0
            || Volatile.Read(ref openOperationInProgress) != 0 || Volatile.Read(ref controlledOpenInProgress) != 0
            || Volatile.Read(ref nativeFileOpenInProgress) != 0
            || ArchiveSession.Current != null && !ArchiveSession.Current.IsFinished) return true;
        return false;
    }

    private bool HasUnconfirmedCacheInstances() => CacheInstanceRegistry.Read(
        controlledWorkspace?.WorkspaceRoot ?? Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot()).UnconfirmedProcesses.Count != 0;
}
