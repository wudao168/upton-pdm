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
 static async Task ArchiveUploadGateScenario(Form host,string output)
 {
  output=Path.Combine(Path.GetFullPath(output),Guid.NewGuid().ToString("N"));
  foreach(var mode in new[]{"early-other-editor","early-missing-file","early-review-locked","confirm","cancel","recheck","changed-during-preflight","server-denied-late"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode,"View");Directory.CreateDirectory(folder);
   var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
   var batchFolder=Type("ArchiveBatchJournal").GetField("directory",Flags);var oldBatch=batchFolder.GetValue(null);
   var uploadFolder=Type("ArchiveUploadJournal").GetField("directory",Flags);var oldUpload=uploadFolder?.GetValue(null);
   var shortJournal=Path.Combine(Path.GetTempPath(),"pdm-gate-"+Guid.NewGuid().ToString("N"));
   bindings.SetValue(null,Path.Combine(folder,"bindings"));resolver.SetValue(null,new Func<string>(()=>Path.GetDirectoryName(folder)));batchFolder.SetValue(null,Path.Combine(folder,"batch-journal"));uploadFolder?.SetValue(null,shortJournal);
   object session=null;
   try
   {
    var plugin=New("PdmAddin");var project=Guid.NewGuid();var checkout=(Guid)Field(plugin,"checkoutSessionId");
    var nodes=new[]{LinkedNode(folder,"SOURCE-A.SLDPRT","Part"),LinkedNode(folder,"SOURCE-B.SLDPRT","Part")};
    var versions=new Dictionary<Guid,object>();
    foreach(var node in nodes)
    {
     Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",checkout);Set(node,"DrawingVersion","V1");Set(node,"Revision","A");Set(node,"OpenedRevision","A");
     Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"NativeFileScopeVerified",true);Set(node,"LatestStructureIndexPending",false);
     var snapshot=LinkedSnapshot(node);Set(snapshot,"Role",1);var version=LinkedVersion(node,snapshot);Set(version,"DrawingVersion","V1");Set(version,"StorageRelativePath","qa/"+Get(node,"FileName"));Set(version,"PropertySnapshot",new Dictionary<string,string>());versions[(Guid)Get(node,"DocumentId")]=version;
     Set(node,"LatestStoredSha256",Get(version,"Sha256"));Set(node,"LatestVersionSha256",Get(version,"Sha256"));Set(node,"OpenedVersionId",Get(version,"Id"));
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{Get(node,"FullPath"),Get(node,"DocumentId"),project,Get(version,"Id"),"A",Get(version,"Sha256"),Get(version,"FileLength")});
     File.AppendAllText((string)Get(node,"FullPath")," changed saved source");
    }
    ((IList)Get(nodes[0],"Children")).Add(nodes[1]);archiveRuleVersions=versions.ToDictionary(p=>p.Key,p=>new[]{p.Value});
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeWrites=0;
    SetField(plugin,"application",ArchiveNativeMock(appType,call=>{
     if(call.MethodName=="GetDocuments")return Array.Empty<object>();if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetUserPreferenceStringValue")return "";
     nativeWrites++;throw new Exception("Unexpected native mutation "+call.MethodName);
    }));
    SetField(plugin,"currentTree",nodes[0]);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
    using(var handler=new ArchiveGateHandler(nodes,versions,project,checkout,mode))
    using(var client=(IDisposable)New("PdmApiClient","http://archive-gate.invalid/"))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://archive-gate.invalid/")})
    using(var pane=(Control)New("PdmTaskPaneControl","存档上传确认隔离验证"))
    using(var lifetime=new CancellationTokenSource())
    using(var driverStop=new CancellationTokenSource())
    {
     pane.CreateControl();SetField(plugin,"taskPaneControl",pane);http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);Set(client,"EditJournalScope","archive-gate-fixture");SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     SetField(plugin,"editJournalScope","archive-gate-fixture|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"edit-journal"),"archive-gate-fixture|tester"));
     if(mode.StartsWith("early-"))
     {
      if(mode=="early-missing-file")File.Delete((string)Get(nodes[1],"FullPath"));
      Exception denied=null;try{await (Task)Type("PdmAddin").GetMethod("ValidateArchiveInputsBeforePreparationAsync",Flags).Invoke(plugin,new object[]{LinkedItems(nodes),project,CancellationToken.None});}catch(InvalidOperationException error){denied=error;}
      if(denied==null||!denied.Message.Contains("尚未上传")||handler.Uploads!=0||handler.Commits!=0||nativeWrites!=0)throw new Exception("基本输入错误未在重建和上传前拦截 "+mode,denied);
      Console.WriteLine("PASS: early archive input preflight "+mode+"; zero native mutations/uploads/commits");continue;
     }
     session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
     var proj=New("ProjectDto");Set(proj,"Id",project);Set(proj,"Code","QA-GATE");Set(proj,"Name","隔离确认验证");Set(proj,"CanSubmitArchive",true);var projects=Array.CreateInstance(Type("ProjectDto"),1);projects.SetValue(proj,0);
     var rechecked=false;
     var selectDriver=Task.Run(async()=>{
      await Task.Delay(300);
      await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any(f=>((Button)Field(f,"execute")).Enabled)),"选好范围后无法直接确认存档");
      if(mode=="recheck")
      {
       ArchiveUi(session,()=>{var form=((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Single();((Button)Field(form,"recheck")).PerformClick();return true;});
       await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any(f=>((Button)Field(f,"execute")).Enabled)),"可选重新检查未返回选择页面");
       if(handler.Uploads!=0||handler.Commits!=0)throw new Exception("重新检查错误触发上传");rechecked=true;
      }
      handler.Confirmed=true;
      ArchiveUi(session,()=>{var form=((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Single();ArchiveWindow(session).BeginInvoke((Action)(()=>((Button)Field(form,"execute")).PerformClick()));return true;});
     });
     _=selectDriver.ContinueWith(t=>{if(t.IsFaulted)TreeCall(session,"RequestCancellation");},TaskScheduler.Default);
     ArchiveUi(session,()=>{
      var form=(Form)New("BatchOperationDialog",nodes[0],LinkedItems(nodes),projects,project,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),nodes.Select(n=>(string)Get(n,"FullPath")).ToArray(),null,null,false,false,0);
      var loader=form.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(form,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));return TreeCall(session,"ShowInline",form,true);
     });await selectDriver;
     handler.OnPreflight=()=>{
      if(mode=="cancel"&&handler.Preflights==1)TreeCall(session,"RequestCancellation");
      if(mode=="changed-during-preflight"&&handler.Preflights==2)File.AppendAllText((string)Get(nodes[0],"FullPath")," external edit during preparation");
     };
     Exception failure=null;object result=null;
     try{
      var task=(Task)Type("PdmAddin").GetMethod("CheckInBatchAsync",Flags).Invoke(plugin,new object[]{LinkedItems(nodes),project,"isolated automatic archive test",null,(CancellationToken)Get(session,"Token"),null,null,null,null,false,null});
      if(await Task.WhenAny(task,Task.Delay(15000))!=task){TreeCall(session,"RequestCancellation");throw new Exception("一次确认后出现额外人工等待");}
      await task;result=Get(task,"Result");
     }catch(Exception error){failure=error;}finally{driverStop.Cancel();}
     var succeeds=mode=="confirm"||mode=="recheck";
     if(succeeds&&(failure!=null||result==null))throw new Exception("正常确认存档验证失败 "+mode,failure);
     if(!succeeds&&failure==null)throw new Exception("应停止的批次未停止 "+mode);
     if(handler.Uploads!=(succeeds?2:0)||handler.Commits!=(succeeds?1:0)||nativeWrites!=0)throw new Exception("上传边界、整批提交或原生调用异常 "+mode+" "+handler.Uploads+"/"+handler.Commits+" native="+nativeWrites,failure);
     if(mode=="recheck"&&(!rechecked||handler.Preflights<4))throw new Exception("可选检查或上传前服务器再次校验未执行");
     if(succeeds&&(int)Get(result,"CreatedVersions")!=2)throw new Exception("未得到两份图档的实际服务端回执");
     Console.WriteLine("PASS: real batch pipeline "+mode+"; one initial confirm, no later operator action; uploads="+handler.Uploads+", commits="+handler.Commits+", native mutations=0");
    }
   }
   finally
   {
    if(session!=null){ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
    bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);batchFolder.SetValue(null,oldBatch);uploadFolder?.SetValue(null,oldUpload);
    var tempPrefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(Directory.Exists(shortJournal)&&Path.GetFullPath(shortJournal).StartsWith(tempPrefix,StringComparison.OrdinalIgnoreCase))Directory.Delete(shortJournal,true);
   }
  }
 }

 sealed class ArchiveGateHandler:HttpMessageHandler
 {
  readonly object[] nodes;readonly Dictionary<Guid,object> versions;readonly Guid project,checkout;readonly string mode;readonly Dictionary<Guid,Dictionary<string,object>> transfers=new Dictionary<Guid,Dictionary<string,object>>();
  public volatile bool Confirmed;public int Uploads,Commits,Preflights;public Action OnPreflight;
  public ArchiveGateHandler(object[] nodes,Dictionary<Guid,object> versions,Guid project,Guid checkout,string mode){this.nodes=nodes;this.versions=versions;this.project=project;this.checkout=checkout;this.mode=mode;}
  object Document(object node,bool committed=false)=>new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),kind=1,checkedOutBy=committed?null:mode=="early-other-editor"?"other-editor":"tester",checkoutSessionId=committed?(Guid?)null:checkout,drawingReviewLocked=mode=="early-review-locked",drawingVersion=committed?"V2":"V1",publishedVersion="未发布",revision=new{display=committed?"W2":"A"}};
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var route=request.RequestUri.AbsolutePath;var json=new JavaScriptSerializer();object value;var status=HttpStatusCode.OK;
   if(route.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(route.EndsWith("/documents"))value=nodes.Select(n=>Document(n)).ToArray();
   else if(route.EndsWith("/reference-tree")){status=HttpStatusCode.NotFound;value=new{message="无根结构"};}
   else if(route.EndsWith("/versions")){var id=versions.Keys.Single(k=>route.Contains(k.ToString()));value=new[]{versions[id]};}
   else if(route.EndsWith("/checkin/preflight")){Preflights++;OnPreflight?.Invoke();if(mode=="server-denied-late"&&Preflights>2){status=HttpStatusCode.Conflict;value=new{message="权限在准备后发生变化，已停止上传"};}else value=new{};}
   else if(route.EndsWith("/uploads/sessions"))
   {
    if(!Confirmed)throw new Exception("未确认即开始上传");Uploads++;var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());var id=Guid.NewGuid();transfers[id]=payload;
    value=new{id,projectId=project,totalLength=payload["totalLength"],expectedSha256=payload["sha256"],receivedLength=0,chunkSize=65536,expiresAt=DateTime.UtcNow.AddHours(1),targetRelativePath="qa/"+id};
   }
   else if(route.Contains("/chunks/"))value=new{};
   else if(route.EndsWith("/complete")){var pair=transfers.Single(t=>route.Contains(t.Key.ToString()));value=new{relativePath="qa/"+pair.Key,length=pair.Value["totalLength"],sha256=pair.Value["sha256"]};}
   else if(route.EndsWith("/archive-batches"))
   {
    if(!Confirmed)throw new Exception("未确认即提交");Commits++;var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());
    value=new{batchId=payload["batchId"],results=nodes.Select(n=>new{document=Document(n,true),version=new{id=Guid.NewGuid(),drawingVersion="V2",revision=new{display="W2"},sha256=WorkingReuseHash((string)Get(n,"FullPath")),fileLength=new FileInfo((string)Get(n,"FullPath")).Length},versionCreated=true}).ToArray()};
   }
   else throw new Exception("Unexpected gate API request "+route);
   return new HttpResponseMessage(status){Content=new StringContent(json.Serialize(value))};
  }
 }
}
