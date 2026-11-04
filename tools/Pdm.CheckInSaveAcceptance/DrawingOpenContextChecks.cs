using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task DrawingOpenContextScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-draw-open-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in new[]{"clean","registered-clean","registered-owned-part","drawing-placeholder","drawing-placeholder-source-copy","archive-closed-drawing","archive-saved-drawing-edit","dirty-total","dirty-owned-part","saved-owned-part","loaded-owned-drawing","missing-drawing","latest-edit-owned","latest-edit-new",
    "stale-version","stale-config","stale-drawing","bad-binding","unknown-edit","other-directory","wrong-view-second-sheet","native-open-null","historical-conflict",
    "resolved-yes","resolved-no","resolved-without-confirmation","resolved-yes-missing","resolved-yes-wrong-revision","resolved-yes-stale-model","resolved-yes-wrong-view",
    "context-yes","context-older-environment","context-no-marker","context-unverified","context-true-source","context-parent-tampered","context-model-mismatch","context-wrong-second-sheet"})
   {
    var folder=Path.Combine(directory,"View",mode);Directory.CreateDirectory(folder);
    var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
    var bytes=Encoding.UTF8.GetBytes("archived model and drawing "+mode);var seed=Path.Combine(folder,"seed");File.WriteAllBytes(seed,bytes);var hash=WorkingReuseHash(seed);File.Delete(seed);
    var owner=New("ControlledOpenManifestDto");Set(owner,"Id",Guid.NewGuid());Set(owner,"ProjectId",project);Set(owner,"ProjectCode","QA-DRAW");Set(owner,"RootRevision","W3");Set(owner,"ForEdit",false);
    var files=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
    var contextCase=mode.StartsWith("context-");var fileCount=contextCase?43:215;
    for(var index=0;index<fileCount;index++)
    {
     var file=New("ControlledOpenFileDto");var name=contextCase?(index==0?"CW004-3.SLDASM":index==1?"标签机钣金.SLDPRT":index==2?"标签机钣金.SLDDRW":index==3?"标签机.SLDASM":"UNTOUCHED-"+index+".SLDPRT"):(index==0?"CW0030002.10.SLDASM":index==1?"7000093.101.03-02.SLDPRT":index==2?"7000093.101.03-02.SLDDRW":"UNTOUCHED-"+index+".SLDPRT");
     Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Configuration","Default");
     Set(file,"Revision",contextCase&&(index==0||index==3)?"W1":"W3");Set(file,"Sha256",hash);Set(file,"FileLength",(long)bytes.Length);Set(file,"IsRoot",index==0);files.Add(file);
     var path=Path.Combine(folder,name);File.WriteAllBytes(path,bytes);
     if(!(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),hash,(long)bytes.Length}))throw new Exception("Fixture binding failed");
     File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    Set(owner,"Files",files);Set(owner,"RootDocumentId",Get(files[0],"DocumentId"));Set(owner,"RootVersionId",Get(files[0],"VersionId"));Set(owner,"RootRelativePath",Get(files[0],"FileName"));
    var contextKey=(string)Type("PdmAddin").GetMethod("NormalizeDirectory",Flags).Invoke(null,new object[]{folder});
    if(mode.StartsWith("registered-"))TreeCall(plugin,"RememberControlledOpenManifest",owner,folder.Replace('\\','/')+"/");
    var json=new JavaScriptSerializer();var requested=json.Deserialize(json.Serialize(owner),owner.GetType());
    var requestedFiles=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
    foreach(var file in new[]{files[2],files[1]}){var copy=json.Deserialize(json.Serialize(file),file.GetType());Set(copy,"IsRoot",file==files[2]);requestedFiles.Add(copy);}
    if(contextCase)foreach(var file in new[]{files[3],files[0]}.Concat(files.Cast<object>().Skip(4)))
    {
     var copy=json.Deserialize(json.Serialize(file),file.GetType());Set(copy,"IsRoot",false);
     if(file==files[0]||file==files[3])Set(copy,"Configuration","");
     if(mode=="context-older-environment"&&(file==files[0]||file==files[3])){Set(copy,"VersionId",Guid.NewGuid());Set(copy,"Sha256",new string('B',64));}
     requestedFiles.Add(copy);
    }
    Set(requested,"Files",requestedFiles);Set(requested,"RootDocumentId",Get(files[2],"DocumentId"));Set(requested,"RootVersionId",Get(files[2],"VersionId"));Set(requested,"RootRelativePath",Get(files[2],"FileName"));
    if(mode.StartsWith("resolved-")||contextCase)
    {
     var revision=mode=="resolved-yes-wrong-revision"?"W8":"W3";
     var warnings=new List<string>{"引用文件"+Get(files[1],"FileName")+"在当前快照中记录了多个版本（W3、W1），已统一使用最新受控版本"+revision+"。"};
     if(mode=="resolved-yes-missing")warnings.Add("引用文件MISSING.SLDPRT缺失，本次按缺失引用打开。");
     Set(requested,"Warnings",warnings);
    }
    var rootPath=Path.Combine(folder,(string)Get(files[0],"FileName"));var partPath=Path.Combine(folder,(string)Get(files[1],"FileName"));var drawingPath=Path.Combine(folder,(string)Get(files[2],"FileName"));
    if(mode=="missing-drawing"){File.SetAttributes(drawingPath,FileAttributes.Normal);File.Delete(drawingPath);files.RemoveAt(2);}
    if(mode=="stale-version"||mode=="resolved-yes-stale-model"||mode=="context-model-mismatch")Set(requestedFiles[1],"VersionId",Guid.NewGuid());
    if(mode=="stale-config")Set(requestedFiles[1],"Configuration","Other");
    if(mode=="stale-drawing")Set(requestedFiles[0],"VersionId",Guid.NewGuid());
    if(mode.StartsWith("drawing-placeholder")){Set(files[2],"Configuration","图纸");Set(requestedFiles[0],"Configuration","默认");}
    if(mode=="drawing-placeholder-source-copy"){Set(requestedFiles[0],"Sha256",new string('A',64));Set(requestedFiles[0],"FileLength",(long)bytes.Length+1207);}
    if(mode=="bad-binding")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{partPath,Guid.NewGuid(),project,Get(files[1],"VersionId"),"W3",hash,(long)bytes.Length});
    var dirty=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var writable=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if(mode=="dirty-total")dirty.Add(rootPath);
    if(mode=="dirty-owned-part"||mode=="registered-owned-part"){dirty.Add(partPath);writable.Add(partPath);}
    if(mode=="saved-owned-part"||mode=="unknown-edit"){File.SetAttributes(partPath,FileAttributes.Normal);File.AppendAllText(partPath," saved geometry edit");writable.Add(partPath);}
    if(mode=="loaded-owned-drawing"||mode=="latest-edit-owned"){dirty.Add(drawingPath);writable.Add(drawingPath);}
    if(mode=="archive-saved-drawing-edit"){File.SetAttributes(drawingPath,FileAttributes.Normal);File.AppendAllText(drawingPath," saved annotation edits");writable.Add(drawingPath);}
    foreach(var path in writable)File.SetAttributes(path,FileAttributes.Normal);
    if(mode=="context-parent-tampered"){var path=Path.Combine(folder,(string)Get(files[3],"FileName"));File.SetAttributes(path,FileAttributes.Normal);File.AppendAllText(path," untracked parent changes");File.SetAttributes(path,FileAttributes.ReadOnly);}
    var before=Directory.GetFiles(folder).ToDictionary(path=>path,path=>Tuple.Create(WorkingReuseHash(path),File.GetAttributes(path)));
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var loaded=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);var closed=new List<string>();var opened=new List<string>();var mutation=new List<string>();var active=partPath;
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    object ModelCall(System.Runtime.Remoting.Messaging.IMethodCallMessage call,string path,int kind)
    {
     switch(call.MethodName){case "GetHashCode":return path.GetHashCode();case "Equals":return loaded.TryGetValue(path,out var same)&&ReferenceEquals(same,call.Args[0]);case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return kind;case "GetSaveFlag":return dirty.Contains(path);case "IsOpenedReadOnly":return !writable.Contains(path);
      case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;
      case "SetReadOnlyState":if(path==drawingPath&&mode=="latest-edit-new"&&!(bool)call.Args[0]){writable.Add(path);return true;}goto case "Save3";
      case "Save3":case "SetSaveFlag":mutation.Add(path+":"+call.MethodName);throw new Exception("Original CAD document was mutated");default:throw new Exception("Unexpected model call "+call.MethodName);}
    }
    var part=new SheetReferenceModelProxy(modelType,false,call=>ModelCall(call,partPath,1)).GetTransparentProxy();
    var component=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?part:call.MethodName=="GetPathName"?partPath:call.MethodName=="get_IsVirtual"?false:call.MethodName=="get_Name2"?"PART":throw new Exception(call.MethodName)).GetTransparentProxy();
    var total=new DiscardAssemblyProxy(modelType,call=>call.MethodName=="GetComponents"?new[]{component}:ModelCall(call,rootPath,2)).GetTransparentProxy();
    var outsidePath=Path.Combine(folder,"outside","FOREIGN.SLDPRT");var outside=new SheetReferenceModelProxy(modelType,false,call=>ModelCall(call,outsidePath,1)).GetTransparentProxy();
    object View(object model,string path)=>new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>call.MethodName=="get_ReferencedDocument"?model:call.MethodName=="GetReferencedModelName"?path:throw new Exception(call.MethodName)).GetTransparentProxy();
    var wrongView=mode=="wrong-view-second-sheet"||mode=="resolved-yes-wrong-view"||mode=="context-wrong-second-sheet";
    var drawing=new SheetReferenceModelProxy(modelType,true,call=>call.MethodName=="GetViews"?new object[]{new object[]{View(outside,outsidePath),View(part,partPath)},new object[]{View(null,"old-sheet-property.SLDASM"),View(wrongView?outside:part,wrongView?outsidePath:partPath)}}:ModelCall(call,drawingPath,3)).GetTransparentProxy();
    loaded.Add(rootPath,total);loaded.Add(partPath,part);if(mode=="loaded-owned-drawing"||mode=="latest-edit-owned")loaded.Add(drawingPath,drawing);
    string parentPath=null;
    if(contextCase){parentPath=Path.Combine(folder,(string)Get(files[3],"FileName"));loaded.Add(parentPath,new DiscardAssemblyProxy(modelType,call=>call.MethodName=="GetComponents"?new[]{component}:ModelCall(call,parentPath,2)).GetTransparentProxy());}
    if(mode=="other-directory")
    {
     var foreign=Path.Combine(directory,"foreign",Path.GetFileName(partPath));
     loaded.Add(foreign,new SheetReferenceModelProxy(modelType,false,call=>ModelCall(call,foreign,1)).GetTransparentProxy());
    }
    var search=false;
    SetField(plugin,"application",new Proxy(appType,call=>{
     switch(call.MethodName){case "GetDocuments":return loaded.Values.ToArray();case "get_ActiveDoc":return loaded.TryGetValue(active,out var current)?current:null;
      case "CloseDoc":var path=loaded.Keys.FirstOrDefault(value=>Path.GetFileName(value)==(string)call.Args[0]);closed.Add(path??(string)call.Args[0]);if(path!=null)loaded.Remove(path);return null;
      case "OpenDoc6":opened.Add((string)call.Args[0]);if(mode=="native-open-null"||(string)call.Args[0]!=drawingPath)throw new IOException("原生工程图打开失败，保留原窗口。");if(mode=="archive-saved-drawing-edit")writable.Remove(drawingPath);active=(string)call.Args[0];loaded[active]=drawing;return drawing;
      case "ActivateDoc3":active=drawingPath;return drawing;
      case "GetDocumentDependencies2":return new string[0];case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;
      case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
      default:mutation.Add(call.MethodName);throw new Exception("Unexpected native mutation "+call.MethodName);}
    }).GetTransparentProxy());
    var contexts=(IDictionary)Field(plugin,"controlledOpenManifests");if(!mode.StartsWith("registered-"))contexts.Add(contextKey,owner);
    var editDrawing=mode=="loaded-owned-drawing"||mode.StartsWith("latest-edit-");
    var doc=New("DocumentDto");var editedPath=editDrawing?drawingPath:partPath;var editedFile=editDrawing?requestedFiles[0]:files[1];
    Set(doc,"Id",Get(editedFile,"DocumentId"));Set(doc,"ProjectId",project);Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);Set(doc,"CheckedOutAt",new DateTime(2026,10,24,7,10,0,DateTimeKind.Utc));
    Set(doc,"FileName",Get(editedFile,"FileName"));Set(doc,"Kind",editDrawing?2:1);
    if(mode=="latest-edit-new"){Set(doc,"CheckedOutBy",null);Set(doc,"CheckoutSessionId",null);}
    var handler=new DrawingOpenHandler(requested,bytes,doc,session,mode);
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://drawing-open.invalid/"))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://drawing-open.invalid/")})
    using(var pane=(Control)New("PdmTaskPaneControl","工程图保留窗口回归"))
    using(var promptTimer=new System.Windows.Forms.Timer{Interval=20})
    {
     var promptCount=0;
     promptTimer.Tick+=(_,__)=>
     {
      var prompt=FindWindow("#32770","确认打开引用版本");if(prompt==IntPtr.Zero)prompt=FindWindow("#32770","确认更新旧装配引用");
      if(prompt==IntPtr.Zero)return;GetWindowThreadProcessId(prompt,out var pid);if(pid!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id)return;
      promptCount++;SendMessage(prompt,0x111,new IntPtr(mode=="resolved-no"?7:6),IntPtr.Zero);
     };
     if((mode.StartsWith("resolved-")||contextCase)&&mode!="resolved-without-confirmation")promptTimer.Start();
     pane.CreateControl();http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
     SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,directory));SetField(plugin,"lifetime",lifetime);SetField(plugin,"taskPaneControl",pane);SetField(plugin,"authenticatedUsername","tester");
     var scope=(string)Get(client,"EditJournalScope")+"|tester";SetField(plugin,"editJournalScope",scope);SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),scope));
     if(mode=="dirty-owned-part"||mode=="registered-owned-part"||mode=="saved-owned-part"||mode=="loaded-owned-drawing"||mode=="latest-edit-owned")
     {
      var node=New("CadTreeNode");Set(node,"DocumentId",Get(doc,"Id"));Set(node,"FullPath",editedPath);Set(node,"FileName",Path.GetFileName(editedPath));Set(node,"OpenedVersionId",Get(editedFile,"VersionId"));
      Type("PdmAddin").GetMethod("RememberLocalEdit",Flags).Invoke(plugin,new[]{node,doc});
     }
     var edits=(IDictionary)Field(plugin,"localEdits");var editCount=edits.Count;
     bool success;
     if(mode.StartsWith("archive-"))success=ReferenceEquals(TreeCall(plugin,"OpenControlledArchiveDrawing",drawingPath,true),drawing);
     else
     {
      var task=(Task)Type("PdmAddin").GetMethod("ExecuteControlledOpenAsync",Flags).Invoke(plugin,new object[]{project,Get(requested,"RootDocumentId"),Enum.Parse(Type("ControlledOpenMode"),mode=="historical-conflict"?"SpecificReadOnly":mode.StartsWith("latest-edit-")?"LatestEdit":"LatestReadOnly"),mode=="historical-conflict"?Get(requested,"RootVersionId"):null,null,null,false});
      await task;for(var index=0;index<200&&(int)Field(plugin,"openOperationInProgress")!=0;index++)await Task.Delay(10);success=(bool)Get(task,"Result");
     }
     promptTimer.Stop();
     var expected=mode.StartsWith("registered-")||mode.StartsWith("drawing-placeholder")||mode.StartsWith("archive-")||new[]{"clean","dirty-total","dirty-owned-part","saved-owned-part","loaded-owned-drawing","missing-drawing","latest-edit-owned","latest-edit-new","resolved-yes","context-yes","context-older-environment"}.Contains(mode);
     var allowedClose=wrongView?1:0;
     if(!loaded.ContainsKey(rootPath)||!loaded.ContainsKey(partPath)||contextCase&&!loaded.ContainsKey(parentPath)||closed.Count!=allowedClose||closed.Any(path=>path!=drawingPath))throw new Exception("Drawing open closed the user's part/total "+mode+": "+string.Join(";",closed));
     if(success!=expected)throw new Exception("Unexpected drawing open result "+mode+": "+Field(plugin,"controlledOpenFailureMessage"));
     if(mutation.Count!=0||handler.Mutations!=0||handler.Downloads!=(mode=="missing-drawing"?1:0))throw new Exception("Unexpected model mutation or broad download "+mode+" native="+string.Join(",",mutation)+" download="+handler.Downloads);
     if(contexts.Count!=1||!ReferenceEquals(contexts[contextKey],owner)||((IList)Get(owner,"Files")).Count!=fileCount||edits.Count!=editCount+(mode=="latest-edit-new"?1:0))throw new Exception("Opening replaced assembly manifest or edit recovery "+mode);
     if(handler.Checkouts!=(mode=="latest-edit-new"?1:0))throw new Exception("Unexpected lease count "+mode);
     foreach(var pair in before)if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||File.GetAttributes(pair.Key)!=(mode=="latest-edit-new"&&pair.Key==drawingPath?FileAttributes.Normal:pair.Value.Item2))throw new Exception("Opening rewrote original bytes or permissions "+mode+" "+pair.Key);
     if(mode=="resolved-no")
     {if(Field(plugin,"controlledOpenFailureMessage")!=null||handler.Manifests!=1||opened.Count!=0)throw new Exception("拒绝引用更新应中止打开，不得作为失败或继续下载");}
     else if(!expected&&!(Field(plugin,"controlledOpenFailureMessage") as string??"").Any(character=>character>='\u4e00'&&character<='\u9fff'))throw new Exception("Failure explanation is not Chinese "+mode);
     if((mode.StartsWith("resolved-")||contextCase)&&promptCount!=(mode=="resolved-without-confirmation"?0:1))throw new Exception("版本选择确认次数错误 "+mode);
     if(expected&&(string)Get(owner,"RootRelativePath")!=(string)Get(files[0],"FileName"))throw new Exception("Assembly context root became drawing");
     Console.WriteLine("PASS: drawing open "+mode+"; "+fileCount+"-file context, original part/total windows, bytes, attributes and edit records retained");
    }
   }
  }
  finally
  {
   bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);
   foreach(var path in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(path,FileAttributes.Normal);
   Directory.Delete(directory,true);
  }
 }

 sealed class DrawingOpenHandler:HttpMessageHandler
 {
  readonly object manifest,document;readonly byte[] bytes;readonly Guid session;readonly string mode;public int Downloads,Mutations,Checkouts,Manifests;
  public DrawingOpenHandler(object manifest,byte[] bytes,object document,Guid session,string mode){this.manifest=manifest;this.bytes=bytes;this.document=document;this.session=session;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/file")){Downloads++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/open-manifest"))
   {
    Manifests++;
    if((mode.StartsWith("resolved-")||mode.StartsWith("context-"))&&mode!="resolved-without-confirmation"&&!request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"resolveLegacyReferences\":true"))
     return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent(new JavaScriptSerializer().Serialize(new{message="引用快照包含同一图档的不同版本：PART.SLDPRT（W3、W1）。请明确确认更新引用后再打开，系统未自动替换子件版本。"}))});
    value=manifest;
   }
   else if(path.EndsWith("/documents"))value=new[]{document};
   else if(path.EndsWith("/versions"))
   {
    var id=Guid.Parse(path.Split('/').Reverse().Skip(1).First());var all=((IEnumerable)Get(manifest,"Files")).Cast<object>().ToArray();var file=all.Single(item=>(Guid)Get(item,"DocumentId")==id);var seed=Path.GetTempFileName();File.WriteAllBytes(seed,bytes);var sourceHash=WorkingReuseHash(seed);File.Delete(seed);
    var kind=(string)Get(file,"FileName")=="标签机钣金.SLDDRW"?2:1;var refs=new List<object>();
    if(mode.StartsWith("context-")&&kind==2){var model=all.Single(item=>(string)Get(item,"FileName")=="标签机钣金.SLDPRT");refs.Add(new{documentId=Get(model,"DocumentId"),fileName=Get(model,"FileName"),kind=1,role=0,children=new object[0]});}
    if(mode.StartsWith("context-")&&kind==1)foreach(var environment in all.Where(item=>((string)Get(item,"FileName")).EndsWith(".SLDASM")))
    {
     refs.Add(new{documentId=Get(environment,"DocumentId"),fileName=Get(environment,"FileName"),kind=0,role=1,isContextReference=mode!="context-no-marker",referenceTypeUnverified=mode=="context-unverified",children=new object[0]});
     if(mode=="context-true-source")refs.Add(new{documentId=Get(environment,"DocumentId"),fileName=Get(environment,"FileName"),kind=0,role=1,isContextReference=false,children=new object[0]});
    }
    value=new[]{new{id=Get(file,"VersionId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength"),propertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",sourceHash}},referenceSnapshot=new{documentId=id,fileName=Get(file,"FileName"),kind=kind,children=refs.ToArray()}}};
   }
   else if(path.EndsWith("/checkout")){Checkouts++;Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);value=document;}
   else{Mutations++;throw new Exception("Unexpected business mutation "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
