using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task UnloadedControlledModelOpenScenario(string output)
 {
  var identity=Type("PdmDocumentIdentityStore");
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  var notifications=New("PdmAddin");var previous="先前独立拦截";SetField(notifications,"blockedControlledOpenMessage",previous);
  TreeCall(notifications,"ClearReportedNativeOpenNotification",previous);
  Check(ReferenceEquals(Field(notifications,"blockedControlledOpenMessage"),previous),"本次打开失败不会消除先前独立拦截");
  SetField(notifications,"blockedControlledOpenMessage","本次打开回调拦截");TreeCall(notifications,"ClearReportedNativeOpenNotification",previous);
  Check(Field(notifications,"blockedControlledOpenMessage")==null,"本次原生打开失败统一提示后不重复弹出子件拦截");
  SetField(notifications,"blockedControlledOpenMessage","后续独立拦截");
  Check((string)Field(notifications,"blockedControlledOpenMessage")=="后续独立拦截","后续真正的新拦截仍然保留");
  foreach(var mode in new[]{"part","assembly","dirty-owner","stale-version","stale-config","tampered-child","tampered-dependency","owner-binding","different-project","no-loaded-owner","missing-inactive-owner","missing-loaded-owner","ambiguous","original-open",
      "reference-loaded","reference-unloaded","reference-other-path","reference-denied","reference-race","explicit-latest-conflict"})
  {
   var folder=Path.Combine(output,"View","unloaded-"+mode);Directory.CreateDirectory(folder);
   var project=Guid.NewGuid();var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var interop=appType.Assembly;var json=new System.Web.Script.Serialization.JavaScriptSerializer();
   object FileEntry(string name)
   {
    var path=Path.Combine(folder,name);File.WriteAllText(path,"controlled "+name);File.SetAttributes(path,FileAttributes.ReadOnly);
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Configuration","Default");Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Revision","W1");
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});return file;
   }
   var parent=FileEntry("OWNER.SLDASM");var target=FileEntry(mode=="assembly"?"CHILD.SLDASM":"CHILD.SLDPRT");var dependency=FileEntry("DEPENDENCY.SLDPRT");
   var parentPath=Path.Combine(folder,(string)Get(parent,"FileName"));var targetPath=Path.Combine(folder,(string)Get(target,"FileName"));
   if(mode=="missing-loaded-owner"){File.SetAttributes(parentPath,FileAttributes.Normal);File.Delete(parentPath);}
   var owner=New("ControlledOpenManifestDto");Set(owner,"ProjectId",mode=="different-project"?Guid.NewGuid():project);Set(owner,"RootDocumentId",Get(parent,"DocumentId"));Set(owner,"RootRelativePath",Get(parent,"FileName"));Set(owner,"RootVersionId",Get(parent,"VersionId"));
   var ownerFiles=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);Set(owner,"Files",ownerFiles);ownerFiles.Add(parent);ownerFiles.Add(target);ownerFiles.Add(dependency);
   var requested=New("ControlledOpenManifestDto");Set(requested,"ProjectId",project);Set(requested,"RootDocumentId",Get(target,"DocumentId"));Set(requested,"RootVersionId",Get(target,"VersionId"));Set(requested,"RootRelativePath",Get(target,"FileName"));
   var requestedFiles=(IList)Activator.CreateInstance(Property(requested,"Files").PropertyType);Set(requested,"Files",requestedFiles);
   var root=json.Deserialize(json.Serialize(target),target.GetType());Set(root,"IsRoot",true);requestedFiles.Add(root);requestedFiles.Add(json.Deserialize(json.Serialize(dependency),dependency.GetType()));
   if(mode=="stale-version")Set(root,"VersionId",Guid.NewGuid());
   if(mode=="stale-config")Set(root,"Configuration","Other");
   if(mode=="tampered-child"||mode=="tampered-dependency")
   {var path=mode=="tampered-child"?targetPath:Path.Combine(folder,(string)Get(dependency,"FileName"));File.SetAttributes(path,FileAttributes.Normal);File.AppendAllText(path," altered");}
   if(mode=="owner-binding")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)parentPath,Get(parent,"DocumentId"),project,Guid.NewGuid(),"W2",Get(parent,"Sha256"),Get(parent,"FileLength")});
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
   object Model(string path)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return path.EndsWith("SLDASM")?2:1;case "GetSaveFlag":return mode=="dirty-owner"&&path==parentPath;case "IsOpenedReadOnly":return true;case "get_ConfigurationManager":return manager;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("不得修改模型："+call.MethodName);}
   }).GetTransparentProxy();
   var referenceEntry=mode.StartsWith("reference-")||mode=="explicit-latest-conflict";
   var parentModel=Model(parentPath);var childModel=Model(targetPath);object active=parentModel;var opened=mode=="reference-loaded"||mode=="explicit-latest-conflict";var opens=0;var mutations=0;var search=false;
   var contexts=(IDictionary)Field(plugin,"controlledOpenManifests");contexts.Add(folder,owner);
   object otherOwner=null;
   if(mode=="ambiguous"||mode=="missing-inactive-owner")
   {
    var otherFolder=Path.Combine(output,"View","another-owner-"+mode);Directory.CreateDirectory(otherFolder);
    var other=json.Deserialize(json.Serialize(owner),owner.GetType());contexts.Add(otherFolder,other);
    if(mode=="ambiguous")
    {
     foreach(var file in ownerFiles.Cast<object>())
     {
      var path=Path.Combine(otherFolder,(string)Get(file,"FileName"));File.Copy(Path.Combine(folder,(string)Get(file,"FileName")),path);
      identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
     }
     otherOwner=Model(Path.Combine(otherFolder,(string)Get(parent,"FileName")));
    }
   }
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName)
    {
     case "GetDocuments":return (mode=="no-loaded-owner"?new object[0]:new[]{parentModel}).Concat(opened?new[]{childModel}:new object[0]).Concat(otherOwner==null?new object[0]:new[]{otherOwner}).Concat(mode=="original-open"?new[]{Model(Path.Combine(output,"original",(string)Get(target,"FileName")))}:new object[0]).ToArray();
     case "get_ActiveDoc":return active;
     case "OpenDoc6":Check((string)call.Args[0]==targetPath,"未加载子件从父装配工作区原路径打开："+mode);Check(((int)call.Args[2]&2)!=0,"查看未加载子件仍使用只读打开："+mode);opens++;opened=true;return childModel;
     case "ActivateDoc3":active=childModel;return childModel;
     case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";
     case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;
     case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
     default:mutations++;throw new Exception("不得关闭父装配、保存或替换文件："+call.MethodName);
    }
   }).GetTransparentProxy());
   // Tampered bytes must be denied even when the project has no owned drafts.
   using(var client=(IDisposable)New("PdmApiClient","http://unloaded-context.invalid/"))
   using(var handler=new ReferenceRecoveryHandler(request=>{
    if(!referenceEntry)return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent("[]")};
    var endpoint=request.RequestUri.AbsolutePath;
    if(endpoint.EndsWith("/document-operation-rules"))return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent("{\"disabledRuleIds\":[]}")};
    if(!endpoint.EndsWith("/open-manifest"))throw new Exception("不应下载文件或获取权限："+endpoint);
    var body=json.DeserializeObject(request.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as System.Collections.Generic.Dictionary<string,object>;
    var latest=mode=="explicit-latest-conflict";
    Check(Equals(body["forEdit"],false)&&Equals(body["versionId"],latest?null:Get(target,"VersionId").ToString()),"普通打开按装配引用请求精确版本，显式最新版仍请求最新："+mode);
    if(mode=="reference-denied")return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden){Content=new System.Net.Http.StringContent("{\"message\":\"当前用户没有该项目图档的对应操作权限。\"}")};
    if(mode=="reference-race")Set(target,"VersionId",Guid.NewGuid());
    if(latest){Set(root,"VersionId",Guid.NewGuid());Set(root,"Revision","W2");Set(root,"Sha256",new string('B',64));Set(requested,"RootVersionId",Get(root,"VersionId"));}
    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(json.Serialize(requested))};
   }))
   using(var http=new System.Net.Http.HttpClient(handler){BaseAddress=new Uri("http://unloaded-context.invalid/")})
   using(var lifetime=new System.Threading.CancellationTokenSource())
   {
    SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
    string ExistingHash(string name)=>File.Exists(Path.Combine(folder,name))?WorkingReuseHash(Path.Combine(folder,name)):null;
    var hashes=ownerFiles.Cast<object>().ToDictionary(file=>(string)Get(file,"FileName"),file=>ExistingHash((string)Get(file,"FileName")));
    if(referenceEntry)
    {
     using(var pane=(System.Windows.Forms.Control)New("PdmTaskPaneControl","引用版本打开回归"))
     {
      SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"taskPaneControl",pane);SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,output));
      SetField(plugin,"currentProjectId",project);
      object Node(object file,string kind,string path){var node=New("CadTreeNode");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"FullPath",path);Set(node,"DocumentId",Get(file,"DocumentId"));return node;}
      SetField(plugin,"currentTree",Node(parent,"Assembly",parentPath));
      var selection=new[]{Node(target,"Part",targetPath),(object)Guid.Empty,Guid.Empty};
      Check((bool)Type("PdmAddin").GetMethod("TryGetCurrentReferenceOpen",Flags).Invoke(plugin,selection)
          &&Equals(selection[1],project)&&Equals(selection[2],Get(root,"VersionId")),"设计树按项目和原路径选中装配清单的版本："+mode);
      var latest=mode=="explicit-latest-conflict";
      var task=(Task)TreeCall(plugin,"ExecuteControlledOpenCoreAsync",project,Get(root,"DocumentId"),
          Enum.Parse(Type("ControlledOpenMode"),latest?"LatestReadOnly":"CurrentReference"),latest?null:selection[2],null,null,false,
          latest?null:mode=="reference-other-path"?Path.Combine(folder,"other",(string)Get(target,"FileName")):targetPath);
      await task;
      var succeeds=mode=="reference-loaded"||mode=="reference-unloaded";
      Check((bool)Get(task,"Result")==succeeds&&ReferenceEquals(active,succeeds?childModel:parentModel)
          &&opens==(mode=="reference-unloaded"?1:0),"单开装配引用仅激活或只读打开原文件，版本、路径、权限冲突受阻："+mode+" "+Field(plugin,"controlledOpenFailureMessage"));
      if(succeeds)
      {
       SetField(plugin,"currentTree",selection[0]);
       Check((bool)Type("PdmAddin").GetMethod("TryGetCurrentReferenceOpen",Flags).Invoke(plugin,selection)
           &&Equals(selection[2],Get(root,"VersionId")),"零件成为活动窗口后再次打开仍使用同一装配引用");
      }
      if(latest)Check(((string)Field(plugin,"controlledOpenFailureMessage")).Contains("打开此图档"),"显式请求真实不同的最新版时提示怎样打开当前引用和最新版");
      Check(mutations==0&&hashes.All(pair=>ExistingHash(pair.Key)==pair.Value),"装配引用打开保留窗口及文件字节："+mode);
     }
     continue;
    }
    Exception failure=null;bool reused=false;
    try{var task=(Task)TreeCall(plugin,"TryOpenModelInLoadedContextAsync",requested,false);await task;reused=(bool)Get(task,"Result");}catch(InvalidOperationException e){failure=e;}catch(IOException e)when(mode=="missing-loaded-owner"){failure=e;}
    var expected=mode=="part"||mode=="assembly"||mode=="dirty-owner"||mode=="missing-inactive-owner";
    var noContext=mode=="no-loaded-owner"||mode=="different-project";
    Check(reused==expected&&(failure==null)==(expected||noContext)&&opens==(expected?1:0),"未加载子件复用与拒绝边界："+mode+" "+failure?.Message);
    if(mode=="missing-loaded-owner")Check(failure is IOException&&failure.Message.Contains("OWNER.SLDASM"),"当前已加载父装配缓存缺失时明确拒绝复用，不生成第二套副本");
    Check(mutations==0&&hashes.All(pair=>ExistingHash(pair.Key)==pair.Value),"父装配未保存修改及全部文件字节保持："+mode);
   }
  }
 }
}
