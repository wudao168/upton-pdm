using System;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Explicit conversion jobs and saved-property path conflicts use the same
    // dedicated reader. Normal local reads stay in the interactive process.
    public static global::SolidWorks.Interop.sldworks.ISldWorks StartNativeArchiveSession(string jobDirectory, Action<int, long> started)
        => ArchiveNativeProcessWorkspace.StartReader(jobDirectory, started);

    public static bool IsNativeArchiveReaderAddin(string jobDirectory, int processId)
        => ArchiveNativeProcessWorkspace.IsReaderAddin(jobDirectory, processId);

    private bool archiveReaderAddin;
}