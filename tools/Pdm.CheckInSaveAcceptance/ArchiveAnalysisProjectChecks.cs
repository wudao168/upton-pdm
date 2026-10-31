using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static TaskCompletionSource<bool> analysisProgressGate;
    static async Task<IReadOnlyList<TVersion>> AnalysisProgressVersions<TNode, TVersion>(TNode node, CancellationToken token)
    {
        if (!((string)Get(node, "FileName")).StartsWith("分析总装", StringComparison.Ordinal))
            await analysisProgressGate.Task;
        token.ThrowIfCancellationRequested();
        return Array.Empty<TVersion>();
    }

    static async Task ArchiveAnalysisProgressScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        var root = LinkedNode(output, "分析总装.SLDASM", "Assembly");
        var nodes = Enumerable.Range(1, 8).Select(i => LinkedNode(output, "分析零件" + i + ".SLDPRT", "Part")).Prepend(root).ToArray();
        foreach (var node in nodes)
        {
            Set(node, "DocumentId", Guid.NewGuid());
            if (!ReferenceEquals(node, root)) ((IList)Get(root, "Children")).Add(node);
        }
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        Form dialog = null;
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        try
        {
            analysisProgressGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TreeCall(session, "ReportAnalysisProgress", 0, 0, null, "正在整理分析范围…", false);
            ArchiveUi(session, () =>
            {
                Check((bool)Get(Field(ArchiveWindow(session), "progress"), "IsIndeterminate")
                    && ((Control)Field(ArchiveWindow(session), "progress")).Text.Contains("分析中"),
                    "尚未取得总数时显示分析中活动进度，不编造百分比");
                return true;
            });
            var pending = ArchiveUi(session, () =>
            {
                dialog = (Form)New("BatchOperationDialog", root, LinkedItems(nodes), Array.CreateInstance(Type("ProjectDto"), 0), null,
                    "tester", Enum.Parse(Type("BatchOperationKind"), "CheckIn"), null, null, null, false, false, 0);
                var loader = dialog.GetType().GetProperty("LoadArchiveVersionsAsync", Flags);
                loader.SetValue(dialog, Delegate.CreateDelegate(loader.PropertyType, typeof(Program).GetMethod("AnalysisProgressVersions", Flags)
                    .MakeGenericMethod(Type("CadTreeNode"), Type("DocumentVersionDto"))));
                return (Task)TreeCall(dialog, "AnalyzeArchiveSelectionAsync", true);
            });
            await TreeWait(() => ArchiveUi(session, () => (int)Get(Field(ArchiveWindow(session), "progress"), "Value") > 0), "分析未出现已完成文件进度");
            ArchiveUi(session, () =>
            {
                var window = ArchiveWindow(session); var progress = (Control)Field(window, "progress");
                Check(progress.Text.Contains("分析中") && progress.Text.Contains("1 / 9"), "分析按钮工作期间，当前进度显示分析状态及真实完成数/总数");
                Check(((Label)Field(window, "file")).Text.Contains(".SLD"), "分析进度同步显示当前图档名称");
                Check(((Label)Field(window, "resultState")).Text.Contains("运行中") && !(bool)Get(session, "IsFinished"), "分析进度不会提前把存档标记为成功");
                using (var bitmap = new System.Drawing.Bitmap(window.Width, window.Height))
                { window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "analysis-progress.png")); }
                return true;
            });
            analysisProgressGate.TrySetResult(true); await pending;
            ArchiveUi(session, () =>
            {
                var window = ArchiveWindow(session); var progress = (Control)Field(window, "progress");
                Check(progress.Text.Contains("分析完成") && progress.Text.Contains("9 / 9"), "全部文件分析完成后显示完整计数");
                Check(((Label)Field(window, "stage")).Text.Contains("核对本次存档范围") && !(bool)Get(session, "IsFinished"), "分析完成后等待确认范围，不替用户提交存档");
                return true;
            });
            analysisProgressGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = ArchiveUi(session, () => (Task)TreeCall(dialog, "AnalyzeArchiveSelectionAsync", true));
            ArchiveUi(session, () => { TreeCall(dialog, "CancelArchiveAnalysis"); return true; });
            analysisProgressGate.TrySetResult(true); await canceled;
            ArchiveUi(session, () =>
            {
                Check(!((Control)Field(ArchiveWindow(session), "progress")).Text.Contains("分析完成"), "取消分析后的晚到结果不会覆盖成分析完成");
                return true;
            });
            TreeCall(session, "SetPhase", 2, "正在准备文件权限…");
            TreeCall(session, "ReportFile", 1, 2, "权限样件.SLDPRT", "正在准备文件权限…");
            ArchiveUi(session, () =>
            {
                var text = ((Control)Field(ArchiveWindow(session), "progress")).Text;
                Check(text.Contains("1 / 2") && !text.Contains("分析"), "后续权限和上传阶段恢复自己的进度，不残留分析标记");
                return true;
            });
        }
        finally
        {
            analysisProgressGate?.TrySetResult(true);
            ArchiveUi(session, () => { dialog?.Dispose(); return true; });
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }

    sealed class InheritedProjectHandler : HttpMessageHandler
    {
        public Guid Project, Parent;
        public int? StoredVersions = 2;
        public bool ParentHasVersion = true;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException("项目继承检查不能写入服务器");
            Calls++;
            if (request.RequestUri.AbsolutePath.EndsWith("/versions"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    new JavaScriptSerializer().Serialize(ParentHasVersion ? new[] { new { Id = Guid.NewGuid() } } : new object[0])) });
            if (!request.RequestUri.AbsolutePath.EndsWith("/documents")) throw new InvalidOperationException("不支持的项目继承读取");
            var payload = new[] { new { Id = Parent, ProjectId = Project, Kind = 0, StoredVersionCount = StoredVersions } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(payload)) });
        }
    }

    static async Task ArchiveProjectInheritanceScenario(string output)
    {
        Directory.CreateDirectory(output);
        var project = Guid.NewGuid(); var parentId = Guid.NewGuid();
        var root = LinkedNode(output, "已存档总装.SLDASM", "Assembly"); Set(root, "DocumentId", parentId);
        Set(root, "StoredVersionStateKnown", true); Set(root, "HasStoredVersion", true);
        var part = LinkedNode(output, "新增底座.SLDPRT", "Part"); Set(part, "DocumentId", null);
        var item = New("BatchOperationItem", part, 1);
        var ancestors = Array.CreateInstance(Type("CadTreeNode"), 1); ancestors.SetValue(root, 0); TreeCall(item, "AddAncestors", ancestors);
        var items = Array.CreateInstance(Type("BatchOperationItem"), 1); items.SetValue(item, 0);
        var handler = new InheritedProjectHandler { Project = project, Parent = parentId };
        using (var client = (IDisposable)New("PdmApiClient", "http://project-inheritance.invalid/"))
        using (var lifetime = new CancellationTokenSource())
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://project-inheritance.invalid/") });
            var plugin = New("PdmAddin"); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime); SetField(plugin, "currentTree", root);
            async Task<bool> NeedsConfirmation()
            {
                var task = (Task)TreeCall(plugin, "ResolveInheritedDrawingProjectsAsync", items, project); await task;
                var inherited = task.GetType().GetProperty("Result").GetValue(task);
                return (bool)Type("BatchOperationDialog").GetMethod("RequiresNewDocumentProjectConfirmation", Flags)
                    .Invoke(null, new[] { item, (object)(Guid?)project, inherited });
            }
            void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
            Check(!await NeedsConfirmation(), "已存档总装下的新增零件沿用服务器确认的总装项目，不再二次确认");
            Set(part, "ProvenanceProjectId", Guid.NewGuid());
            Check(await NeedsConfirmation(), "已有其他项目来源的图档保留归属确认，不静默改归属");
            Set(part, "ProvenanceProjectId", null);
            handler.StoredVersions = 0;
            Check(await NeedsConfirmation(), "总装仅登记但尚未存档时仍确认新增归属");
            handler.StoredVersions = 2; handler.Project = Guid.NewGuid();
            Check(await NeedsConfirmation(), "服务器项目与所选项目不一致时不继承");
            handler.Project = project; handler.Parent = Guid.NewGuid();
            Check(await NeedsConfirmation(), "服务器不能确认总装身份时不继承");
            handler.Parent = parentId;
            handler.StoredVersions = null;
            Set(root, "StoredVersionStateKnown", false); Set(root, "HasStoredVersion", false);
            Check(!await NeedsConfirmation(), "服务器未返回版本数量时查询实际版本，不依赖界面缓存标记");
            handler.ParentHasVersion = false;
            Check(await NeedsConfirmation(), "实际没有存档版本时仍保留首次归属确认");
            handler.ParentHasVersion = true;
            handler.StoredVersions = 2;
            Set(root, "DocumentId", null);
            Check(await NeedsConfirmation(), "总装首次存档缺少受控身份时保留首次归属确认");
            Set(root, "DocumentId", parentId);
            var independent = New("BatchOperationItem", part, 0); items.SetValue(independent, 0); item = independent;
            Check(await NeedsConfirmation(), "没有总装父级的独立新图档仍需首次确认");
        }
    }
}
