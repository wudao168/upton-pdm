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
 static async Task AcquireAssemblyEditScenario(string modeFilter=null)
 {
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);var oldIsolated=isolated.GetValue(null);
  try
  {
   foreach(var mode in new[]{"delete","undo","delete-after-own-archive","undo-after-own-archive","part-receipt-rebase","source-copy-archive","root-version-conflict","child-version-conflict","new-drawing-no-receipt","foreign-child-copy","unknown-component","component-configuration-change","source-modified-after-receipt","receipt-other-path","receipt-other-user","receipt-other-server","race-after-checkout","undo-clean-flag","foreign-lease","root-disk-change","removed-membership","removed-membership-part-rebase-after-archive","reinsert-known","reinsert-source-copy-archive","reinsert-altered-file","reinsert-foreign-project","reinsert-old-version","reinsert-wrong-configuration","reinsert-root-race"})
   {
    if(modeFilter!=null&&!mode.StartsWith(modeFilter,StringComparison.Ordinal))continue;
    var directory=Path.Combine(Path.GetTempPath(),"pdm-acquire-assembly-edit-"+Guid.NewGuid().ToString("N"));var folder=Path.Combine(directory,"View","P700014-1");Directory.CreateDirectory(folder);
    bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));isolated.SetValue(null,true);
    var project=Guid.NewGuid();var plugin=New("PdmAddin");var session=(Guid)Field(plugin,"checkoutSessionId");var serializer=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
    SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
    var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var part=LinkedNode(folder,"7000093.101.02-01.SLDPRT","Part");var removed=LinkedNode(folder,"REMOVED.SLDPRT","Part");
    Set(root,"Configuration","Default");Set(part,"Configuration","Default");Set(removed,"Configuration","Default");
    ((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(removed);SetField(plugin,"currentTree",root);
    var nodes=new List<object>{root,part,removed};for(var i=0;i<212;i++)nodes.Add(LinkedNode(folder,"UNTOUCHED-"+i+".SLDPRT","Part"));
    var versions=new Dictionary<Guid,object>();var files=(IList)Activator.CreateInstance(Property(New("ControlledOpenManifestDto"),"Files").PropertyType);
    foreach(var node in nodes)
    {
     var id=(Guid)Get(node,"DocumentId");var path=(string)Get(node,"FullPath");var version=LinkedVersion(node,LinkedSnapshot(node));versions[id]=version;
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",Get(version,"Id"));Set(file,"FileName",Get(node,"FileName"));Set(file,"RelativePath",Get(node,"FileName"));Set(file,"Revision","W1");Set(file,"Configuration","Default");Set(file,"IsRoot",ReferenceEquals(node,root));Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);files.Add(file);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,id,project,Get(version,"Id"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
     Set(node,"OpenedVersionId",Get(version,"Id"));File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    var rootId=(Guid)Get(root,"DocumentId");var partId=(Guid)Get(part,"DocumentId");var rootPath=(string)Get(root,"FullPath");var partPath=(string)Get(part,"FullPath");var removedPath=(string)Get(removed,"FullPath");
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","P700014-1");Set(manifest,"RootDocumentId",rootId);Set(manifest,"RootVersionId",Get(versions[rootId],"Id"));Set(manifest,"RootRelativePath",Get(root,"FileName"));Set(manifest,"ForEdit",false);Set(manifest,"Files",files);
    ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,manifest);
    var includeArchive=mode.Contains("archive")||mode.Contains("receipt")||mode=="new-drawing-no-receipt";var latest=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());Set(latest,"ForEdit",true);
    if(includeArchive)
    using(var receiptClient=(IDisposable)New("PdmApiClient",mode=="receipt-other-server"?"http://other-server.invalid/":"http://assembly-preedit.invalid/"))
    {
     TreeCall(receiptClient,"SetActiveCompany",Guid.Empty);
     SetField(plugin,"apiClient",receiptClient);
     var oldPart=serializer.Serialize(files[1]);File.SetAttributes(partPath,FileAttributes.Normal);File.AppendAllText(partPath," actual owned model edit, already archived");
     var next=LinkedVersion(part,LinkedSnapshot(part));Set(next,"Id",Guid.NewGuid());Set(next,"Revision",LinkedRevision("W2"));versions[partId]=next;
     var warning=(string)TreeCall(plugin,"RememberControlledVersionIdentityAndManifest",partPath,partId,project,next);if(warning!="")throw new Exception("无法记录本人存档："+warning);
     var drawing=LinkedNode(folder,"7000093.101.02-01.SLDDRW","Drawing");var drawingPath=(string)Get(drawing,"FullPath");var drawingId=(Guid)Get(drawing,"DocumentId");var drawingVersion=LinkedVersion(drawing,LinkedSnapshot(drawing));versions[drawingId]=drawingVersion;
     if(mode=="source-copy-archive"||mode=="reinsert-source-copy-archive")
     {Set(drawingVersion,"PropertySnapshot",new Dictionary<string,string>{{"SourceFileSha256",WorkingReuseHash(drawingPath)}});Set(drawingVersion,"Sha256",new string('B',64));Set(drawingVersion,"FileLength",1234L);}
     var receiptPath=drawingPath;
     if(mode=="receipt-other-path"){receiptPath=Path.Combine(directory,"other",Path.GetFileName(drawingPath));Directory.CreateDirectory(Path.GetDirectoryName(receiptPath));File.Copy(drawingPath,receiptPath);}
     if(mode!="new-drawing-no-receipt")
     {warning=(string)TreeCall(plugin,"RememberControlledVersionIdentityAndManifest",receiptPath,drawingId,project,drawingVersion);if(warning!="")throw new Exception(warning);}
     if(mode=="source-modified-after-receipt")File.AppendAllText(drawingPath," later unarchived change");
     latest=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());Set(latest,"ForEdit",true);
     var drawingFile=New("ControlledOpenFileDto");Set(drawingFile,"DocumentId",drawingId);Set(drawingFile,"VersionId",Get(drawingVersion,"Id"));Set(drawingFile,"FileName",Get(drawing,"FileName"));Set(drawingFile,"RelativePath",Get(drawing,"FileName"));Set(drawingFile,"Configuration","图纸");Set(drawingFile,"Sha256",Get(drawingVersion,"Sha256"));Set(drawingFile,"FileLength",Get(drawingVersion,"FileLength"));((IList)Get(latest,"Files")).Add(drawingFile);
     if(mode=="part-receipt-rebase"||mode=="removed-membership-part-rebase-after-archive")files[1]=serializer.Deserialize(oldPart,Type("ControlledOpenFileDto"));
     if(mode=="reinsert-source-copy-archive")
     {
      var sourceDrawing=serializer.Deserialize(serializer.Serialize(drawingFile),drawingFile.GetType());
      Set(sourceDrawing,"Sha256",WorkingReuseHash(drawingPath));Set(sourceDrawing,"FileLength",new FileInfo(drawingPath).Length);files.Add(sourceDrawing);
     }
     File.SetAttributes(partPath,FileAttributes.ReadOnly);File.SetAttributes(drawingPath,FileAttributes.ReadOnly);
    }
    var membership=mode.StartsWith("removed-membership")||mode.StartsWith("reinsert-");
    if(membership)
    {
     var saved=serializer.Deserialize(serializer.Serialize(Get(versions[rootId],"ReferenceSnapshot")),Type("DocumentReferenceNodeDto"));
     var children=(IList)Get(saved,"Children");foreach(var child in children.Cast<object>().Where(child=>Equals(Get(child,"DocumentId"),Get(removed,"DocumentId"))).ToArray())children.Remove(child);
     File.SetAttributes(rootPath,FileAttributes.Normal);File.AppendAllText(rootPath," archived deletion of TZ component");
     var next=LinkedVersion(root,saved);versions[rootId]=next;
     Set(manifest,"RootVersionId",Get(next,"Id"));Set(files[0],"VersionId",Get(next,"Id"));Set(files[0],"Sha256",Get(next,"Sha256"));Set(files[0],"FileLength",Get(next,"FileLength"));
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)rootPath,rootId,project,Get(next,"Id"),"W2",Get(next,"Sha256"),Get(next,"FileLength")});File.SetAttributes(rootPath,FileAttributes.ReadOnly);
     var previousLatest=latest;
     latest=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());Set(latest,"ForEdit",true);var latestFiles=(IList)Get(latest,"Files");
     foreach(var serverFile in ((IEnumerable)Get(previousLatest,"Files")).Cast<object>().Where(file=>!Equals(Get(file,"DocumentId"),rootId)))
     {
      var existing=latestFiles.Cast<object>().FirstOrDefault(file=>Equals(Get(file,"DocumentId"),Get(serverFile,"DocumentId")));
      if(existing!=null)latestFiles[latestFiles.IndexOf(existing)]=serverFile;else latestFiles.Add(serverFile);
     }
     foreach(var file in latestFiles.Cast<object>().Where(file=>Equals(Get(file,"DocumentId"),Get(removed,"DocumentId"))).ToArray())latestFiles.Remove(file);
     ((IList)Get(root,"Children")).Remove(removed);
     if(mode=="reinsert-altered-file"){File.SetAttributes(removedPath,FileAttributes.Normal);File.AppendAllText(removedPath," unarchived edit");File.SetAttributes(removedPath,FileAttributes.ReadOnly);}
     if(mode=="reinsert-old-version")Set(versions[(Guid)Get(removed,"DocumentId")],"Id",Guid.NewGuid());
    }
    if(mode=="root-version-conflict"){var changed=Guid.NewGuid();Set(latest,"RootVersionId",changed);Set(((IList)Get(latest,"Files"))[0],"VersionId",changed);}
    if(mode=="child-version-conflict")Set(((IList)Get(latest,"Files"))[1],"VersionId",Guid.NewGuid());
    if(mode=="root-disk-change"){File.SetAttributes(rootPath,FileAttributes.Normal);File.AppendAllText(rootPath," unproven saved root change");File.SetAttributes(rootPath,FileAttributes.ReadOnly);}
    if(mode=="receipt-other-user")SetField(plugin,"authenticatedUsername","other-user");
    var before=Directory.GetFiles(folder).ToDictionary(path=>path,path=>Tuple.Create(WorkingReuseHash(path),File.GetAttributes(path)));
    var dirty=mode!="undo-clean-flag";var rootReadOnly=true;var unlocks=0;var nativeMutations=new List<string>();
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
    object PartModel(string path)=>new Proxy(modelType,call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?false:call.MethodName=="IsOpenedReadOnly"?true:call.MethodName=="GetConfigurationByName"?((string)call.Args[0]=="Default"?configuration:null):throw new Exception("非所选零件被操作："+call.MethodName)).GetTransparentProxy();
    var actualPartPath=partPath;if(mode=="foreign-child-copy"){actualPartPath=Path.Combine(directory,"foreign",Path.GetFileName(partPath));Directory.CreateDirectory(Path.GetDirectoryName(actualPartPath));File.Copy(partPath,actualPartPath);}
    var partModel=PartModel(actualPartPath);var removedModel=PartModel(removedPath);
    object Component(string path,object model,string config="Default")=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="get_ReferencedConfiguration"?config:call.MethodName=="get_IsVirtual"?false:call.MethodName=="GetModelDoc2"?model:throw new Exception(call.MethodName)).GetTransparentProxy();
    var components=new List<object>{Component(partPath,partModel,mode=="component-configuration-change"?"Other":"Default")};
    if(mode.StartsWith("undo"))components.Add(Component(removedPath,removedModel));
    if(mode.StartsWith("reinsert-"))components.Add(Component(removedPath,removedModel,mode=="reinsert-wrong-configuration"?"Other":"Default"));
    if(mode=="unknown-component")components.Add(Component(Path.Combine(folder,"UNREGISTERED.SLDPRT"),PartModel(Path.Combine(folder,"UNREGISTERED.SLDPRT"))));
    var assemblyModel=new AssemblyProxy(modelType,call=>{
     if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return modelType;
     switch(call.MethodName){case "GetPathName":return rootPath;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return rootReadOnly;case "GetType":return 2;case "get_ConfigurationManager":return manager;case "GetComponents":return components.ToArray();case "SetReadOnlyState":unlocks++;rootReadOnly=(bool)call.Args[0];return true;default:nativeMutations.Add(call.MethodName);throw new Exception("获取权限不能保存、关闭、重开或重建："+call.MethodName);}
    }).GetTransparentProxy();
    SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{assemblyModel,partModel,removedModel}:throw new Exception("获取权限不能更改窗口："+call.MethodName)).GetTransparentProxy());
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://assembly-preedit.invalid/"))
    using(var handler=new AssemblyEditAcquireHandler(latest,versions,rootId,project,session,mode))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://assembly-preedit.invalid/")})
    {
     TreeCall(client,"SetActiveCompany",Guid.Empty);
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(directory,"journal"),"fixture"));SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|"+((string)Field(plugin,"authenticatedUsername")).ToLowerInvariant());
     if(mode.StartsWith("removed-membership"))
     {
      await (Task)TreeCall(plugin,"RefreshCommittedWorkingMembershipsAsync",new[]{rootId});
      if(((IEnumerable)Get(((IDictionary)Field(plugin,"controlledOpenManifests"))[folder],"Files")).Cast<object>().Any(file=>Equals(Get(file,"DocumentId"),Get(removed,"DocumentId"))))
       throw new Exception("存档回执后未同步移除旧装配成员");
      if(!File.Exists(removedPath)||WorkingReuseHash(removedPath)!=before[removedPath].Item1)throw new Exception("同步清单删除了独立零件原文件");
     }
     Exception failure=null;var result=false;
     try{var task=(Task)TreeCall(plugin,"TryAcquireReusableWorkingCopyAsync",LinkedItems(root),project,rootId);await task;result=(bool)Get(task,"Result");}catch(Exception error){failure=error;}
     var ignoredDrawing=new[]{"new-drawing-no-receipt","source-modified-after-receipt","receipt-other-path","receipt-other-server","receipt-other-user"}.Contains(mode);
     var blocked=new[]{"root-version-conflict","child-version-conflict","foreign-child-copy","unknown-component","component-configuration-change","race-after-checkout","foreign-lease","root-disk-change","reinsert-altered-file","reinsert-foreign-project","reinsert-old-version","reinsert-wrong-configuration","reinsert-root-race"}.Contains(mode);
     if((failure!=null)!=blocked||!blocked&&!result)throw new Exception("装配体获取结果不符："+mode,failure);
     if(!blocked&&(handler.Checkouts!=1||unlocks!=1||rootReadOnly||dirty!=(mode!="undo-clean-flag")))throw new Exception("未原位获取或丢掉未保存修改："+mode);
     if(blocked&&(unlocks!=0||handler.Checkouts!=(mode=="race-after-checkout"||mode=="reinsert-root-race"?1:0)))throw new Exception("冲突校验前已改变权限："+mode);
     if(blocked&&mode=="child-version-conflict"&&!failure.Message.Contains("7000093.101.02-01.SLDPRT"))throw new Exception("冲突提示未指出实际文件");
     if(mode=="race-after-checkout"&&((IDictionary)Field(plugin,"localEdits"))[rootId]==null)throw new Exception("获取后并发冲突丢掉恢复记录");
     foreach(var pair in before){if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||pair.Key!=rootPath&&File.GetAttributes(pair.Key)!=pair.Value.Item2)throw new Exception("获取覆盖文件或解锁无关零件："+mode);}
     if(nativeMutations.Count!=0||handler.OtherWrites!=0)throw new Exception("获取执行了保存、开关窗口或升版："+mode);
     if(!blocked&&includeArchive&&((IList)Get(((IDictionary)Field(plugin,"controlledOpenManifests"))[folder],"Files")).Count!=((ignoredDrawing?215:216)-(mode.StartsWith("removed-membership")?1:0)))throw new Exception("工作清单误纳入无关工程图或遗漏本人确认文件："+mode);
     if(!blocked&&membership)
     {
      var kept=((IEnumerable)Get(((IDictionary)Field(plugin,"controlledOpenManifests"))[folder],"Files")).Cast<object>().Any(file=>Equals(Get(file,"DocumentId"),Get(removed,"DocumentId")));
      if(kept!=mode.StartsWith("reinsert-"))throw new Exception("旧成员未移除或已核对的新装配成员丢失："+mode);
     }
     Console.WriteLine("PASS: assembly pre-permission edit "+mode+"; 215-file baseline, deletion/undo retained, no save/open/close/version write; conflicts checked before unlock");
    }
   }
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);isolated.SetValue(null,oldIsolated);}
 }

 sealed class AssemblyEditAcquireHandler:HttpMessageHandler
 {
  readonly object manifest;readonly Dictionary<Guid,object> versions;readonly Guid rootId,project,session;readonly string mode;readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};int manifestReads;
  public int Checkouts,OtherWrites;
  public AssemblyEditAcquireHandler(object manifest,Dictionary<Guid,object> versions,Guid rootId,Guid project,Guid session,string mode){this.manifest=manifest;this.versions=versions;this.rootId=rootId;this.project=project;this.session=session;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/open-manifest")){manifestReads++;value=manifest;if((mode=="race-after-checkout"||mode=="reinsert-root-race")&&manifestReads>1){value=json.Deserialize(json.Serialize(manifest),manifest.GetType());Set(value,"RootVersionId",Guid.NewGuid());}}
   else if(path.EndsWith("/documents"))value=versions.Select(pair=>new{id=pair.Key,projectId=mode=="reinsert-foreign-project"&&(string)Get(Get(pair.Value,"ReferenceSnapshot"),"FileName")=="REMOVED.SLDPRT"?Guid.NewGuid():project,
    fileName=Get(Get(pair.Value,"ReferenceSnapshot"),"FileName"),kind=Get(Get(pair.Value,"ReferenceSnapshot"),"Kind"),checkedOutBy=mode=="foreign-lease"&&pair.Key==rootId?"another-user":null,checkoutSessionId=(Guid?)null}).ToArray();
   else if(path.EndsWith("/versions")){var id=Guid.Parse(path.Split('/')[3]);value=new[]{versions[id]};}
   else if(path.EndsWith("/document-relations"))value=new object[0];
   else if(path.EndsWith("/checkout")){Checkouts++;value=new{id=rootId,projectId=project,checkedOutBy=mode=="receipt-other-user"?"other-user":"tester",checkoutSessionId=(Guid?)session,checkedOutAt=new DateTime(2026,10,24,13,30,0,DateTimeKind.Utc)};}
   else{OtherWrites++;throw new Exception("不允许的业务写入 "+request.Method+" "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(value))});
  }
 }
}
