using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks.PreviewWorker;

internal static partial class Program
{
    private sealed partial class ConversionSession
    {
        private void StartPrivateApplication()
        {
            if (dedicatedHost)
                foreach (var existing in Process.GetProcessesByName("SLDWORKS"))
                    using (existing)
                    {
                        if (existing.HasExited) continue;
                        var started = existing.StartTime.ToUniversalTime().Ticks;
                        Console.Error.WriteLine("专用转图电脑自动回收残留SolidWorks；进程=" + existing.Id + "；启动标识=" + started);
                        existing.CloseMainWindow();
                        if (!existing.WaitForExit(5000) && existing.StartTime.ToUniversalTime().Ticks == started)
                        { existing.Kill(); existing.WaitForExit(5000); }
                        if (!existing.HasExited) throw new InvalidOperationException("专用转图电脑的残留SolidWorks未能退出，请检查代理运行权限后重试。");
                    }
            // Launch explicitly and bind only its PID. COM activation can return an existing user's instance.
            using var classKey = Registry.ClassesRoot.OpenSubKey(@"SldWorks.Application\CLSID");
            using var serverKey = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classKey?.GetValue(null) + @"\LocalServer32");
            var command = System.Convert.ToString(serverKey?.GetValue(null))?.Trim() ?? "";
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var executable = end >= 0 ? command.Substring(0, end + 4).Trim('"') : "";
            if (!File.Exists(executable)) throw new InvalidOperationException("未找到已安装的SolidWorks程序，请在转图电脑修复SolidWorks安装后重试。");
            using var journal = PreviewJournalScope.Begin(executable);
            ownedJob = new OwnedSolidWorksJob();
            ownedProcess = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = journal.WorkingDirectory
            });
            if (ownedProcess == null) throw new InvalidOperationException("无法启动专用SolidWorks转图进程，请检查许可和程序安装。");
            ownedStartTicksUtc = ownedProcess.StartTime.ToUniversalTime().Ticks;
            ownedJob.Attach(ownedProcess);
            Console.Error.WriteLine("专用SolidWorks已启动；进程=" + ownedProcess.Id + "；启动标识=" + ownedStartTicksUtc
                + "；桌面会话=" + ownedProcess.SessionId);
            var timer = Stopwatch.StartNew();
            while (!ownedProcess.HasExited && timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                application = ApplicationFromProcess(ownedProcess.Id);
                if (application != null && application.GetProcessID() == ownedProcess.Id && application.StartupProcessCompleted)
                {
                    // Only the process created above may receive these settings.
                    application.UserControl = false;
                    application.Visible = false;
                    return;
                }
                if (application != null) { Marshal.FinalReleaseComObject(application); application = null; }
                Thread.Sleep(200);
            }
            Console.Error.WriteLine("专用SolidWorks启动失败；进程=" + ownedProcess.Id + "；已退出=" + ownedProcess.HasExited
                + (ownedProcess.HasExited ? "；退出码=" + ownedProcess.ExitCode : "；连接等待超时"));
            throw new InvalidOperationException("专用SolidWorks启动未完成，请检查转图电脑的许可或启动弹窗后重试；现有工作会话已保留。");
        }
    }

    [DllImport("ole32.dll")] private static extern int CreateBindCtx(uint reserved, out IBindCtx context);
    private static ISldWorks ApplicationFromProcess(int processId)
    {
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
        IRunningObjectTable table = null; IEnumMoniker entries = null;
        try
        {
            context.GetRunningObjectTable(out table); table.EnumRunning(out entries);
            var values = new IMoniker[1];
            while (entries.Next(1, values, IntPtr.Zero) == 0)
            {
                var entry = values[0];
                try
                {
                    entry.GetDisplayName(context, null, out var name);
                    if (string.Equals(name, "SolidWorks_PID_" + processId, StringComparison.OrdinalIgnoreCase))
                    { table.GetObject(entry, out var value); return value as ISldWorks; }
                }
                catch (UnauthorizedAccessException) { }
                finally { Marshal.ReleaseComObject(entry); }
            }
            return null;
        }
        finally
        {
            if (entries != null) Marshal.ReleaseComObject(entries);
            if (table != null) Marshal.ReleaseComObject(table);
            Marshal.ReleaseComObject(context);
        }
    }

    // The handle belongs to the worker, so its crash or timeout cannot leave an orphan CAD process.
    // Only explicitly launched processes are attached; an existing engineer's process is never included.
    private sealed class OwnedSolidWorksJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public OwnedSolidWorksJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new InvalidOperationException("无法创建专用转图进程保护，请检查系统资源后重试。");
            SetLimits(0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        }
        public void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                // The caller has just created this exact process; no COM object has been attached yet.
                process.Kill();
                throw new InvalidOperationException("无法保护专用转图进程，已停止本次新建进程，请检查系统资源后重试。");
            }
        }
        public void PreserveProcess() => SetLimits(0);
        private void SetLimits(uint flags)
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = flags } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new InvalidOperationException("专用转图进程保护设置失败，请检查系统资源后重试。");
        }
        public void Dispose() => handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr Minimum, Maximum;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint length);
    }
}
