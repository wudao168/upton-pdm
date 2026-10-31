using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr ResidencyOwner(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static async Task ArchiveWindowResidencyScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        var interactive = Environment.GetEnvironmentVariable("UPLM_RESIDENCY_INTERACTIVE") == "1";
        if (interactive) { host.Opacity = 1; host.ShowInTaskbar = true; host.Text = "UPLM 显示回归：模拟 SolidWorks"; }
        async Task WaitForForeground(string step)
        {
            if (!interactive) return;
            File.WriteAllText(Path.Combine(output, "await-" + step), "等待桌面窗口切换验证");
            for (var attempt = 0; attempt < 1800; attempt++)
            { if (File.Exists(Path.Combine(output, "continue-" + step))) return; await Task.Delay(100); }
            throw new TimeoutException("未完成隔离窗口前台切换：" + step);
        }
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
        try
        {
            Check(ArchiveUi(session, () => { LayerShow(handle, 0); return LayerVisible(handle); }),
                "存档期间直接隐藏窗口的请求不能让窗口消失");
            Check(ArchiveUi(session, () => { LayerRaise(handle, IntPtr.Zero, 0, 0, 0, 0, 0x97); return LayerVisible(handle); }),
                "存档期间通过窗口位置标记隐藏的请求也被拦截");
            ShowOwnedPopups(host.Handle, false); await Task.Delay(400);
            Check(LayerVisible(handle), "SolidWorks批量隐藏附属弹窗时存档窗口常驻");
            ArchiveUi(session, () => LayerRaise(handle, IntPtr.Zero, 0, 0, 0, 0, 0x497));
            await Task.Delay(400);
            Check(LayerVisible(handle), "绕过隐藏通知的原生操作仍由独立显示检查恢复");
            ArchiveUi(session, () => { ResidencyOwner(handle, -8, IntPtr.Zero); LayerRaise(handle, new IntPtr(1), 0, 0, 0, 0, 0x497); return true; });
            await Task.Delay(400);
            Check(LayerWindow(handle, 4) == (IntPtr)Get(session, "PresentationOwnerHandle") && LayerVisible(handle),
                "工程图切换后恢复同一界面线程的所属窗口，不重新绑定忙碌的CAD线程");
            Check((LayerWindowStyle(handle, -20) & 8) == 0 && !ArchiveUi(session, () => ArchiveWindow(session).TopMost),
                "保持前端显示不使用系统级置顶");
            Check(!ArchiveUi(session, () => ArchiveWindow(session).MinimizeBox), "存档窗口不能单独最小化而使操作者误判，仍可最小化SolidWorks");
            using (var foreground = new Form { Text = "UPLM 显示回归：其他应用", Opacity = interactive ? 1 : 0, ShowInTaskbar = interactive })
            {
                foreground.Show();
                await WaitForForeground("foreign");
                ArchiveUi(session, () => LayerRaise(handle, foreground.Handle, 0, 0, 0, 0, 0x13));
                Check(LayerDepth(foreground.Handle) < LayerDepth(handle), "其他应用模拟窗口可正常覆盖存档窗口");
                var actualForeground = GetForegroundWindow();
                ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", foreground.Handle); return true; });
                Check(LayerDepth(foreground.Handle) < LayerDepth(handle) && GetForegroundWindow() == actualForeground,
                    "其他应用在前端时不改变窗口顺序或抢焦点");
            }
            using (var cadFrame = new Form { Text = "UPLM 显示回归：模拟工程图", Opacity = interactive ? 1 : 0, ShowInTaskbar = interactive })
            {
                cadFrame.Show(host);
                ArchiveUi(session, () => LayerRaise(handle, cadFrame.Handle, 0, 0, 0, 0, 0x13));
                Check(LayerDepth(cadFrame.Handle) < LayerDepth(handle), "隔离复现SolidWorks新图档窗口遮住存档窗口");
                host.Enabled = true;
                await WaitForForeground("cad");
                await Task.Delay(150);
                Check(GetForegroundWindow() == host.Handle || GetForegroundWindow() == handle || GetForegroundWindow() == cadFrame.Handle, "隔离模拟SolidWorks切换图档后所属窗口组取得前端");
                ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", host.Handle); return true; });
                await Task.Delay(150);
                Check(LayerDepth(handle) < LayerDepth(cadFrame.Handle), "SolidWorks前端的新图档窗口不会持续遮住存档窗口");
                var beforeActivation = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
                cadFrame.Activate();
                Thread.Sleep(1200);
                Check(ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks")) >= beforeActivation + 2,
                    "切换工程图后CAD主线程长时间读取，存档窗口计时和显示维护仍可响应");
            }
            LayerShow(host.Handle, 6); await Task.Delay(400);
            Check(!LayerVisible(handle), "最小化SolidWorks时存档窗口正常随之隐藏");
            LayerShow(host.Handle, 9); await Task.Delay(400);
            Check(LayerVisible(handle), "恢复SolidWorks时自动恢复原存档窗口");
            var ticks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
            Thread.Sleep(1000);
            Check(ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks")) > ticks,
                "CAD调用线程阻塞时窗口及计时仍由独立界面线程维护");
            TreeCall(session, "RequestCancellation");
            await Task.Delay(300);
            Check(LayerVisible(handle), "等待安全取消时仍保持存档窗口显示");
            TreeCall(session, "Complete", "本次隔离显示检查完成。", false, false);
            ArchiveUi(session, () => { ArchiveWindow(session).Hide(); return true; });
            await Task.Delay(300);
            Check(!LayerVisible(handle), "操作真正结束后关闭或隐藏不被重新弹出");
        }
        finally
        {
            ShowOwnedPopups(host.Handle, true);
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }
}
