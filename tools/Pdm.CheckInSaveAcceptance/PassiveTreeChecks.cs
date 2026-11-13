using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
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
            var healthRoot = New("CadTreeNode"); var healthChild = New("CadTreeNode");
            var healthRootPath = Path.Combine(output, "健康总装.SLDASM"); var healthChildPath = Path.Combine(output, "健康子件.SLDPRT");
            File.WriteAllText(healthRootPath, "assembly"); File.WriteAllText(healthChildPath, "part");
            Set(healthRoot, "FullPath", healthRootPath); Set(healthRoot, "FileName", "健康总装.SLDASM");
            Set(healthChild, "FullPath", healthChildPath); Set(healthChild, "FileName", "健康子件.SLDPRT");
            ((IList)Get(healthRoot, "Children")).Add(healthChild); SetField(pane, "rootNode", healthRoot);
            TreeCall(pane, "UpdateTreeHealth"); var healthLabel = (Label)Field(pane, "treeHealth");
            Check(healthLabel.Text.Contains("缺失 0"), "结构刷新核对当前文件存在状态");
            File.Delete(healthChildPath); TreeCall(pane, "UpdateSelected", healthRoot); TreeCall(pane, "UpdateSelected", new object[] { null });
            Check(healthLabel.Text.Contains("缺失 0"), "选择与取消选择不重复探测整棵树的磁盘文件");
            var details = (IEnumerable)TreeCall(pane, "MissingReferenceDetails");
            Check(details.Cast<object>().Any(), "显式详情仍立即重新核对磁盘缺失引用");
            TreeCall(pane, "UpdateTreeHealth");
            Check(healthLabel.Text.Contains("缺失 1"), "结构刷新重新核对缺失，不沿用过期健康结果");
            SetField(pane, "rootNode", null);
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
                    var manifest = New("ControlledOpenManifestDto"); Set(manifest, "ProjectId", project);
                    var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                    foreach(var drawingId in new[]{network.RootDrawing, network.SubDrawing})
                    {
                        var file=New("ControlledOpenFileDto");Set(file,"DocumentId",drawingId);Set(file,"VersionId",Guid.NewGuid());
                        Set(file,"FileName",drawingId.ToString("N")+".SLDDRW");Set(file,"RelativePath",drawingId.ToString("N")+".SLDDRW");files.Add(file);
                    }
                    Set(manifest,"Files",files);((IDictionary)Field(plugin,"controlledOpenManifests"))[output]=manifest;
                    await (Task)TreeCall(plugin, "RefreshPassiveMetadataAsync", root, project, CancellationToken.None);
                    var branch = ((IList)Get(root, "Children"))[0];
                    SetField(pane, "authenticatedUsername", "permission-fixture");
                    TreeCall(pane, "UpdateSelected", branch);
                    Check(((Button)Field(pane, "checkoutButton")).Enabled,
                        "首次打开新建子节点即可获取，不需要第二次刷新补权限");
                    var root2d=((IList)Get(root,"Children")).Cast<object>().SingleOrDefault(node=>Equals(Get(node,"DocumentId"),network.RootDrawing));
                    Check(root2d!=null,"工作区打开后的轻量设计树补入总装关联工程图");
                    TreeCall(pane, "UpdateSelected", root2d);
                    Check(((Button)Field(pane, "checkoutButton")).Enabled,
                        "首次补入关联工程图同步服务端权限结果");
                    Check((string)Get(root2d, "DrawingVersion") == "V2", "新增工程图立即显示服务端图纸版本");
                    Check((string)Get(root2d,"FullPath")==Path.Combine(output,network.RootDrawing.ToString("N")+".SLDDRW")
                        && ReferenceEquals(((IList)Get(root2d,"ReferencedModels"))[0],root),"总装工程图绑定受控清单路径及实际模型节点");
                    Check(!File.Exists((string)Get(root2d,"FullPath")),"已入库但尚未加载的2D按关联显示，不要求先打开或搜索磁盘");
                    Check((bool)Get(root, "LocalStructureDeferred"), "PLM索引始终标记为展示数据，不能冒充存档实际结构");
                    Check((bool)Get(branch, "DisplayStructurePending") && network.Versions == 1, "首屏只取根版本，未展开子装配不提前请求");
                    var args = New("CadTreeNodeEventArgs", branch);
                    TreeCall(plugin, "OnStructureNodeRequested", null, args);
                    for (var i = 0; i < 150 && (bool)Get(branch, "DisplayStructurePending"); i++) await Task.Delay(20);
                    Check(!(bool)Get(branch, "DisplayStructurePending") && network.Versions == 2, "展开后只读取所选分支的服务器结构");
                    var embedded = ((IList)Get(branch, "Children"))[0];
                    Check(((IList)Get(embedded, "Children")).Count == 1 && Get(embedded, "DocumentId") == null, "虚拟组件及内部层级保留嵌入归属，不独立登记");
                    var sub2d=((IList)Get(branch,"Children")).Cast<object>().Single(node=>Equals(Get(node,"DocumentId"),network.SubDrawing));
                    TreeCall(pane, "UpdateSelected", sub2d);
                    Check(((Button)Field(pane, "checkoutButton")).Enabled, "按需展开新增工程图也有权限，不依赖刷新");
                    Check((string)Get(sub2d,"FullPath")==Path.Combine(output,network.SubDrawing.ToString("N")+".SLDDRW")
                        && ReferenceEquals(((IList)Get(sub2d,"ReferencedModels"))[0],branch),"按需展开的子装配保留配套2D及受控路径");
                    await (Task)TreeCall(plugin,"RefreshPassiveMetadataAsync",root,project,CancellationToken.None);
                    Check(((IList)Get(root,"Children")).Cast<object>().Count(node=>Equals(Get(node,"DocumentId"),network.RootDrawing))==1
                        && ((IList)Get(branch,"Children")).Cast<object>().Count(node=>Equals(Get(node,"DocumentId"),network.SubDrawing))==1,
                        "重复轻量刷新不重复添加2D图档");
                    Check(network.Relations==1,"根与子装配复用同一关联清单，不逐组件重复读取关联");
                    Check(reads == 0 && network.Writes == 0, "索引展示和展开零原生结构操作、零服务器写入");
                    Check(network.FullHistories == 0, "当前绑定版本无需逐文件读取历史版本");
                    await PassiveOperationAccessChecks(plugin, pane, root, branch, root2d, project, output);
                }
            }
            await PassiveCycleChecks(pane, output);
        }
    }

    static async Task PassiveOperationAccessChecks(object plugin, Control pane, object root, object branch, object drawing, Guid project, string output)
    {
        var docs = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentDto")));
        var model = New("DocumentDto"); var id = (Guid)Get(branch, "DocumentId"); Set(model, "Id", id); Set(model, "ProjectId", project);
        docs.Add(id, model);
        var source = New("DocumentReferenceNodeDto"); Set(source, "DocumentId", id); Set(source, "Kind", 1); Set(source, "FileName", "权限零件.SLDPRT");
        foreach (var state in new[] { "已授权", "未授权", "撤销", "跨项目拒绝", "跨公司拒绝", "无结果" })
        foreach (var kind in new[] { 0, 1, 2 })
        {
            Set(source, "Kind", kind);
            object access = state == "无结果" ? null : New("DocumentOperationAccessDto");
            if (access != null) { Set(access, "CanCheckout", state == "已授权"); Set(access, "CanArchive", state == "已授权"); Set(access, "Reason", state == "未授权"
                ? "权限中心未授权当前图档的获取操作，请联系授权人员核对项目及目录范围。" : state); }
            Set(model, "OperationAccess", access);
            var mapped = TreeCall(plugin, "MapProjectTree", source, docs, true);
            Set(mapped, "IsLatestReadOnlyPreview", true);
            TreeCall(pane, "SetTree", mapped);
            for (var spin = 0; spin < 150 && (bool)Field(pane, "treeBuildPending"); spin++) await Task.Delay(20);
            TreeCheck(ReferenceEquals(Get(pane, "SelectedNode"), mapped), "首次打开最新图档必须默认选中当前图档，不依赖手动点击：" + state);
            TreeCheck(((Button)Field(pane, "checkoutButton")).Enabled == (state == "已授权"), "索引权限结果不一致：" + state);
            var hint = (Label)Field(pane, "checkoutAccessStatus");
            TreeCheck((state == "已授权") == string.IsNullOrWhiteSpace(hint.Text), "最新只读图档需直接显示获取禁用原因：" + state);
            TreeCheck(hint.Parent != null && hint.Enabled && (state == "已授权" || !string.IsNullOrWhiteSpace(hint.AccessibleDescription)),
                "禁用原因必须显示在可交互区域，不能仅依赖禁用按钮悬停：" + state);
            if (kind == 1 && (state == "已授权" || state == "未授权"))
            {
                var host = pane.FindForm(); var previousSize = host.ClientSize;
                foreach (var width in new[] { 320, 480 })
                {
                    host.ClientSize = new Size(width, 900); host.PerformLayout(); pane.PerformLayout();
                    await Task.Delay(30);
                    using (var screenshot = new Bitmap(pane.Width, pane.Height))
                    {
                        pane.DrawToBitmap(screenshot, new Rectangle(Point.Empty, pane.Size));
                        screenshot.Save(Path.Combine(output, "checkout-" + (state == "已授权" ? "allowed" : "denied") + "-" + width + ".png"));
                    }
                }
                host.ClientSize = previousSize;
            }
            Console.WriteLine("PASS: 新建索引按服务端结果判定：" + state + "；图档类型=" + kind);
        }
        var granted = New("DocumentOperationAccessDto"); Set(granted, "CanCheckout", true); Set(granted, "CanArchive", true);
        Set(model, "OperationAccess", granted);
        var login = (string)Field(pane, "authenticatedUsername");
        Set(source, "CheckedOutBy", login);
        var snapshotWithOldCheckout = TreeCall(plugin, "MapProjectTree", source, docs, true);
        TreeCheck(string.IsNullOrEmpty((string)Get(snapshotWithOldCheckout, "CheckedOutBy")), "已解除的历史编辑状态不能覆盖当前服务端的未获取状态");
        Set(model, "CheckedOutBy", login); Set(model, "CheckoutSessionId", Guid.NewGuid()); Set(model, "CheckoutMachine", "OTHER-WS");
        var currentCheckout = TreeCall(plugin, "MapProjectTree", source, docs, true);
        var currentHint = (string)TreeCall(pane, "CheckoutUnavailableReason", currentCheckout);
        TreeCheck(currentHint.Contains("另一编辑会话") && currentHint.Contains("OTHER-WS"), "当前登录账号的跨会话锁应明确说明原编辑会话和电脑");
        Set(model, "CheckedOutBy", null); Set(model, "CheckoutSessionId", null); Set(model, "CheckoutMachine", null);
        Set(model, "DrawingVersion", "V2"); Set(model, "PublishedVersion", "A"); Set(branch, "LoadedDrawingVersion", "V1");
        TreeCall(plugin, "ApplyPassiveDocumentAccess", root, docs);
        var paneType = Type("PdmTaskPaneControl");
        var versionText = paneType.GetMethod("VersionText", Flags);
        var outdated = paneType.GetMethod("IsVersionOutdated", Flags);
        TreeCheck(((string)versionText.Invoke(null, new[] { branch })).Contains("本地 V1")
            && (bool)outdated.Invoke(null, new[] { branch }), "V2/A和实际工作副本V1独立显示，结构健康统计版本落后");
        Set(branch, "LoadedDrawingVersion", "V2");
        TreeCheck(!(bool)outdated.Invoke(null, new[] { branch }), "实际加载V2后版本落后统计清零");
        TreeCall(pane, "UpdateSelected", branch);
        TreeCheck(((Button)Field(pane, "checkoutButton")).Enabled, "刷新已有只读子件能获得服务器授权");
        Set(model, "OperationAccess", null);
        TreeCall(plugin, "ApplyPassiveDocumentAccess", root, docs);
        TreeCall(pane, "UpdateSelected", branch);
        TreeCheck(!((Button)Field(pane, "checkoutButton")).Enabled && Get(branch, "OperationAccess") == null,
            "最新授权结果缺失时不能沿用已有节点的旧授权");
        Set(model, "OperationAccess", granted);
        TreeCall(plugin, "ApplyPassiveDocumentAccess", root, docs);
        docs.Clear();
        TreeCall(plugin, "ApplyPassiveDocumentAccess", root, docs);
        TreeCall(pane, "UpdateSelected", branch);
        TreeCheck(!((Button)Field(pane, "checkoutButton")).Enabled && Get(branch, "OperationAccess") == null,
            "图档从当前可访问清单消失后清除旧授权");
        docs.Add(id, model);
        Set(branch, "OperationAccess", granted); Set(branch, "LifecycleState", "Obsolete");
        TreeCall(pane, "UpdateSelected", branch);
        TreeCheck(!((Button)Field(pane, "checkoutButton")).Enabled && ((Label)Field(pane, "checkoutAccessStatus")).Text.Contains("作废"),
            "已有授权的作废图档显示业务阻止原因，空授权说明不覆盖原因");
        Set(branch, "LifecycleState", "");
        Console.WriteLine("PASS: 已有只读图档的授权缺失、撤销范围及业务原因均可见且保持拒绝");
        var drawingDoc = New("DocumentDto"); Set(drawingDoc, "Id", Get(drawing, "DocumentId")); Set(drawingDoc, "Kind", 2);
        docs.Add(Get(drawing, "DocumentId"), drawingDoc);
        var relations = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("DocumentModelDrawingRelationDto")));
        var relation = New("DocumentModelDrawingRelationDto"); Set(relation, "ModelDocumentId", Get(root, "DocumentId")); Set(relation, "DrawingDocumentId", Get(drawing, "DocumentId")); relations.Add(relation);
        TreeCall(plugin, "ReconcileProjectDrawings", root, docs, relations);
        TreeCall(pane, "UpdateSelected", drawing);
        TreeCheck(!((Button)Field(pane, "checkoutButton")).Enabled && Get(drawing, "OperationAccess") == null, "工程图不得保留旧授权");
        Console.WriteLine("PASS: 关联工程图再次核对无授权时清除旧结果");
        var local = New("CadTreeNode"); Set(local, "Kind", Enum.Parse(Type("CadDocumentKind"), "Drawing"));
        Set(local, "FileName", "本机工程图.SLDDRW"); Set(local, "IsModifiedInSolidWorks", true);
        ((IList)Get(root, "Children")).Add(local);
        relations.Clear();
        TreeCall(plugin, "ReconcileProjectDrawings", root, docs, relations);
        TreeCheck(!((IList)Get(root, "Children")).Contains(drawing), "服务端纠正关联后不得残留合成工程图节点");
        TreeCheck(((IList)Get(root, "Children")).Contains(local) && (bool)Get(local, "IsModifiedInSolidWorks"), "本机未存档工程图和修改需保留");
        Console.WriteLine("PASS: 刷新删除已撤销的服务端配套投影，保留本机工程图及未保存修改");
        await Task.CompletedTask;
    }

    static void PassiveDrawingDataReplay(string jsonPath)
    {
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(jsonPath));
        var documentRows=((IEnumerable)data["Documents"]).Cast<Dictionary<string,object>>().ToArray();
        var relationRows=((IEnumerable)data["Relations"]).Cast<Dictionary<string,object>>().ToArray();
        var documents=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),Type("DocumentDto")));
        foreach(var row in documentRows)
        {
            var doc=New("DocumentDto");var id=Guid.Parse((string)row["Id"]);
            Set(doc,"Id",id);Set(doc,"FileName",(string)row["FileName"]);Set(doc,"Name",(string)row["FileName"]);Set(doc,"Kind",2);
            documents.Add(id,doc);
        }
        var models=new Dictionary<Guid,object>();
        var relations=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("DocumentModelDrawingRelationDto")));
        foreach(var row in relationRows)
        {
            var modelId=Guid.Parse((string)row["ModelId"]);var drawingId=Guid.Parse((string)row["DrawingId"]);
            if(!models.ContainsKey(modelId))
            {
                var model=New("CadTreeNode");Set(model,"DocumentId",modelId);Set(model,"FileName",(string)row["ModelFile"]);
                Set(model,"Kind",Enum.Parse(Type("CadDocumentKind"),((string)row["ModelFile"]).EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?"Assembly":"Part"));
                models.Add(modelId,model);
            }
            var relation=New("DocumentModelDrawingRelationDto");Set(relation,"ModelDocumentId",modelId);Set(relation,"DrawingDocumentId",drawingId);relations.Add(relation);
        }
        var root=models.Values.First(model=>(string)Get(model,"FileName")=="CW004-3.SLDASM");
        foreach(var model in models.Values.Where(model=>!ReferenceEquals(root,model)))((IList)Get(root,"Children")).Add(model);
        var plugin=New("PdmAddin");SetField(plugin,"currentTree",root);
        for(var refresh=0;refresh<3;refresh++)TreeCall(plugin,"ReconcilePassiveDrawings",root,documents,relations);
        foreach(var model in models)
        {
            var expected=relationRows.Where(row=>Guid.Parse((string)row["ModelId"])==model.Key).Select(row=>Guid.Parse((string)row["DrawingId"])).Distinct().OrderBy(id=>id).ToArray();
            var actual=((IList)Get(model.Value,"Children")).Cast<object>().Where(child=>Get(child,"Kind").ToString()=="Drawing").ToArray();
            if(!expected.SequenceEqual(actual.Select(child=>(Guid)Get(child,"DocumentId")).OrderBy(id=>id))
                || actual.Any(child=>!((IList)Get(child,"ReferencedModels")).Cast<object>().Contains(model.Value)))
                throw new Exception("真实项目关联显示不符："+Get(model.Value,"FileName"));
        }
        Console.WriteLine("PASS: 真实只读数据回放："+documentRows.Length+"份2D、"+relationRows.Length+"条关联均显示；重复刷新不重复，未连接CAD或写入服务器");
    }

    sealed class PassiveIndexHandler : HttpMessageHandler
    {
        readonly Guid project, root, sub; internal int Versions, FullHistories, Writes, Relations;
        internal readonly Guid RootDrawing=Guid.NewGuid(), SubDrawing=Guid.NewGuid();
        internal PassiveIndexHandler(Guid project, Guid root, Guid sub) { this.project = project; this.root = root; this.sub = sub; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("展示不得写入服务器"); }
            object response;
            if (request.RequestUri.AbsolutePath.EndsWith("/documents"))
                response = new[] { new { Id = root, ProjectId = project, FileName = "受控总装.SLDASM", Kind = 0, Name = "总装", StoredVersionCount = 1 },
                    new { Id = sub, ProjectId = project, FileName = "子装配.SLDASM", Kind = 0, Name = "子装配", StoredVersionCount = 1 },
                    new { Id = RootDrawing, ProjectId = project, FileName = RootDrawing.ToString("N")+".SLDDRW", Kind = 2, Name = "总装工程图", StoredVersionCount = 1 },
                    new { Id = SubDrawing, ProjectId = project, FileName = SubDrawing.ToString("N")+".SLDDRW", Kind = 2, Name = "子装配工程图", StoredVersionCount = 1 } }
                    .Select(doc => new { doc.Id, doc.ProjectId, doc.FileName, doc.Kind, doc.Name, doc.StoredVersionCount,
                        DrawingVersion = "V2", OperationAccess = new { CanCheckout = true, CanArchive = true } }).ToArray();
            else if(request.RequestUri.AbsolutePath.EndsWith("/document-relations"))
            {
                Relations++; response=new[]{new{ModelDocumentId=root,DrawingDocumentId=RootDrawing},new{ModelDocumentId=sub,DrawingDocumentId=SubDrawing}};
            }
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
