using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

// Lifecycle records only: never enumerate nodes or query an accessibility provider.
internal sealed class TreeControlDiagnostics
{
    private readonly TreeView tree;
    private readonly string role;
    private readonly string instance = Guid.NewGuid().ToString("N");
    private IntPtr handle, parent;
    private uint process, nativeThread;
    private int generation;

    private TreeControlDiagnostics(TreeView tree, string role)
    {
        this.tree = tree; this.role = role;
        tree.HandleCreated += Created;
        tree.HandleDestroyed += Destroyed;
        tree.Disposed += Disposed;
    }

    internal static void Attach(TreeView tree, string role) => new TreeControlDiagnostics(tree, role);

    private void Created(object sender, EventArgs args)
    {
        try
        {
            handle = tree.Handle;
            parent = tree.Parent?.IsHandleCreated == true ? tree.Parent.Handle : IntPtr.Zero;
            nativeThread = GetWindowThreadProcessId(handle, out process);
            generation++;
            Record("创建完成");
        }
        catch { /* Diagnostics must never prevent native control creation. */ }
    }

    private void Destroyed(object sender, EventArgs args) => Record("句柄销毁");

    private void Disposed(object sender, EventArgs args)
    {
        Record("控件释放");
        tree.HandleCreated -= Created;
        tree.HandleDestroyed -= Destroyed;
        tree.Disposed -= Disposed;
    }

    private void Record(string stage)
    {
        try
        {
            // Use the saved handle during teardown; reading Control.Handle can recreate it.
            PluginDiagnosticLog.Record("树控件生命周期", "控件=" + role + "；类型=" + tree.GetType().Name
                + "；实例=" + instance + "；代次=" + generation + "；事件=" + stage
                + "；句柄=0x" + handle.ToInt64().ToString("x") + "；父句柄=0x" + parent.ToInt64().ToString("x")
                + "；进程=" + process + "；所属原生线程=" + nativeThread + "；当前原生线程=" + GetCurrentThreadId()
                + "；托管线程=" + Thread.CurrentThread.ManagedThreadId + "；正在释放=" + tree.Disposing + "；已释放=" + tree.IsDisposed);
        }
        catch { /* Logging failures must not affect disposal or SolidWorks. */ }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
