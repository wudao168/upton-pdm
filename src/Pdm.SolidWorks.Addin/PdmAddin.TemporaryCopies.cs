using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private Task<ArchiveTemporaryCopyCleanupResult> CleanArchiveTemporaryCopiesAsync() =>
        Task.Run(() => ArchiveTemporaryCopyCleaner.Clean(ArchiveTemporaryCopyCleanupBusy));

    private bool ArchiveTemporaryCopyCleanupBusy()
    {
        if (Volatile.Read(ref checkInOperationInProgress) != 0 || Volatile.Read(ref workspaceOperationInProgress) != 0
            || Volatile.Read(ref openOperationInProgress) != 0 || Volatile.Read(ref controlledOpenInProgress) != 0
            || Volatile.Read(ref automaticDrawingOperationInProgress) != 0
            || ArchiveSession.Current != null && !ArchiveSession.Current.IsFinished) return true;
        int currentId;
        using (var current = Process.GetCurrentProcess()) currentId = current.Id;
        var otherInstance = false;
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
            using (process) if (process.Id != currentId) otherInstance = true;
        return otherInstance;
    }
}
