using System;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // The dedicated reader remains available to explicit conversion/diagnostic
    // jobs. Ordinary PDM check-in/check-out never starts this session.
    public static global::SolidWorks.Interop.sldworks.ISldWorks StartNativeArchiveSession(string jobDirectory, Action<int, long> started)
        => ArchiveNativeProcessWorkspace.StartReader(jobDirectory, started);

    public static bool IsNativeArchiveReaderAddin(string jobDirectory, int processId)
        => ArchiveNativeProcessWorkspace.IsReaderAddin(jobDirectory, processId);

    private bool archiveReaderAddin;
}