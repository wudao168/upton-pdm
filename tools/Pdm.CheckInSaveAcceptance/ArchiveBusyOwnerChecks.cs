using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint BusyOwnerThread(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr BusyOwnerPing(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    static async Task ArchiveBusyOwnerScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        host.Text = "UPLM 隔离验证：忙碌的 CAD 主窗口";
        var ownerHandle = host.Handle;
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { ownerHandle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        try
        {
            var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
            var presentationOwner = LayerWindow(handle, 4);
            for (var ancestor = presentationOwner; ancestor != IntPtr.Zero; ancestor = LayerWindow(ancestor, 4))
                TreeCheck(BusyOwnerThread(ancestor, out _) == BusyOwnerThread(handle, out _), "原生所属链仍跨越CAD线程");
            Check(presentationOwner != IntPtr.Zero && presentationOwner != ownerHandle
                && BusyOwnerThread(presentationOwner, out _) == BusyOwnerThread(handle, out _),
                "存档窗口的原生所属窗口在同一界面线程，不与忙碌的CAD输入队列绑定");
            Check((IntPtr)Get(session, "OwnerHandle") == ownerHandle, "逻辑归属仍指向原SolidWorks主窗口");
            Check(!LayerVisible(presentationOwner) && !ArchiveUi(session, () => ArchiveWindow(session).ShowInTaskbar),
                "隔离所属窗口不可见，存档窗口不增加任务栏入口");
            Check((LayerWindowStyle(handle, -20) & 8) == 0, "持续响应不依赖系统级置顶");
            Check(ArchiveUi(session, () =>
            {
                var bounds = ArchiveWindow(session).Bounds;
                var area = Screen.FromHandle(ownerHandle).WorkingArea;
                return area.Contains(bounds) && Math.Abs(bounds.Left + bounds.Right - area.Left - area.Right) <= 1
                    && Math.Abs(bounds.Top + bounds.Bottom - area.Top - area.Bottom) <= 1;
            }), "隔离所属关系后仍居中于SolidWorks所在显示器工作区");

            for (var phase = 2; phase <= 4; phase++)
            {
                var currentPhase = phase;
                var ticks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
                var cadReleased = new ManualResetEventSlim();
                var blocked = Stopwatch.StartNew();
                var work = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        TreeCall(session, "SetPhase", currentPhase, "正在核对原生图档，请等待当前读取结束");
                        var uiStage = "等待界面调用";
                        var uiElapsed = Stopwatch.StartNew();
                        var ui = Task.Run(() => ArchiveUi(session, () =>
                        {
                            uiStage = "维护显示关系";
                            var window = ArchiveWindow(session);
                            TreeCall(window, "MaintainResidency", ownerHandle);
                            uiStage = "激活窗口"; window.Activate();
                            uiStage = "重绘窗口"; window.Invalidate(true); window.Update();
                            uiStage = "捕获绘制结果";
                            var bitmap = new Bitmap(window.Width, window.Height);
                            try
                            {
                                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                var nonBlack = 0;
                                for (var y = 40; y < bitmap.Height - 20; y += 16)
                                    for (var x = 20; x < bitmap.Width - 20; x += 16)
                                    { var pixel = bitmap.GetPixel(x, y); if (pixel.R + pixel.G + pixel.B > 60) nonBlack++; }
                                TreeCheck(nonBlack > 100, "CAD主线程阻塞时存档页面无法完成绘制");
                                uiStage = "界面绘制完成";
                                return bitmap;
                            }
                            catch { bitmap.Dispose(); throw; }
                        }));
                        if (await Task.WhenAny(ui, Task.Delay(2000)).ConfigureAwait(false) != ui)
                            throw new TimeoutException("CAD主线程阻塞时界面检查超时，位置=" + uiStage + "，耗时=" + uiElapsed.ElapsedMilliseconds + "ms");
                        using (var bitmap = await ui.ConfigureAwait(false))
                        {
                            File.AppendAllText(Path.Combine(output, "paint-timing.txt"), "阶段" + currentPhase + "界面响应=" + uiElapsed.ElapsedMilliseconds + "ms" + Environment.NewLine);
                            // Artifact I/O is deliberately outside the presentation STA.
                            bitmap.Save(Path.Combine(output, "busy-phase-" + currentPhase + ".png"));
                        }
                        while (blocked.ElapsedMilliseconds < 6500)
                        {
                            if (BusyOwnerPing(handle, 0, IntPtr.Zero, IntPtr.Zero, 2, 250, out _) == IntPtr.Zero)
                                throw new TimeoutException("CAD主线程阻塞超过5秒时存档窗口不能响应消息");
                            await Task.Delay(200).ConfigureAwait(false);
                        }
                        var updatedTicks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
                        TreeCheck(updatedTicks >= ticks + 12, "CAD主线程阻塞期间计时或消息循环停止");
                        if (currentPhase == 4)
                        {
                            ArchiveUi(session, () => { TreeCall(session, "RequestCancellation"); return true; });
                            TreeCheck(((CancellationToken)Get(session, "Token")).IsCancellationRequested,
                                "CAD主线程阻塞时无法发出安全取消请求");
                        }
                    }
                    finally { cadReleased.Set(); }
                });
                // Deliberately no message pump: native CAD calls can block its STA for many seconds.
                if (!cadReleased.Wait(12000)) throw new TimeoutException("隔离忙碌主窗口测试超时");
                await work;
                cadReleased.Dispose();
                Check(LayerVisible(handle) && ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle,
                    "阶段" + phase + "：主线程持续阻塞时同一存档窗口仍可见并完成重绘");
                Check(ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks")) >= ticks + 12,
                    "阶段" + phase + "：超过系统未响应判断时长，计时和消息仍持续响应");
            }
            Check(((CancellationToken)Get(session, "Token")).IsCancellationRequested, "原生读取未结束时取消请求及时传递且窗口保留");
            TreeCall(session, "Complete", "隔离验证完成，本地文件未改动。", false, false);
            LayerShow(ownerHandle, 6); await Task.Delay(400);
            Check(!LayerVisible(handle), "完成结果页面仍随SolidWorks最小化隐藏");
            LayerShow(ownerHandle, 9); await Task.Delay(400);
            Check(LayerVisible(handle), "恢复SolidWorks后显示原结果页面");
            ArchiveUi(session, () => { ArchiveWindow(session).Hide(); return true; });
            await Task.Delay(400);
            Check(!LayerVisible(handle), "用户隐藏完成窗口后不被显示维护反复弹出");
        }
        finally
        {
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
        Check(IsWindowEnabled(ownerHandle), "关闭后恢复CAD可操作状态");
        using (var closingOwner = new Form { ShowInTaskbar = false, Opacity = 0 })
        {
            closingOwner.Show(host);
            var closingSession = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { closingOwner.Handle, CancellationToken.None });
            await (Task)Get(closingSession, "Ready");
            try
            {
                closingOwner.Close();
                var closed = (Task)Get(closingSession, "Closed");
                Check(await Task.WhenAny(closed, Task.Delay(2000)) == closed,
                    "SolidWorks主窗口真正销毁时结束存档窗口，不留下独立残窗");
                Check(((CancellationToken)Get(closingSession, "Token")).IsCancellationRequested,
                    "主窗口销毁时取消未完成操作");
            }
            finally
            {
                if (!(bool)Get(closingSession, "IsClosed")) ArchiveClose(closingSession);
                await (Task)Get(closingSession, "Closed"); ((IDisposable)closingSession).Dispose();
            }
        }
    }
}
