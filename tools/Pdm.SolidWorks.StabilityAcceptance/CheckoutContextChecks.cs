using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private static void CheckoutContext(Assembly plugin, SldWorks app, IModelDoc2 parent, string copies, string qa, bool discardChain = false)
    {
        object New(string name, params object[] args) => Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks." + name, true), Members & ~BindingFlags.Static, null, args, null);
        void Set(object value, string name, object data) => value.GetType().GetProperty(name).SetValue(value, data);
        void Field(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        var addin = New("PdmAddin"); var project = Guid.NewGuid(); var rootId = Guid.NewGuid();
        var rootName = Path.GetFileName(parent.GetPathName());
        var selected = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>()
            .First(model => model.GetType() == 1 && model.GetPathName().StartsWith(copies + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var targetName = Path.GetFileName(selected.GetPathName()); var targetId = Guid.NewGuid();
        var sourceHashes = HashFiles(copies); var owner = New("ControlledOpenManifestDto");
        Set(owner, "Id", Guid.NewGuid()); Set(owner, "ProjectId", project); Set(owner, "ProjectCode", "QA-CONTEXT");
        Set(owner, "RootDocumentId", rootId); Set(owner, "RootVersionId", Guid.NewGuid()); Set(owner, "RootRevision", "W1");
        Set(owner, "RootRelativePath", rootName);
        var files = (IList)Activator.CreateInstance(owner.GetType().GetProperty("Files").PropertyType);
        foreach (var path in Directory.GetFiles(copies).Where(path => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            var file = New("ControlledOpenFileDto"); var name = Path.GetFileName(path);
            Set(file, "DocumentId", name == rootName ? rootId : name == targetName ? targetId : Guid.NewGuid());
            Set(file, "VersionId", Guid.NewGuid()); Set(file, "FileName", name); Set(file, "RelativePath", name);
            Set(file, "Sha256", Hash(path)); Set(file, "FileLength", new FileInfo(path).Length); Set(file, "Revision", "W1");
            Set(file, "IsRoot", name == rootName); files.Add(file);
        }
        Set(owner, "Files", files);
        var requested = New("ControlledOpenManifestDto");
        foreach (var property in owner.GetType().GetProperties().Where(property => property.CanWrite)) property.SetValue(requested, property.GetValue(owner));
        Set(requested, "RootDocumentId", targetId); Set(requested, "RootRelativePath", targetName);
        var selectedFile = files.Cast<object>().Single(file => (Guid)Get(file, "DocumentId") == targetId);
        var requestedFiles = (IList)Activator.CreateInstance(files.GetType()); requestedFiles.Add(selectedFile); Set(requested, "Files", requestedFiles);
        Set(requested, "RootVersionId", Get(selectedFile, "VersionId"));
        var client = New("PdmApiClient", "http://isolated-edit.invalid/");
        var session = (Guid)addin.GetType().GetField("checkoutSessionId", Members).GetValue(addin);
        using (var handler = new CheckoutFixtureHandler(requested, owner, copies, project, targetId, targetName, session))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://isolated-edit.invalid/") })
        using (var host = new Form { Opacity = 0, ShowInTaskbar = false })
        using (var pane = (Control)New("PdmTaskPaneControl", "原生编辑切换验收"))
        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            foreach (var name in new[] { "httpClient", "controlledOpenHttpClient" })
            { ((HttpClient)client.GetType().GetField(name, Members).GetValue(client)).Dispose(); Field(client, name, http); }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "isolated-fixture");
            var workspace = New("ControlledWorkspaceManager", client, Path.Combine(qa, "workspace"));
            Field(addin, "application", app); Field(addin, "apiClient", client); Field(addin, "controlledWorkspace", workspace);
            Field(addin, "authenticatedUsername", "qa"); Field(addin, "currentProjectId", project); Field(addin, "taskPaneControl", pane);
            Field(addin, "lifetime", new CancellationTokenSource());
            var identity = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true).GetField("bindingDirectory", Members);
            var previousBindings = identity.GetValue(null); identity.SetValue(null, Path.Combine(qa, "bindings"));
            var settings = plugin.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true).GetField("SettingsPath", Members);
            var previousSettings = settings.GetValue(null); var settingsPath = Path.Combine(qa, "workspace-settings.json");
            File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new { WorkspaceRoot = Path.Combine(qa, "workspace") })); settings.SetValue(null, settingsPath);
            var previousContext = SynchronizationContext.Current;
            try
            {
                host.Controls.Add(pane); var handle = host.Handle; pane.CreateControl();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var scope = (string)client.GetType().GetProperty("EditJournalScope").GetValue(client) + "|qa";
                Field(addin, "editJournalScope", scope); Field(addin, "editJournal", New("EditDraftJournal", Path.Combine(qa, "drafts"), scope));
                addin.GetType().GetMethod("RememberControlledOpenManifest", Members).Invoke(addin, new[] { owner, (object)copies });
                var errorShown = false;
                timer.Tick += (_, __) => { var window = FindWindow("#32770", "UPLM"); if (window == IntPtr.Zero) return;
                    GetWindowThreadProcessId(window, out var pid); if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                    errorShown = true; PostMessage(window, 0x111, new IntPtr(1), IntPtr.Zero); };
                timer.Start();
                addin.GetType().GetMethods(Members).Single(method => method.Name == "BeginControlledOpen" && method.GetParameters().Length == 5).Invoke(addin, new object[] { project, targetId,
                    Enum.Parse(plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenMode", true), "LatestEdit"), null, null });
                var until = DateTime.UtcNow.AddMinutes(10);
                bool Queued() => ((ICollection)addin.GetType().GetField("queuedControlledOpens", Members).GetValue(addin)).Count > 0;
                void DrainQueue() => addin.GetType().GetMethod("TryStartQueuedControlledOpen", Members).Invoke(addin, null);
                while (((int)addin.GetType().GetField("controlledOpenInProgress", Members).GetValue(addin) != 0 || Queued()) && DateTime.UtcNow < until)
                { Application.DoEvents(); DrainQueue(); Thread.Sleep(10); }
                if ((int)addin.GetType().GetField("controlledOpenInProgress", Members).GetValue(addin) != 0)
                    throw new IOException("首轮PLM取回尚未完成，停止后续场景。");
                timer.Stop();
                var parentPath = (string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace, new[] { owner, (object)rootId });
                var targetPath = (string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace, new[] { owner, (object)targetId });
                var nativeParent = app.GetOpenDocumentByName(parentPath) as IModelDoc2;
                var nativeTarget = app.GetOpenDocumentByName(targetPath) as IModelDoc2;
                if (errorShown || handler.Checkouts != 1 || handler.Releases != 0 || nativeParent == null || !nativeParent.IsOpenedReadOnly()
                    || nativeTarget == null || nativeTarget.IsOpenedReadOnly()) throw new Exception("原生上下文切换未完成或扩大了权限范围。");
                var tree = New("CadTreeNode"); Set(tree, "FullPath", parentPath); Set(tree, "FileName", rootName);
                Set(tree, "Kind", Enum.ToObject(tree.GetType().GetProperty("Kind").PropertyType, 0));
                foreach (var path in Directory.GetFiles(copies, "*.SLDDRW"))
                {
                    var child = New("CadTreeNode"); Set(child, "FullPath", path); Set(child, "FileName", Path.GetFileName(path));
                    Set(child, "Kind", Enum.ToObject(child.GetType().GetProperty("Kind").PropertyType, 2));
                    ((IList)Get(tree, "Children")).Add(child);
                }
                Field(addin, "currentTree", tree);
                var items = addin.GetType().GetMethod("BuildBatchOperationItems", Members).Invoke(null, new[] { tree });
                var beforeDrawings = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>().Count(model => model.GetType() == 3);
                Pump((Task)addin.GetType().GetMethod("PreloadBatchCheckInDocumentsAsync", Members).Invoke(addin, new object[] { items, null, CancellationToken.None }));
                var afterDrawings = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>().Count(model => model.GetType() == 3);
                if (beforeDrawings != afterDrawings || !nativeParent.IsOpenedReadOnly() || nativeTarget.IsOpenedReadOnly())
                    throw new Exception("最终插件的准备阶段打开了未选择的工程图或改变了原窗口权限。");
                Report["finalPreloadInventoryPassed"] = true;
                if (!SameHashes(sourceHashes, HashFiles(copies))) throw new Exception("原预览图档在编辑切换中被改写。");
                Report["checkoutContextPassed"] = true; Report["checkoutTarget"] = targetName;
                Report["checkoutCalls"] = handler.Checkouts; Report["parentReadOnlyAfterChildCheckout"] = true;
                Console.WriteLine("NATIVE CHECKOUT PASS: parent remains read-only; only " + targetName + " acquires editing; source hashes unchanged");
                if (discardChain)
                {
                    var childHash = Hash(targetPath);
                    var oldChildPath = targetPath;
                    nativeTarget.Extension.CustomPropertyManager[""].Add3("UPLM_AUTHORITY_UNSAVED_QA", 30, "local-only", 0);
                    nativeTarget.SetSaveFlag();
                    void WaitFor(Func<bool> busy)
                    {
                        var deadline = DateTime.UtcNow.AddMinutes(10);
                        DateTime? idleSince = null;
                        while (DateTime.UtcNow < deadline)
                        {
                            // Controlled preparation hands off to a queued native open.
                            // Drain that continuation before starting the next user action.
                            Application.DoEvents(); DrainQueue(); Thread.Sleep(10);
                            if (busy() || Queued()) idleSince = null;
                            else if (!idleSince.HasValue) idleSince = DateTime.UtcNow;
                            else if ((DateTime.UtcNow - idleSince.Value).TotalMilliseconds >= 500) break;
                        }
                        if(busy() || Queued() || errorShown)
                        {
                            Report["fixtureDiagnostic"] = plugin.GetType("Upton.Pdm.SolidWorks.PluginDiagnosticLog",true)
                                .GetMethod("Snapshot",Members).Invoke(null,null);
                            throw new IOException("原生获取、放弃、重开链路没有正常结束；弹窗="+errorShown
                                +"；工作区="+addin.GetType().GetField("workspaceOperationInProgress",Members).GetValue(addin)
                                +"；取回="+addin.GetType().GetField("controlledOpenInProgress",Members).GetValue(addin)
                                +"；打开="+addin.GetType().GetField("openOperationInProgress",Members).GetValue(addin));
                        }
                    }
                    var begin=addin.GetType().GetMethods(Members).Single(method=>method.Name=="BeginControlledOpen"&&method.GetParameters().Length==5);
                    var modeType=plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenMode",true);
                    timer.Start();
                    begin.Invoke(addin,new object[]{project,rootId,Enum.Parse(modeType,"LatestEdit"),null,null});
                    WaitFor(()=>(int)addin.GetType().GetField("controlledOpenInProgress",Members).GetValue(addin)!=0);
                    parentPath=(string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace,new[]{owner,(object)rootId});
                    targetPath=(string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace,new[]{owner,(object)targetId});
                    nativeParent=app.GetOpenDocumentByName(parentPath) as IModelDoc2;
                    if(nativeParent==null||nativeParent.IsOpenedReadOnly()||handler.Checkouts!=2)throw new IOException("子件获取后总装未进入编辑状态。");
                    nativeTarget=app.GetOpenDocumentByName(targetPath) as IModelDoc2;
                    var childWasUnloaded = nativeTarget == null;
                    if (nativeTarget == null)
                    {
                        var errors = 0; var warnings = 0;
                        nativeTarget = app.OpenDoc6(targetPath, 1, 1, "", ref errors, ref warnings);
                        Report["childInspectionOpen"] = new { childWasUnloaded, errors, warnings };
                    }
                    var localPropertyRetained = nativeTarget != null
                        && ((nativeTarget.Extension.CustomPropertyManager[""].GetNames() as string[]) ?? new string[0]).Contains("UPLM_AUTHORITY_UNSAVED_QA");
                    Report["authoritativeChildInspection"] = new { targetPath, oldChildPath, childWasUnloaded,
                        nativeAvailable = nativeTarget != null, oldDiskUnchanged = Hash(oldChildPath) == childHash, localPropertyRetained };
                    if(nativeTarget==null||targetPath==oldChildPath||Hash(oldChildPath)!=childHash
                        || localPropertyRetained)
                        throw new IOException("显式PLM取回未替换本地未保存内容，或破坏原磁盘副本。");
                    Set(tree,"FullPath",parentPath);
                    Set(tree,"DocumentId",rootId);Set(tree,"CheckedOutBy","qa");Set(tree,"CheckoutSessionId",session);
                    Set(tree,"Revision","W1");Set(tree,"CurrentRevision","W1");
                    var eventArgs=New("CadTreeNodeEventArgs",tree);
                    addin.GetType().GetMethod("OnDiscardCheckoutRequested",Members).Invoke(addin,new object[]{pane,eventArgs});
                    WaitFor(()=>(int)addin.GetType().GetField("workspaceOperationInProgress",Members).GetValue(addin)!=0
                        ||(int)addin.GetType().GetField("controlledOpenInProgress",Members).GetValue(addin)!=0
                        ||(int)addin.GetType().GetField("openOperationInProgress",Members).GetValue(addin)!=0);
                    if(handler.Releases!=1||handler.RootEditing||!handler.ChildEditing||Hash(oldChildPath)!=childHash)
                        throw new IOException("放弃总装破坏了子件内容或编辑权限。");
                    begin.Invoke(addin,new object[]{project,rootId,Enum.Parse(modeType,"LatestReadOnly"),null,null});
                    WaitFor(()=>(int)addin.GetType().GetField("controlledOpenInProgress",Members).GetValue(addin)!=0
                        ||(int)addin.GetType().GetField("openOperationInProgress",Members).GetValue(addin)!=0);
                    var reopened=app.ActiveDoc as IModelDoc2;
                    if(reopened==null||Path.GetFileName(reopened.GetPathName())!=rootName||!reopened.IsOpenedReadOnly())
                        throw new IOException("放弃总装后打开最新没有恢复只读总装窗口。");
                    timer.Stop();
                    Report["checkoutDiscardChain"]=new{passed=true,childAcquired=true,parentAcquiredAfterChild=true,
                        parentDiscarded=true,childPermissionPreserved=handler.ChildEditing,oldChildBytesUnchanged=Hash(oldChildPath)==childHash,
                        unsavedLocalContentReplacedFromPlm=true,
                        reopenedLatestReadOnly=true,sourceUnchanged=SameHashes(sourceHashes,HashFiles(copies)),mockPermissionServer=true};
                    Console.WriteLine("NATIVE CHAIN PASS: child edit -> parent edit -> discard parent -> reopen latest; child bytes and permission preserved");
                }
            }
            finally { identity.SetValue(null, previousBindings); settings.SetValue(null, previousSettings); SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }

    private sealed class CheckoutFixtureHandler : HttpMessageHandler
    {
        private readonly object manifest,owner; private readonly Guid project, document, rootDocument, session; private readonly string fileName,rootName;
        private readonly Dictionary<Guid,byte[]> content;
        internal int Checkouts, Releases;
        internal bool RootEditing,ChildEditing;
        internal CheckoutFixtureHandler(object manifest, object owner, string copies, Guid project, Guid document, string fileName, Guid session)
        {
            this.manifest = manifest;this.owner=owner; this.project = project; this.document = document; this.fileName = fileName; this.session = session;
            rootDocument=(Guid)Get(owner,"RootDocumentId");rootName=(string)Get(owner,"RootRelativePath");
            content=((IEnumerable)Get(owner,"Files")).Cast<object>().ToDictionary(file=>(Guid)Get(file,"DocumentId"),
                file=>File.ReadAllBytes(Path.Combine(copies,(string)Get(file,"RelativePath"))));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath; object response;
            var selected=path.Contains(rootDocument.ToString())?rootDocument:document;
            if(path.EndsWith("/file")){var id=Guid.Parse(path.Split('/')[3]);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(content[id])});}
            if (path.EndsWith("/open-manifest")) response = selected==rootDocument?owner:manifest;
            else if(path=="/api/document-operation-rules")response=new{revision=0,disabledRuleIds=new string[0]};
            else if(path.EndsWith("/relations")||path.EndsWith("/document-relations"))response=new object[0];
            else if(path.EndsWith("/versions"))response=new[]{new{id=Guid.NewGuid(),revision=new{display="W1"}}};
            else if (path.EndsWith("/documents") && request.Method == HttpMethod.Get) response = new[] { Doc(document),Doc(rootDocument) };
            else if (path.EndsWith("/checkout")) { Checkouts++; if(selected==rootDocument)RootEditing=true;else ChildEditing=true;response=Doc(selected); }
            else if (path.EndsWith("/discard-checkout")) { Releases++;if(selected==rootDocument)RootEditing=false;else ChildEditing=false;response=Doc(selected); }
            else throw new Exception("Unexpected isolated API call: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(response)) });
        }
        private object Doc(Guid id)
        {
            var editing=id==rootDocument?RootEditing:ChildEditing;var name=id==rootDocument?rootName:fileName;
            return new{id,projectId=project,fileName=name,name,kind=id==rootDocument?0:1,revision=new{display="W1"},
                checkedOutBy=editing?"qa":null,checkoutSessionId=editing?(Guid?)session:null};
        }
    }
}
