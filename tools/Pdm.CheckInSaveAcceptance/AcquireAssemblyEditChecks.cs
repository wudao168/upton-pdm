using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static readonly string[] IndexedOccurrenceCases={
  "indexed-occurrence-direct","indexed-occurrence-member-only","indexed-occurrence-wrong-configuration",
  "indexed-occurrence-duplicate","indexed-occurrence-wrong-loaded-path","indexed-occurrence-local-disk-edit",
  "indexed-occurrence-foreign-memory-edit","indexed-occurrence-missing-file"
 };

 static async Task AcquireAssemblyEditScenario(string modeFilter=null, NativeMissingAcquisition nativeMissing=null, bool compactWorkspace=false)
 {
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);var oldIsolated=isolated.GetValue(null);
  try
  {
   var cases=new[]{"delete","undo","delete-after-own-archive","undo-after-own-archive","part-receipt-rebase","source-copy-archive","root-version-conflict","child-version-conflict","new-drawing-no-receipt","foreign-child-copy","unknown-component","component-configuration-change","source-modified-after-receipt","receipt-other-path","receipt-other-user","receipt-other-server","race-after-checkout","undo-clean-flag","foreign-lease","root-disk-change","removed-membership","removed-membership-part-rebase-after-archive","reinsert-known","reinsert-source-copy-archive","reinsert-altered-file","reinsert-foreign-project","reinsert-old-version","reinsert-wrong-configuration","reinsert-root-race","archived-suppressed-reference","archived-suppressed-reference-other-path","archived-suppressed-reference-resolved","archived-managed-reference","archived-managed-reference-lightweight","archived-managed-reference-other-path","archived-managed-reference-extra-instance","archived-managed-reference-resolved","native-write-transient","native-write-revoked","native-write-blocked","native-write-reported-false",
    "previous-session-suppressed-reference","previous-session-suppressed-reference-other-path","previous-session-structure-changed","previous-session-foreign-owner"}.Concat(ReuseReferenceLoadFixture.Cases).Concat(IndexedOccurrenceCases).Concat(ColdAcquisition.Cases);
   var suppliedNativeFixture=nativeMissing;
   foreach(var mode in suppliedNativeFixture==null?cases.Concat(NativeMissingAcquisition.Cases):NativeMissingAcquisition.Cases)
   {
    if(modeFilter!=null&&!mode.StartsWith(modeFilter,StringComparison.Ordinal))continue;
    var directory=Path.Combine(Path.GetTempPath(),"pdm-acquire-assembly-edit-"+Guid.NewGuid().ToString("N"));
    var project=Guid.NewGuid();var plannedRoot=Guid.NewGuid();
    var folder=compactWorkspace?(string)TreeCallStatic("ManagedCadPaths","CompactDirectory",directory,"w",project+"|"+plannedRoot,"P700014-1"):Path.Combine(directory,"View","P700014-1");
    Directory.CreateDirectory(folder);
    nativeMissing=mode.StartsWith("stored-missing-",StringComparison.Ordinal)?suppliedNativeFixture??NativeMissingAcquisition.Synthetic(directory):null;
    bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));isolated.SetValue(null,true);
    var plugin=New("PdmAddin");var session=(Guid)Field(plugin,"checkoutSessionId");var serializer=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
    SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
    var root=LinkedNode(folder,nativeMissing?.OwnerFileName??"CW0030002.10.SLDASM","Assembly");var part=LinkedNode(folder,"7000093.101.02-01.SLDPRT","Part");var removed=LinkedNode(folder,"REMOVED.SLDPRT","Part");
    if(compactWorkspace)Set(root,"DocumentId",plannedRoot);
    nativeMissing?.PrepareWorkingFile((string)Get(root,"FullPath"));
    var managedDeferred=mode.StartsWith("archived-managed-reference",StringComparison.Ordinal);
    var previousSession=mode.StartsWith("previous-session-",StringComparison.Ordinal);
    var archivedMissing=previousSession||mode.StartsWith("archived-suppressed-reference",StringComparison.Ordinal);
    var retryUnlock=mode is "native-write-transient" or "previous-session-suppressed-reference";
    Set(root,"Configuration","Default");Set(part,"Configuration","Default");Set(removed,"Configuration","Default");
    ((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(removed);SetField(plugin,"currentTree",root);
    if(managedDeferred) ((IList)Get(root,"Children")).Add(part);
    var cold=mode.StartsWith("cold-occurrence-",StringComparison.Ordinal)?new ColdAcquisition(mode):null;
    var indexedOccurrence=cold!=null||mode.StartsWith("indexed-occurrence-",StringComparison.Ordinal);
    var nodes=new List<object>{root,part,removed};
    for(var i=0;i<212;i++)
    {
     var assembly=cold!=null&&i==1;
     var name=assembly?"70002727.M1.SLDASM":indexedOccurrence&&i==0?"1-BC1-三色指示灯001.SLDPRT":"UNTOUCHED-"+i+".SLDPRT";
     nodes.Add(LinkedNode(folder,name,assembly?"Assembly":"Part"));
    }
    if(indexedOccurrence)
    {
     Set(nodes[3],"Configuration","默认");
     if(cold==null&&mode!="indexed-occurrence-member-only")((IList)Get(root,"Children")).Add(nodes[3]);
     if(cold!=null)
     {
      ((IList)Get(root,"Children")).Add(nodes[4]);
      ((IList)Get(nodes[4],"Children")).Add(nodes[3]);
     }
    }
    var versions=new Dictionary<Guid,object>();var files=(IList)Activator.CreateInstance(Property(New("ControlledOpenManifestDto"),"Files").PropertyType);
    foreach(var node in nodes)
    {
     var id=(Guid)Get(node,"DocumentId");var path=(string)Get(node,"FullPath");var version=LinkedVersion(node,LinkedSnapshot(node));versions[id]=version;
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",Get(version,"Id"));Set(file,"FileName",Get(node,"FileName"));Set(file,"RelativePath",Get(node,"FileName"));Set(file,"Revision","W1");Set(file,"Configuration",Get(node,"Configuration"));Set(file,"IsRoot",ReferenceEquals(node,root));Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);files.Add(file);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,id,project,Get(version,"Id"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
     Set(node,"OpenedVersionId",Get(version,"Id"));File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    var rootId=(Guid)Get(root,"DocumentId");var partId=(Guid)Get(part,"DocumentId");var rootPath=(string)Get(root,"FullPath");var partPath=(string)Get(part,"FullPath");var removedPath=(string)Get(removed,"FullPath");
    if(nativeMissing!=null)Set(Get(versions[rootId],"ReferenceSnapshot"),"ReferenceWarnings",new List<string>{"存档引用提示："+nativeMissing.ChildPath+"；引用文件缺失，未勾选，本次不上传；原有关联保留，结构待补全"});
    if(cold!=null)
    {
     var snapshot=Get(versions[rootId],"ReferenceSnapshot");
     Set(snapshot,"StructureIndexPending",mode!="cold-occurrence-complete-index");
     Set(snapshot,"UsesNativeFileInventory",mode!="cold-occurrence-no-inventory-flag");
     Set(snapshot,"IncludesDesignDependencies",true);
     Set(snapshot,"NativeIsolationVerified",false);
     Set(snapshot,"ReferenceWarnings",new List<string>{
      "存档引用提示："+Path.Combine(directory,"旧目录",(string)Get(nodes[3],"FileName"))+"；引用文件缺失，未勾选，本次不上传；原有关联保留，结构待补全"
     });
     var subassembly=((IList)Get(snapshot,"Children")).Cast<object>().Single(child=>Equals(Get(child,"DocumentId"),Get(nodes[4],"DocumentId")));
     var nestedLamp=((IList)Get(subassembly,"Children"))[0];Set(nestedLamp,"IsSuppressed",true);
     if(mode!="cold-occurrence-no-root-inventory")
     {
      var nativeFile=LinkedSnapshot(nodes[3],false);
      Set(nativeFile,"Role",Convert.ToInt32(Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency")));
      if(mode=="cold-occurrence-inventory-id-conflict")Set(nativeFile,"DocumentId",Guid.NewGuid());
      ((IList)Get(snapshot,"Children")).Add(nativeFile);
     }
    }
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
    if(mode=="root-version-conflict"||mode=="reuse-load-version-conflict"){var changed=Guid.NewGuid();Set(latest,"RootVersionId",changed);Set(((IList)Get(latest,"Files"))[0],"VersionId",changed);}
    if(mode=="child-version-conflict")Set(((IList)Get(latest,"Files"))[1],"VersionId",Guid.NewGuid());
    if(mode=="root-disk-change"){File.SetAttributes(rootPath,FileAttributes.Normal);File.AppendAllText(rootPath," unproven saved root change");File.SetAttributes(rootPath,FileAttributes.ReadOnly);}
    if(mode=="receipt-other-user")SetField(plugin,"authenticatedUsername","other-user");
    var lampPath=indexedOccurrence?(string)Get(nodes[3],"FullPath"):null;
    if(mode is "indexed-occurrence-local-disk-edit" or "indexed-occurrence-missing-file")
    {
     File.SetAttributes(lampPath,FileAttributes.Normal);
     if(mode=="indexed-occurrence-missing-file")File.Delete(lampPath);
     else File.AppendAllText(lampPath," unarchived part edit");
    }
    Dictionary<string,Tuple<string,FileAttributes>> CaptureFiles()=>Directory.GetFiles(folder)
     .ToDictionary(path=>path,path=>Tuple.Create(WorkingReuseHash(path),File.GetAttributes(path)));
    var before=CaptureFiles();
    var initiallyDirty=mode!="undo-clean-flag"&&!indexedOccurrence;
    var dirty=initiallyDirty;var rootReadOnly=true;var unlocks=0;var nativeMutations=new List<string>();
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
    object PartModel(string path,bool unsavedEdit=false)=>new Proxy(modelType,call=>
    {
     switch(call.MethodName)
     {
      case "GetPathName":return path;case "GetSaveFlag":return unsavedEdit;case "IsOpenedReadOnly":return !unsavedEdit;
      case "GetConfigurationByName":return (string)call.Args[0]=="Default"?configuration:null;
      case "GetTitle":return Path.GetFileName(path);case "GetType":return 1;
      case "get_Extension":case "GetEquationMgr":case "GetDesignTable":return null;
      default:throw new Exception("非所选零件被操作："+call.MethodName);
     }
    }).GetTransparentProxy();
    var actualPartPath=partPath;if(mode=="foreign-child-copy"){actualPartPath=Path.Combine(directory,"foreign",Path.GetFileName(partPath));Directory.CreateDirectory(Path.GetDirectoryName(actualPartPath));File.Copy(partPath,actualPartPath);}
    var partModel=PartModel(actualPartPath);var removedModel=PartModel(removedPath);
    var nextId=0;
    object Component(string path,object model,string config="Default",int state=2,int? originalId=null,string instance=null)
    {
     var id=originalId??++nextId;
     return new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
    {
     switch(call.MethodName)
     {
      case "GetPathName":return path;case "get_Name2":return instance??Path.GetFileNameWithoutExtension(path)+"-"+id;case "GetID":return id;
      case "get_ReferencedConfiguration":return config;case "get_IsVirtual":return false;
      case "GetModelDoc2":return model;
      case "IsSuppressed":return state==0 || state==1 || state==4;
      case "GetSuppression":case "GetSuppression2":return state;
      case "GetChildren":return new object[0];
      default:throw new Exception(call.MethodName);
     }
    }).GetTransparentProxy();
    }
    var components=new List<object>{Component(partPath,partModel,mode=="component-configuration-change"?"Other":"Default")};
    object loadedLamp=null;
    if(indexedOccurrence)
    {
     components.Add(Component(removedPath,removedModel));
     var actualPath=lampPath;
     if(mode=="indexed-occurrence-wrong-loaded-path")
     {
      actualPath=Path.Combine(directory,"同名外部文件",Path.GetFileName(lampPath));
      Directory.CreateDirectory(Path.GetDirectoryName(actualPath));File.Copy(lampPath,actualPath);
     }
     if(mode!="indexed-occurrence-missing-file")loadedLamp=PartModel(actualPath,mode=="indexed-occurrence-foreign-memory-edit");
     components.Add(Component(lampPath,loadedLamp,mode=="indexed-occurrence-wrong-configuration"?"其他配置":"默认"));
     if(mode=="indexed-occurrence-duplicate")components.Add(Component(lampPath,loadedLamp,"默认"));
     if(cold!=null)components.Add(Component((string)Get(nodes[4],"FullPath"),null,state:0));
    }
    if(mode.StartsWith("undo"))components.Add(Component(removedPath,removedModel));
    if(mode.StartsWith("reinsert-"))components.Add(Component(removedPath,removedModel,mode=="reinsert-wrong-configuration"?"Other":"Default"));
    if(mode=="unknown-component")components.Add(Component(Path.Combine(folder,"UNREGISTERED.SLDPRT"),PartModel(Path.Combine(folder,"UNREGISTERED.SLDPRT"))));
    var oldReference=Path.Combine(directory,"旧目录","旧夹具.SLDASM");
    if(archivedMissing)
     components.Add(Component(oldReference,null,state:mode=="archived-suppressed-reference-resolved"?2:0));
    if(archivedMissing)
    {
     var historicalPath=mode.EndsWith("-other-path",StringComparison.Ordinal)?Path.Combine(directory,"其他目录",Path.GetFileName(oldReference)):oldReference;
     NativeMissingAcquisition.InstallReader(plugin,rootPath,new[]{new NativeMissingAcquisition.Row{Path=historicalPath,Configuration="Default",Instance=Path.GetFileNameWithoutExtension(oldReference)+"-"+nextId,Suppression=0}});
    }
    if(nativeMissing!=null)
    {
     nativeMissing.InstallSyntheticReader(plugin,rootPath);
     foreach(var row in nativeMissing.Current(mode,directory))
      components.Add(Component(row.Path,mode=="stored-missing-resolved"?PartModel(row.Path):null,row.Configuration,row.Suppression,instance:row.Instance));
    }
    if(previousSession&&mode!="previous-session-structure-changed")
     components.Add(Component(removedPath,removedModel));
    if(managedDeferred)
    {
     oldReference=Path.Combine(directory,"旧目录",Path.GetFileName(partPath));
     var state=mode=="archived-managed-reference-resolved"?2:mode=="archived-managed-reference-lightweight"?1:0;
     components.Clear();
     components.Add(Component(oldReference,null,state:state));
     components.Add(Component(oldReference,null,state:state));
     if(mode=="archived-managed-reference-extra-instance") components.Add(Component(oldReference,null,state:state));
     components.Add(Component(removedPath,removedModel));
    }
    object AssemblyModel()=>new AssemblyProxy(modelType,call=>{
     if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return modelType;
     switch(call.MethodName){case "GetPathName":return rootPath;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return rootReadOnly;case "GetType":return 2;case "get_ConfigurationManager":return manager;case "GetComponents":return components.ToArray();
     case "GetUpdateStamp":return cold?.ReadStamp()??0;case "GetBlockingState":return 0;
     case "GetTitle":return Path.GetFileName(rootPath);
     case "get_Extension":case "GetEquationMgr":case "GetDesignTable":return null;
     case "add_ModifyNotify":case "add_DestroyNotify2":case "add_FileReloadNotify":
     case "remove_ModifyNotify":case "remove_DestroyNotify2":case "remove_FileReloadNotify":return null;
     case "SetReadOnlyState":
      unlocks++;
      if(mode is "native-write-revoked" or "native-write-blocked" || retryUnlock&&unlocks==1)return false;
      rootReadOnly=(bool)call.Args[0];return mode!="native-write-reported-false";
     default:nativeMutations.Add(call.MethodName);throw new Exception("获取权限不能保存、关闭、重开或重建："+call.MethodName);}
    }).GetTransparentProxy();
    var assemblyModel=AssemblyModel();
    var reuseLoad=mode.StartsWith("reuse-load-",StringComparison.Ordinal)
     ? new ReuseReferenceLoadFixture(plugin,mode,assemblyModel,modelType,rootPath,nodes.Skip(1).Take(7).Select(node=>(string)Get(node,"FullPath")).ToArray()) : null;
    SetField(plugin,"application",new Proxy(appType,call=>
    {
     if(nativeMissing!=null&&nativeMissing.TryCaller(call,rootPath,out var nativeValue))return nativeValue;
     if(reuseLoad!=null&&reuseLoad.NativeCall(call,out var nativeResult))return nativeResult;
     if(cold!=null&&cold.NativeCall(call,assemblyModel,out var coldResult))return coldResult;
     if(call.MethodName=="GetDocuments")return new[]{assemblyModel,partModel,removedModel,loadedLamp}.Where(model=>model!=null).ToArray();
     if(call.MethodName=="GetRunningCommandInfo")return new ReturnMessage(null,new object[]{0,"",false},3,call.LogicalCallContext,call);
     if(call.MethodName=="GetDocumentDependencies2"&&(managedDeferred||archivedMissing))
     {
      if(call.Args.Skip(1).Cast<bool>().Any(flag=>flag)||string.Equals((string)call.Args[0],rootPath,StringComparison.OrdinalIgnoreCase))
       throw new Exception("只读取独立存档副本及其直接保存引用");
      var savedPath=mode.EndsWith("-other-path",StringComparison.Ordinal)?Path.Combine(directory,"其他目录",Path.GetFileName(oldReference)):oldReference;
      return new[]{Path.GetFileName(savedPath),savedPath};
     }
     throw new Exception("获取权限不能更改窗口："+call.MethodName);
    }).GetTransparentProxy());
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://assembly-preedit.invalid/"))
    using(var handler=new AssemblyEditAcquireHandler(latest,versions,rootId,project,session,mode,File.ReadAllBytes(rootPath)))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://assembly-preedit.invalid/")})
    {
     TreeCall(client,"SetActiveCompany",Guid.Empty);
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(directory,"journal"),"fixture"));SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|"+((string)Field(plugin,"authenticatedUsername")).ToLowerInvariant());
     var previousSessionId=Guid.NewGuid();
     if(previousSession)
     {
      var previousDraft=New("LocalEditDraft");
      Set(previousDraft,"DocumentId",rootId);Set(previousDraft,"ProjectId",project);
      Set(previousDraft,"SessionId",previousSessionId);Set(previousDraft,"SourcePath",rootPath);
      Set(previousDraft,"RestoreOnOpen",true);Set(previousDraft,"UnsavedChanges",true);
      TreeCall(Field(plugin,"editJournal"),"Save",previousDraft);
     }
     if(mode.StartsWith("removed-membership"))
     {
      await (Task)TreeCall(plugin,"RefreshCommittedWorkingMembershipsAsync",new[]{rootId});
      if(((IEnumerable)Get(((IDictionary)Field(plugin,"controlledOpenManifests"))[folder],"Files")).Cast<object>().Any(file=>Equals(Get(file,"DocumentId"),Get(removed,"DocumentId"))))
       throw new Exception("存档回执后未同步移除旧装配成员");
      if(!File.Exists(removedPath)||WorkingReuseHash(removedPath)!=before[removedPath].Item1)throw new Exception("同步清单删除了独立零件原文件");
     }
     Exception failure=null;var result=false;
     if(mode=="stored-missing-checkout-race")
      handler.OnCheckout=()=>
      {
       var replacement=nativeMissing.Current("stored-missing-renamed",directory).Last();
       components[components.Count-1]=Component(replacement.Path,null,replacement.Configuration,replacement.Suppression,instance:replacement.Instance);
      };
     if(cold!=null)
     {
      if(mode=="cold-occurrence-dirty-at-open")dirty=true;
      cold.Open(plugin,assemblyModel,rootPath,manifest);
      if(mode=="cold-occurrence-dirty-at-open")dirty=false;
      if(mode=="cold-occurrence-reference-race")handler.OnCheckout=()=>components.Add(Component(lampPath,loadedLamp,"默认"));
      if(mode=="cold-occurrence-extra-instance")components.Add(Component(lampPath,loadedLamp,"默认"));
      if(mode=="cold-occurrence-configuration-change")components[2]=Component(lampPath,loadedLamp,"其他配置",originalId:3);
      if(mode=="cold-occurrence-different-object")assemblyModel=AssemblyModel();
      if(mode=="cold-occurrence-replaced-instance")components[2]=Component(lampPath,loadedLamp,"默认");
      if(mode is "cold-occurrence-wrong-loaded-path" or "cold-occurrence-outside-manifest")
      {
       var foreignPath=Path.Combine(directory,"外部来源",mode=="cold-occurrence-wrong-loaded-path"?Path.GetFileName(lampPath):"清单外.SLDPRT");
       Directory.CreateDirectory(Path.GetDirectoryName(foreignPath));File.Copy(lampPath,foreignPath);
       loadedLamp=PartModel(foreignPath);
       components[2]=Component(mode=="cold-occurrence-wrong-loaded-path"?lampPath:foreignPath,loadedLamp,"默认",originalId:3);
      }
      if(mode=="cold-occurrence-foreign-memory-edit")
      {
       loadedLamp=PartModel(lampPath,true);
       components[2]=Component(lampPath,loadedLamp,"默认",originalId:3);
      }
      if(mode is "cold-occurrence-local-disk-edit" or "cold-occurrence-missing-file")
      {
       File.SetAttributes(lampPath,FileAttributes.Normal);
       if(mode=="cold-occurrence-missing-file")
       {
        File.Delete(lampPath);loadedLamp=null;
        components[2]=Component(lampPath,null,"默认",state:0,originalId:3);
       }
       else File.AppendAllText(lampPath," unsent edit after read-only open");
       before=CaptureFiles();
      }
     }
     try
     {
      if(mode=="reuse-load-open-latest"||mode=="cold-occurrence-open-latest-edit")
      {
       Set(latest,"UsesLatestModelReferences",true);
       var open=(Task<(bool Outdated,bool Reused)>)TreeCall(plugin,"CheckLoadedModelForOpenAsync",latest,true,true);
       var checkedModel=await open;
       result=!checkedModel.Outdated&&checkedModel.Reused;
      }
      else
      {
       var task=(Task)TreeCall(plugin,"TryAcquireReusableWorkingCopyAsync",LinkedItems(root),project,rootId);
       await task;result=(bool)Get(task,"Result");
      }
     }catch(Exception error){failure=error;}
     var ignoredDrawing=new[]{"new-drawing-no-receipt","source-modified-after-receipt","receipt-other-path","receipt-other-server","receipt-other-user"}.Contains(mode);
     var blocked=new[]{"root-version-conflict","child-version-conflict","foreign-child-copy","unknown-component","component-configuration-change","race-after-checkout","foreign-lease","root-disk-change","reinsert-altered-file","reinsert-foreign-project","reinsert-old-version","reinsert-wrong-configuration","reinsert-root-race","archived-suppressed-reference-other-path","archived-suppressed-reference-resolved","archived-managed-reference-other-path","archived-managed-reference-extra-instance","archived-managed-reference-resolved"}.Contains(mode);
     if(nativeMissing!=null)blocked=mode!="stored-missing-unchanged-pair";
     var nativeWriteFailure=mode is "native-write-revoked" or "native-write-blocked";
     var foreignCheckout=mode=="previous-session-foreign-owner";
     blocked|=nativeWriteFailure||previousSession&&!retryUnlock||mode=="reuse-load-version-conflict";
     blocked|=indexedOccurrence&&mode!="indexed-occurrence-direct"&&mode!="cold-occurrence-verified"&&mode!="cold-occurrence-open-latest-edit";
     var acquisitionSucceeded=failure==null&&result;
     if(acquisitionSucceeded==blocked||!indexedOccurrence&&blocked&&failure==null)
       throw new Exception("装配体获取结果不符："+mode,failure);
     reuseLoad?.Verify();
     if(!blocked&&(handler.Checkouts!=1||unlocks!=(retryUnlock?2:1)||rootReadOnly||dirty!=initiallyDirty))throw new Exception("未原位获取或丢掉未保存修改："+mode);
     if(blocked&&!nativeWriteFailure&&(unlocks!=0||handler.Checkouts!=(mode=="race-after-checkout"||mode=="reinsert-root-race"||mode=="cold-occurrence-reference-race"||mode=="stored-missing-checkout-race"||foreignCheckout?1:0)))throw new Exception("冲突校验前已改变权限："+mode);
     if(previousSession&&handler.NativeLeaseReads!=(retryUnlock?1:0))
       throw new Exception("旧会话接续后的写入确认次数不符："+mode);
     if(previousSession&&!((IEnumerable)TreeCall(Field(plugin,"editJournal"),"ReadAll")).Cast<object>()
        .Any(item=>Equals(Get(item,"SessionId"),previousSessionId)&&(bool)Get(item,"RestoreOnOpen")&&!(bool)Get(item,"Closed")))
       throw new Exception("重新获取权限影响了旧会话的草稿记录："+mode);
     if(mode.StartsWith("native-write-",StringComparison.Ordinal)&&(handler.NativeLeaseReads!=(mode=="native-write-reported-false"?0:1)
         || nativeWriteFailure&&(handler.Checkouts!=1||unlocks!=(mode=="native-write-revoked"?1:2)||!rootReadOnly||!dirty)))
       throw new Exception("原生失败后的重试次数、会话和内存修改需要核对："+mode);
     if(blocked&&mode=="child-version-conflict"&&!failure.Message.Contains("7000093.101.02-01.SLDPRT"))throw new Exception("冲突提示未指出实际文件");
     if((mode=="race-after-checkout"||mode=="cold-occurrence-reference-race"||mode=="stored-missing-checkout-race")&&((IDictionary)Field(plugin,"localEdits"))[rootId]==null)throw new Exception("获取后并发冲突丢掉恢复记录");
     foreach(var pair in before){if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||pair.Key!=rootPath&&File.GetAttributes(pair.Key)!=pair.Value.Item2)throw new Exception("获取覆盖文件或解锁无关零件："+mode);}
     if(nativeMutations.Count!=0||handler.OtherWrites!=0)throw new Exception("获取执行了保存、开关窗口或升版："+mode);
     if(nativeMissing!=null)nativeMissing.Verify(mode,handler.ArchivedDownloads,handler.Checkouts);
     if(indexedOccurrence)
     {
      TreeCheck(rootReadOnly==blocked&&dirty==initiallyDirty,"获取结果与窗口可写状态或内存修改标记不符："+mode);
      if(mode.EndsWith("-missing-file",StringComparison.Ordinal))TreeCheck(!File.Exists(lampPath),"获权流程创建了缺失的零件："+mode);
      Console.WriteLine("PASS: "+mode+"; checkouts="+handler.Checkouts+", native unlocks="+unlocks+", no save, replacement or version write");
      continue;
     }
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
  public int Checkouts,OtherWrites,NativeLeaseReads,ArchivedDownloads;
  public Action OnCheckout;
  readonly byte[] archivedBytes;
  public AssemblyEditAcquireHandler(object manifest,Dictionary<Guid,object> versions,Guid rootId,Guid project,Guid session,string mode,byte[] archivedBytes){this.manifest=manifest;this.versions=versions;this.rootId=rootId;this.project=project;this.session=session;this.mode=mode;this.archivedBytes=archivedBytes;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   var previousSession=mode.StartsWith("previous-session-",StringComparison.Ordinal);
   if(mode.StartsWith("stored-missing-",StringComparison.Ordinal)&&path.EndsWith("/file"))
   {
    var versionId=(Guid)Get(versions[rootId],"Id");
    if(request.Method!=HttpMethod.Get||path!="/api/documents/"+rootId+"/versions/"+versionId+"/file")
     throw new Exception("读取了错误文档或错误版本的原件："+path);
    ArchivedDownloads++;
    if(mode=="stored-missing-unavailable")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    var bytes=mode=="stored-missing-corrupt"?new byte[]{1,2,3}:archivedBytes;
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
   }
   if(path.EndsWith("/file")&&(mode.StartsWith("archived-suppressed-reference",StringComparison.Ordinal)
    ||mode.StartsWith("archived-managed-reference",StringComparison.Ordinal)||previousSession))
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(archivedBytes)});
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/open-manifest")){manifestReads++;value=manifest;if((mode=="race-after-checkout"||mode=="reinsert-root-race")&&manifestReads>1){value=json.Deserialize(json.Serialize(manifest),manifest.GetType());Set(value,"RootVersionId",Guid.NewGuid());}}
   else if(path.EndsWith("/documents"))value=versions.Select(pair=>new{id=pair.Key,projectId=mode=="reinsert-foreign-project"&&(string)Get(Get(pair.Value,"ReferenceSnapshot"),"FileName")=="REMOVED.SLDPRT"?Guid.NewGuid():project,
    fileName=Get(Get(pair.Value,"ReferenceSnapshot"),"FileName"),kind=Get(Get(pair.Value,"ReferenceSnapshot"),"Kind"),
    checkedOutBy=pair.Key!=rootId?null:mode is "foreign-lease" or "previous-session-foreign-owner"?"another-user":previousSession?"tester":null,
    checkoutSessionId=previousSession&&pair.Key==rootId?(Guid?)Guid.NewGuid():null}).ToArray();
   else if(path.EndsWith("/versions")){var id=Guid.Parse(path.Split('/')[3]);value=new[]{versions[id]};}
   else if(path.EndsWith("/document-relations"))value=new object[0];
   else if(path.EndsWith("/checkout")){
    Checkouts++;OnCheckout?.Invoke();
    if(mode=="previous-session-foreign-owner")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
      {Content=new StringContent("{\"message\":\"图档正在由其他用户编辑\"}")});
    value=new{id=rootId,projectId=project,checkedOutBy=mode=="receipt-other-user"?"other-user":"tester",checkoutSessionId=(Guid?)session,checkedOutAt=new DateTime(2026,10,24,13,30,0,DateTimeKind.Utc)};
   }
   else if(path.EndsWith("/"+rootId)&&request.Method==HttpMethod.Get&&(mode.StartsWith("native-write-",StringComparison.Ordinal)||previousSession))
   {NativeLeaseReads++;value=new{id=rootId,projectId=project,checkedOutBy="tester",checkoutSessionId=(Guid?)(mode=="native-write-revoked"?Guid.NewGuid():session),checkedOutAt=new DateTime(2026,10,24,13,30,0,DateTimeKind.Utc)};}
   else{OtherWrites++;throw new Exception("不允许的业务写入 "+request.Method+" "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(value))});
  }
 }
}
