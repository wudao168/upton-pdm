using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task TreeRefreshScenario(Form host)
    {
        using (var fixture = new TreeRefreshFixture(host))
        {
            await fixture.WaitForTree();
            var generation = fixture.Generation;
            fixture.CheckedPaths.Add(fixture.Path);
            await fixture.Heartbeat(false);
            TreeCheck(fixture.Generation == generation, "无状态变化的心跳重建了设计树");
            TreeCheck(fixture.CheckedPaths.Contains(fixture.Path), "无状态变化的心跳清除了勾选");
            TreeCheck((int)Field(fixture.Plugin, "checkoutHeartbeatSeconds") == 60, "未读取真实问题的60秒心跳配置");
            Console.WriteLine("PASS: 60-second lease heartbeat updates lease without tree rebuild or selection loss");

            SetField(fixture.Plugin, "batchDialogInProgress", 1);
            SetField(fixture.Plugin, "refreshSuppressionDepth", 1);
            await fixture.Heartbeat(true);
            TreeCheck((bool)Get(fixture.Root, "CheckoutSessionLost"), "权限丢失未及时记录");
            TreeCheck(((Control)Field(fixture.Pane, "checkoutReminder")).Text.Contains("编辑权限已被释放"), "权限丢失提醒未显示");
            TreeCheck(fixture.Generation == generation, "模态窗口期间权限心跳重建了设计树");
            TreeCheck((int)Field(fixture.Plugin, "pendingTreeRefresh") == 1, "真实权限变更未排队刷新");
            TreeCheck(fixture.CheckedPaths.Contains(fixture.Path), "模态窗口期间勾选丢失");
            SetField(fixture.Plugin, "batchDialogInProgress", 0);
            SetField(fixture.Plugin, "refreshSuppressionDepth", 0);
            TreeCheck(!(bool)TreeCall(fixture.Plugin, "DeferBackgroundTreeRefresh"), "窗口关闭后仍阻止后台刷新");
            TreeCall(fixture.Pane, "SetTree", fixture.Root);
            await fixture.WaitForTree();
            TreeCheck(fixture.CheckedPaths.Contains(fixture.Path), "必要状态刷新清除了同一图档的勾选");
            Console.WriteLine("PASS: Lost lease still warns immediately, queues modal-time redraw, and preserves selection on redraw");
        }

        using (var fixture = new TreeRefreshFixture(host))
        {
            await fixture.WaitForTree();
            var tree = (TreeView)Field(fixture.Pane, "structureTree");
            tree.Nodes[0].Expand();
            fixture.CheckedPaths.Add(fixture.Path);
            TreeCall(fixture.Pane, "SetTree", fixture.Root);
            await fixture.WaitForTree();
            TreeCheck(fixture.CheckedPaths.Contains(fixture.Path) && tree.Nodes[0].IsExpanded,
                "同一根图档的必要刷新未保留勾选和展开");
            var other = New("CadTreeNode");
            Set(other, "FullPath", fixture.Path + ".other");
            Set(other, "FileName", "other.SLDASM");
            TreeCall(fixture.Pane, "SetTree", other);
            await fixture.WaitForTree();
            TreeCheck(fixture.CheckedPaths.Count == 0, "切换图档错误保留了上一图档勾选");
            Console.WriteLine("PASS: Same-document rebuild preserves selection/expansion; another document resets selection");
        }

        foreach (var gate in new[] { "batchDialogInProgress", "refreshSuppressionDepth", "workspaceOperationInProgress" })
        using (var fixture = new TreeRefreshFixture(host))
        {
            await fixture.WaitForTree();
            SetField(fixture.Plugin, gate, 1);
            var generation = fixture.Generation;
            TreeCall(fixture.Plugin, "SynchronizeActiveDocumentContextOnIdle");
            TreeCheck(ReferenceEquals(Field(fixture.Plugin, "currentTree"), fixture.Root)
                && fixture.Generation == generation && fixture.NativeCalls == 0,
                "后台受抑制时仍读取瞬时活动文档并清树：" + gate);
            SetField(fixture.Plugin, gate, 0);
            TreeCall(fixture.Plugin, "SynchronizeActiveDocumentContextOnIdle");
            TreeCheck(Field(fixture.Plugin, "currentTree") == null && fixture.NativeCalls == 1,
                "操作结束后真实关闭文档不能清除旧树：" + gate);
            Console.WriteLine("PASS: Idle transient empty document is suppressed during " + gate + " and reconciles after release");
        }

        foreach (var boundary in new[] { "/documents", "/document-relations" })
        using (var fixture = new TreeRefreshFixture(host))
        {
            await fixture.WaitForTree();
            fixture.Handler.BlockSuffix = boundary;
            var generation = fixture.Generation;
            var pending = (Task)TreeCall(fixture.Plugin, "RefreshMetadataAsync", fixture.Project, CancellationToken.None);
            await TreeWait(() => fixture.Handler.Blocked, "元数据测试未到达等待边界 " + boundary);
            SetField(fixture.Plugin, "batchDialogInProgress", 1);
            SetField(fixture.Plugin, "refreshSuppressionDepth", 1);
            fixture.Handler.Resume.SetResult(true);
            await pending;
            TreeCheck(fixture.Generation == generation && fixture.NativeCalls == 0
                && (string)Get(fixture.Root, "DisplayName") == "本地选择快照",
                "在途元数据请求于模态窗口期间修改了候选或重建树：" + boundary);
            TreeCheck((int)Field(fixture.Plugin, "pendingTreeRefresh") == 1, "元数据刷新未延至窗口结束");
            Console.WriteLine("PASS: In-flight metadata response deferred before node mutation at " + boundary);
        }

        using (var fixture = new TreeRefreshFixture(host))
        {
            Set(fixture.Root, "DocumentId", null);
            Set(fixture.Root, "LatestRevision", "保留已确认版本");
            using (var cancellation = new CancellationTokenSource())
            {
                var queued = new TreeQueuedContext();
                var previous = SynchronizationContext.Current;
                Task pending;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(queued);
                    pending = (Task)TreeCall(fixture.Plugin, "ApplyWorkingStatesAsync", fixture.Root, cancellation.Token);
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
                await TreeWait(() => queued.Count > 0, "文件哈希续体未排队");
                cancellation.Cancel();
                queued.ResumeOne();
                var canceled = false;
                try { await pending; }
                catch (OperationCanceledException) { canceled = true; }
                TreeCheck(canceled && (string)Get(fixture.Root, "LatestRevision") == "保留已确认版本",
                    "取消发生在哈希完成、续体执行前，旧元数据仍覆盖了候选");
                Console.WriteLine("PASS: Cancelled metadata hash continuation cannot overwrite the selection snapshot");
            }
        }
    }

    static object TreeCall(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Flags).Invoke(target, arguments);

    static void TreeCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static async Task TreeWait(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        TreeCheck(condition(), failure);
    }

    sealed class TreeRefreshFixture : IDisposable
    {
        public readonly object Plugin, Root, Client;
        public readonly Control Pane;
        public readonly TreeRefreshHandler Handler = new TreeRefreshHandler();
        public readonly Guid Project = Guid.NewGuid();
        public readonly string Path;
        public int NativeCalls;
        public int Generation => (int)Field(Pane, "treeBuildGeneration");
        public HashSet<string> CheckedPaths => (HashSet<string>)Field(Pane, "checkedCheckInPaths");
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        public TreeRefreshFixture(Form host)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UPLM-tree-refresh", Guid.NewGuid().ToString("N"), "root.SLDASM");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            File.WriteAllText(Path, "isolated managed test; not a CAD document");
            Plugin = New("PdmAddin");
            Root = New("CadTreeNode");
            Set(Root, "FullPath", Path); Set(Root, "FileName", "root.SLDASM");
            Set(Root, "DisplayName", "本地选择快照"); Set(Root, "InstancePath", "root");
            Set(Root, "DocumentId", Handler.DocumentId); Set(Root, "CheckedOutBy", "tester");
            Set(Root, "CheckoutSessionId", Field(Plugin, "checkoutSessionId"));
            Set(Root, "CheckedOutAt", DateTime.UtcNow.AddMinutes(-10));
            Set(Root, "WorkState", Enum.Parse(Property(Root, "WorkState").PropertyType, "Editable"));
            var child = New("CadTreeNode");
            Set(child, "FullPath", Path + ".child"); Set(child, "FileName", "child.SLDPRT"); Set(child, "InstancePath", "root/child");
            Set(child, "Kind", Enum.Parse(Property(child, "Kind").PropertyType, "Part"));
            ((IList)Get(Root, "Children")).Add(child);
            Pane = (Control)New("PdmTaskPaneControl", "");
            host.Controls.Add(Pane); Pane.CreateControl();
            SetField(Plugin, "taskPaneControl", Pane); SetField(Plugin, "currentTree", Root);
            TreeCall(Plugin, "GetTaskPaneSynchronizationContext");
            SetField(Plugin, "currentProjectId", Project); SetField(Plugin, "authenticatedUsername", "tester");
            SetField(Plugin, "lifetime", lifetime);
            ((HashSet<Guid>)Field(Plugin, "activeCheckoutDocumentIds")).Add(Handler.DocumentId);
            var appType = Plugin.GetType().GetField("application", Flags).FieldType;
            SetField(Plugin, "application", new Proxy(appType, call =>
            {
                NativeCalls++;
                if (call.MethodName == "get_ActiveDoc") return null;
                throw new Exception("Unexpected native call in managed tree-refresh test: " + call.MethodName);
            }).GetTransparentProxy());
            Client = New("PdmApiClient", "http://fixture.invalid/");
            ((HttpClient)Field(Client, "httpClient")).Dispose();
            var http = new HttpClient(Handler) { BaseAddress = new Uri("http://fixture.invalid/") };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
            SetField(Client, "httpClient", http); SetField(Plugin, "apiClient", Client);
            TreeCall(Pane, "SetTree", Root);
        }

        public async Task WaitForTree() => await TreeWait(() =>
        {
            var plan = Field(Pane, "activeTreeBuildPlan");
            return ((TreeView)Field(Pane, "structureTree")).Nodes.Count > 0
                && plan != null && !(bool)Get(plan, "UpdateOpen");
        }, "设计树测试未完成构建");

        public async Task Heartbeat(bool lost)
        {
            Handler.Lost = lost;
            await (Task)TreeCall(Plugin, "HeartbeatCheckoutSessionAsync", new List<Guid> { Handler.DocumentId });
            await TreeWait(() => lost ? (bool)Get(Root, "CheckoutSessionLost")
                : Get(Root, "CheckoutLastHeartbeatAt") != null, "心跳回调未完成");
        }

        public void Dispose()
        {
            SetField(Plugin, "disconnecting", true);
            lifetime.Cancel(); lifetime.Dispose();
            ((IDisposable)Client).Dispose(); Pane.Dispose();
            File.SetAttributes(Path, FileAttributes.Normal);
        }
    }

    sealed class TreeRefreshHandler : HttpMessageHandler
    {
        public readonly Guid DocumentId = Guid.NewGuid();
        public readonly TaskCompletionSource<bool> Resume = new TaskCompletionSource<bool>();
        public string BlockSuffix;
        public volatile bool Blocked;
        public bool Lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri.AbsolutePath;
            if (BlockSuffix != null && path.EndsWith(BlockSuffix)) { Blocked = true; await Resume.Task; }
            object payload;
            if (path.EndsWith("/heartbeat")) payload = new
            {
                ServerTime = DateTime.UtcNow, LostDocumentIds = Lost ? new[] { DocumentId } : Array.Empty<Guid>(),
                ReleaseRequestedDocumentIds = Array.Empty<Guid>(),
                Settings = new { CheckoutHeartbeatSeconds = 60, CheckoutReminderHours = 4, CheckoutStrongReminderHours = 8 }
            };
            else if (path.EndsWith("/documents")) payload = new[] { new { Id = DocumentId, Name = "服务器返回的新状态" } };
            else if (path.EndsWith("/document-relations")) payload = Array.Empty<object>();
            else if (path.EndsWith("/document-operation-rules")) payload = new { DisabledRuleIds = Array.Empty<string>() };
            else throw new Exception("Unexpected fixture request: " + request.Method + " " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(payload)) };
        }
    }

    sealed class TreeQueuedContext : SynchronizationContext
    {
        readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
        public int Count => callbacks.Count;
        public override void Post(SendOrPostCallback callback, object state) => callbacks.Enqueue(() => callback(state));
        public void ResumeOne() { if (!callbacks.TryDequeue(out var callback)) throw new Exception("Missing queued continuation"); callback(); }
    }
}
