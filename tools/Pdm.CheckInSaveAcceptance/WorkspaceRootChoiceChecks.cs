using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static object workspaceOpenArgs;
    static void RecordWorkspaceOpen(object sender, EventArgs args) => workspaceOpenArgs = args;

    static async Task WorkspaceRootChoiceScenario(Form host)
    {
        foreach (var mode in new[] { "single", "independent", "forest", "no-content", "not-found", "other-project" })
        using (var handler = new WorkspaceRootChoiceHandler(mode))
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        using (var pane = (Control)New("PdmTaskPaneControl", ""))
        using (var lifetime = new CancellationTokenSource())
        {
            var plugin = New("PdmAddin");
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
            SetField(plugin, "taskPaneControl", pane);
            host.Controls.Add(pane); pane.CreateControl();
            var tabs = (TabControl)Field(pane, "tabs");
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "工作区");
            var workspace = (Control)Field(pane, "projectDocuments");
            var selector = Field(workspace, "projectSelector");
            var projects = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ProjectDto")));
            var project = New("ProjectDto");
            Set(project, "Id", handler.Project); Set(project, "Code", "P700014-1"); Set(project, "Name", "AAAA");
            projects.Add(project);
            TreeCall(workspace, "SetProjects", projects); TreeCall(selector, "SelectProject", handler.Project);
            TreeCall(workspace, "SetAuthenticatedUser", "tester");
            await (Task)TreeCall(plugin, "LoadProjectTreeAsync", handler.Project);
            var tree = (TreeView)Field(workspace, "tree");
            TreeCheck(tree.Nodes.Count == 1, mode + "：工作区没有加载图档");
            tree.ExpandAll();
            var rows = WorkspaceRows(tree.Nodes, workspace).ToArray();
            var ids = rows.Select(row => Get(row.Tag, "DocumentId")).Where(id => id != null).Cast<Guid>().ToArray();
            var expected = mode == "single" || mode == "other-project"
                ? new[] { handler.First, handler.Child, handler.FirstDrawing }
                : new[] { handler.First, handler.Child, handler.FirstDrawing, handler.Second, handler.SecondDrawing, handler.LoosePart };
            TreeCheck(expected.All(ids.Contains) && ids.All(expected.Contains), mode + "：图档清单不完整或跨项目混入");
            TreeCheck(ids.Length == ids.Distinct().Count(), mode + "：已在结构或模型下的图档被重复列出");
            var open = (Button)Field(workspace, "openLatest");
            var edit = (Button)Field(workspace, "openEdit");
            if (expected.Contains(handler.Second))
            {
                TreeCheck(!open.Enabled && !edit.Enabled, mode + "：多个结构默认选中了实际图档");
                TreeCheck(((Label)Field(workspace, "selectedHint")).Text.Contains("展开分组"), "分组未显示中文选择提示");
            }
            var evt = workspace.GetType().GetEvent("OpenRequested", Flags);
            var capture = Delegate.CreateDelegate(evt.EventHandlerType, typeof(Program).GetMethod("RecordWorkspaceOpen", Flags));
            evt.AddEventHandler(workspace, capture);
            foreach (var row in rows.Where(row => Get(row.Tag, "DocumentId") != null))
            {
                tree.SelectedNode = row; workspaceOpenArgs = null;
                TreeCheck(open.Enabled, "所选图档无法只读打开");
                TreeCall(workspace, "Raise", Enum.Parse(Type("ControlledOpenMode"), "LatestReadOnly"));
                TreeCheck(ReferenceEquals(Get(workspaceOpenArgs, "Node"), row.Tag)
                    && (Guid)Get(workspaceOpenArgs, "ProjectId") == handler.Project
                    && Get(workspaceOpenArgs, "Mode").ToString() == "LatestReadOnly", "打开的不是当前所选图档");
                workspaceOpenArgs = null;
                TreeCall(workspace, "Raise", Enum.Parse(Type("ControlledOpenMode"), "LatestEdit"));
                TreeCheck(ReferenceEquals(Get(workspaceOpenArgs, "Node"), row.Tag)
                    && Get(workspaceOpenArgs, "Mode").ToString() == "LatestEdit", "获取编辑的不是当前所选图档");
            }
            foreach (var row in rows.Where(row => Get(row.Tag, "DocumentId") == null))
            {
                tree.SelectedNode = row; workspaceOpenArgs = null;
                TreeCall(workspace, "Raise", Enum.Parse(Type("ControlledOpenMode"), "LatestReadOnly"));
                TreeCheck(!open.Enabled && !edit.Enabled && workspaceOpenArgs == null, "分组触发了打开请求");
                TreeCheck((string)TreeCall(workspace, "LocalStateText", row.Tag) == "", "分组被标成未下载");
            }
            if (mode == "forest")
            {
                var first = rows.Single(row => Equals(Get(row.Tag, "DocumentId"), handler.First));
                TreeCheck(first.Nodes.Cast<TreeNode>().Any(row => Equals(Get(row.Tag, "DocumentId"), handler.Child)), "原有装配层级丢失");
            }
            if (expected.Contains(handler.Second))
            {
                var second = rows.Single(row => Equals(Get(row.Tag, "DocumentId"), handler.Second));
                TreeCheck(second.Nodes.Cast<TreeNode>().Any(row => Equals(Get(row.Tag, "DocumentId"), handler.LoosePart)), "第二套独立结构的内部层级丢失");
            }
            Console.WriteLine("PASS: Workspace choice " + mode + " lists documents, preserves hierarchy, and opens only the selected document");
        }
    }

    static IEnumerable<TreeNode> WorkspaceRows(TreeNodeCollection nodes, object workspace)
    {
        foreach (TreeNode node in nodes)
        {
            TreeCall(workspace, "Materialize", node);
            yield return node;
            foreach (var child in WorkspaceRows(node.Nodes, workspace)) yield return child;
        }
    }

    sealed class WorkspaceRootChoiceHandler : HttpMessageHandler
    {
        public readonly Guid Project = Guid.NewGuid(), First = Guid.NewGuid(), Child = Guid.NewGuid(),
            FirstDrawing = Guid.NewGuid(), Second = Guid.NewGuid(), SecondDrawing = Guid.NewGuid(), LoosePart = Guid.NewGuid();
        readonly string mode;
        public WorkspaceRootChoiceHandler(string mode) => this.mode = mode;
        object Document(Guid id, string name, int kind, Guid? project = null) => new
        {
            Id = id, ProjectId = project ?? Project, DrawingNumber = name, Name = name,
            FileName = name + (kind == 0 ? ".SLDASM" : kind == 1 ? ".SLDPRT" : ".SLDDRW"), Kind = kind,
            Revision = new { Display = "A" }, DrawingVersion = "A-D", PublishedVersion = "未发布", StoredVersionCount = 1
        };
        object Node(Guid? id, string name, int kind, params object[] children) => new
        {
            DocumentId = id, DisplayName = name, FileName = id.HasValue ? name + (kind == 0 ? ".SLDASM" : ".SLDPRT") : "",
            Kind = kind, InstancePath = name, Quantity = 1, Children = children, Revision = new { Display = "A" }
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri.AbsolutePath;
            object payload;
            if (path.EndsWith("/document-operation-rules")) payload = new { DisabledRuleIds = Array.Empty<string>() };
            else if (path.EndsWith("/documents"))
            {
                var list = new List<object> { Document(First, "CW0030002.10", 0), Document(Child, "内部零件", 1), Document(FirstDrawing, "总装工程图", 2) };
                if (mode != "single") list.AddRange(new[] { Document(Second, "第二套结构", 0, mode == "other-project" ? Guid.NewGuid() : (Guid?)null),
                    Document(SecondDrawing, "第二套工程图", 2, mode == "other-project" ? Guid.NewGuid() : (Guid?)null),
                    Document(LoosePart, "独立零件", 1, mode == "other-project" ? Guid.NewGuid() : (Guid?)null) });
                payload = list;
            }
            else if (path.EndsWith("/document-relations")) payload = new[] { new { ModelDocumentId = First, DrawingDocumentId = FirstDrawing }, new { ModelDocumentId = Second, DrawingDocumentId = SecondDrawing } };
            else if (path.EndsWith("/versions"))
            {
                TreeCheck(request.RequestUri.Query == "?take=1", "工作区读取了整个版本历史");
                var first = path.Contains(First.ToString());
                payload = new[] { new { ReferenceSnapshot = first
                    ? Node(First, "CW0030002.10", 0, Node(Child, "内部零件", 1))
                    : Node(Second, "第二套结构", 0, Node(LoosePart, "独立零件", 1)) } };
            }
            else if (path.EndsWith("/reference-tree"))
            {
                if (mode == "no-content" || mode == "not-found") return Task.FromResult(new HttpResponseMessage(mode == "no-content" ? HttpStatusCode.NoContent : HttpStatusCode.NotFound) { Content = new StringContent("") });
                var first = Node(First, "CW0030002.10", 0, Node(Child, "内部零件", 1));
                payload = mode == "forest" ? Node(null, "项目装配结构", 0, first, Node(Second, "第二套结构", 0, Node(LoosePart, "独立零件", 1))) : first;
            }
            else throw new InvalidOperationException("Unexpected workspace request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(payload)) });
        }
    }
}
