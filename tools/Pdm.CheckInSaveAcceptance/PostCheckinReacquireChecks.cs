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

internal static partial class Program
{
 static async Task PostCheckinReacquireScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  object Clone(object value)=>json.Deserialize(json.Serialize(value),value.GetType());
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  try
  {
   foreach(var mode in new[]{"selected","no-session-receipt","unselected-no-receipt","new-head","new-dependency","configuration","path","binding","permission-denied","head-binding-race","dependency-binding-race","checkout-race","drawing-memory-edit","frozen-child-old","frozen-child-race"})
   {
    var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);
    var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
    var owner=New("ControlledOpenManifestDto");Set(owner,"Id",Guid.NewGuid());Set(owner,"ProjectId",project);Set(owner,"ProjectCode","QA-REACQUIRE");Set(owner,"RootRevision","W1");
    var files=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
    void Bind(object file)=>identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)Path.Combine(folder,(string)Get(file,"FileName")),Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")});
    for(var i=0;i<43;i++)
    {
     var name=i==0?"OWNER.SLDASM":i==1?"PART.SLDPRT":i==2?"PART.SLDDRW":"OTHER-"+i+".SLDPRT";var path=Path.Combine(folder,name);File.WriteAllText(path,"old controlled source "+name);
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Configuration","Default");Set(file,"Revision","W1");Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"IsRoot",i==0);files.Add(file);Bind(file);File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    Set(owner,"Files",files);Set(owner,"RootDocumentId",Get(files[0],"DocumentId"));Set(owner,"RootVersionId",Get(files[0],"VersionId"));Set(owner,"RootRelativePath",Get(files[0],"FileName"));
    var parent=Clone(owner);Set(parent,"ForEdit",true);var frozen=((IList)Get(parent,"Files")).Cast<object>().ToArray();
    var rootPath=Path.Combine(folder,"OWNER.SLDASM");var partPath=Path.Combine(folder,"PART.SLDPRT");var drawingPath=Path.Combine(folder,"PART.SLDDRW");
    File.SetAttributes(partPath,FileAttributes.Normal);File.WriteAllText(partPath,"new successfully checked-in ordinary part");Set(files[1],"VersionId",Guid.NewGuid());Set(files[1],"Revision","A-W1");Set(files[1],"Sha256",WorkingReuseHash(partPath));Set(files[1],"FileLength",new FileInfo(partPath).Length);Bind(files[1]);File.SetAttributes(partPath,FileAttributes.ReadOnly);
    var partHead=Clone(files[1]);Set(partHead,"IsRoot",true);
    if(mode.StartsWith("frozen-child-"))
    {
     File.SetAttributes(partPath,FileAttributes.Normal);File.WriteAllText(partPath,"old controlled source PART.SLDPRT");
     files[1]=Clone(frozen[1]);Bind(files[1]);File.SetAttributes(partPath,FileAttributes.ReadOnly);
    }
    var events=new List<string>();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var partNative=new Model(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),partPath,false,events);var partModel=partNative.GetTransparentProxy();
    var drawingNative=new Model(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),drawingPath,true,events,false);var drawingModel=drawingNative.GetTransparentProxy();
    using(var memory=(IDisposable)New("PdmAddin+ArchiveMemoryCopy",drawingModel,drawingPath))
    {
     var copyPath=Path.Combine(folder,"submitted-copy","PART.SLDDRW");Directory.CreateDirectory(Path.GetDirectoryName(copyPath));File.WriteAllText(copyPath,"new submitted drawing memory");
     TreeCall(memory,"ReplaceCopy",copyPath,WorkingReuseHash(drawingPath),new FileInfo(drawingPath).Length,WorkingReuseHash(copyPath),1,drawingNative.UpdateStamp,0);
     var drawingVersion=New("DocumentVersionDto");Set(drawingVersion,"Id",Guid.NewGuid());Set(drawingVersion,"DocumentId",Get(files[2],"DocumentId"));Set(drawingVersion,"Sha256",WorkingReuseHash(copyPath));Set(drawingVersion,"FileLength",new FileInfo(copyPath).Length);
     SetField(memory,"CommittedVersion",drawingVersion);SetField(memory,"CommittedDocumentId",Get(files[2],"DocumentId"));SetField(memory,"CommittedUser","tester");
     Set(frozen[2],"VersionId",Get(drawingVersion,"Id"));Set(frozen[2],"Revision","A-W1");Set(frozen[2],"Sha256",Get(drawingVersion,"Sha256"));Set(frozen[2],"FileLength",Get(drawingVersion,"FileLength"));
     var noReceipt=mode=="no-session-receipt"||mode=="unselected-no-receipt";
     if(noReceipt)
     {
      File.SetAttributes(drawingPath,FileAttributes.Normal);File.WriteAllText(drawingPath,"new submitted drawing memory");files[2]=Clone(frozen[2]);Bind(files[2]);File.SetAttributes(drawingPath,FileAttributes.ReadOnly);drawingNative.Dirty=false;
     }
     else ((IDictionary)Field(plugin,"archiveMemoryCopies")).Add(drawingPath,memory);
     var independent=New("ControlledOpenManifestDto");Set(independent,"Id",Guid.NewGuid());Set(independent,"ProjectId",project);Set(independent,"RootDocumentId",Get(partHead,"DocumentId"));Set(independent,"RootVersionId",Get(partHead,"VersionId"));Set(independent,"RootRelativePath","PART.SLDPRT");Set(independent,"RootRevision","A-W1");Set(independent,"ForEdit",true);
     var independentFiles=(IList)Activator.CreateInstance(Property(independent,"Files").PropertyType);independentFiles.Add(partHead);independentFiles.Add(Clone(frozen[2]));Set(independent,"Files",independentFiles);
     object Snapshot(object file,int kind,object[] children)=>new{documentId=Get(file,"DocumentId"),fileName=Get(file,"FileName"),configuration=Get(file,"Configuration"),kind,role=0,quantity=1,status=0,includesDesignDependencies=true,usesNativeFileInventory=true,children};
     object Version(object file,object snapshot)=>json.Deserialize(json.Serialize(new{id=Get(file,"VersionId"),documentId=Get(file,"DocumentId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength"),revision=new{display=Get(file,"Revision")},referenceSnapshot=snapshot}),Type("DocumentVersionDto"));
     var partDependencies=mode=="dependency-binding-race"?new[]{Snapshot(files[3],1,new object[0])}:new object[0];
     if(mode=="dependency-binding-race")independentFiles.Add(Clone(files[3]));
     var oldVersion=Version(frozen[1],Snapshot(frozen[1],1,partDependencies));var headVersion=Version(partHead,Snapshot(partHead,1,partDependencies));
     Set(headVersion,"DrawingVersion","V2");
     var rootChildren=files.Cast<object>().Where(file=>((string)Get(file,"FileName")).EndsWith("SLDPRT")).Select(file=>Snapshot(file,1,new object[0])).ToArray();var rootVersion=Version(files[0],Snapshot(files[0],0,rootChildren));
     var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
     var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
     var mutations=0;var rootReadOnly=true;var rootUnlocks=0;
     var components=files.Cast<object>().Where(file=>((string)Get(file,"FileName")).EndsWith("SLDPRT")).Select(file=>new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{switch(call.MethodName){case "GetPathName":return Path.Combine(folder,(string)Get(file,"FileName"));case "get_ReferencedConfiguration":return "Default";case "get_IsVirtual":return false;case "GetModelDoc2":return ReferenceEquals(file,files[1])?partModel:null;case "GetSuppression":return 2;case "GetChildren":return new object[0];default:throw new Exception(call.MethodName);}}).GetTransparentProxy()).ToArray();
     var rootModel=new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return rootPath;case "GetTitle":return "OWNER.SLDASM";case "GetType":return 2;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return rootReadOnly;case "get_ConfigurationManager":return manager;case "GetComponents":return components;case "SetReadOnlyState":rootReadOnly=(bool)call.Args[0];rootUnlocks++;return true;default:mutations++;throw new Exception("不得改变父装配："+call.MethodName);}}).GetTransparentProxy();
     SetField(plugin,"application",new Proxy(appType,call=>{switch(call.MethodName){case "GetDocuments":return new[]{rootModel,partModel,drawingModel};case "get_ActiveDoc":return partModel;default:mutations++;throw new Exception("不得保存、关闭或重新打开："+call.MethodName);}}).GetTransparentProxy());
     ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,owner);
     object Node(object file,string kind){var node=New("CadTreeNode");Set(node,"DocumentId",Get(file,"DocumentId"));Set(node,"FullPath",Path.Combine(folder,(string)Get(file,"FileName")));Set(node,"FileName",Get(file,"FileName"));Set(node,"Configuration","Default");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"OpenedVersionId",Get(file,"VersionId"));return node;}
     var rootNode=Node(files[0],"Assembly");var partNode=Node(files[1],"Part");var drawingNode=Node(files[2],"Drawing");((IList)Get(rootNode,"Children")).Add(partNode);((IList)Get(partNode,"Children")).Add(drawingNode);SetField(plugin,"currentTree",rootNode);
     var selected=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));selected.Add(New("BatchOperationItem",mode=="unselected-no-receipt"?rootNode:partNode,0));
     object Document(object file,int kind){var doc=New("DocumentDto");Set(doc,"Id",Get(file,"DocumentId"));Set(doc,"ProjectId",project);Set(doc,"FileName",Get(file,"FileName"));Set(doc,"Kind",kind);return doc;}
     var partDoc=Document(files[1],1);var drawingDoc=Document(files[2],2);var rootDoc=Document(files[0],0);var checkoutRequests=new List<Guid>();var granted=new List<Guid>();var headReads=0;var dependencyRaceTriggered=false;var unexpected=new List<string>();
     using(var lifetime=new CancellationTokenSource())
     using(var client=(IDisposable)New("PdmApiClient","http://post-checkin.invalid/"))
     using(var handler=new ReferenceRecoveryHandler(request=>{
      var path=request.RequestUri.AbsolutePath;object data;
      if(path.EndsWith("/document-operation-rules"))data=new{disabledRuleIds=new string[0]};
      else if(path.EndsWith("/document-relations"))data=new[]{new{modelDocumentId=Get(files[1],"DocumentId"),drawingDocumentId=Get(files[2],"DocumentId")}};
      else if(path.EndsWith("/documents"))data=new[]{rootDoc,partDoc,drawingDoc};
      else if(path.EndsWith("/open-manifest"))data=path.Contains(Get(files[1],"DocumentId").ToString())?independent:parent;
      else if(path.EndsWith("/versions"))
      {
       if(path.Contains(Get(files[1],"DocumentId").ToString()))
       {
        headReads++;if(mode=="head-binding-race"&&headReads==1)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(files[1],"DocumentId"),project,Guid.NewGuid(),"W9",Get(files[1],"Sha256"),Get(files[1],"FileLength")});
        if(mode=="dependency-binding-race"&&headReads==3){dependencyRaceTriggered=true;identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)Path.Combine(folder,(string)Get(files[3],"FileName")),Get(files[3],"DocumentId"),project,Guid.NewGuid(),"W9",Get(files[3],"Sha256"),Get(files[3],"FileLength")});}
        data=mode=="frozen-child-race"&&headReads==1?new[]{oldVersion}
            :request.RequestUri.Query.Contains("take=1")?new[]{headVersion}:new[]{headVersion,oldVersion};
       }
       else data=path.Contains(Get(files[0],"DocumentId").ToString())?new[]{rootVersion}:new[]{drawingVersion};
      }
      else if(path.EndsWith("/checkout"))
      {
       var id=Guid.Parse(path.Split('/').Reverse().Skip(1).First());checkoutRequests.Add(id);
       if(mode=="permission-denied")return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"当前账号没有该项目图档的获取权限。\"}")};
       var doc=id.Equals(Get(files[1],"DocumentId"))?partDoc:id.Equals(Get(files[2],"DocumentId"))?drawingDoc:rootDoc;
       granted.Add(id);Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);Set(doc,"CheckedOutAt",new DateTime(2026,11,12,5,50,0,DateTimeKind.Utc));data=doc;
       if(mode=="checkout-race"){var changed=Guid.NewGuid();Set(headVersion,"Id",changed);Set(partHead,"VersionId",changed);Set(independent,"RootVersionId",changed);}
      }
      else {unexpected.Add(path);throw new Exception("不得下载、提交或扩大权限："+path);}
      return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
     }))
     using(var http=new HttpClient(handler){BaseAddress=new Uri("http://post-checkin.invalid/")})
     {
      http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);SetField(memory,"CommittedScope",Get(client,"EditJournalScope"));
      var scope=(string)Get(client,"EditJournalScope")+"|tester";SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),scope));SetField(plugin,"editJournalScope",scope);
      if(!noReceipt)
      {
       var receipt=New("PdmAddin+CompletedWorkingArchive");SetField(receipt,"ProjectId",project);SetField(receipt,"DocumentId",Get(files[1],"DocumentId"));SetField(receipt,"VersionId",Get(files[1],"VersionId"));SetField(receipt,"StoredSha256",Get(files[1],"Sha256"));SetField(receipt,"SourceSha256",Get(files[1],"Sha256"));SetField(receipt,"StoredLength",Get(files[1],"FileLength"));SetField(receipt,"SourceLength",Get(files[1],"FileLength"));SetField(receipt,"Username","tester");SetField(receipt,"ServerScope",Get(client,"EditJournalScope"));((IDictionary)Field(plugin,"completedWorkingArchives")).Add(partPath,receipt);
      }
      if(mode=="new-head"){var id=Guid.NewGuid();Set(partHead,"VersionId",id);Set(headVersion,"Id",id);Set(independent,"RootVersionId",id);}
      if(mode=="new-dependency"){var extra=Clone(files[3]);Set(extra,"DocumentId",Guid.NewGuid());Set(extra,"FileName","NEW.SLDPRT");Set(extra,"RelativePath","NEW.SLDPRT");independentFiles.Add(extra);}
      if(mode=="configuration"){Set(partHead,"Configuration","Other");Set(Get(headVersion,"ReferenceSnapshot"),"Configuration","Other");}
      if(mode=="path"){Set(partHead,"RelativePath","different/PART.SLDPRT");Set(independent,"RootRelativePath","different/PART.SLDPRT");}
      if(mode=="binding")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(files[1],"DocumentId"),project,Guid.NewGuid(),"W9",Get(files[1],"Sha256"),Get(files[1],"FileLength")});
      if(mode=="drawing-memory-edit")drawingNative.Modify(false);
      var hashes=files.Cast<object>().ToDictionary(file=>(string)Get(file,"FileName"),file=>WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName"))));var attributes=files.Cast<object>().ToDictionary(file=>(string)Get(file,"FileName"),file=>File.GetAttributes(Path.Combine(folder,(string)Get(file,"FileName"))));var parentVersion=Get(parent,"RootVersionId");var parentFacts=frozen.ToDictionary(file=>(Guid)Get(file,"DocumentId"),file=>new{Version=Get(file,"VersionId"),Hash=Get(file,"Sha256"),Length=Get(file,"FileLength"),Configuration=Get(file,"Configuration"),Path=Get(file,"RelativePath")});var sourceVersion=Get(files[1],"VersionId");var drawingSourceVersion=Get(files[2],"VersionId");
      Exception failure=null;var success=false;try{var task=(Task)TreeCall(plugin,"TryAcquireReusableWorkingCopyAsync",selected,project,Get(files[0],"DocumentId"));await task;success=(bool)Get(task,"Result");}catch(InvalidOperationException error){failure=error;}
      var expected=mode=="selected"||mode=="no-session-receipt";if(expected&&failure!=null)Console.WriteLine("DIAGNOSTIC: "+failure);
      Check(success==expected,mode+"：父装配冻结旧引用时，按所选子件独立最新基准核对再次获取");
      Check(granted.All(id=>id.Equals(Get(files[1],"DocumentId"))||id.Equals(Get(files[2],"DocumentId")))&&granted.Count==(expected?2:mode=="checkout-race"||mode=="frozen-child-race"?1:0),mode+"：仅选中零件及关联工程图获授权，父装配与其它零件不获取");
      if(mode.StartsWith("frozen-child-"))Check(failure?.Message.Contains("V2")==true&&!failure.Message.Contains("W1")
          &&checkoutRequests.Count==(mode=="frozen-child-old"?0:1),"按子件当前图纸版本拒绝旧基准解锁并使用 V 编号："+mode);
      Check(rootReadOnly&&rootUnlocks==0&&mutations==0&&unexpected.Count==0&&events.Count==0&&files.Cast<object>().All(file=>WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName")))==hashes[(string)Get(file,"FileName")]),mode+"：43份CAD原字节与父装配只读状态保留，无保存、关闭、重开或下载");
      Check(files.Cast<object>().All(file=>{var name=(string)Get(file,"FileName");return expected&&(name=="PART.SLDPRT"||name=="PART.SLDDRW")||File.GetAttributes(Path.Combine(folder,name))==attributes[name];})&&!partNative.Dirty&&drawingNative.Dirty==(!noReceipt),mode+"：未选文件属性和模型修改标记保持原样");
      Check(Equals(Get(parent,"RootVersionId"),parentVersion)&&frozen.All(file=>{var before=parentFacts[(Guid)Get(file,"DocumentId")];return Equals(Get(file,"VersionId"),before.Version)&&Equals(Get(file,"Sha256"),before.Hash)&&Equals(Get(file,"FileLength"),before.Length)&&Equals(Get(file,"Configuration"),before.Configuration)&&Equals(Get(file,"RelativePath"),before.Path);})&&Equals(Get(files[1],"VersionId"),sourceVersion)&&Equals(Get(files[2],"VersionId"),drawingSourceVersion),mode+"：父快照与真实磁盘基线保持，工程图内存提交不冒写磁盘版本");
      if(mode=="permission-denied")Check(checkoutRequests.Count==1&&failure!=null&&failure.Message.Contains("获取权限"),"独立基准验证不绕过服务器获取授权");
      if(mode=="dependency-binding-race")Check(dependencyRaceTriggered&&checkoutRequests.Count==0&&failure!=null&&failure.Message.Contains("本地基准"),"获取前网络请求期间依赖发生同字节重绑定，完整依赖复核拒绝并保持零checkout");
      if(mode=="new-head")Check(failure!=null&&failure.Message.Contains("PART.SLDPRT")&&!failure.Message.Contains("PART.SLDDRW"),"差异提示只列真实零件基准冲突，不再误列已确认内存提交的工程图");
      if(expected)Check(Equals(Get(((IDictionary)Field(plugin,"localEdits"))[Get(files[1],"DocumentId")],"BaseVersionId"),sourceVersion),"再次获取的草稿采用实际已存档磁盘基准："+mode);
     }
    }
   }
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
