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

internal static partial class Program
{
 static async Task IndependentImportScenario(string output)
 {
  output=Path.Combine(Path.GetFullPath(output),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var store=Type("PdmDocumentIdentityStore");var bindings=store.GetField("bindingDirectory",Flags);var resolver=store.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);var managed=Path.Combine(output,"managed");
  bindings.SetValue(null,Path.Combine(Path.GetDirectoryName(output),"bindings"));resolver.SetValue(null,(Func<string>)(()=>managed));
  var json=new JavaScriptSerializer();
  object Meta(string path,Guid project){var args=new object[]{path,project,null};return (bool)store.GetMethod("TryReadArchiveMetadata",Flags).Invoke(null,args)?args[2]:null;}
  string Legacy(string path,Guid project,Guid document,Guid version){
   var record=(string)store.GetMethod("BindingPath",Flags).Invoke(null,new object[]{path});Directory.CreateDirectory(Path.GetDirectoryName(record));
   File.WriteAllText(record,json.Serialize(new{SchemaVersion=1,FullPath=path,MachineName=Environment.MachineName,ProjectId=project,DocumentId=document,VersionId=version,Revision="W1",Sha256=WorkingReuseHash(path),FileLength=new FileInfo(path).Length}));return record;
  }
  try{
   // One physical source can be independently imported into two projects without
   // overwriting the earlier import's identity or touching the source file.
   var source=Path.Combine(output,"SAME.SLDPRT");File.WriteAllText(source,"all saved configurations and geometry");
   var a=Guid.NewGuid();var b=Guid.NewGuid();var da=Guid.NewGuid();var db=Guid.NewGuid();var va=Guid.NewGuid();var vb=Guid.NewGuid();
   var legacy=Legacy(source,a,da,va);var legacyHash=WorkingReuseHash(legacy);var sourceHash=WorkingReuseHash(source);File.SetAttributes(source,FileAttributes.ReadOnly);
   Check((bool)store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{source,db,b,vb,"W1",sourceHash,new FileInfo(source).Length}),"只读源文件可记录第二项目的成功回执，不要求原文件写权限");
   Check(WorkingReuseHash(legacy)==legacyHash&&Equals(Get(Meta(source,a),"DocumentId"),da)&&Equals(Get(Meta(source,b),"DocumentId"),db),"旧登记保持不变，A/B项目分别取得各自图档及版本");
   var node=New("CadTreeNode");Set(node,"FullPath",source);Set(node,"FileName",Path.GetFileName(source));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(node,"ProvenanceProjectId",a);Set(node,"ProvenanceDocumentId",da);
   var restore=Type("ArchiveSourceIdentity").GetMethod("RestoreForProject",Flags);
   Set(node,"CheckedOutBy","old-project-user");Set(node,"DrawingReviewLocked",true);
   restore.Invoke(null,new object[]{node,b});Check(Equals(Get(node,"DocumentId"),db)&&Equals(Get(node,"OpenedVersionId"),vb),"再次选择B项目恢复B图档，不会当成首次存档");
   Check(Get(node,"CheckedOutBy")==null&&!(bool)Get(node,"DrawingReviewLocked"),"独立导入不携带旧项目检出或审核状态，原文件只读属性仍保留");
   restore.Invoke(null,new object[]{node,a});Check(Equals(Get(node,"DocumentId"),da)&&Equals(Get(node,"OpenedVersionId"),va),"切回A项目仍恢复原图档及版本");
   restore.Invoke(null,new object[]{node,Guid.NewGuid()});Check(Get(node,"DocumentId")==null&&Get(node,"OpenedVersionId")==null,"新目标项目不携带旧图档编号或基准版本");
   Check(WorkingReuseHash(source)==sourceHash&&(File.GetAttributes(source)&FileAttributes.ReadOnly)!=0,"身份恢复未改写源内容和只读属性");
   File.SetAttributes(source,FileAttributes.Normal);

   foreach(var mode in new[]{"foreign-source","previously-omitted","target-match","same-project","name-conflict","controlled-foreign"}){
    var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);var project=Guid.NewGuid();var foreign=Guid.NewGuid();var oldId=Guid.NewGuid();var targetId=Guid.NewGuid();var versionId=Guid.NewGuid();
    var owner=LinkedNode(folder,"CW004-3.SLDASM","Assembly");var ownerPath=(string)Get(owner,"FullPath");
    Set(owner,"Revision","W1");Set(owner,"CurrentRevision","W1");
    var step=Path.Combine(mode=="controlled-foreign"?managed:folder,"H620_外观.STEP");Directory.CreateDirectory(Path.GetDirectoryName(step));File.WriteAllText(step,"actual imported source bytes");
    var stepHash=WorkingReuseHash(step);var ownerHash=WorkingReuseHash(ownerPath);var record=Legacy(step,mode=="same-project"?project:foreign,mode=="same-project"?targetId:oldId,versionId);var recordHash=WorkingReuseHash(record);
    File.SetAttributes(step,FileAttributes.ReadOnly);
    var unselected=LinkedNode(folder,"UNSELECTED.SLDPRT","Part");((IList)Get(owner,"Children")).Add(unselected);
    if(mode=="previously-omitted"){
     var omitted=New("CadTreeNode");Set(omitted,"FullPath",step);Set(omitted,"FileName",Path.GetFileName(step));Set(omitted,"Kind",Enum.Parse(Type("CadDocumentKind"),"Other"));Set(omitted,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(omitted,"IsOutsideNativeFileScope",true);((IList)Get(owner,"Children")).Add(omitted);
    }
    var items=LinkedItems(owner);var selected=Array.CreateInstance(Type("CadTreeNode"),1);selected.SetValue(owner,0);
    TreeCallStatic("ArchiveMissingReferencePolicy","Prepare",owner,selected);
    var plugin=New("PdmAddin");SetField(plugin,"currentTree",owner);SetField(plugin,"authenticatedUsername","tester");
    using var pane=(IDisposable)New("PdmTaskPaneControl","fixture");SetField(plugin,"taskPaneControl",pane);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var directory=folder;
    var app=new Proxy(appType,call=>{
     switch(call.MethodName){
      case "GetDocuments":return new object[0];case "GetOpenDocumentByName":return null;
      case "GetCurrentWorkingDirectory":return directory;case "SetCurrentWorkingDirectory":directory=(string)call.Args[0];return true;
      case "GetDocumentDependencies2":return (string)call.Args[0]==ownerPath?new[]{"STEP",step,"ordinary",(string)Get(unselected,"FullPath")}:new string[0];
      default:throw new Exception("导入不得打开、解析、保存或断链原图："+call.MethodName);
     }
    }).GetTransparentProxy();SetField(plugin,"application",app);
    var requests=new List<string>();
    using(var client=(IDisposable)New("PdmApiClient","http://independent-import.invalid/"))
    using(var http=new HttpClient(new ReferenceRecoveryHandler(request=>{
     requests.Add(request.Method+" "+request.RequestUri.AbsolutePath);
     var path=request.RequestUri.AbsolutePath;string body;
     if(path=="/api/projects/"+project+"/documents")body=mode=="foreign-source"||mode=="previously-omitted"||mode=="controlled-foreign"?"[]":json.Serialize(new[]{new{id=targetId,projectId=project,fileName=Path.GetFileName(step),name="STEP",kind=5,revision=new{display="W1"}}});
     else if(path=="/api/documents/"+targetId+"/versions")body=json.Serialize(new[]{new{id=versionId,sha256=mode=="name-conflict"?new string('F',64):stepHash,revision=new{display="W1"},propertySnapshot=new Dictionary<string,string>()}});
     else throw new Exception("不允许访问旧项目、获取权限或上传："+request.Method+" "+path);
     return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)};
    })){BaseAddress=new Uri("http://independent-import.invalid/")}){
     SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);object result=null;Exception failure=null;
     try{var task=(Task)TreeCall(plugin,"CompletePdmCheckInDependenciesAsync",items,project,CancellationToken.None);await task;result=Get(task,"Result");}catch(Exception error){failure=error;}
     if(mode=="name-conflict"||mode=="controlled-foreign"){
      Check(failure!=null&&failure.ToString().Contains(mode=="name-conflict"?"内容不同":"其他PLM项目"),mode+"：不覆盖同名不同内容，也不转移库内受控图档归属");
     }else{
      if(failure!=null)throw new Exception(mode+"依赖收集失败",failure);
      var rows=((IEnumerable)result).Cast<object>().ToArray();var resource=((IList)Get(owner,"Children")).Cast<object>().Single(n=>(string)Get(n,"FullPath")==step);
      Check(!(bool)Get(resource,"IsOutsideNativeFileScope")&&((ISet<string>)Get(owner,"ConfirmedArchivePaths")).Contains(step),mode+"：实际可读取的STEP形成受控依赖，不再被遗漏后拦截");
      Check(rows.Length==(mode=="foreign-source"||mode=="previously-omitted"?2:1)&&!rows.Any(item=>(string)Get(Get(item,"Node"),"FullPath")==Get(unselected,"FullPath").ToString()),mode+"：只收集必要辅助来源，不选中其他3D图档");
      if(mode=="foreign-source"||mode=="previously-omitted"){
       var dependency=Get(rows.Single(item=>(string)Get(Get(item,"Node"),"FullPath")==step),"Node");
       Check(Get(dependency,"DocumentId")==null&&(bool)Get(dependency,"IndependentImportDependency"),"旧项目源文件按独立新依赖登记，不继承旧图档编号");
       var decision=TreeCall(plugin,"RegistrationDecisionFor",dependency,null);
       Check((bool)Get(decision,"AllowDuplicateContent"),"辅助来源沿用已确认的独立复制授权，不新增人工确认操作");
       var registered=New("DocumentDto");Set(registered,"Id",targetId);Set(registered,"ProjectId",project);Set(registered,"FileName",Path.GetFileName(step));Set(registered,"Kind",5);Set(registered,"Revision",LinkedRevision("W1"));
       TreeCall(plugin,"ApplyPreparedDocumentToMatchingInstances",dependency,registered,false);
       TreeCallStatic("PdmAddin","SynchronizeBatchFileIdentities",result);
       var captured=TreeCallStatic("PdmApiClient","CaptureArchiveSnapshot",owner,new HashSet<string>{ownerPath,step});
       var graph=json.Serialize(TreeCall(captured,"CreateRoot"));
       Check(graph.Contains(targetId.ToString())&&!graph.Contains(oldId.ToString()),"身份预留后上传快照使用目标项目新编号，不把旧项目资源带入图档关系");
      }else Check(Equals(Get(resource,"DocumentId"),targetId)&&Equals(Get(resource,"OpenedVersionId"),versionId),mode+"：同项目已存档且字节匹配的资源只复用原版本");
      Check(!requests.Any(request=>!request.StartsWith("GET ")||request.Contains(foreign.ToString())||request.Contains(oldId.ToString())),mode+"：没有旧项目写入、检出、下载或升版");
     }
     Check(WorkingReuseHash(ownerPath)==ownerHash&&WorkingReuseHash(step)==stepHash&&WorkingReuseHash(record)==recordHash&&(File.GetAttributes(step)&FileAttributes.ReadOnly)!=0,mode+"：原文件、只读属性及旧项目登记均未改变");
    }
    File.SetAttributes(step,FileAttributes.Normal);
   }
   await IndependentImportRecoveredSource(output,Check);
   IndependentImportMaterialization(output,Check);
  }finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }

 static object TreeCallStatic(string type,string method,params object[] args)=>Type(type).GetMethod(method,Flags).Invoke(null,args);

 static async Task IndependentImportRecoveredSource(string output,Action<bool,string> Check)
 {
  var folder=Path.Combine(output,"recovered");var library=Path.Combine(folder,"library");Directory.CreateDirectory(library);
  var owner=LinkedNode(folder,"RECOVER.SLDPRT","Part");var ownerPath=(string)Get(owner,"FullPath");var source=Path.Combine(folder,"old-profile","H620_外观.STEP");
  var actual=source;Directory.CreateDirectory(Path.GetDirectoryName(actual));File.WriteAllText(actual,"available source bytes");var hash=WorkingReuseHash(actual);var ownerHash=WorkingReuseHash(ownerPath);
  var project=Guid.NewGuid();var plugin=New("PdmAddin");SetField(plugin,"currentTree",owner);
  var projectList=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ProjectDto")));var dto=New("ProjectDto");Set(dto,"Id",project);Set(dto,"Code","RECOVER");projectList.Add(dto);SetField(plugin,"availableProjects",projectList);
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var current=folder;
  SetField(plugin,"application",new Proxy(appType,call=>{
   switch(call.MethodName){
    case "GetDocuments":return new object[0];case "GetOpenDocumentByName":return null;
    case "GetCurrentWorkingDirectory":return current;case "SetCurrentWorkingDirectory":current=(string)call.Args[0];return true;
    case "GetSearchFolders":case "GetUserPreferenceStringListValue":return library;
    case "GetDocumentDependencies2":return (string)call.Args[0]==ownerPath?new[]{"source",source}:new string[0];
    default:throw new Exception("辅助来源补齐不得打开、保存或改写原图："+call.MethodName);
   }
  }).GetTransparentProxy());
  var array=Array.CreateInstance(Type("CadTreeNode"),1);array.SetValue(owner,0);TreeCallStatic("ArchiveMissingReferencePolicy","Prepare",owner,array);
  using(var client=(IDisposable)New("PdmApiClient","http://recovered-import.invalid/"))
  using(var http=new HttpClient(new ReferenceRecoveryHandler(request=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")})){BaseAddress=new Uri("http://recovered-import.invalid/")}){
   SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,Path.Combine(output,"managed")));
   var recovery=(Task)TreeCall(plugin,"RecoverPdmReferencesAsync",LinkedItems(owner),project,CancellationToken.None);await recovery;
   var recovered=Get(recovery,"Result");Check(((IEnumerable)recovered).Cast<object>().Count()==2,"已找到且未单独勾选的STEP在前置引用收集时也随父图档保存，不再遗漏");
   var task=(Task)TreeCall(plugin,"CompletePdmCheckInDependenciesAsync",recovered,project,CancellationToken.None);await task;
   var resource=((IEnumerable)Get(task,"Result")).Cast<object>().Select(row=>Get(row,"Node")).Single(node=>(string)Get(node,"FileName")==Path.GetFileName(source));
   var copied=(string)Get(resource,"FullPath");Check(copied!=actual&&WorkingReuseHash(copied)==hash&&!(bool)Get(resource,"IsOutsideNativeFileScope"),"自动补齐与后续登记核对使用同一份原名原文的目标副本");
   Check(WorkingReuseHash(actual)==hash&&WorkingReuseHash(ownerPath)==ownerHash,"前置引用收集保留供应来源和原图内容");
  }
 }

 static void IndependentImportMaterialization(string output,Action<bool,string> Check)
 {
  var old=Path.Combine(output,"isolated-source");var cache=Path.Combine(output,"managed","PROJECT-B");Directory.CreateDirectory(old);Directory.CreateDirectory(cache);
  var source=Path.Combine(old,"ROOT.SLDPRT");var step=Path.Combine(old,"H620_外观.STEP");File.WriteAllText(source,"saved native document");File.WriteAllText(step,"source geometry");
  var original=WorkingReuseHash(source);var root=Path.Combine(cache,Path.GetFileName(source));var target=Path.Combine(cache,Path.GetFileName(step));File.Copy(source,root);File.Copy(step,target);
  var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());var files=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));Set(manifest,"Files",files);
  foreach(var path in new[]{root,target}){
   var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);files.Add(file);
  }
  File.Delete(step); // Isolate the original resource in this test fixture only.
  var current=step;var rewrites=0;var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  var app=new Proxy(appType,call=>{
   if(call.MethodName=="GetDocuments")return new object[0];
   if(call.MethodName=="GetDocumentDependencies2")return (string)call.Args[0]==root?new[]{"source",current}:new string[0];
   if(call.MethodName=="ReplaceReferencedDocument"){
    if((string)call.Args[0]!=root||(string)call.Args[1]!=step||(string)call.Args[2]!=target)throw new Exception("越界引用改写");current=target;File.AppendAllText(root,"|controlled reference");rewrites++;return true;
   }
   throw new Exception("仅允许受控副本路径核对："+call.MethodName);
  }).GetTransparentProxy();
  TreeCallStatic("ControlledReferenceMaterializer","Prepare",app,root,manifest);
  Check(rewrites==1&&current==target&&File.Exists(target)&&!File.Exists(step),"隔离原来源后，受控副本的STEP引用仍定位到当前项目缓存");
  Check(WorkingReuseHash(source)==original&&Path.GetFileName(root)==Path.GetFileName(source),"只改写下载副本引用，原文件内容和名称保持不变");
 }
}
