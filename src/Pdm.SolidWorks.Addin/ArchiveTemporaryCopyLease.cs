using System;
using System.Collections.Generic;
using System.IO;

namespace Upton.Pdm.SolidWorks;

// A copy is leased from its creation through the last upload/recovery use. The
// OS releases the lease on a crash, so abandoned copies can still be reclaimed.
internal static class ArchiveTemporaryCopyLease
{
    internal const string FileName = ".uplm-upload-active";
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, FileStream> Active = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
    internal static void Start(string directory)
    {
        lock (Sync) Active.Add(Path.GetFullPath(directory), new FileStream(Path.Combine(directory, FileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
    }
    internal static void Release(string directory)
    {
        lock (Sync)
        {
            var key = Path.GetFullPath(directory);
            if (!Active.TryGetValue(key, out var lease)) return;
            Active.Remove(key); lease.Dispose();
        }
    }
}
