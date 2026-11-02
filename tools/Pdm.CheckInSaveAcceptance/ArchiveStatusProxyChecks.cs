using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ArchiveStatusProxyScenario(Form host, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "sldProcMon.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, fixture, true);
        var config = Assembly.GetExecutingAssembly().Location + ".config";
        if (File.Exists(config)) File.Copy(config, fixture + ".config", true);
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        Process monitor = null;
        void Check(bool value, string message) { TreeCheck(value, message); Console.WriteLine("PASS: " + message); }
        try
        {
            var archive = ArchiveUi(session, () => { var form = ArchiveWindow(session); form.Opacity = 0; ((System.Windows.Forms.Timer)Field(form, "residencyTimer")).Stop(); return form.Handle; });
            var bounds = host.Bounds; var handleFile = Path.Combine(output, "proxy-handle.txt");
            if (File.Exists(handleFile)) File.Delete(handleFile);
            monitor = Process.Start(new ProcessStartInfo(fixture, "--archive-status-proxy-host " + bounds.X + " " + bounds.Y + " " + bounds.Width + " " + bounds.Height + " \"" + handleFile + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            for (var attempt = 0; attempt < 100 && !File.Exists(handleFile); attempt++) await Task.Delay(50);
            var proxy = new IntPtr(long.Parse(File.ReadAllText(handleFile)));
            using (var foreign = new Form { Opacity = 0, ShowInTaskbar = false })
            {
                foreign.Show();
                LayerRaise(archive, new IntPtr(1), 0, 0, 0, 0, 0x13);
                LayerRaise(host.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                LayerRaise(proxy, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                LayerRaise(foreign.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                LayerRaise(proxy, foreign.Handle, 0, 0, 0, 0, 0x13);
                LayerRaise(host.Handle, proxy, 0, 0, 0, 0, 0x13);
                LayerRaise(archive, host.Handle, 0, 0, 0, 0, 0x13);
                Console.WriteLine("层级：foreign=" + LayerDepth(foreign.Handle) + ",proxy=" + LayerDepth(proxy) + ",cad=" + LayerDepth(host.Handle) + ",archive=" + LayerDepth(archive));
                Check(LayerDepth(foreign.Handle) < LayerDepth(proxy) && LayerDepth(proxy) < LayerDepth(archive), "复现独立资源监视进程的状态窗口覆盖存档窗口");
                var foreground = GetForegroundWindow();
                ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", foreign.Handle); return true; });
                Check(LayerDepth(foreign.Handle) < LayerDepth(archive) && LayerDepth(archive) < LayerDepth(proxy), "存档恢复到对应未响应替代窗口上方，其他软件仍在其上方");
                Check(GetForegroundWindow() == foreground && (LayerWindowStyle(archive, -20) & 8) == 0, "恢复层级不抢焦点、不使用全局置顶");
                var matcher = New("CadStatusWindow");
                Check((bool)TreeCall(matcher, "Matches", proxy, host.Handle), "识别当前CAD进程创建的资源监视器及其相同区域状态窗口");
                Check(!(bool)TreeCall(matcher, "Matches", foreign.Handle, host.Handle), "普通窗口不因位于相同进程或区域而被当成状态窗口");
                var identity = Type("CadStatusWindow").GetMethod("IsMonitorFor", Flags);
                Check(!(bool)identity.Invoke(null, new object[] { (uint)monitor.Id, (uint)monitor.Id }), "不把其他CAD实例的资源监视器关联到当前存档");
                Check(!(bool)identity.Invoke(null, new object[] { (uint)Process.GetCurrentProcess().Id, (uint)Process.GetCurrentProcess().Id }), "非资源监视器进程不参与层级识别");
                LayerRaise(proxy, IntPtr.Zero, bounds.X + 60, bounds.Y, bounds.Width, bounds.Height, 0x14);
                Check(!(bool)TreeCall(matcher, "Matches", proxy, host.Handle), "区域不对应的状态窗口不参与当前CAD置前");
                LayerRaise(proxy, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x14);
                Check((bool)TreeCall(matcher, "Matches", proxy, host.Handle), "位置恢复后重新核对窗口区域");
            }
        }
        finally
        {
            if (monitor != null) { if (!monitor.HasExited) monitor.Kill(); monitor.WaitForExit(); monitor.Dispose(); }
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }

    static readonly StatusWndProc statusProc = StatusWindowProcedure;
    static void RunArchiveStatusProxyHost(string[] args)
    {
        var cls = new StatusWindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(statusProc), Instance = StatusModule(null), Name = "swPM_GhostWindow" };
        if (StatusRegister(ref cls) == 0) throw new InvalidOperationException("无法创建隔离状态窗口类");
        var handle = StatusCreate(0x80080, cls.Name, "UPLM 隔离状态窗口", 0x90000000, int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), IntPtr.Zero, IntPtr.Zero, cls.Instance, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("无法创建隔离状态窗口");
        StatusAlpha(handle, 0, 0, 2);
        File.WriteAllText(args[5], handle.ToInt64().ToString());
        Application.Run();
    }
    static IntPtr StatusWindowProcedure(IntPtr h, uint m, IntPtr w, IntPtr l) { if (m == 2) StatusQuit(0); return StatusDefault(h, m, w, l); }
    delegate IntPtr StatusWndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StatusWindowClass { public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string Menu, Name; }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] static extern IntPtr StatusModule(string name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)] static extern ushort StatusRegister(ref StatusWindowClass value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] static extern IntPtr StatusCreate(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr StatusDefault(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")] static extern void StatusQuit(int code);
    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")] static extern bool StatusAlpha(IntPtr h, uint color, byte alpha, uint flags);
}
