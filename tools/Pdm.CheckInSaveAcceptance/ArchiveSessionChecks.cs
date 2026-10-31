using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    static object ArchiveCurrent => Type("ArchiveSession").GetProperty("Current", Flags).GetValue(null);
    static T ArchiveUi<T>(object session, Func<T> action) => (T)session.GetType().GetMethod("Invoke", Flags)
        .MakeGenericMethod(typeof(T)).Invoke(session, new object[] { action });
    static Form ArchiveWindow(object session) => (Form)Field(session, "window");
    static string ArchiveHistory(object session) => ArchiveUi(session, () => ((TextBox)Field(ArchiveWindow(session), "history")).Text);
    static void ArchiveClose(object session)
    {
        TreeCall(session, "FinishIfRunning");
        ArchiveUi(session, () => { ArchiveWindow(session).Close(); return true; });
    }

    static async Task ArchiveSessionScenario(Form host, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Control.CheckForIllegalCrossThreadCalls = true;
        await ArchiveSlowEntry(host);
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        try
        {
            var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var uiThread = ArchiveUi(session, () => Thread.CurrentThread.ManagedThreadId);
            TreeCheck(mainThread != uiThread, "存档界面仍使用SolidWorks主线程");
            var ticks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
            Thread.Sleep(850); // Simulate an uninterruptible CAD read on the caller's STA.
            TreeCheck(ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks")) >= ticks + 2,
                "主线程同步读取时存档窗口停止刷新");
            Console.WriteLine("PASS: Independent presentation STA keeps elapsed/status refresh alive during a blocked CAD caller");
            await ArchiveInlineSelection(session, output, handle);

            using (var proxy = (Form)New("BatchProgressDialog", 3))
            {
                TreeCall(proxy, "SetStage", "正在检查结构");
                TreeCall(proxy, "ReportFile", 0, 3, "ROOT.SLDASM", "正在核对文件");
                TreeCall(proxy, "ReportFile", 1, 3, "PART.SLDPRT", "正在准备上传");
                var history = ArchiveHistory(session);
                TreeCheck(history.Contains("正在检查结构") && history.Contains("ROOT.SLDASM") && history.Contains("PART.SLDPRT"), "阶段更新覆盖了以前的过程记录");
                TreeCheck(!proxy.IsHandleCreated && ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle, "进度代理创建或切换了前台窗口");
                Console.WriteLine("PASS: Legacy progress calls append the full process to one unchanged foreground window");
            }

            var answerDriver = Task.Run(async () =>
            {
                await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "confirmation")).Controls.Count > 0), "未出现同页确认区");
                ArchiveUi(session, () =>
                {
                    var actions = (FlowLayoutPanel)Field(ArchiveWindow(session), "confirmationActions");
                    actions.Controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.Yes).PerformClick();
                    return true;
                });
            });
            var answer = (DialogResult)TreeCall(session, "Confirm", "确认本次存档范围", "存档范围", MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2);
            await answerDriver;
            TreeCheck(answer == DialogResult.Yes && ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle,
                "同页确认没有保留窗口或返回了错误结果");
            Console.WriteLine("PASS: Business confirmation executes inline without another top-level window");

            ArchiveUi(session, () =>
            {
                var window = ArchiveWindow(session);
                using (var bitmap = new Bitmap(window.Width, window.Height))
                {
                    window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, "archive-session.png"), ImageFormat.Png);
                }
                return true;
            });
            TreeCall(session, "RequestCancellation");
            TreeCheck(((CancellationToken)Get(session, "Token")).IsCancellationRequested, "取消请求未传递到工作任务");
            TreeCall(session, "Fail", "已取消本次操作，本地修改已保留。请查看过程记录。");
            var resultText = ArchiveHistory(session);
            TreeCheck(resultText.Contains("ROOT.SLDASM") && resultText.Contains("已取消本次操作"), "结果替换或丢失了存档过程");
            TreeCheck(ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle && !(bool)Get(session, "IsClosed"), "结果页提前关闭或切换窗口");
            Console.WriteLine("PASS: Cancel request reaches the worker and the terminal result retains all prior records in the same window");
        }
        finally
        {
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
        TreeCheck(IsWindowEnabled(host.Handle), "关闭存档窗口后未恢复所属窗口");
        Console.WriteLine("PASS: Closing the completed session restores its owner and clears the singleton session");
        var cleanupSession = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(cleanupSession, "Ready");
        try
        {
            Set(cleanupSession, "WorkflowStarted", true);
            TreeCall(cleanupSession, "Complete", "服务器已完成本批次存档。", false, false);
            TreeCheck(!ArchiveUi(cleanupSession, () => (bool)Get(cleanupSession, "IsFinished")), "临时图档清理完成前已允许结束会话");
            TreeCall(cleanupSession, "SetStage", "正在结束临时读取");
            TreeCall(cleanupSession, "FinishIfRunning");
            var text = ArchiveHistory(cleanupSession);
            TreeCheck((bool)Get(cleanupSession, "IsFinished") && text.Contains("正在结束临时读取") && text.Contains("服务器已完成本批次存档"), "最终结果没有等待清理结束或丢失原结果");
            Console.WriteLine("PASS: Terminal result waits for CAD cleanup and preserves the original server outcome before enabling close");
        }
        finally { ArchiveClose(cleanupSession); await (Task)Get(cleanupSession, "Closed"); ((IDisposable)cleanupSession).Dispose(); }
    }

    static async Task ArchiveInlineSelection(object session, string output, IntPtr handle)
    {
        var path = Path.Combine(output, "选择回归.SLDPRT"); File.WriteAllText(path, "isolated archive selection fixture");
        var root = New("CadTreeNode"); Set(root, "FullPath", path); Set(root, "FileName", Path.GetFileName(path));
        Set(root, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part")); Set(root, "WorkState", Enum.Parse(Type("CadWorkState"), "Editable"));
        var item = New("BatchOperationItem", root, 0);
        var items = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("BatchOperationItem"))); items.Add(item);
        var project = New("ProjectDto"); var projectId = Guid.NewGuid();
        Set(project, "Id", projectId); Set(project, "Code", "P-ARCHIVE"); Set(project, "Name", "同页存档回归"); Set(project, "CanSubmitArchive", true);
        var projects = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("ProjectDto"))); projects.Add(project);
        var driver = Task.Run(async () =>
        {
            try
            {
                await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "selection")).Controls.OfType<Form>().Any()), "未显示同页图档选择");
                await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "selection")).Controls.OfType<Form>()
                    .Any(form => ((Button)Field(form, "recheck")).Enabled)), "图档变更分析没有完成");
                ArchiveUi(session, () =>
                {
                    var window = ArchiveWindow(session);
                    var form = ((Panel)Field(window, "selection")).Controls.OfType<Form>().Single();
                    window.BeginInvoke((Action)(() => ((Button)Field(form, "execute")).PerformClick()));
                    return true;
                });
                await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "confirmation")).Controls.OfType<Form>().Any(f => f.Text == "新增图档归属确认")), "归属确认没有进入原页面");
                ArchiveUi(session, () =>
                {
                    var window = ArchiveWindow(session);
                    var form = ((Panel)Field(window, "confirmation")).Controls.OfType<Form>().Single(f => f.Text == "新增图档归属确认");
                    var records = (Panel)Field(window, "processRecords");
                    TreeCheck(form.RectangleToScreen(form.ClientRectangle) == records.RectangleToScreen(records.ClientRectangle)
                        && !((TextBox)Field(window, "history")).Visible
                        && !((FlowLayoutPanel)Field(window, "confirmationActions")).Visible,
                        "项目归属确认没有完整占用过程记录区域");
                    var execute = ArchiveControls(form).OfType<Button>().Single(button => button.DialogResult == DialogResult.OK);
                    var cancel = ArchiveControls(form).OfType<Button>().Single(button => button.DialogResult == DialogResult.Cancel);
                    TreeCheck(!execute.Enabled && cancel.Visible && !form.TopLevel && !form.TopMost,
                        "项目归属确认丢失二次确认、返回修改或所属窗口层级");
                    using (var bitmap = new Bitmap(window.Width, window.Height))
                    {
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(output, "archive-project-admission.png"), ImageFormat.Png);
                    }
                    Console.WriteLine("PASS: Project admission fills the complete record area with both actions inside the original archive window");
                    ArchiveControls(form).OfType<Button>().Single(button => button.Text == "确认").PerformClick();
                    execute.PerformClick();
                    return true;
                });
            }
            catch { TreeCall(session, "RequestCancellation"); throw; }
        });
        var snapshot = ArchiveUi(session, () =>
        {
            var form = New("BatchOperationDialog", root, items, projects, projectId, "tester", Enum.Parse(Type("BatchOperationKind"), "CheckIn"),
                new[] { path }, null, null, false, false, 0);
            var result = (DialogResult)TreeCall(session, "ShowInline", form, true);
            return Type("ArchiveSelection").GetMethod("Read", Flags).Invoke(null, new[] { form, (object)result });
        });
        await driver;
        TreeCheck((DialogResult)Field(snapshot, "Result") == DialogResult.OK
            && (Guid?)Field(snapshot, "SelectedProjectId") == projectId
            && ((System.Collections.IEnumerable)Field(snapshot, "SelectedItems")).Cast<object>().Single() == item, "同页确认丢失了项目或选择范围");
        TreeCheck(ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle
            && ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "selection")).Controls.OfType<Form>().Any(f => !((Button)Field(f, "execute")).Enabled)),
            "确认后未保留原选择区域");
        Console.WriteLine("PASS: File selection, nested project admission and frozen selection remain in the same session with safe UI-thread snapshots");
        var originalHistory = ArchiveHistory(session);
        var originalStage = ArchiveUi(session, () => ((Label)Field(ArchiveWindow(session), "stage")).Text);
        var cancelDriver = Task.Run(async () =>
        {
            try
            {
                await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "confirmation")).Controls.OfType<Form>()
                    .Any(form => form.Text == "新增图档归属确认")), "再次确认归属没有显示");
                ArchiveUi(session, () =>
                {
                    var window = ArchiveWindow(session); var previousSize = window.ClientSize;
                    window.Size = window.MinimumSize;
                    var form = ((Panel)Field(window, "confirmation")).Controls.OfType<Form>().Single();
                    var records = (Panel)Field(window, "processRecords");
                    var cancel = ArchiveControls(form).OfType<Button>().Single(button => button.DialogResult == DialogResult.Cancel);
                    TreeCheck(form.RectangleToScreen(form.ClientRectangle) == records.RectangleToScreen(records.ClientRectangle)
                        && records.RectangleToScreen(records.ClientRectangle).Contains(cancel.RectangleToScreen(cancel.ClientRectangle)),
                        "缩小存档窗口后确认区没有填满或返回修改按钮被裁切");
                    Console.WriteLine("PASS: Admission confirmation resizes with the complete record area and keeps return action reachable at minimum size");
                    window.ClientSize = previousSize; cancel.PerformClick(); return true;
                });
            }
            catch { TreeCall(session, "RequestCancellation"); throw; }
        });
        var cancelled = ArchiveUi(session, () =>
        {
            using (var form = (Form)New("ProjectAdmissionConfirmationDialog", project, null, 1, null))
                return (DialogResult)TreeCall(session, "ShowInline", form, false);
        });
        await cancelDriver;
        TreeCheck(cancelled == DialogResult.Cancel && ArchiveUi(session, () => ((TextBox)Field(ArchiveWindow(session), "history")).Visible)
            && ArchiveHistory(session).StartsWith(originalHistory, StringComparison.Ordinal)
            && ArchiveUi(session, () => ((Label)Field(ArchiveWindow(session), "stage")).Text) == originalStage,
            "返回修改没有恢复处理信息及完整过程记录");
        Console.WriteLine("PASS: Returning from project admission restores the full original history and processing status");
    }

    static System.Collections.Generic.IEnumerable<Control> ArchiveControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in ArchiveControls(child)) yield return nested;
        }
    }

    static async Task ArchiveSlowEntry(Form host)
    {
        using (var handler = new ArchiveDelayedHandler())
        using (var client = (IDisposable)New("PdmApiClient", "http://archive-fixture.invalid/"))
        using (var pane = (Control)New("PdmTaskPaneControl", "存档入口回归"))
        using (var lifetime = new CancellationTokenSource())
        {
            host.Controls.Add(pane); pane.CreateControl();
            var plugin = New("PdmAddin");
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            SetField(plugin, "application", new Proxy(appType, call =>
            {
                if (call.MethodName != "GetRunningCommandInfo") throw new Exception("入口读取权限前不应访问原生图档：" + call.MethodName);
                call.Args[0] = 0; call.Args[1] = ""; call.Args[2] = false; return null;
            }).GetTransparentProxy());
            var root = New("CadTreeNode"); Set(root, "FullPath", Path.Combine(Path.GetTempPath(), "archive-entry.SLDPRT"));
            Set(root, "FileName", "archive-entry.SLDPRT"); Set(root, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
            SetField(plugin, "taskPaneControl", pane); SetField(plugin, "currentTree", root); SetField(plugin, "lifetime", lifetime);
            ((HttpClient)Field(client, "httpClient")).Dispose();
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://archive-fixture.invalid/") };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
            SetField(client, "httpClient", http); SetField(plugin, "apiClient", client);
            var project = New("ProjectDto"); Set(project, "Id", Guid.NewGuid()); Set(project, "Code", "TEST");
            var projects = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("ProjectDto")));
            projects.Add(project); SetField(plugin, "availableProjects", projects);
            TreeCheck(!(bool)TreeCall(plugin, "IsSolidWorksCommandActive"), "入口测试应先模拟空闲的SolidWorks命令状态");
            var timer = Stopwatch.StartNew();
            var args = New("CadTreeNodeEventArgs", root);
            TreeCall(plugin, "OnCheckInRequested", null, args);
            var started = ArchiveCurrent;
            if (started != null) await (Task)Get(started, "Ready");
            else throw new Exception("未创建会话；入口=" + Type("PdmAddin").GetMethod("OnCheckInRequested", Flags).GetCustomAttributes(false).Length
                + " workspace=" + Field(plugin, "workspaceOperationInProgress") + " paneDisposed=" + pane.IsDisposed + " invoke=" + pane.InvokeRequired);
            await TreeWait(() => ArchiveCurrent != null && ((Task)Get(ArchiveCurrent, "Ready")).IsCompleted, "存档入口没有先显示窗口");
            var session = ArchiveCurrent;
            await (Task)Get(session, "Ready");
            TreeCheck(timer.ElapsedMilliseconds < 1500, "存档窗口首帧等待过长：" + timer.ElapsedMilliseconds);
            await TreeWait(() => handler.Started, "读取权限请求未进入慢网络边界");
            var docked = Type("ArchiveSession").GetProperty("IsDocked", Flags)?.GetValue(session) as bool? == true;
            TreeCheck(IsWindowEnabled(host.Handle) == docked, "存档视图所属窗口状态不符合当前展示方式");
            var presentation = ArchiveWindow(session);
            var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
            TreeCall(plugin, "OnCheckInRequested", null, args);
            TreeCheck(ReferenceEquals(session, ArchiveCurrent) && handler.Requests == 1, "重复点击创建了新的会话或重复检查");
            Console.WriteLine("PASS: Real archive entry shows its first frame before blocked network work; duplicate click reuses session; firstFrameMs=" + timer.ElapsedMilliseconds);
            TreeCall(session, "RequestCancellation");
            await TreeWait(() => (bool)Get(session, "IsFinished"), "慢网络取消后未在原窗口结束");
            TreeCheck(handler.Canceled && handler.Requests == 1 && (docked ? presentation.Handle : ArchiveUi(session, () => ArchiveWindow(session).Handle)) == handle,
                "取消没有中断初始检查或启动了后续请求");
            TreeCheck(docked ? !presentation.IsDisposed && presentation.Visible : !(bool)Get(session, "IsClosed"), "取消结果没有保留在原显示区域");
            Console.WriteLine("PASS: Cancel during initial permission wait interrupts the request before structure reads or uploads and preserves the result window");
            if (!docked) ArchiveClose(session);
            await (Task)Get(session, "Closed");
            await Task.Delay(30);
            SetField(plugin, "disconnecting", true);
        }
    }

    sealed class ArchiveDelayedHandler : HttpMessageHandler
    {
        public volatile bool Started, Canceled;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (!request.RequestUri.AbsolutePath.EndsWith("/projects")) throw new Exception("Unexpected archive request: " + request.RequestUri.AbsolutePath);
            Started = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Canceled = true; throw; }
            throw new Exception("Unreachable");
        }
    }
}
