using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Upton.Pdm.SolidWorks;

// SOLIDWORKS Resource Monitor creates its busy overlay in a separate process.
// Associate only that CAD instance's monitor; it is not part of the HWND owner chain.
internal sealed class CadStatusWindow
{
    private IntPtr cachedWindow;
    private uint cachedProcess, cachedCadProcess;
    private bool cachedMatch;
    private long checkedAt;

    internal bool Matches(IntPtr window, IntPtr cadWindow)
    {
        var name = new StringBuilder(64);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() != "swPM_GhostWindow") return false;
        if (GetWindowThreadProcessId(window, out var process) == 0
            || GetWindowThreadProcessId(cadWindow, out var cadProcess) == 0) return false;
        var now = Stopwatch.GetTimestamp();
        if (cachedWindow != window || cachedProcess != process || cachedCadProcess != cadProcess
            || now - checkedAt >= Stopwatch.Frequency)
        {
            cachedWindow = window; cachedProcess = process; cachedCadProcess = cadProcess; checkedAt = now;
            cachedMatch = IsMonitorFor(process, cadProcess);
        }
        return cachedMatch && GetWindowRect(window, out var overlay) && GetWindowRect(cadWindow, out var cad)
            && cad.Right > cad.Left && cad.Bottom > cad.Top
            && Math.Abs((long)overlay.Left - cad.Left) <= 16 && Math.Abs((long)overlay.Top - cad.Top) <= 16
            && Math.Abs((long)overlay.Right - cad.Right) <= 16 && Math.Abs((long)overlay.Bottom - cad.Bottom) <= 16;
    }

    private static bool IsMonitorFor(uint process, uint cadProcess)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return false;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return false;
            do
            {
                if (entry.Process == process)
                    return entry.Parent == cadProcess && string.Equals(entry.File, "sldProcMon.exe", StringComparison.OrdinalIgnoreCase);
            } while (Process32Next(snapshot, ref entry));
            return false;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Process;
        public IntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
