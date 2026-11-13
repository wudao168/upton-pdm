using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 && args.Length != 5) return 2;
        var mode = args[0];
        if (mode != "download" && mode != "restart" && mode != "exit" && mode != "recover") return 2;
        if (args.Length == 1 && mode != "recover") return 2;
        var root = args.Length == 1 ? Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) : Path.GetFullPath(args[1]);
        var expectedParent = Path.Combine(ClientUpdateState.LocalRoot, "updater");
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase)) return 2;

        try
        {
            using var request = WaitForRequest(root);
            using var lease = request == null ? null : ClientUpdateState.TryLock(Path.Combine(root, "worker.lock"));
            if (lease == null) return 1;
            var state = ClientUpdateState.Read<ClientUpdateState>(Path.Combine(root, "state.json"));
            if (state == null) return 2;
            var job = new UpdateJob(root, state);
            using var parent = args.Length == 5 ? FindParent(args[3], args[4]) : null;
            if (args.Length == 5)
            {
                if (!EventWaitHandle.TryOpenExisting(@"Local\UPLM.Update." + args[2], out var signal))
                    return 1;
                using (signal) signal.Set();
            }
            request.Dispose();
            if (mode == "restart" || mode == "exit")
            {
                if (parent != null && !parent.WaitForExit(15000)) return 1;
            }
            job.Run(mode);
            return 0;
        }
        catch (Exception error)
        {
            ClientUpdateState.Log(root, error);
            return 1;
        }
    }

    private static FileStream WaitForRequest(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var lease = ClientUpdateState.TryLock(Path.Combine(root, "request.lock"));
            if (lease != null) return lease;
            Thread.Sleep(100);
        }
        return null;
    }

    private static Process FindParent(string pidText, string ticksText)
    {
        if (!int.TryParse(pidText, out var pid) || !long.TryParse(ticksText, out var ticks)) return null;
        Process process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == ticks) return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }
}
