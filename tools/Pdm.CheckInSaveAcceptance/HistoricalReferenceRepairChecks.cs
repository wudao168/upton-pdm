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
 static async Task HistoricalReferenceRepairScenario(string fixture,string output)
 {
  fixture=Path.GetFullPath(fixture);output=Path.Combine(Path.GetFullPath(output),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
  var names=Directory.GetFiles(Path.Combine(fixture,"historical"),"*.SLDPRT").Select(Path.GetFileName).ToArray();
  if(names.Length!=8)throw new Exception("原报错样本的八个引用副本不完整");
  foreach(var mode in new[]{"new","same-project-reuse","other-project-same-name","same-name-conflict","foreign-binding","missing","changed-owner","wrong-owner-version","changed-during-preflight","permission-denied","new-child","part-only","cancel"})
  {
   var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);
   object Node(string name,string kind,bool registered,string source=null)
   {
    var node=LinkedNode(folder,name,kind);Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");
    if(source!=null)File.Copy(source,(string)Get(node,"FullPath"),true);
    File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.Normal);
    if(!registered){Set(node,"DocumentId",null);Set(node,"Revision","");Set(node,"CurrentRevision","");}
    Set(node,"InstancePath",Path.GetFileNameWithoutExtension(name));Set(node,"NativeFileScopeVerified",true);return node;
   }
   var root=Node("CW0030002.10.SLDASM","Assembly",true,Path.Combine(fixture,"working","CW0030002.10.SLDASM"));
   var owner=Node("SS5Y-8位电磁阀座.SLDASM","Assembly",true,Path.Combine(fixture,"working","SS5Y-8位电磁阀座.SLDASM"));
   var part=Node("零件1.SLDPRT","Part",false,Path.Combine(fixture,"working","零件1.SLDPRT"));
   var oldParts=names.Select(name=>Node(name,"Part",false,Path.Combine(fixture,"historical",name))).ToArray();
   foreach(var node in oldParts){((IList)Get(owner,"Children")).Add(node);File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.ReadOnly);}
   ((IList)Get(root,"Children")).Add(owner);((IList)Get(root,"Children")).Add(part);
   var oldVersion=LinkedVersion(owner,LinkedSnapshot(owner,false));Set(oldVersion,"Revision",LinkedRevision("W1"));
   var originalOwnerVersion=(Guid)Get(oldVersion,"Id");
   Set(owner,"SuppressedArchiveVersion",oldVersion);Set(owner,"SuppressedArchiveSha256",Get(oldVersion,"Sha256"));Set(owner,"SuppressedLocalStructureSha256",Get(oldVersion,"Sha256"));
   var rootVersion=LinkedVersion(root,LinkedSnapshot(root));Set(rootVersion,"Revision",LinkedRevision("W1"));
   Set(rootVersion,"Sha256",new string('B',64));
   Set(root,"OpenedVersionId",Get(rootVersion,"Id"));Set(root,"OpenedRevision","W1");
   Set(root,"CheckedOutBy","tester");
   var all=new[]{root,owner,part}.Concat(oldParts).ToArray();
   var nativePaths=new HashSet<string>(all.Select(node=>(string)Get(node,"FullPath")),StringComparer.OrdinalIgnoreCase);Set(root,"NativeFileScopePaths",nativePaths);
   var plugin=New("PdmAddin");var project=Guid.NewGuid();var checkout=(Guid)Field(plugin,"checkoutSessionId");Set(root,"CheckoutSessionId",checkout);
   var versions=new Dictionary<Guid,object[]>{{(Guid)Get(root,"DocumentId"),new[]{rootVersion}},{(Guid)Get(owner,"DocumentId"),new[]{oldVersion}}};
   var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var oldBinding=binding.GetValue(null);
   var resolver=identity.GetField("workspaceRootResolver",Flags);var oldResolver=resolver.GetValue(null);
   var batchFolder=Type("ArchiveBatchJournal").GetField("directory",Flags);var oldBatch=batchFolder.GetValue(null);
   var uploadFolder=Type("ArchiveUploadJournal").GetField("directory",Flags);var oldUpload=uploadFolder.GetValue(null);
   binding.SetValue(null,Path.Combine(folder,"bindings"));resolver.SetValue(null,new Func<string>(()=>Path.Combine(folder,"workspace")));batchFolder.SetValue(null,Path.Combine(folder,"journal"));
   var shortJournal=Path.Combine(Path.GetTempPath(),"pdm-history-"+Guid.NewGuid().ToString("N"));uploadFolder.SetValue(null,shortJournal);
   var hashes=all.ToDictionary(node=>(string)Get(node,"FullPath"),node=>WorkingReuseHash((string)Get(node,"FullPath")));
   var attributes=oldParts.ToDictionary(node=>(string)Get(node,"FullPath"),node=>File.GetAttributes((string)Get(node,"FullPath")));
   var nativeCalls=0;var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   SetField(plugin,"application",ArchiveNativeMock(appType,call=>{
    if(call.MethodName=="GetDocuments")return Array.Empty<object>();if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetUserPreferenceStringValue")return "";
    if(call.MethodName=="GetConfigurationNames")return new[]{"Default","默认"};
    nativeCalls++;throw new Exception("不应打开、重建、关闭或修改原生文档："+call.MethodName);
   }));
   SetField(plugin,"currentTree",root);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
   using(var pane=(Control)New("PdmTaskPaneControl","历史引用补齐隔离验证"))
   using(var handler=new HistoricalRepairHandler(all,oldParts,owner,versions,project,checkout,mode,folder))
   using(var client=(IDisposable)New("PdmApiClient","http://historical-repair.invalid/"))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://historical-repair.invalid/")})
   using(var lifetime=new CancellationTokenSource())
   try
   {
    pane.CreateControl();SetField(plugin,"taskPaneControl",pane);http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);Set(client,"EditJournalScope","historical-repair-fixture");SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
    SetField(plugin,"editJournalScope","historical-repair-fixture|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"edit-journal"),"historical-repair-fixture|tester"));
    if(mode=="same-project-reuse")foreach(var node in oldParts)handler.AddExisting(node,false);
    if(mode=="other-project-same-name")foreach(var node in oldParts)handler.AddExisting(node,true);
    if(mode=="same-name-conflict")handler.AddExisting(oldParts[0],false,true);
    if(mode=="foreign-binding")identity.GetMethod("TryWrite",Flags).Invoke(null,new object[]{Get(oldParts[0],"FullPath"),Guid.NewGuid(),Guid.NewGuid()});
    if(mode=="missing"){File.SetAttributes((string)Get(oldParts[0],"FullPath"),FileAttributes.Normal);File.Delete((string)Get(oldParts[0],"FullPath"));}
    if(mode=="changed-owner")File.AppendAllText((string)Get(owner,"FullPath")," saved actual edit");
    if(mode=="wrong-owner-version")Set(oldVersion,"Id",Guid.NewGuid());
    if(mode=="new-child"){Set(owner,"SuppressedLocalStructureSha256","different-parent");Set(owner,"SuppressedArchiveSha256","different-parent");Set(owner,"IsModifiedInSolidWorks",true);}
    var selected=mode=="part-only"?LinkedItems(part):LinkedItems(root,part);
    var include=Type("PdmAddin").GetMethod("IncludeHistoricalReferenceRepairsAsync",Flags);
    // The installed baseline reproduces the original scope failure on this same sample.
    if(include==null){Type("PdmAddin").GetMethod("ValidateHistoricalReferenceScope",Flags).Invoke(null,new object[]{root,selected});throw new Exception("旧版未能自动准备历史引用");}
    var blocked=new[]{"same-name-conflict","foreign-binding","missing","changed-owner","wrong-owner-version"}.Contains(mode);
    Exception failure=null;object planned=null;
    try{var dry=(Task)include.Invoke(plugin,new object[]{root,selected,project,CancellationToken.None,true});await dry;
     if(oldParts.Any(node=>Get(node,"HistoricalRepair")!=null||Get(node,"DocumentId")!=null))throw new Exception("早期只读核对修改了图档身份");
     var task=(Task)include.Invoke(plugin,new object[]{root,selected,project,CancellationToken.None,false});await task;planned=Get(task,"Result");
    }catch(Exception error){failure=error;}
    if(blocked){if(failure==null||handler.Uploads!=0||handler.Commits!=0||handler.Registers!=0)throw new Exception("冲突未在原生处理及登记上传前阻止："+mode,failure);Console.WriteLine("PASS: 历史引用前置拦截 "+mode+"；登记/上传/提交均为0");continue;}
    if(failure!=null)throw failure;
    var plannedNodes=((IEnumerable)planned).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
    if(mode=="new-child"||mode=="part-only")
    {
     if(plannedNodes.Length!=selected.Length||oldParts.Any(node=>Get(node,"HistoricalRepair")!=null))throw new Exception("无关历史或真正新引用被错误自动纳入");
     if(mode=="new-child"){
      var denied=false;try{await (Task)Type("PdmAddin").GetMethod("ResolveBatchReferenceVersionsAsync",Flags).Invoke(plugin,new object[]{planned,project,null,CancellationToken.None});}catch(InvalidOperationException){denied=true;}
      if(!denied)throw new Exception("新引用借用了历史豁免");
     }
     Console.WriteLine("PASS: "+mode+"；未改变的无关装配不纳入，真正新引用仍必须登记");continue;
    }
    var expectedCount=mode=="same-project-reuse"?2:10;
    if(plannedNodes.Length!=expectedCount||plannedNodes.Contains(owner)||oldParts.Any(node=>Get(node,"HistoricalRepair")==null))throw new Exception("历史补齐未完整纳入或旧父装配被误升版");
    // Native opening/property reads are independently covered by the archive regressions.
    // Use the file-companion path here to isolate the real reference/upload/atomic receipt pipeline.
    Set(root,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(part,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
    Set(Get(rootVersion,"ReferenceSnapshot"),"Role",1);
    handler.Planned=plannedNodes;
    if(mode=="changed-during-preflight")handler.OnPreflight=()=>{
     File.SetAttributes((string)Get(oldParts[7],"FullPath"),FileAttributes.Normal);File.AppendAllText((string)Get(oldParts[7],"FullPath")," external edit");
    };
    var token=mode=="cancel"?new CancellationToken(true):CancellationToken.None;
    object result=null;failure=null;
    try{var task=(Task)Type("PdmAddin").GetMethod("CheckInBatchAsync",Flags).Invoke(plugin,new object[]{planned,project,"零件1插入总装",null,token,null,null,null,null,true,"helper"});await task;result=Get(task,"Result");}catch(Exception error){failure=error;}
    var succeeds=!new[]{"changed-during-preflight","permission-denied","cancel"}.Contains(mode);
    if(succeeds&&(failure!=null||result==null))throw new Exception("自动补齐整批存档失败："+mode,failure);
    if(!succeeds&&(failure==null||handler.Uploads!=0||handler.Commits!=0))throw new Exception("危险批次未在上传前停止："+mode,failure);
    if(nativeCalls!=0||versions[(Guid)Get(owner,"DocumentId")].Length!=1||(Guid)Get(oldVersion,"Id")!=originalOwnerVersion)throw new Exception("原父装配发生变化或执行了原生写操作");
    if(succeeds)
    {
     if(handler.Uploads!=expectedCount||handler.Commits!=1||(int)Get(result,"CreatedVersions")!=2||(int)Get(result,"HistoricalRepairs")!=(expectedCount-2))throw new Exception("工程变更与历史补齐的回执统计不符");
     if(oldParts.Any(node=>WorkingReuseHash((string)Get(node,"FullPath"))!=hashes[(string)Get(node,"FullPath")]||File.GetAttributes((string)Get(node,"FullPath"))!=attributes[(string)Get(node,"FullPath")]))throw new Exception("补齐修改了历史原文件或只读属性");
     if(oldParts.Any(node=>(bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,new object[]{Get(node,"FullPath"),null})))throw new Exception("给外部历史源写入了项目绑定");
     Console.WriteLine("PASS: "+mode+"；工程变更2，历史补齐"+(expectedCount-2)+"；旧父版本保留，原文件/属性/绑定未改变；一次整批提交");
    }
    else Console.WriteLine("PASS: "+mode+"；上传前停止且临时登记已清理，本批次无版本");
   }
   finally
   {
    binding.SetValue(null,oldBinding);resolver.SetValue(null,oldResolver);batchFolder.SetValue(null,oldBatch);uploadFolder.SetValue(null,oldUpload);
    if(Directory.Exists(shortJournal)&&Path.GetFullPath(shortJournal).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(shortJournal,true);
   }
  }
 }

 sealed class HistoricalRepairHandler:HttpMessageHandler
 {
  readonly object[] nodes,oldParts;readonly object owner;readonly Dictionary<Guid,object[]> versions;readonly Guid project,checkout;readonly string mode,folder;
  readonly List<object> extraDocuments=new List<object>();readonly Dictionary<Guid,Dictionary<string,object>> transfers=new Dictionary<Guid,Dictionary<string,object>>();
  readonly HashSet<Guid> reserved=new HashSet<Guid>();readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  public object[] Planned;public int Uploads,Commits,Registers;public Action OnPreflight;
  public HistoricalRepairHandler(object[] nodes,object[] oldParts,object owner,Dictionary<Guid,object[]> versions,Guid project,Guid checkout,string mode,string folder){this.nodes=nodes;this.oldParts=oldParts;this.owner=owner;this.versions=versions;this.project=project;this.checkout=checkout;this.mode=mode;this.folder=folder;}
  object Document(object node,bool committed=false)=>new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),kind=(int)Get(node,"Kind"),checkedOutBy=committed?null:"tester",checkoutSessionId=committed?(Guid?)null:checkout,drawingVersion=committed?"V2":"V1",publishedVersion="未发布",revision=new{display=committed?"W2":"W1"}};
  public void AddExisting(object node,bool foreign,bool conflict=false)
  {
   var id=Guid.NewGuid();var v=LinkedVersion(node,LinkedSnapshot(node));Set(v,"Revision",LinkedRevision("W1"));if(conflict)Set(v,"Sha256",new string('F',64));versions[id]=new[]{v};
   extraDocuments.Add(new{id,projectId=foreign?Guid.NewGuid():project,fileName=Get(node,"FileName"),kind=1});
  }
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var route=request.RequestUri.AbsolutePath;object value;var status=HttpStatusCode.OK;
   if(route.EndsWith("/documents")&&request.Method==HttpMethod.Get)value=nodes.Where(node=>Get(node,"DocumentId")!=null).Select(node=>Document(node)).Concat(extraDocuments).ToArray();
   else if(route.EndsWith("/reference-tree")){status=HttpStatusCode.NotFound;value=new{message="隔离样本未设置项目根"};}
   else if(route.EndsWith("/versions")){var id=versions.Keys.FirstOrDefault(key=>route.Contains(key.ToString()));value=id==Guid.Empty||mode=="wrong-owner-version"&&id==(Guid)Get(owner,"DocumentId")?Array.Empty<object>():versions[id];}
   else if(route.EndsWith("/registrations")&&request.Method==HttpMethod.Post)
   {
    Registers++;var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());var name=(string)((Dictionary<string,object>)payload["document"])["fileName"];var node=nodes.Single(n=>(string)Get(n,"FileName")==name);var id=Guid.NewGuid();Set(node,"DocumentId",id);reserved.Add(id);versions[id]=Array.Empty<object>();value=Document(node);
   }
   else if(route.EndsWith("/checkout")){if(mode=="permission-denied"){status=HttpStatusCode.Conflict;value=new{message="其他人已获取编辑权限"};}else value=Document(nodes.Single(node=>Get(node,"DocumentId")!=null&&route.Contains(Get(node,"DocumentId").ToString())));}
   else if(route.EndsWith("/checkin/preflight")){var action=OnPreflight;OnPreflight=null;action?.Invoke();if(mode=="permission-denied"){status=HttpStatusCode.Conflict;value=new{message="编辑权限已被其他人取得"};}else value=new{};}
   else if(route.Contains("/archive-batches/")&&route.EndsWith("/cancel-registration")&&request.Method==HttpMethod.Post){foreach(var id in reserved)versions.Remove(id);reserved.Clear();value=new{};}
   else if(route.EndsWith("/uploads/sessions"))
   {
    Uploads++;var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());var id=Guid.NewGuid();transfers[id]=payload;
    value=new{id,projectId=project,totalLength=payload["totalLength"],expectedSha256=payload["sha256"],receivedLength=0,chunkSize=1048576,expiresAt=DateTime.UtcNow.AddHours(1),targetRelativePath="history/"+id};
   }
   else if(route.Contains("/chunks/"))value=new{};
   else if(route.EndsWith("/complete")){var pair=transfers.Single(t=>route.Contains(t.Key.ToString()));value=new{relativePath="history/"+pair.Key,length=pair.Value["totalLength"],sha256=pair.Value["sha256"]};}
   else if(route.EndsWith("/archive-batches"))
   {
    var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());File.WriteAllText(Path.Combine(folder,"batch-request.json"),json.Serialize(payload));
    var files=((IEnumerable)payload["files"]).Cast<Dictionary<string,object>>().ToArray();
    if(files.Any(file=>file["documentId"].ToString()==Get(owner,"DocumentId").ToString()))throw new Exception("旧父装配被误提交");
    var rootFile=files.Single(file=>file["documentId"].ToString()==Get(nodes[0],"DocumentId").ToString());var snapshot=(Dictionary<string,object>)((Dictionary<string,object>)rootFile["request"])["root"];
    var closure=((IEnumerable)snapshot["children"]).Cast<Dictionary<string,object>>().Where(child=>Convert.ToInt32(child["role"])==1).ToArray();
    if(oldParts.Any(node=>!closure.Any(child=>child["documentId"].ToString()==Get(node,"DocumentId").ToString())))throw new Exception("旧父空快照解析后将丢失实际文件");
    foreach(var file in files.Where(file=>oldParts.Any(node=>file["documentId"].ToString()==Get(node,"DocumentId").ToString())))
    {
     var body=(Dictionary<string,object>)file["request"];if((bool)body["keepEditing"]||body["handoffTo"]!=null||!body["comment"].ToString().Contains("历史资料补齐"))throw new Exception("补齐误保留编辑权、交接或被当作工程变更");
    }
    Commits++;value=new{batchId=payload["batchId"],results=Planned.Select(node=>new{document=Document(node,true),version=new{id=Guid.NewGuid(),drawingVersion=oldParts.Contains(node)?"V1":"V2",revision=new{display=oldParts.Contains(node)?"W1":"W2"},sha256=WorkingReuseHash((string)Get(node,"FullPath")),fileLength=new FileInfo((string)Get(node,"FullPath")).Length},versionCreated=true}).ToArray()};
   }
   else throw new Exception("未预期的历史补齐 API："+route+" "+request.Method);
   return new HttpResponseMessage(status){Content=new StringContent(json.Serialize(value))};
  }
 }
}
