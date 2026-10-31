using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task PassiveTreeScenario(Form host, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using (var pane = (Control)New("PdmTaskPaneControl", "按需读取回归"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            var plugin = New("PdmAddin"); SetField(plugin, "taskPaneControl", pane);
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            var calls = new List<string>(); object active = null;
            var dirty = false; var reads = 0;
            object Model(string path, int kind) => new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                calls.Add("model:" + call.MethodName);
                switch (call.MethodName)
                {
                    case "GetPathName": return path;
                    case "GetTitle": return Path.GetFileName(path);
                    case "GetType": return kind;
                    case "GetSaveFlag": return dirty;
                    default: reads++; throw new Exception("被动刷新禁止读取原生结构：" + call.MethodName);
                }
            }).GetTransparentProxy();
            var app = new Proxy(appType, call =>
            {
                calls.Add("app:" + call.MethodName);
                if (call.MethodName == "get_ActiveDoc") return active;
                if (call.MethodName == "GetRunningCommandInfo")
                    return new ReturnMessage(null, new object[] { -3, "", false }, 3, call.LogicalCallContext, call);
                reads++; throw new Exception("被动刷新禁止后台组件操作：" + call.MethodName);
            }).GetTransparentProxy();
            SetField(plugin, "application", app);
            void Check(bool condition, string text) { TreeCheck(condition, text); Console.WriteLine("PASS: " + text); }
            var models = new[] { Model(Path.Combine(output, "普通大总装.SLDASM"), 2), Model(Path.Combine(output, "普通零件.SLDPRT"), 1), Model(Path.Combine(output, "普通工程图.SLDDRW"), 3) };
            foreach (var model in models.Concat(new[] { models[0] }))
            {
                active = model; calls.Clear(); dirty = false;
                TreeCall(plugin, "RefreshTree", false, false);
                var root = Field(plugin, "currentTree");
                Check(root != null && (bool)Get(root, "LocalStructureDeferred"), "普通图档只显示按需读取状态：" + Get(root, "FileName"));
                Check(((IList)Get(root, "Children")).Count == 0, "未读取组件或工程图引用，不伪造完整空结构");
                Check(!calls.Any(call => call.Contains("Configuration") || call.Contains("Documents") || call.Contains("View") || call.Contains("Dependenc")), "打开时零配置、组件、图模关联调用");
                dirty = true; TreeCall(plugin, "OnAssemblyFileSaved", 0, Get(root, "FullPath"));
                TreeCall(plugin, "RefreshTree", false, false);
                Check((bool)Get(Field(plugin, "currentTree"), "IsModifiedInSolidWorks"), "普通编辑事件只刷新当前文档修改标记");
                var workState = Get(root, "WorkState");
                Check((string)TreeCall(pane, "WorkStateTextCore", root) == "库外文件 · 需保存", "库外图档需保存时不直接声称人工修改：" + Get(root, "FileName"));
                Check(Equals(Get(root, "WorkState"), workState) && (bool)Get(root, "IsModifiedInSolidWorks"), "显示文案不清除真实修改标记或改变存档判断");
                Check(!Get(root, "FullPath").ToString().Contains("Workspace"), "普通文件不会转入受控工作区");
            }
            Check(reads == 0, "打开、切换、保存均未触发原生结构读取或其他CAD操作");
            var controlled = New("CadTreeNode");
            Set(controlled, "DocumentId", Guid.NewGuid()); Set(controlled, "WorkState", Enum.Parse(Type("CadWorkState"), "ModifiedUnsaved"));
            Check((string)TreeCall(pane, "WorkStateTextCore", controlled) == "未获取权限 · 内存状态待核对", "受控图档未检出时仍显示权限核对提示");
            Set(controlled, "CheckedOutBy", "tester");
            Check((string)TreeCall(pane, "WorkStateTextCore", controlled) == "修改未保存", "已获取权限的受控修改继续显示修改未保存");
            Set(controlled, "CheckoutSessionLost", true);
            Check((string)TreeCall(pane, "WorkStateTextCore", controlled) == "权限已失效 · 修改已保留", "权限失效及修改保留提示保持优先");
            Set(controlled, "IsHistoricalPreview", true);
            Check((string)TreeCall(pane, "WorkStateTextCore", controlled) == "历史预览（只读）", "历史版本只读状态不被待保存文案覆盖");
            var current = Field(plugin, "currentTree");
            var child = New("CadTreeNode"); Set(child, "FullPath", Path.Combine(output, "缓存子件.SLDPRT"));
            ((IList)Get(current, "Children")).Add(child); Set(current, "LocalStructureDeferred", false);
            dirty = false; TreeCall(plugin, "RefreshTree", false, true);
            Check(ReferenceEquals(current, Field(plugin, "currentTree")) && ((IList)Get(current, "Children")).Count == 1, "无结构事件时复用已有树，不重读或丢失缓存层级");
            TreeCall(plugin, "InvalidateCurrentTreeCache");
            Check((bool)Get(current, "LocalStructureDeferred"), "结构变更使实际证据失效，缓存仅用于展示");
            Check(reads == 0, "失效处理不读取配置或解析组件");
            active = null; TreeCall(plugin, "RefreshTree", false, false);
            Check(Field(plugin, "currentTree") == null, "关闭所有图档后立即清空，不保留上一图档结构");
            using (var client = (IDisposable)New("PdmApiClient", "http://passive-tree.invalid/"))
            using (var lifetime = new CancellationTokenSource())
            {
                var root = LinkedNode(output, "受控总装.SLDASM", "Assembly");
                Set(root, "LocalStructureDeferred", true);
                var project = Guid.NewGuid(); var id = (Guid)Get(root, "DocumentId"); var sub = Guid.NewGuid();
                var network = new PassiveIndexHandler(project, id, sub);
                using (var http = new HttpClient(network) { BaseAddress = new Uri("http://passive-tree.invalid/") })
                {
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "isolated-fixture");
                    SetField(client, "httpClient", http); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
                    SetField(plugin, "currentTree", root); SetField(plugin, "currentProjectId", project);
                    await (Task)TreeCall(plugin, "RefreshPassiveMetadataAsync", root, project, CancellationToken.None);
                    var branch = ((IList)Get(root, "Children"))[0];
                    Check((bool)Get(root, "LocalStructureDeferred"), "PLM索引始终标记为展示数据，不能冒充存档实际结构");
                    Check((bool)Get(branch, "DisplayStructurePending") && network.Versions == 1, "首屏只取根版本，未展开子装配不提前请求");
                    var args = New("CadTreeNodeEventArgs", branch);
                    TreeCall(plugin, "OnStructureNodeRequested", null, args);
                    for (var i = 0; i < 150 && (bool)Get(branch, "DisplayStructurePending"); i++) await Task.Delay(20);
                    Check(!(bool)Get(branch, "DisplayStructurePending") && network.Versions == 2, "展开后只读取所选分支的服务器结构");
                    var embedded = ((IList)Get(branch, "Children"))[0];
                    Check(((IList)Get(embedded, "Children")).Count == 1 && Get(embedded, "DocumentId") == null, "虚拟组件及内部层级保留嵌入归属，不独立登记");
                    Check(reads == 0 && network.Writes == 0, "索引展示和展开零原生结构操作、零服务器写入");
                    Check(network.FullHistories == 0, "当前绑定版本无需逐文件读取历史版本");
                }
            }
        }
    }

    sealed class PassiveIndexHandler : HttpMessageHandler
    {
        readonly Guid project, root, sub; internal int Versions, FullHistories, Writes;
        internal PassiveIndexHandler(Guid project, Guid root, Guid sub) { this.project = project; this.root = root; this.sub = sub; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("展示不得写入服务器"); }
            object response;
            if (request.RequestUri.AbsolutePath.EndsWith("/documents"))
                response = new[] { new { Id = root, ProjectId = project, FileName = "受控总装.SLDASM", Kind = 0, Name = "总装", StoredVersionCount = 1 },
                    new { Id = sub, ProjectId = project, FileName = "子装配.SLDASM", Kind = 0, Name = "子装配", StoredVersionCount = 1 } };
            else if (request.RequestUri.AbsolutePath.Contains("/versions"))
            {
                Versions++; if (!request.RequestUri.Query.Contains("take=1")) FullHistories++;
                var child = new { DocumentId = (Guid?)sub, FileName = "子装配.SLDASM", DisplayName = "子装配", Kind = 0, InstancePath = "ROOT/SUB", Configuration = "默认" };
                object[] children = request.RequestUri.AbsolutePath.Contains(root.ToString()) ? new object[] { child }
                    : new object[] { new { DocumentId = (Guid?)null, FileName = "嵌入.SLDASM", Kind = 0, Status = 4, InstancePath = "SUB/EMBEDDED", Children = new[] { new { FileName = "内部零件.SLDPRT", Kind = 1, Status = 4, InstancePath = "SUB/EMBEDDED/PART" } } } };
                var isRoot = request.RequestUri.AbsolutePath.Contains(root.ToString());
                response = new[] { new { Id = isRoot ? root : sub, Revision = new { Display = "W1" }, ReferenceSnapshot = new { DocumentId = isRoot ? root : sub, Kind = 0, FileName = isRoot ? "受控总装.SLDASM" : "子装配.SLDASM", InstancePath = isRoot ? "ROOT" : "SUB", Configuration = "默认", Children = children } } };
            }
            else throw new Exception("多余服务器请求：" + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(response)) });
        }
    }
}
