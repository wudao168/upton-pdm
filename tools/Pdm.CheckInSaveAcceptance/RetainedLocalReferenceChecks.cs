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
 static async Task RetainedLocalReferenceScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
  var oldBinding=binding.GetValue(null);var oldResolver=resolver.GetValue(null);var oldIsolated=isolated.GetValue(null);
  binding.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));isolated.SetValue(null,true);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  try
  {
   foreach(var mode in new[]{"external","loaded","copied-peer","workspace","subassembly","registered-empty","managed","managed-stale","managed-other-path","missing-source","conflicting-peer","ambiguous","registered","bound-file","source-race","owner-race","no-resume","no-retained","invalid-list","tamper"})
   {
    var folder=Path.Combine(output,mode);var directory=Path.Combine(folder,"View","QA","装配工作区");var desktop=Path.Combine(folder,"Desktop","测试");Directory.CreateDirectory(directory);Directory.CreateDirectory(desktop);
    var rootPath=Path.Combine(directory,"ROOT.SLDASM");File.WriteAllText(rootPath,"server base assembly");var baseHash=WorkingReuseHash(rootPath);var baseLength=new FileInfo(rootPath).Length;
    var project=Guid.NewGuid();var id=Guid.NewGuid();var version=Guid.NewGuid();
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName","ROOT.SLDASM");Set(file,"RelativePath","ROOT.SLDASM");Set(file,"Configuration","Default");Set(file,"Sha256",baseHash);Set(file,"FileLength",baseLength);
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",version);Set(manifest,"RootRelativePath","ROOT.SLDASM");
    var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{rootPath,id,project,version,"W2",baseHash,baseLength});
    File.WriteAllText(rootPath,"saved local assembly with a new unarchived component");var savedHash=WorkingReuseHash(rootPath);
    if(mode!="no-retained")((IDictionary)Get(manifest,"RetainedWorkingHashes")).Add(id,savedHash);
    if(mode!="no-resume")Get(manifest,"ResumedDocumentIds").GetType().GetMethod("Add").Invoke(Get(manifest,"ResumedDocumentIds"),new object[]{id});
    var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");var session=(Guid)Field(plugin,"checkoutSessionId");var draft=New("LocalEditDraft");Set(draft,"DocumentId",id);Set(draft,"ProjectId",project);Set(draft,"SessionId",session);Set(draft,"SourcePath",rootPath);((IDictionary)Field(plugin,"localEdits")).Add(id,draft);
    var managed=mode.StartsWith("managed");var managedId=Guid.NewGuid();var managedVersion=Guid.NewGuid();
    var name=mode=="subassembly"?"新增子装配.SLDASM":"底座.SLDPRT";var source=Path.Combine(mode=="workspace"||managed&&mode!="managed-other-path"?directory:desktop,name);File.WriteAllText(source,"new local design; never uploaded");
    object serverVersion=null;
    if(managed)
    {
     var hash=WorkingReuseHash(source);var length=new FileInfo(source).Length;
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{source,managedId,project,managedVersion,"W1",hash,length});
     serverVersion=New("DocumentVersionDto");Set(serverVersion,"Id",mode=="managed-stale"?Guid.NewGuid():managedVersion);Set(serverVersion,"Sha256",hash);Set(serverVersion,"FileLength",length);
    }
    var peer=Path.Combine(directory,name);if(mode=="copied-peer"||mode=="conflicting-peer")File.WriteAllText(peer,mode=="conflicting-peer"?"different preexisting local file":File.ReadAllText(source));
    var child=Path.Combine(desktop,"子件.SLDPRT");if(mode=="subassembly")File.WriteAllText(child,"nested new part");
    var other=Path.Combine(folder,"Other",name);if(mode=="ambiguous"){Directory.CreateDirectory(Path.GetDirectoryName(other));File.WriteAllText(other,"second distinct file with same name");}
    if(mode=="missing-source")File.Delete(source);
    if(mode=="bound-file")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{source,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"W1",WorkingReuseHash(source),new FileInfo(source).Length});
    var reads=0;var forbidden=0;var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var selectedPath=mode=="copied-peer"?peer:source;
    var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var includeExistingPart=false;var loadedDirty=false;
    var loadedPart=new Proxy(modelType,call=>{switch(call.MethodName){case "GetPathName":return selectedPath;case "GetSaveFlag":return loadedDirty;case "GetType":return 1;case "get_Extension":return null;default:forbidden++;throw new Exception(call.MethodName);}}).GetTransparentProxy();
    var componentType=interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
    var component=new Proxy(componentType,call=>{switch(call.MethodName){case "GetPathName":return selectedPath;case "GetModelDoc2":return mode=="loaded"?loadedPart:null;case "get_IsVirtual":return false;case "get_Name2":return "底座-1";case "GetParent":return null;default:throw new Exception(call.MethodName);}}).GetTransparentProxy();
    var model=new AssemblyProxy(modelType,call=>{switch(call.MethodName){case "GetPathName":return rootPath;case "GetType":return 2;case "GetComponents":return new[]{component};case "get_Extension":return null;default:forbidden++;throw new Exception("恢复不得保存、关闭或改写模型："+call.MethodName);}}).GetTransparentProxy();
    var app=new Proxy(appType,call=>{
     if(call.MethodName=="GetDocuments")return includeExistingPart?new[]{loadedPart}:new object[0];
     if(call.MethodName=="GetDocumentDependencies2")
     {
      reads++;if((bool)call.Args[1]||(bool)call.Args[2]||(bool)call.Args[3])throw new Exception("仅可读取保存的直接引用，不进行全盘搜索或打开模型");
      var owner=(string)call.Args[0];if(mode=="invalid-list")return new[]{"incomplete"};
      if(owner==rootPath)return mode=="ambiguous"?new[]{name,source,name,other}:new[]{name,source};
      if(mode=="subassembly"&&owner==source)return new[]{"子件.SLDPRT",child};return new string[0];
     }
     forbidden++;throw new Exception("恢复不得打开、关闭、保存或修改模型："+call.MethodName);
    }).GetTransparentProxy();SetField(plugin,"application",app);
    var document=New("DocumentDto");Set(document,"Id",id);Set(document,"ProjectId",project);Set(document,"FileName","ROOT.SLDASM");
    var documents=new List<object>{document};if(mode=="registered"||mode=="registered-empty"){var registered=New("DocumentDto");Set(registered,"Id",Guid.NewGuid());Set(registered,"ProjectId",project);Set(registered,"FileName",name);Set(registered,"StoredVersionCount",mode=="registered"?1:0);documents.Add(registered);}
    if(managed){var registered=New("DocumentDto");Set(registered,"Id",managedId);Set(registered,"ProjectId",project);Set(registered,"FileName",name);Set(registered,"Kind",(int)Enum.Parse(Type("CadDocumentKind"),"Part"));Set(registered,"StoredVersionCount",1);documents.Add(registered);}
    var network=new RetainedLocalHandler(documents,()=>{if(mode=="source-race")File.AppendAllText(source,"concurrent local edit");if(mode=="owner-race")File.AppendAllText(rootPath,"concurrent assembly edit");},serverVersion);
    using(var client=(IDisposable)New("PdmApiClient","http://local-recovery.invalid/"))using(var http=new HttpClient(network){BaseAddress=new Uri("http://local-recovery.invalid/")})using(var lifetime=new CancellationTokenSource())
    {
     SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(path=>path,WorkingReuseHash);
     Exception failure=null;try{var method=Type("PdmAddin").GetMethod("PrepareRetainedLocalReferencesAsync",Flags);if(method!=null)await (Task)method.Invoke(plugin,new[]{rootPath,manifest});}catch(Exception error){failure=error is TargetInvocationException?error.InnerException:error;}
     var rejected=new[]{"managed-stale","managed-other-path","missing-source","conflicting-peer","ambiguous","registered","bound-file","source-race","owner-race","invalid-list"}.Contains(mode);
     if(rejected)Check(failure is InvalidOperationException,mode+"：缺件、同名冲突、登记身份和并发变化均停止恢复，保留本地文件");
     else
     {
      Check(failure==null,mode+"：恢复准备完成");
      var scoped=Property(manifest,"RetainedLocalReferences")?.GetValue(manifest) as IDictionary;
      var permitted=mode!="no-resume"&&mode!="no-retained";
      Check((scoped?.Count??0)==(permitted?(mode=="subassembly"?2:1):0),mode+"：仅原会话已验证的保存修改可以附带新增引用");
      var blocked=false;using(var boundary=(IDisposable)New("ControlledOpenBoundary",directory,manifest))blocked=(bool)Type("ControlledOpenBoundary").GetMethod("Reject",Flags).Invoke(null,new object[]{selectedPath});
      Check(blocked!=permitted,mode+"：受控边界仅放行精确核对的本地新增文件");
      if(permitted)
      {
       TreeCall(plugin,"ValidateControlledLoadedSources",model,rootPath,manifest,app);Check(true,mode+"：未加载的新增零件或子装配按本地待存档引用核对，不误报服务器缺件");
       if(managed)
       {
        includeExistingPart=true;loadedDirty=true;var blockedUnsaved=false;
        try{Type("ControlledOpenBoundary").GetMethod("RequireLoadedSources",Flags).Invoke(null,new[]{app,rootPath,manifest});}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){blockedUnsaved=true;}
        Check(blockedUnsaved,"已有PLM零件的未保存窗口不因新装配关系被降为只读或覆盖，须先核对其权限");
        includeExistingPart=false;loadedDirty=false;
       }
       var tree=New("CadTreeNode");Set(tree,"FullPath",rootPath);Set(tree,"FileName","ROOT.SLDASM");Set(tree,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(tree,"DocumentId",id);
       var added=New("CadTreeNode");Set(added,"FullPath",selectedPath);Set(added,"FileName",name);Set(added,"Kind",Enum.Parse(Type("CadDocumentKind"),mode=="subassembly"?"Assembly":"Part"));((IList)Get(tree,"Children")).Add(added);
       if(managed)Set(added,"DocumentId",managedId);
       var archiveItems=((IEnumerable)TreeCall(plugin,"BuildBatchOperationItems",tree)).Cast<object>().ToArray();
       Check(archiveItems.Any(item=>ReferenceEquals(Get(item,"Node"),added))&&(managed?(Guid?)Get(added,"DocumentId")==managedId:Get(added,"DocumentId")==null),mode+"：存档清单保留新增引用，已有图档沿用原身份，未入库图档沿用首次登记流程");
       ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(directory,manifest);TreeCall(plugin,"ValidateControlledArchiveSources",tree);
       Check(true,mode+"：恢复后可以进入原有存档来源校验，等待用户确认新增图档");
       var ordinary=new JavaScriptSerializer().Deserialize(new JavaScriptSerializer().Serialize(manifest),Type("ControlledOpenManifestDto"));
       using(var boundary=(IDisposable)New("ControlledOpenBoundary",directory,ordinary))Check((bool)Type("ControlledOpenBoundary").GetMethod("Reject",Flags).Invoke(null,new object[]{selectedPath}),mode+"：普通服务器清单和历史打开不能继承本地草稿的新增放行许可");
       var unknown=Path.Combine(folder,"Other","ROOT.SLDASM");Directory.CreateDirectory(Path.GetDirectoryName(unknown));File.WriteAllText(unknown,"uncontrolled older assembly");
       using(var boundary=(IDisposable)New("ControlledOpenBoundary",directory,manifest))Check((bool)Type("ControlledOpenBoundary").GetMethod("Reject",Flags).Invoke(null,new object[]{unknown}),mode+"：已有受控图档的外部同名原文件仍被拒绝");
       File.Delete(unknown);
       if(mode=="tamper")
       {
        File.AppendAllText(source,"changed after proof");var caught=false;try{TreeCall(plugin,"ValidateControlledLoadedSources",model,rootPath,manifest,app);}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){caught=true;}
        Check(caught,"新增文件在原生打开期间变化时不能认定打开完成");
       }
      }
      if(!permitted)Check(reads==0&&network.Requests==0,mode+"：普通服务器或历史打开不读取、更不自动收纳本地额外文件");
      var json=new JavaScriptSerializer().Serialize(manifest);Check(!json.Contains("RetainedLocalReferences"),mode+"：本机新增引用许可不进入服务器或缓存清单");
     }
     Check(network.Writes==0&&forbidden==0,mode+"：不登记、不上传、不保存、不关闭或改名模型");
     if(!new[]{"source-race","owner-race","tamper","missing-source"}.Contains(mode))Check(before.All(pair=>File.Exists(pair.Key)&&WorkingReuseHash(pair.Key)==pair.Value),mode+"：所有原图档内容保持原样");
     Check(files.Count==1,mode+"：不伪造服务器图档编号、版本或扩大服务器清单");
    }
   }
  }
  finally{binding.SetValue(null,oldBinding);resolver.SetValue(null,oldResolver);isolated.SetValue(null,oldIsolated);}
 }
 sealed class RetainedLocalHandler:HttpMessageHandler
 {
  readonly List<object> documents;readonly Action read;readonly object version;internal int Writes,Requests;
  internal RetainedLocalHandler(List<object> documents,Action read,object version){this.documents=documents;this.read=read;this.version=version;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   Requests++;if(request.Method==HttpMethod.Get&&request.RequestUri.AbsolutePath.EndsWith("/versions")&&version!=null)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(new[]{version}))});
   if(request.Method!=HttpMethod.Get||!request.RequestUri.AbsolutePath.EndsWith("/documents")){Writes++;throw new Exception("只能读取项目图档及版本，不能登记或上传");}
   read();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(documents))});
  }
 }
}
