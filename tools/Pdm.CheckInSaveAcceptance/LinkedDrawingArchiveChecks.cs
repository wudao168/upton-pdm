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
 static async Task LinkedArchiveScenario(bool drawingOnly = false)
 {
  if(!drawingOnly) foreach(var change in new[]{"insert","remove","replace","child-version-only","denied"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-parent-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
   SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
   var root=LinkedNode(directory,"ROOT.SLDASM","Assembly");var child=LinkedNode(directory,"CHILD.SLDPRT","Part");
   ((IList)Get(root,"Children")).Add(child);var snapshot=LinkedSnapshot(root);
   if(change=="insert"||change=="denied")((IList)Get(root,"Children")).Add(LinkedNode(directory,"NEW.SLDPRT","Part"));
   if(change=="remove")((IList)Get(root,"Children")).Clear();
   if(change=="replace")Set(child,"DocumentId",Guid.NewGuid());
   if(change=="child-version-only")Set(child,"CurrentRevision","A-W1");
   SetField(plugin,"currentTree",root);var events=new List<string>();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var model=new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),(string)Get(root,"FullPath"),true,events);
   File.SetAttributes(model.Path,FileAttributes.ReadOnly);
   SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{model.GetTransparentProxy()}:call.MethodName=="GetCurrentWorkingDirectory"?directory:throw new Exception(call.MethodName)).GetTransparentProxy());
   using(var pane=(Control)New("PdmTaskPaneControl",""))
   using(var handler=new LinkedArchiveHandler(new[]{root},new Dictionary<Guid,object>{{(Guid)Get(root,"DocumentId"),LinkedVersion(root,snapshot)}},project,session,events,change=="denied"))
   using(var client=(IDisposable)New("PdmApiClient","http://linked-qa.invalid/"))
   {
    pane.CreateControl();SetField(plugin,"taskPaneControl",pane);LinkedClient(plugin,client,handler);
    var original=File.ReadAllBytes(model.Path);
    await (Task)Type("PdmAddin").GetMethod("EnsureBatchCheckInSavedStateAsync",Flags).Invoke(plugin,new object[]{LinkedItems(root),project,CancellationToken.None});
    if(!model.Dirty||!events.SequenceEqual(new[]{"copy:ROOT.SLDASM"})||!original.SequenceEqual(File.ReadAllBytes(model.Path))||(File.GetAttributes(model.Path)&FileAttributes.ReadOnly)==0)
        throw new Exception("Snapshot preparation wrote a readonly parent or acquired source permissions: "+change);
    TreeCall(plugin,"PruneArchiveMemoryCopies",true);
    Console.WriteLine("PASS: readonly parent snapshot preserves current memory and source permissions; change="+change);
   }
   File.SetAttributes(model.Path,FileAttributes.Normal);Directory.Delete(directory,true);
  }
  foreach(var mode in new[]{"changed-model","unchanged","stale-reference","other-editor","published-same-content","published-context-model","published-context-unknown-config","published-context-pending-config","published-context-pending-drawing-changed","published-context-pending-known-config","published-context-extra-dependency","published-context-configuration-changed","published-content-changed","published-properties-changed","published-configuration-changed","published-reference-changed","published-history-missing"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-linked-drawing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
   SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
   var model=LinkedNode(directory,"PART.SLDPRT","Part");var drawing=LinkedNode(directory,"DIFFERENT-NAME.SLDDRW","Drawing");
   ((IList)Get(model,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(model);
   SetField(plugin,"currentTree",model);Set(model,"CheckedOutBy","tester");Set(model,"CheckoutSessionId",session);
   var modelSnapshot=LinkedSnapshot(model);((IList)Get(modelSnapshot,"Children")).Clear();
   var drawingSnapshot=LinkedSnapshot(drawing);((IList)Get(drawingSnapshot,"Children")).Add(LinkedSnapshot(model,false));
   var versions=new Dictionary<Guid,object>{{(Guid)Get(model,"DocumentId"),LinkedVersion(model,modelSnapshot)},
    {(Guid)Get(drawing,"DocumentId"),LinkedVersion(drawing,drawingSnapshot)}};
   var histories=new Dictionary<Guid,object[]>();
   if(mode.StartsWith("published-"))
   {
    var workSnapshot=LinkedSnapshot(model,false);Set(workSnapshot,"Revision",LinkedRevision("W1"));
    var work=LinkedVersion(model,workSnapshot);Set(work,"Revision",LinkedRevision("W1"));Set(work,"Status",0);Set(work,"CreatedAt",DateTime.UtcNow.AddMinutes(-1));Set(work,"PropertySnapshot",new Dictionary<string,string>{{"Material","steel"}});
    var formal=versions[(Guid)Get(model,"DocumentId")];Set(formal,"ReferenceSnapshot",workSnapshot);Set(formal,"Status",1);Set(formal,"CreatedAt",DateTime.UtcNow);Set(formal,"PropertySnapshot",new Dictionary<string,string>{{"Material","steel"}});
    Set(((IList)Get(drawingSnapshot,"Children"))[0],"Revision",LinkedRevision("W1"));
    if(mode=="published-content-changed")Set(work,"Sha256",new string('F',64));
    if(mode=="published-properties-changed")Set(formal,"PropertySnapshot",new Dictionary<string,string>{{"Material","aluminum"}});
    if(mode=="published-configuration-changed")Set(((IList)Get(drawingSnapshot,"Children"))[0],"Configuration","Other");
    if(mode.StartsWith("published-context-"))Set(model,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
    if(mode=="published-context-unknown-config")Set(model,"Configuration","");
    if(mode.StartsWith("published-context-pending-"))
    {
     Set(drawing,"StructureIndexPending",true);Set(model,"Configuration",mode=="published-context-pending-known-config"?"Other":"");
     if(mode=="published-context-pending-drawing-changed")File.AppendAllText((string)Get(drawing,"FullPath"),"changed views");
    }
    if(mode=="published-context-extra-dependency"){var external=LinkedNode(directory,"CONTEXT.SLDPRT","Part");Set(external,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));((IList)Get(drawing,"ReferencedModels")).Add(external);}
    if(mode=="published-context-configuration-changed")Set(((IList)Get(drawingSnapshot,"Children"))[0],"Configuration","Other");
    if(mode=="published-reference-changed"){var changed=LinkedSnapshot(model,false);Set(changed,"Revision",LinkedRevision("W1"));Set(changed,"Quantity",2);Set(formal,"ReferenceSnapshot",changed);}
    histories[(Guid)Get(model,"DocumentId")]=mode=="published-history-missing"?new[]{formal}:new[]{formal,work};
   }
   if(mode=="changed-model"||mode=="other-editor")File.AppendAllText((string)Get(model,"FullPath"),"new geometry");
   if(mode=="stale-reference")Set(((IList)Get(drawingSnapshot,"Children"))[0],"Revision",LinkedRevision("W1"));
   if(mode=="other-editor")Set(drawing,"CheckedOutBy","other");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?Array.Empty<object>():call.MethodName=="get_ActiveDoc"?null:throw new Exception("Unexpected CAD mutation "+call.MethodName)).GetTransparentProxy());
   using(var handler=new LinkedArchiveHandler(new[]{model,drawing},versions,project,session,new List<string>(),mode=="other-editor",histories))
   using(var client=(IDisposable)New("PdmApiClient","http://linked-qa.invalid/"))
   using(var pane=(System.Windows.Forms.Control)New("PdmTaskPaneControl","配套工程图验证"))
   {
    LinkedClient(plugin,client,handler);
    SetField(plugin,"taskPaneControl",pane);
    var candidates=LinkedItems(model,drawing);
    if(mode=="changed-model")
    {
     Set(drawing,"CheckedOutBy","tester");Set(drawing,"CheckoutSessionId",session);
     candidates.SetValue(New("BatchOperationItem",drawing,5),1);
    }
    var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{candidates,null,CancellationToken.None});await task;
    var plan=Get(task,"Result");var planned=((IEnumerable)Get(plan,"Items")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
    if(planned.Select(node=>(string)Get(node,"FullPath")).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=planned.Length)
     throw new Exception("同一工程图被重复纳入提交计划："+mode);
    if(planned.Contains(drawing)==(mode=="unchanged"||mode=="published-same-content"||mode=="published-context-model"||mode=="published-context-unknown-config"||mode=="published-context-pending-config"||mode=="published-context-extra-dependency"))throw new Exception("Drawing dependency change was skipped or unchanged drawing was cascaded: "+mode);
    if(mode.StartsWith("published-")&&(string)Get(Get(((IList)Get(drawingSnapshot,"Children"))[0],"Revision"),"Display")!="W1")throw new Exception("Reference comparison rewrote the frozen drawing snapshot");
    if(mode=="published-context-unknown-config"&&((string)Get(model,"Configuration")!=""||(string)Get(((IList)Get(drawingSnapshot,"Children"))[0],"Configuration")!="Default"))throw new Exception("Unknown configuration was guessed or the archived view was rewritten");
    if(mode=="other-editor")
    {
     task=(Task)Type("PdmAddin").GetMethod("UpdateLinkedDrawingsAsync",Flags).Invoke(plugin,new object[]{plan,project,CancellationToken.None});
     await task;
     var updated=Get(task,"Result");
     var included=((IEnumerable)Get(updated,"Items")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
     var deferred=((IEnumerable)Get(updated,"DeferredDrawings")).Cast<object>().ToArray();
     if(!included.SequenceEqual(planned.Where(node=>node!=drawing))||!included.Contains(model)
      ||!deferred.SequenceEqual(new[]{drawing})||string.IsNullOrEmpty((string)Get(drawing,"DrawingUpdatePendingReason")))
      throw new Exception("配套图无法更新时应保留模型提交计划，并单独报告未提交图纸。");
    }
    if(mode=="unchanged"||mode=="other-editor")
    {
     File.SetAttributes((string)Get(drawing,"FullPath"),FileAttributes.ReadOnly);
     await (Task)Type("PdmAddin").GetMethod("AcquireLinkedDrawingPermissionsAsync",Flags).Invoke(plugin,
      new object[]{project,new[]{(Guid)Get(model,"DocumentId")},directory,CancellationToken.None});
     var readOnly=(File.GetAttributes((string)Get(drawing,"FullPath"))&FileAttributes.ReadOnly)!=0;
     if(readOnly!=(mode=="other-editor"))throw new Exception("Linked checkout failed to prepare drawing or overrode another editor");
     File.SetAttributes((string)Get(drawing,"FullPath"),FileAttributes.Normal);
     Console.WriteLine("PASS: model checkout prepares only associated drawing; conflict="+(mode=="other-editor"));
    }
    Console.WriteLine("PASS: associated drawing plan "+mode);
   }
   Directory.Delete(directory,true);
  }
 }
 static void CommittedArchiveDrawingWindowScenario(string output)
 {
  Directory.CreateDirectory(output);
  foreach(var mode in new[]{"saved","confirmed-memory","unconfirmed-memory","user-opened","window-replaced",
      "later-memory-edit","later-disk-edit","close-not-confirmed"})
  {
   var directory=Path.Combine(output,mode);
   Directory.CreateDirectory(directory);
   var plugin=New("PdmAddin");
   var path=Path.Combine(directory,"TEMP.SLDDRW");
   var otherPath=Path.Combine(directory,"USER.SLDDRW");
   File.WriteAllText(path,"已存档的工程图");File.WriteAllText(otherPath,"用户正在编辑的工程图");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   object Drawing(string file,bool dirty)=>new SheetReferenceModelProxy(modelType,true,call=>
   {
    switch(call.MethodName)
    {
     case "GetPathName":return file;
     case "GetTitle":return Path.GetFileName(file);
     case "GetType":return 3;
     case "GetSaveFlag":return dirty;
     case "GetUpdateStamp":return mode=="later-memory-edit"?75:74;
     case "get_Visible":return true;
     case "add_ModifyNotify":case "add_DestroyNotify2":case "add_FileReloadNotify":
     case "remove_ModifyNotify":case "remove_DestroyNotify2":case "remove_FileReloadNotify":return null;
     default:throw new Exception("关闭已存档工程图不应执行原生操作 "+call.MethodName);
    }
   },events:true).GetTransparentProxy();
   var memorySnapshot=mode is "confirmed-memory" or "unconfirmed-memory" or "later-memory-edit";
   var owned=Drawing(path,memorySnapshot);
   var user=Drawing(otherPath,true);
   var resident=mode=="window-replaced"?Drawing(path,true):owned;
   var closed=new List<string>();var app=new Proxy(appType,call=>
   {
    if(call.MethodName=="GetOpenDocumentByName")
    {
     var lookup=(string)call.Args[0];
     return lookup==path?resident:lookup==otherPath?user:null;
    }
    if(call.MethodName=="CloseDoc")
    {
     var title=(string)call.Args[0];closed.Add(title);
     TreeCheck(title=="TEMP.SLDDRW","只允许关闭本批打开的工程图");
     if(mode!="close-not-confirmed")resident=null;
     return null;
    }
    throw new Exception("存档清理不应保存或重开窗口 "+call.MethodName);
   }).GetTransparentProxy();
   SetField(plugin,"application",app);
   var stamp=TreeCallStatic("PdmAddin+ArchiveSourceStamp","Read",path);
   if(mode=="later-disk-edit")File.AppendAllText(path,"存档之后的新保存");
   var target=New("PdmAddin+CommittedArchiveDrawingWindow");
   SetField(target,"Path",path);SetField(target,"Document",owned);SetField(target,"SourceStamp",stamp);
   if(memorySnapshot)
   {
    var copyPath=Path.Combine(directory,"submitted-copy.SLDDRW");
    File.WriteAllText(copyPath,"由插件更新、已有原生保存副本的工程图");
    var memory=New("PdmAddin+ArchiveMemoryCopy",owned,path);
    SetField(memory,"CopyPath",copyPath);
    SetField(memory,"CopyHash","saved-hash");
    SetField(memory,"Stamp",74);
    var version=New("DocumentVersionDto");
    Set(version,"Sha256",mode=="unconfirmed-memory"?"another-hash":"saved-hash");
    SetField(memory,"CommittedVersion",version);
    SetField(target,"Memory",memory);
   }
   var windows=Array.CreateInstance(target.GetType(),mode=="user-opened"?0:1);
   if(windows.Length!=0)windows.SetValue(target,0);
   var result=New("PdmAddin+BatchCheckInResult");
   TreeCall(plugin,"CloseCommittedArchiveDrawingWindows",windows,result);
   var warnings=(IList)Get(result,"BomWarnings");
   var shouldClose=mode is "saved" or "confirmed-memory" or "close-not-confirmed";
   TreeCheck(closed.Count==(shouldClose?1:0) && (resident==null)==(mode is "saved" or "confirmed-memory")
    && warnings.Count==(mode is "unconfirmed-memory" or "later-memory-edit" or "later-disk-edit" or "close-not-confirmed"?1:0)
    && File.ReadAllText(otherPath)=="用户正在编辑的工程图",
    "关闭范围、后续修改保护及关闭结果核对 "+mode);
   Console.WriteLine("PASS: 存档后清理临时工程图 "+mode);
  }
 }
 static object LinkedNode(string directory,string name,string kind)
 {
  var node=New("CadTreeNode");var path=Path.Combine(directory,name);File.WriteAllText(path,"archived "+name);
  Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"DocumentId",Guid.NewGuid());Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"Configuration","Default");Set(node,"CurrentRevision","A");return node;
 }
 static object LinkedRevision(string text){var revision=New("RevisionDto");Set(revision,"Display",text);return revision;}
 static object LinkedSnapshot(object node,bool children=true)
 {
  var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"DocumentId",Get(node,"DocumentId"));Set(snapshot,"FileName",Get(node,"FileName"));Set(snapshot,"Kind",(int)Get(node,"Kind"));Set(snapshot,"Configuration",Get(node,"Configuration"));Set(snapshot,"Quantity",1);Set(snapshot,"Revision",LinkedRevision((string)Get(node,"CurrentRevision")));
  Set(snapshot,"Children",Activator.CreateInstance(Property(snapshot,"Children").PropertyType));
  if(children)foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>())((IList)Get(snapshot,"Children")).Add(LinkedSnapshot(child));return snapshot;
 }
 static object LinkedVersion(object node,object snapshot)
 {
  var version=New("DocumentVersionDto");Set(version,"Id",Guid.NewGuid());Set(version,"DocumentId",Get(node,"DocumentId"));Set(version,"Revision",LinkedRevision("A"));Set(version,"ReferenceSnapshot",snapshot);
  Set(version,"Sha256",Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new[]{Get(node,"FullPath")}));Set(version,"FileLength",new FileInfo((string)Get(node,"FullPath")).Length);return version;
 }
 static Array LinkedItems(params object[] nodes){var items=Array.CreateInstance(Type("BatchOperationItem"),nodes.Length);for(var i=0;i<nodes.Length;i++)items.SetValue(New("BatchOperationItem",nodes[i],i),i);return items;}
 static void LinkedClient(object plugin,object client,HttpMessageHandler handler)
 {
  ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://linked-qa.invalid/")});SetField(plugin,"apiClient",client);
 }
 sealed class LinkedArchiveHandler:HttpMessageHandler
 {
  readonly object[] nodes;readonly Dictionary<Guid,object> versions;readonly Guid project,session;readonly List<string> events;readonly bool denied;readonly Dictionary<Guid,object[]> histories;
  public LinkedArchiveHandler(object[] nodes,Dictionary<Guid,object> versions,Guid project,Guid session,List<string> events,bool denied,Dictionary<Guid,object[]> histories=null){this.nodes=nodes;this.versions=versions;this.project=project;this.session=session;this.events=events;this.denied=denied;this.histories=histories;}
  object Document(object node)=>new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),kind=Convert.ToInt32(Get(node,"Kind")),checkedOutBy=Get(node,"CheckedOutBy"),checkoutSessionId=Get(node,"CheckoutSessionId")};
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(request.RequestUri.AbsolutePath.EndsWith("/document-relations"))return Reply(new[]{new{modelDocumentId=Get(nodes[0],"DocumentId"),drawingDocumentId=Get(nodes[1],"DocumentId")}});
   if(request.RequestUri.AbsolutePath.EndsWith("/documents"))return Reply(nodes.Select(Document).ToArray());
   var node=nodes.Single(n=>request.RequestUri.AbsolutePath.Contains(((Guid)Get(n,"DocumentId")).ToString()));object body;var status=HttpStatusCode.OK;
   if(request.RequestUri.AbsolutePath.EndsWith("/versions"))body=histories!=null&&histories.TryGetValue((Guid)Get(node,"DocumentId"),out var history)?history:new[]{versions[(Guid)Get(node,"DocumentId")]};
   else if(request.Method==HttpMethod.Get && request.RequestUri.AbsolutePath=="/api/documents/"+Get(node,"DocumentId"))body=Document(node);
   else if(request.RequestUri.AbsolutePath.EndsWith("/checkout")){events.Add("checkout:"+Get(node,"FileName"));if(denied){body=new{message="图档正在由其他人编辑"};status=HttpStatusCode.Conflict;}else body=new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),checkedOutBy="tester",checkoutSessionId=session,revision=new{display="A"}};}
   else throw new Exception("Unexpected request "+request.RequestUri.AbsolutePath);
   return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(new JavaScriptSerializer().Serialize(body))});
  }
  static Task<HttpResponseMessage> Reply(object body)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(body))});
 }
}
