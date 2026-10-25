using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static partial class Program
{
    private static void LinkedNativeArchive(Assembly plugin, SldWorks app, string copies, string qa)
    {
        app.UserControl = true;
        object New(string name, params object[] args) => Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks." + name, true), Members & ~BindingFlags.Static, null, args, null);
        void Set(object value, string name, object data) => value.GetType().GetProperty(name).SetValue(value, data);
        void Field(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        object Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Members).Invoke(value, args);
        var addin = New("PdmAddin"); var project = Guid.NewGuid();
        var session = (Guid)addin.GetType().GetField("checkoutSessionId", Members).GetValue(addin);
        var names = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(Path.Combine(copies,"linked-fixture.json")));
        var partPath = Path.Combine(copies, names[0]); var drawingPath = Path.Combine(copies, names[1]);
        var insertPath = Path.Combine(copies, names[2]); var rootPath = Path.Combine(copies,"QA-ROOT.SLDASM");
        foreach (var path in Directory.GetFiles(copies)) File.SetAttributes(path, FileAttributes.Normal);
        var dependencies = (app.GetDocumentDependencies2(drawingPath, false, false, false) as Array)?.Cast<string>().ToArray() ?? Array.Empty<string>();
        foreach (var old in dependencies.Where((value, index) => index % 2 == 1))
            if (string.Equals(Path.GetFileName(old), names[0], StringComparison.OrdinalIgnoreCase) && !string.Equals(old, partPath, StringComparison.OrdinalIgnoreCase))
                if (!app.ReplaceReferencedDocument(drawingPath, old, partPath)) throw new IOException("隔离工程图引用重定位失败。");
        IModelDoc2 Open(string path, int kind)
        {
            var errors = 0; var warnings = 0;
            var document = app.OpenDoc6(path, kind, 1, "", ref errors, ref warnings) as IModelDoc2;
            if (document == null || errors != 0 || !string.Equals(document.GetPathName(), path, StringComparison.OrdinalIgnoreCase)) throw new IOException("原生打开失败：" + path + ";" + errors);
            return document;
        }
        void Save(IModelDoc2 document)
        {
            var errors = 0; var warnings = 0;
            if (!document.Save3(1, ref errors, ref warnings) || errors != 0) throw new IOException("原生保存失败：" + document.GetPathName() + ";" + errors);
        }
        var part = Open(partPath, 1);
        var root = (IModelDoc2)app.NewDocument(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2022\templates\gb_assembly.asmdot", 0, 0, 0);
        var error = 0; var warning = 0;
        if (root == null || !root.Extension.SaveAs3(rootPath, 0, 1, null, null, ref error, ref warning) || error != 0) throw new IOException("未建立隔离父装配。");
        if (((IAssemblyDoc)root).AddComponent5(partPath, 0, "", false, "", 0, 0, 0) == null) throw new IOException("未插入原始子件。");
        Save(root);
        var scanner = New("SolidWorksReferenceTreeScanner", app);
        var identities = new Dictionary<string,Guid>(StringComparer.OrdinalIgnoreCase) { [rootPath]=Guid.NewGuid(),[partPath]=Guid.NewGuid(),[drawingPath]=Guid.NewGuid() };
        var versionIds = identities.ToDictionary(pair=>pair.Key,pair=>Guid.NewGuid(),StringComparer.OrdinalIgnoreCase);
        var identityType = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore",true);
        foreach (var pair in identities) identityType.GetMethod("TryWriteControlledVersion", Members).Invoke(null,new object[]{pair.Key,pair.Value,project,versionIds[pair.Key],"W1",Hash(pair.Key),new FileInfo(pair.Key).Length});
        void Bind(object node)
        {
            var path=(string)Get(node,"FullPath");
            if(identities.TryGetValue(path,out var id)){Set(node,"DocumentId",id);Set(node,"CurrentRevision","W1");Set(node,"Revision","W1");}
            foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>())Bind(child);
        }
        object Scan(IModelDoc2 document){var node=Invoke(scanner,"ScanDocument",document,false);Bind(node);return node;}
        object Snapshot(object node)
        {
            var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"DocumentId",Get(node,"DocumentId"));Set(snapshot,"FileName",Get(node,"FileName"));Set(snapshot,"Kind",Convert.ToInt32(Get(node,"Kind")));Set(snapshot,"Configuration",Get(node,"Configuration"));Set(snapshot,"Quantity",Get(node,"Quantity"));
            var revision=New("RevisionDto");Set(revision,"Display","W1");Set(snapshot,"Revision",revision);
            var children=(IList)Activator.CreateInstance(snapshot.GetType().GetProperty("Children").PropertyType);
            foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>().Where(child=>Convert.ToInt32(Get(child,"Kind"))!=2))children.Add(Snapshot(child));
            Set(snapshot,"Children",children);return snapshot;
        }
        object Version(object node, object snapshot)
        {
            var path=(string)Get(node,"FullPath");var version=New("DocumentVersionDto");Set(version,"Id",versionIds[path]);var revision=New("RevisionDto");Set(revision,"Display","W1");Set(version,"Revision",revision);Set(version,"ReferenceSnapshot",snapshot);Set(version,"Sha256",Hash(path));Set(version,"FileLength",new FileInfo(path).Length);return version;
        }
        Array Items(params object[] nodes){var array=Array.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks.BatchOperationItem",true),nodes.Length);for(var i=0;i<nodes.Length;i++)array.SetValue(New("BatchOperationItem",nodes[i],i),i);return array;}
        var rootBefore=Scan(root);var rootVersion=Version(rootBefore,Snapshot(rootBefore));
        var partNode=Scan(part);((IList)Get(partNode,"Children")).Clear();var partVersion=Version(partNode,Snapshot(partNode));
        var drawingNode=New("CadTreeNode");Set(drawingNode,"FullPath",drawingPath);Set(drawingNode,"FileName",names[1]);Set(drawingNode,"Kind",Enum.Parse(drawingNode.GetType().GetProperty("Kind").PropertyType,"Drawing"));Set(drawingNode,"Configuration","工程图");Bind(drawingNode);
        ((IList)Get(drawingNode,"ReferencedModels")).Add(partNode);
        var drawingSnapshot=Snapshot(drawingNode);((IList)Get(drawingSnapshot,"Children")).Add(Snapshot(partNode));
        var drawingVersion=Version(drawingNode,drawingSnapshot);
        var nodes=new[]{rootBefore,partNode,drawingNode};
        var versions=new Dictionary<Guid,object>{{identities[rootPath],rootVersion},{identities[partPath],partVersion},{identities[drawingPath],drawingVersion}};
        var client=New("PdmApiClient","http://native-linked.invalid/");
        using(var handler=new NativeLinkedHandler(nodes,versions,project,session))
        using(var http=new HttpClient(handler){BaseAddress=new Uri("http://native-linked.invalid/")})
        using(var host=new Form{Opacity=0,ShowInTaskbar=false})
        using(var pane=(Control)New("PdmTaskPaneControl","原生图模联动验收"))
        {
            ((HttpClient)client.GetType().GetField("httpClient",Members).GetValue(client)).Dispose();Field(client,"httpClient",http);
            Field(addin,"application",app);Field(addin,"apiClient",client);Field(addin,"scanner",scanner);Field(addin,"currentProjectId",project);Field(addin,"authenticatedUsername","qa");Field(addin,"taskPaneControl",pane);Field(addin,"lifetime",new CancellationTokenSource());
            host.Controls.Add(pane);var handle=host.Handle;pane.CreateControl();
            var previousContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try
            {
                Open(insertPath,1);app.ActivateDoc3(root.GetTitle(),false,0,ref error);
                File.SetAttributes(rootPath,FileAttributes.ReadOnly);root.SetReadOnlyState(true);
                if(((IAssemblyDoc)root).AddComponent5(insertPath,0,"",false,"",0.1,0,0)==null)throw new IOException("只读父装配内新增组件失败。");
                var rootAfter=Scan(root);Field(addin,"currentTree",rootAfter);
                Pump((Task)Invoke(addin,"EnsureBatchCheckInSavedStateAsync",Items(rootAfter),project,CancellationToken.None));
                if(root.GetSaveFlag()||root.IsOpenedReadOnly()||!handler.Checkouts.Contains(identities[rootPath]))throw new Exception("结构变化父装配未取得权限并保存。");
                Report["readonlyParentInsertionSaved"]=true;
                Console.WriteLine("NATIVE PASS: inserted component into read-only parent; plugin acquired permission and saved it");
                app.CloseDoc(root.GetTitle());
                part = Open(partPath, 1);
                part.SetReadOnlyState(false);File.SetAttributes(partPath,FileAttributes.Normal);
                var changedDimension="";var originalValue=0.0;var changedValue=0.0;
                for(var feature=part.FirstFeature() as IFeature;feature!=null&&changedDimension.Length==0;feature=feature.GetNextFeature() as IFeature)
                    for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display!=null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension)
                    {
                        var dimension=display.GetDimension2(0) as IDimension;if(dimension==null||dimension.SystemValue<=0)continue;
                        originalValue=dimension.SystemValue;dimension.SystemValue=originalValue*1.1;changedValue=dimension.SystemValue;
                        if(Math.Abs(changedValue-originalValue)<1e-9)continue;changedDimension=dimension.FullName;break;
                    }
                if(changedDimension.Length==0)throw new Exception("真实样本没有可修改的尺寸，未以属性修改替代几何验证。");
                part.ForceRebuild3(false);Save(part);Set(partNode,"CheckedOutBy","qa");Set(partNode,"CheckoutSessionId",session);Set(partNode,"IsModifiedInSolidWorks",false);
                ((IList)Get(partNode,"Children")).Add(drawingNode);Field(addin,"currentTree",partNode);
                var task=(Task)Invoke(addin,"BuildBatchCheckInPlanAsync",Items(partNode,drawingNode),null,CancellationToken.None);Pump(task);var plan=Get(task,"Result");
                task=(Task)Invoke(addin,"UpdateLinkedDrawingsAsync",plan,project,CancellationToken.None);Pump(task);plan=Get(task,"Result");
                if(!string.IsNullOrWhiteSpace((string)Get(drawingNode,"DrawingUpdatePendingReason")))throw new Exception((string)Get(drawingNode,"DrawingUpdatePendingReason"));
                if (part.GetSaveFlag()) throw new Exception("更新工程图后模型仍有待保存状态，存档流程尚未闭合。");
                if(!((IEnumerable)Get(plan,"Items")).Cast<object>().Any(item=>ReferenceEquals(Get(item,"Node"),drawingNode)))throw new Exception("变化工程图未进入存档计划。");
                if(Hash(drawingPath)==(string)Get(drawingVersion,"Sha256"))throw new Exception("工程图更新后没有保存新字节。");
                var drawing=Open(drawingPath,3);var sheets=((IDrawingDoc)drawing).GetViews() as object[];
                var views=sheets.OfType<object[]>().SelectMany(sheet=>sheet.Skip(1)).OfType<IView>().Where(view=>view.ReferencedDocument!=null).ToArray();
                if(views.Length==0||views.Any(view=>!string.Equals(((IModelDoc2)view.ReferencedDocument).GetPathName(),partPath,StringComparison.OrdinalIgnoreCase)))throw new Exception("重新打开工程图没有加载隔离模型。");
                Report["linkedDrawingRebuiltSavedReopened"]=true;Report["modifiedDimension"]=new{name=changedDimension,originalValue,changedValue};Report["drawingViewCount"]=views.Length;Report["drawingSha256"]=Hash(drawingPath);Report["nativeFiles"]=new[]{rootPath,partPath,drawingPath};
                Console.WriteLine("NATIVE PASS: actual dimension changed; closed drawing acquired, rebuilt, saved and reopened with correct model");
            }
            finally{SynchronizationContext.SetSynchronizationContext(previousContext);}
        }
    }

    private sealed class NativeLinkedHandler:HttpMessageHandler
    {
        readonly object[] nodes;readonly Dictionary<Guid,object> versions;readonly Guid project,session;
        public readonly HashSet<Guid> Checkouts=new HashSet<Guid>();
        public NativeLinkedHandler(object[] nodes,Dictionary<Guid,object> versions,Guid project,Guid session){this.nodes=nodes;this.versions=versions;this.project=project;this.session=session;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var node=nodes.Single(n=>request.RequestUri.AbsolutePath.Contains(((Guid)Get(n,"DocumentId")).ToString()));var id=(Guid)Get(node,"DocumentId");object body;
            if(request.RequestUri.AbsolutePath.EndsWith("/versions"))body=new[]{versions[id]};
            else if(request.RequestUri.AbsolutePath.EndsWith("/checkout")){Checkouts.Add(id);body=new{id,projectId=project,fileName=Get(node,"FileName"),checkedOutBy="qa",checkoutSessionId=session,revision=new{display="W1"}};}
            else throw new Exception("验收接口拒绝未预期请求："+request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(body))});
        }
    }
}
