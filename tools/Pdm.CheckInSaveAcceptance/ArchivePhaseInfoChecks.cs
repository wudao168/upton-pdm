using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll")] static extern bool ShowOwnedPopups(IntPtr owner, bool show);

    static async Task ArchivePhaseInfoScenario(Form host, string output, bool visibilityOnly)
    {
        Directory.CreateDirectory(output);
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        string Stage() => ArchiveUi(session, () => ((Label)Field(ArchiveWindow(session), "stage")).Text);
        try
        {
            if (visibilityOnly)
            {
                var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
                ShowOwnedPopups(host.Handle, false);
                await Task.Delay(400);
                Check(LayerVisible(handle), "图档切换期间所属窗口暂时隐藏弹窗，存档窗口保持可见");
                Check(!ArchiveUi(session, () => ArchiveWindow(session).TopMost) && (LayerWindowStyle(handle, -20) & 8) == 0,
                    "保持可见不使用系统级置顶");
                Check((IntPtr)Get(session, "OwnerHandle") == host.Handle
                    && LayerWindow(handle, 4) == (IntPtr)Get(session, "PresentationOwnerHandle"),
                    "保持SolidWorks逻辑归属及同一存档窗口，原生所属关系与CAD线程隔离");
                using (var foreign = new Form { Opacity = 0, ShowInTaskbar = false })
                {
                    foreign.Show();
                    ArchiveUi(session, () => LayerRaise(handle, foreign.Handle, 0, 0, 0, 0, 0x13));
                    Check(LayerDepth(foreign.Handle) < LayerDepth(handle), "隔离的其他应用模拟窗口可排在存档窗口之前");
                    ShowOwnedPopups(host.Handle, false); await Task.Delay(500);
                    Check(LayerVisible(handle) && LayerDepth(foreign.Handle) < LayerDepth(handle),
                        "临时隐藏后恢复保持原有窗口顺序，不抢到其他应用之前");
                }
                LayerShow(host.Handle, 6); await Task.Delay(400);
                Check(!LayerVisible(handle), "用户最小化SolidWorks时存档窗口仍随之隐藏");
                LayerShow(host.Handle, 9); await Task.Delay(400);
                Check(LayerVisible(handle), "恢复SolidWorks后同一存档窗口恢复");
                var ticks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
                Thread.Sleep(1000);
                Check(ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks")) > ticks,
                    "显示保护不阻塞独立界面线程及计时");
                TreeCall(session, "Complete", "存档已完成。", false, false);
                ArchiveUi(session, () => { ArchiveWindow(session).Hide(); return true; });
                await Task.Delay(400);
                Check(!LayerVisible(handle), "完成后主动关闭或隐藏窗口不会被重新弹出");
                return;
            }
            Check(Stage().StartsWith("步骤1/10：读取文件清单｜"), "首帧读取即显示阶段编号及名称");
            var names = new[] { "读取文件清单", "核对版本与权限", "固定快照与最终校验", "上传文件" };
            var steps = new[] { 1, 4, 7, 8 };
            for (var phase = 1; phase <= 4; phase++)
            {
                TreeCall(session, "SetPhase", phase, "正在执行当前动作");
                TreeCall(session, "ReportFile", 1, 2, "阶段样件.SLDPRT", "没有编号的文件进度");
                Check(Stage() == "步骤" + steps[phase - 1] + "/10：" + names[phase - 1] + "｜没有编号的文件进度",
                    "阶段" + phase + "：无编号的嵌套动作继承实际阶段");
                TreeCall(session, "SetStage", "阶段1/4：正在核对引用版本");
                Check(Stage() == "步骤" + steps[phase - 1] + "/10：" + names[phase - 1] + "｜正在核对引用版本",
                    "阶段" + phase + "：旧消息前缀不能错误回退阶段");
                var history = ArchiveHistory(session);
                Check(history.Contains("步骤" + steps[phase - 1] + "/10：" + names[phase - 1] + "。提示："),
                    "阶段" + phase + "：进入阶段即记录中文说明");
                Check(history.Split(new[] { "步骤" + steps[phase - 1] + "/10：" + names[phase - 1] + "。提示：" }, StringSplitOptions.None).Length == 2,
                    "阶段" + phase + "：密集更新不会重复刷阶段说明");
            }
            ArchiveUi(session, () =>
            {
                var window = ArchiveWindow(session);
                Check(((ToolTip)Field(window, "stageTip")).GetToolTip((Control)Field(window, "stage")).Contains("等待整批提交确认"),
                    "处理信息悬停可查看完整动作和阶段提示");
                using (var bitmap = new Bitmap(window.Width, window.Height))
                { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "phase-info.png")); }
                return true;
            });
            TreeCall(session, "RequestCancellation");
            Check(Stage().StartsWith("步骤8/10：上传文件｜正在取消"), "取消等待仍保留所处阶段");
            TreeCall(session, "Complete", "已停止并核对服务器结果。", true, false);
            Check(Stage() == "存档未完成，请查看原因和处理建议。", "最终失败结果使用实际结果，不被阶段文案覆盖");
        }
        finally
        {
            ShowOwnedPopups(host.Handle, true);
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }
}
