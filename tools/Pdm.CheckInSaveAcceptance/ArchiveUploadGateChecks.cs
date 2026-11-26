using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 const string LaterPropertyMemoryName="后读取触发的当前内存属性";
 const string LaterPropertyMemoryPayload="current child contents after owner property read";

 static async Task ArchiveUploadGateScenario(Form host,string output,bool memoryOnly=false,string caseName=null)
 {
  output=Path.Combine(Path.GetFullPath(output),Guid.NewGuid().ToString("N"));
  var modes=new[]{"early-other-editor","early-missing-file","early-review-locked","confirm","long-upload-staging-name","cancel","recheck","changed-during-preflight","server-denied-late","missing-references","missing-references-cache","strict-missing-references","payload-too-large","background-upload","cancel-upload","upload-failure","cycle-after-initial-check","pending-model-saves-during-history","pending-model-save-from-later-properties","changed-after-commit","cancel-after-commit","chunked-final-sync","memory-snapshot","memory-late-properties","memory-during-index","memory-copy-recheck"};
  if(caseName!=null&&!modes.Contains(caseName,StringComparer.Ordinal))throw new ArgumentException("未知存档回归场景："+caseName);
  foreach(var mode in modes)
  {
   var pendingFromLaterProperties=mode=="pending-model-save-from-later-properties";
   var pendingModelSaves=pendingFromLaterProperties||mode=="pending-model-saves-during-history";
   if(caseName!=null?mode!=caseName:memoryOnly&&!mode.StartsWith("memory-")&&!pendingModelSaves)continue;
   var longStagingName=mode=="long-upload-staging-name";
   var shortJournal=Path.Combine(Path.GetTempPath(),"pdm-gate-"+Guid.NewGuid().ToString("N"));
   var folder=longStagingName?Path.Combine(shortJournal,"V"):
    Path.Combine(Path.GetFullPath(output),pendingFromLaterProperties?"later-properties":mode,"View");
   Directory.CreateDirectory(folder);
   var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
   var batchFolder=Type("ArchiveBatchJournal").GetField("directory",Flags);var oldBatch=batchFolder.GetValue(null);
   var uploadFolder=Type("ArchiveUploadJournal").GetField("directory",Flags);var oldUpload=uploadFolder?.GetValue(null);
   var workspaceRoot=longStagingName?Path.Combine(shortJournal,"controlled"):Path.GetDirectoryName(folder);
   bindings.SetValue(null,Path.Combine(folder,"bindings"));resolver.SetValue(null,new Func<string>(()=>workspaceRoot));batchFolder.SetValue(null,Path.Combine(folder,"batch-journal"));uploadFolder?.SetValue(null,shortJournal);
   object session=null,plugin=null;
   try
   {
    plugin=New("PdmAddin");var project=Guid.NewGuid();var checkout=(Guid)Field(plugin,"checkoutSessionId");
    var warningMode=mode.Contains("missing-references");var memoryMode=mode.StartsWith("memory-");
    var assemblyRoot=warningMode||memoryMode||pendingModelSaves;
    var childFolder=pendingFromLaterProperties?Path.Combine(folder,"controlled-child"):folder;
    if(pendingFromLaterProperties)
    {
     Directory.CreateDirectory(childFolder);
     resolver.SetValue(null,new Func<string>(()=>childFolder));
    }
    var childName=pendingModelSaves?"SOURCE-B.SLDASM":"SOURCE-B.SLDPRT";
    if(longStagingName)
    {
     const string extension=".SLDPRT";
     var compactDirectory=Path.Combine(Path.GetTempPath(),"UPTON-PDM","checkin","c-"+new string('0',32));
     childName=new string('长',259-compactDirectory.Length-extension.Length)+extension;
     if(Path.Combine(compactDirectory,childName).Length!=260||Path.Combine(childFolder,childName).Length>=260)
      throw new Exception("用例必须使源路径可读，而保留原名的最短临时路径超出长度上限");
    }
    var nodes=new[]{LinkedNode(folder,assemblyRoot?"SOURCE-A.SLDASM":"SOURCE-A.SLDPRT",assemblyRoot?"Assembly":"Part"),
     LinkedNode(childFolder,childName,pendingModelSaves?"Assembly":"Part")};
    if(mode=="chunked-final-sync")nodes=nodes.Concat(Enumerable.Range(2,15).Select(i=>LinkedNode(folder,"SOURCE-"+i+".SLDPRT","Part"))).ToArray();
    var absentPaths=Enumerable.Range(0,3).Select(i=>Path.Combine(folder,"old-user","MISSING-"+i+".SLDPRT")).ToArray();
    if(mode=="missing-references-cache")absentPaths=Enumerable.Range(0,3).Select(i=>Path.Combine(Path.GetTempPath(),"swx-fixture-"+Guid.NewGuid().ToString("N"),i==2?"VC~~":"IC~~","MISSING-"+i+".stp.SLDASM")).ToArray();
    var versions=new Dictionary<Guid,object>();
    foreach(var node in nodes)
    {
     Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),memoryMode||pendingModelSaves?"Component":"ExternalFileDependency"));Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",checkout);Set(node,"DrawingVersion","V1");Set(node,"Revision","A");Set(node,"OpenedRevision","A");
     Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"NativeFileScopeVerified",true);Set(node,"LatestStructureIndexPending",false);
     var snapshot=LinkedSnapshot(node);Set(snapshot,"Role",memoryMode||pendingModelSaves?0:1);var version=LinkedVersion(node,snapshot);Set(version,"DrawingVersion","V1");Set(version,"StorageRelativePath","qa/"+Get(node,"FileName"));Set(version,"PropertySnapshot",new Dictionary<string,string>());versions[(Guid)Get(node,"DocumentId")]=version;
     Set(node,"LatestStoredSha256",Get(version,"Sha256"));Set(node,"LatestVersionSha256",Get(version,"Sha256"));Set(node,"OpenedVersionId",Get(version,"Id"));
     if(memoryMode||pendingModelSaves||mode=="confirm")Set(version,"PropertySnapshot",new Dictionary<string,string>{{"SourceFileSha256",(string)Get(version,"Sha256")}});
     var bindingRecorded=(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{Get(node,"FullPath"),Get(node,"DocumentId"),project,Get(version,"Id"),"A",Get(version,"Sha256"),Get(version,"FileLength")});
     if(pendingFromLaterProperties&&ReferenceEquals(node,nodes[1])&&!bindingRecorded)
      throw new Exception("未能建立可写受控子装配体的版本基线");
     File.AppendAllText((string)Get(node,"FullPath")," changed saved source");
    }
    foreach(var child in nodes.Skip(1))((IList)Get(nodes[0],"Children")).Add(child);archiveRuleVersions=versions.ToDictionary(p=>p.Key,p=>new[]{p.Value});
    var ownerThread=Thread.CurrentThread.ManagedThreadId;
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeWrites=0;
    var memoryEvents=new List<string>();
    var memoryModels=memoryMode||pendingModelSaves?nodes.Select(n=>new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),
     (string)Get(n,"FullPath"),memoryMode,memoryEvents,memoryMode){MemoryPayload="native current state "+Get(n,"FileName")}).ToArray():new Model[0];
    if(mode=="memory-late-properties")foreach(var model in memoryModels){model.Dirty=false;model.OnConfigurationNames=()=>model.Modify(false);}
    if(mode=="memory-during-index")foreach(var model in memoryModels){model.Dirty=false;model.OnPropertyRead=()=>model.Modify(false);}
    var diskBefore=nodes.ToDictionary(n=>(string)Get(n,"FullPath"),n=>File.ReadAllBytes((string)Get(n,"FullPath")));
    var attributesBefore=pendingFromLaterProperties?diskBefore.Keys.ToDictionary(path=>path,File.GetAttributes):null;
    if(memoryMode)foreach(var node in nodes)File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.ReadOnly);
    SetField(plugin,"application",ArchiveNativeMock(appType,call=>{
     if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new Exception("后台上传访问了 SolidWorks 接口 "+call.MethodName);
     if(call.MethodName=="GetDocuments")return memoryModels.Select(m=>m.GetTransparentProxy()).ToArray();if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetUserPreferenceStringValue")return "";
     if(call.MethodName=="GetDocumentDependencies2")return warningMode&&(string)call.Args[0]==(string)Get(nodes[0],"FullPath")?absentPaths.SelectMany(path=>new[]{Path.GetFileName(path),path}).ToArray():new string[0];
     if((warningMode||memoryMode||pendingFromLaterProperties)&&call.MethodName=="GetCurrentWorkingDirectory")return folder;
     if(call.MethodName=="GetOpenDocumentByName")return null;
     if(call.MethodName=="GetDocumentVisible")return true;
     if(call.MethodName=="DocumentVisible")return null;
     // The saved fixture bytes are deliberately not SolidWorks files. The native
     // reader may fail to load the file for properties, but never writes it.
     if(call.MethodName=="OpenDoc6")return null;
     if(warningMode&&call.MethodName=="SetCurrentWorkingDirectory")return true;
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
     if(mode=="upload-failure")Type("PdmApiClient").GetField("archiveRecoveryWindow",Flags)?.SetValue(client,TimeSpan.Zero);
     SetField(plugin,"editJournalScope","archive-gate-fixture|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"edit-journal"),"archive-gate-fixture|tester"));
     if(warningMode)
     {
      foreach(var path in absentPaths){var missing=New("CadTreeNode");Set(missing,"FullPath",path);Set(missing,"FileName",Path.GetFileName(path));Set(missing,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(missing,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));((IList)Get(nodes[0],"Children")).Add(missing);}
      var policy=Type("ArchiveMissingReferencePolicy");policy.GetProperty("BlockMissingReferences",Flags).SetValue(null,mode=="strict-missing-references");
      var selections=Array.CreateInstance(Type("CadTreeNode"),2);selections.SetValue(nodes[0],0);selections.SetValue(nodes[1],1);
      Exception rejected=null;try{policy.GetMethod("Prepare",Flags).Invoke(null,new object[]{nodes[0],selections});}catch(TargetInvocationException error){rejected=error.InnerException;}
      if(mode=="strict-missing-references")
      {
       if(rejected==null||!rejected.Message.Contains("已启用")||handler.Uploads!=0||handler.Commits!=0||nativeWrites!=0)throw new Exception("严格选项未在昂贵读取和上传前拒绝缺失引用",rejected);
       Console.WriteLine("PASS: configured missing references block before native reads/uploads/commits");continue;
      }
      if(rejected!=null||((IList)Get(nodes[0],"ReferenceWarnings")).Count!=3)throw new Exception("默认缺失引用未转为可持久化提示",rejected);
      var inventory=nodes.Select(n=>(string)Get(n,"FullPath")).Concat(absentPaths).ToArray();
      Type("NativeArchiveManifest").GetMethod("ConfirmFileScope",Flags).Invoke(null,new object[]{nodes[0],inventory});
      Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{nodes[0],inventory});
      Console.WriteLine("PASS: three missing unselected parts pass native scope validation without files or invented identities");
      var recovered=(Task)TreeCall(plugin,"RecoverPdmReferencesAsync",LinkedItems(nodes),project,CancellationToken.None);await recovered;
      TreeCall(plugin,"ValidatePdmNativeDependencyPaths",LinkedItems(nodes),CancellationToken.None);
      if(((IEnumerable)Get(recovered,"Result")).Cast<object>().Count()!=2||((IList)Get(nodes[0],"ReferenceWarnings")).Count!=3)
       throw new Exception("恢复或提前引用检查重新扩大了范围、清除了提示或再次拦截");
      Console.WriteLine("PASS: reference recovery and early dependency checks preserve warnings and exact checked scope");
     }
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
     var cycleInjected=0;
     var pendingModelSavesInjectedAt=0;
     var earlierModelInspected=false;
     if(pendingFromLaterProperties)memoryModels[0].OnPropertyRead=()=>{
      // The child is inspected first. Reading the owner's properties can dirty it.
      earlierModelInspected=Get(nodes[1],"ArchiveInspectedProperties")!=null;
      pendingModelSavesInjectedAt=Convert.ToInt32(Get(session,"DisplayStep"));
      memoryModels[1].MemoryPayload=LaterPropertyMemoryPayload;
      memoryModels[1].MemoryProperties["物料名称"]=LaterPropertyMemoryName;
      memoryModels[1].Modify(false);
     };
     handler.OnHistoryRead=()=>{
      if(Convert.ToInt32(Get(session,"DisplayStep"))!=6)return;
      if(pendingModelSaves&&!pendingFromLaterProperties&&Interlocked.CompareExchange(ref pendingModelSavesInjectedAt,6,0)==0)
       foreach(var model in memoryModels)model.Dirty=true;
      if(mode!="cycle-after-initial-check"||Interlocked.CompareExchange(ref cycleInjected,1,0)!=0)return;
      var back=New("CadTreeNode");foreach(var name in new[]{"DocumentId","FullPath","FileName","DisplayName","Kind","Revision","CurrentRevision","InstancePath"})Set(back,name,Get(nodes[0],name));
      Set(back,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(back,"ReferenceTypeVerified",true);Set(back,"NativeFileScopeVerified",true);((IList)Get(nodes[1],"Children")).Add(back);
     };
     var priorContext=SynchronizationContext.Current;
     var uploadContext=new ArchiveUploadHoldContext(priorContext);
     handler.OwnerThread=ownerThread;
     handler.OnUploadStart=()=>
     {
      if(mode=="background-upload")uploadContext.Hold=true;
      if(!longStagingName||handler.Uploads!=1)return;
      var leaseType=Type("ArchiveTemporaryCopyLease");
      var active=(IDictionary)leaseType.GetField("Active",Flags).GetValue(null);
      var aliasPath=active.Keys.Cast<string>().Where(path=>Path.GetFileName(path).StartsWith("c-",StringComparison.Ordinal))
       .Select(path=>Path.Combine(path,"upload.SLDPRT")).SingleOrDefault(File.Exists);
      if(aliasPath==null||!File.ReadAllBytes(aliasPath).SequenceEqual(diskBefore[(string)Get(nodes[1],"FullPath")]))
       throw new Exception("未在持有租约的短路径中生成用于上传的原始字节副本");
     };
     handler.OnUploadCompleted=()=>{
      if(mode=="background-upload"&&handler.CompletedUploads==2)uploadContext.Release();
      if(mode=="cancel-upload")TreeCall(session,"RequestCancellation");
     };
     Exception failure=null;object result=null;
     var copyPreparationReports=0;
     var localPreflightStarts=0;
     try{
      Task task;
      SynchronizationContext.SetSynchronizationContext(uploadContext);
      Action<int,int,string,string> syncProgress=mode=="cancel-after-commit" ? (Action<int,int,string,string>)((done,total,name,status)=>{if(Convert.ToInt32(Get(session,"DisplayStep"))==10)TreeCall(session,"RequestCancellation");}) : null;
      if(pendingModelSaves)syncProgress=(done,total,name,status)=>{
       if(status.Contains("正在核对副本准备方式"))copyPreparationReports++;
       if(done==0&&status.Contains("正在核对文件、引用结构和属性"))localPreflightStarts++;
      };
      if(mode=="memory-copy-recheck") { var injected=false; syncProgress=(done,total,name,status)=>{if(!injected && status.Contains("正在核对副本准备方式")){injected=true;foreach(var model in memoryModels)model.Modify();}}; }
      try{task=(Task)Type("PdmAddin").GetMethod("CheckInBatchAsync",Flags).Invoke(plugin,new object[]{LinkedItems(nodes),project,"isolated automatic archive test",syncProgress,(CancellationToken)Get(session,"Token"),null,null,null,null,false,null});}
      finally{SynchronizationContext.SetSynchronizationContext(priorContext);}
      if(await Task.WhenAny(task,Task.Delay(15000))!=task){uploadContext.Release();TreeCall(session,"RequestCancellation");throw new Exception("上传依赖了主线程逐文件回调，或出现额外人工等待");}
      await task;result=Get(task,"Result");
     }catch(Exception error){failure=error;}finally{uploadContext.Release();driverStop.Cancel();}
     var succeeds=mode=="confirm"||longStagingName||mode=="recheck"||mode=="missing-references"||mode=="missing-references-cache"||mode=="background-upload"||mode=="changed-after-commit"||mode=="cancel-after-commit"||mode=="chunked-final-sync"||memoryMode||pendingFromLaterProperties;
     if(succeeds&&(failure!=null||result==null))throw new Exception("正常确认存档验证失败 "+mode,failure);
     if(!succeeds&&failure==null)throw new Exception("应停止的批次未停止 "+mode);
     if(mode=="payload-too-large"&&(failure==null||!failure.ToString().Contains("本次尚未上传文件")||handler.Uploads!=0||handler.Commits!=0))throw new Exception("提交容量超限未在上传前停止",failure);
     if(mode=="cycle-after-initial-check"&&(cycleInjected!=1||failure==null||!failure.ToString().Contains("最终提交快照")))throw new Exception("未在冻结最终快照后阻止新增的循环",failure);
     if(pendingModelSaves)
     {
      var sourceChanged=diskBefore.Any(source=>!source.Value.SequenceEqual(File.ReadAllBytes(source.Key)));
      if(pendingFromLaterProperties)
      {
       var lateBytes=Encoding.UTF8.GetBytes(LaterPropertyMemoryPayload);
       if(pendingModelSavesInjectedAt!=4||!earlierModelInspected||localPreflightStarts!=2
          ||copyPreparationReports!=2||handler.Preflights!=2||handler.CompletedUploads!=2
          ||!memoryEvents.SequenceEqual(new[]{"copy:SOURCE-B.SLDASM"})||sourceChanged
          ||attributesBefore.Any(source=>File.GetAttributes(source.Key)!=source.Value)
          ||memoryModels.Any(model=>model.ReadOnly)||memoryModels[0].Dirty||!memoryModels[1].Dirty)
        throw new Exception("阶段4中新出现的待保存状态没有只重试一次并只生成子装配体副本，或原图档发生变化");
       if(handler.UploadedChunks.Count!=2||!handler.UploadedChunks.Any(chunk=>chunk.SequenceEqual(lateBytes))
          ||!handler.UploadedChunks.Any(chunk=>chunk.SequenceEqual(diskBefore[memoryModels[0].Path])))
        throw new Exception("上传内容不是重新固定的子装配体和原始已保存的总装");
       SavedDrawingPreflightCacheScenario(folder);
      }
      else if(pendingModelSavesInjectedAt!=6||Convert.ToInt32(Get(session,"DisplayStep"))!=6
         ||copyPreparationReports!=0||handler.Preflights!=0||memoryEvents.Count!=0||sourceChanged
         ||!failure.Message.Contains("尚未上传")||!failure.Message.Contains("检查期间模型变为未保存状态")
         ||nodes.Any(n=>!failure.Message.Contains((string)Get(n,"FileName")))||memoryModels.Any(model=>!model.Dirty))
       throw new Exception("检查期间出现的待保存状态未在阶段6汇总拦截，或原图档发生变化",failure);
     }
     var expectedUploads=succeeds||mode=="upload-failure"?nodes.Length:mode=="cancel-upload"?1:0;
     if(handler.Uploads!=expectedUploads||handler.Commits!=(succeeds?1:0)||nativeWrites!=0)throw new Exception("上传边界、整批提交或原生调用异常 "+mode+" "+handler.Uploads+"/"+handler.Commits+" native="+nativeWrites,failure);
     if(mode=="recheck"&&(!rechecked||handler.Preflights!=2))throw new Exception("可选检查或每个提交文件的一次最终服务器预检未执行，或出现重复预检");
     if(succeeds&&(int)Get(result,"CreatedVersions")!=nodes.Length)throw new Exception("未得到全部所选图档的实际服务端回执");
     if(longStagingName)
     {
      if(handler.UploadedChunks.Count!=nodes.Length||diskBefore.Any(source=>
        !source.Value.SequenceEqual(File.ReadAllBytes(source.Key))||!handler.UploadedChunks.Any(chunk=>chunk.SequenceEqual(source.Value))))
       throw new Exception("长文件名图档的源文件或实际上传内容发生变化");
      Console.WriteLine("PASS: leased short staging name preserves the original uploaded filename and bytes");
     }
     if(mode=="chunked-final-sync"&&(nodes.Any(n=>(string)Get(n,"DrawingVersion")!="V2"||!string.IsNullOrWhiteSpace((string)Get(n,"CheckedOutBy")))
        || Get(nodes.Last(),"WorkState").ToString()!="PendingCheckIn"||!File.ReadAllText((string)Get(nodes.Last(),"FullPath")).EndsWith(" later-after-commit")
        || ((IEnumerable)Get(result,"BomWarnings")).Cast<string>().Any(w=>w.Contains("本机状态同步失败"))))
         throw new Exception("跨16个文件分组的回执状态或末组后续修改未正确保留");
     if(mode=="changed-after-commit" && (!File.ReadAllText((string)Get(nodes[0],"FullPath")).EndsWith(" later-after-commit")
        || Get(nodes[0],"WorkState").ToString()!="PendingCheckIn" || !((IEnumerable)Get(result,"BomWarnings")).Cast<string>().Any(w=>w.Contains("之后的本地修改"))))
         throw new Exception("服务器提交后的新增修改未保留为待提交状态");
     if(memoryMode)
     {
      if(memoryEvents.Count!=(mode=="memory-copy-recheck"?4:2)||memoryEvents.Any(e=>!e.StartsWith("copy:"))||memoryModels.Any(m=>!m.Dirty)
        ||nodes.Any(n=>!diskBefore[(string)Get(n,"FullPath")].SequenceEqual(File.ReadAllBytes((string)Get(n,"FullPath")))||(File.GetAttributes((string)Get(n,"FullPath"))&FileAttributes.ReadOnly)==0))
         throw new Exception("整批内存副本提交改变了源文件、权限或原待保存标记");
      if(((IEnumerable)Get(result,"BomWarnings")).Cast<string>().Any(w=>w.Contains("本机状态同步失败")||w.Contains("之后的本地修改")))throw new Exception("已提交内存副本被误判为未提交修改");
      Console.WriteLine("PASS: current memory payloads commit atomically, sources stay readonly and unchanged, source hashes are not mislabeled as new content");
     }
     if(mode=="missing-references"||mode=="missing-references-cache")
     {
      if(absentPaths.Any(File.Exists)||!handler.WarningSnapshotCommitted||((IEnumerable)Get(nodes[0],"ReferenceWarnings")).Cast<string>().Count(w=>absentPaths.Any(path=>w.Contains(Path.GetFileName(path))))!=3)throw new Exception("缺失提示未随快照提交，或缺失文件被制造/范围被扩大");
      Console.WriteLine("PASS: missing-reference notices persist in committed snapshot; only two checked files upload and commit");
     }
     Console.WriteLine("PASS: real batch pipeline "+mode+"; one initial confirm, no later operator action; uploads="+handler.Uploads+", commits="+handler.Commits+", native mutations=0");
    }
   }
   finally
   {
    if(session!=null){ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
    if(plugin!=null)TreeCall(plugin,"PruneArchiveMemoryCopies",true);
    bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);batchFolder.SetValue(null,oldBatch);uploadFolder?.SetValue(null,oldUpload);
    Type("ArchiveMissingReferencePolicy").GetProperty("BlockMissingReferences",Flags).SetValue(null,false);
    var tempPrefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(Directory.Exists(shortJournal)&&Path.GetFullPath(shortJournal).StartsWith(tempPrefix,StringComparison.OrdinalIgnoreCase))Directory.Delete(shortJournal,true);
   }
  }
 }

 sealed class ArchiveUploadHoldContext:SynchronizationContext
 {
  readonly SynchronizationContext target;readonly Queue<Action> pending=new Queue<Action>();
  public volatile bool Hold;
  public ArchiveUploadHoldContext(SynchronizationContext target){this.target=target;}
  public override void Post(SendOrPostCallback callback,object state)
  {
   Action invoke=()=>target.Post(_=>{var old=Current;SetSynchronizationContext(this);try{callback(state);}finally{SetSynchronizationContext(old);}},null);
   lock(pending){if(Hold){pending.Enqueue(invoke);return;}}invoke();
  }
  public void Release(){lock(pending){Hold=false;while(pending.Count>0)pending.Dequeue()();}}
 }

 sealed class ArchiveGateHandler:HttpMessageHandler
 {
  readonly object[] nodes;readonly Dictionary<Guid,object> versions;readonly Guid project,checkout;readonly string mode;readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,Dictionary<string,object>> transfers=new System.Collections.Concurrent.ConcurrentDictionary<Guid,Dictionary<string,object>>();
  public readonly List<byte[]> UploadedChunks=new List<byte[]>();
  public volatile bool Confirmed;public int Uploads,Commits,Preflights,CompletedUploads,OwnerThread;public Action OnPreflight,OnUploadStart,OnUploadCompleted,OnHistoryRead;public bool WarningSnapshotCommitted;
  public ArchiveGateHandler(object[] nodes,Dictionary<Guid,object> versions,Guid project,Guid checkout,string mode){this.nodes=nodes;this.versions=versions;this.project=project;this.checkout=checkout;this.mode=mode;}
  object Document(object node,bool committed=false)=>new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),kind=Convert.ToInt32(Get(node,"Kind")),checkedOutBy=committed?null:mode=="early-other-editor"?"other-editor":"tester",checkoutSessionId=committed?(Guid?)null:checkout,drawingReviewLocked=mode=="early-review-locked",drawingVersion=committed?"V2":"V1",publishedVersion="未发布",revision=new{display=committed?"W2":"A"}};
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var route=request.RequestUri.AbsolutePath;var json=new JavaScriptSerializer();object value;var status=HttpStatusCode.OK;
   if(route.EndsWith("/archive-batch-limits"))value=new{maxRequestBytes=mode=="payload-too-large"?1024L:256L*1024*1024};
   else if(route.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(route.EndsWith("/archive-warning-policy"))value=new{revision=0,blockAll=false,blockingCodes=new string[0]};
   else if(route.EndsWith("/archive-capabilities"))value=new{referenceWarnings=true,contextReferences=true,unverifiedReferences=true};
   else if(route.EndsWith("/version-verifications")){var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());value=((IEnumerable)payload["Documents"]).Cast<Dictionary<string,object>>().Select(row=>{var id=Guid.Parse((string)row["DocumentId"]);return new{documentId=id,accessible=true,latest=new{id=Get(versions[id],"Id")}};}).ToArray();}
   else if(route.EndsWith("/documents"))value=nodes.Select(n=>Document(n)).ToArray();
   else if(request.Method==HttpMethod.Get && route.StartsWith("/api/documents/",StringComparison.Ordinal) && Guid.TryParse(route.Substring("/api/documents/".Length),out var documentId))value=Document(nodes.Single(n=>(Guid)Get(n,"DocumentId")==documentId));
   else if(route.EndsWith("/reference-tree")){status=HttpStatusCode.NotFound;value=new{message="无根结构"};}
   else if(route.EndsWith("/versions")){OnHistoryRead?.Invoke();var id=versions.Keys.Single(k=>route.Contains(k.ToString()));value=new[]{versions[id]};}
   else if(route.EndsWith("/checkin/preflight")){Preflights++;OnPreflight?.Invoke();if(mode=="server-denied-late"&&Preflights==2){status=HttpStatusCode.Conflict;value=new{message="权限在准备后发生变化，已停止上传"};}else value=new{};}
   else if(route.EndsWith("/uploads/sessions"))
   {
    if(Thread.CurrentThread.ManagedThreadId==OwnerThread)throw new Exception("上传仍在 SolidWorks 主线程启动");
    var configured=Environment.GetEnvironmentVariable("UPLM_ARCHIVE_UPLOAD_CONCURRENCY");var limit=int.TryParse(configured,out var n)?Math.Max(1,Math.Min(4,n)):2;
    if(!Confirmed)throw new Exception("未确认即开始上传");if(Interlocked.Increment(ref Uploads)-Volatile.Read(ref CompletedUploads)>limit)throw new Exception("上传超出并发上限");OnUploadStart?.Invoke();
    var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());var id=Guid.NewGuid();transfers[id]=payload;
    if(mode=="long-upload-staging-name")
    {
     var uploadDocumentId=Guid.Parse((string)payload["documentId"]);
     var original=nodes.Single(node=>(Guid)Get(node,"DocumentId")==uploadDocumentId);
     if((string)payload["fileName"]!=(string)Get(original,"FileName"))
      throw new Exception("创建上传会话使用了本机临时名称，原图档名称未提交");
    }
    value=new{id,projectId=project,totalLength=payload["totalLength"],expectedSha256=payload["sha256"],receivedLength=0,chunkSize=65536,expiresAt=DateTime.UtcNow.AddHours(1),targetRelativePath=mode=="long-upload-staging-name"?string.Empty:"qa/"+id};
   }
   else if(route.Contains("/chunks/"))
   {
    if(mode=="pending-model-save-from-later-properties"||mode=="long-upload-staging-name")
     UploadedChunks.Add(await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    value=new{};
   }
   else if(route.EndsWith("/complete")){
    await Task.Delay(5).ConfigureAwait(false);
    if(mode=="upload-failure"&&Uploads==2)throw new HttpRequestException("fixture upload failure");
    var pair=transfers.Single(t=>route.Contains(t.Key.ToString()));
    var relativePath="qa/"+pair.Key;
    if(mode=="long-upload-staging-name")
    {
     var payload=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());
     relativePath=(string)payload["relativeTargetPath"];
     if(Path.GetFileName(relativePath)!=(string)pair.Value["fileName"])
      throw new Exception("服务端未提供路径时，提交目标未沿用原图档名称");
    }
    value=new{relativePath,length=pair.Value["totalLength"],sha256=pair.Value["sha256"]};Interlocked.Increment(ref CompletedUploads);OnUploadCompleted?.Invoke();
   }
   else if(route.EndsWith("/archive-batches"))
   {
    if(!Confirmed||CompletedUploads!=nodes.Length)throw new Exception("未确认或暂存不完整即提交");Commits++;var body=await request.Content.ReadAsStringAsync();var missingExtension=mode=="missing-references-cache"?".stp.SLDASM":".SLDPRT";WarningSnapshotCommitted=Enumerable.Range(0,3).All(i=>body.Contains("MISSING-"+i+missingExtension));var payload=json.Deserialize<Dictionary<string,object>>(body);
    var submitted=((IEnumerable)payload["files"]).Cast<Dictionary<string,object>>().ToDictionary(row=>Guid.Parse((string)row["documentId"]),row=>(Dictionary<string,object>)row["request"]);
    if(mode=="long-upload-staging-name")foreach(var node in nodes)
    {
     var row=submitted[(Guid)Get(node,"DocumentId")];
     var properties=(Dictionary<string,object>)row["properties"];
     var originalName=(string)Get(node,"FileName");
     using(var sha=SHA256.Create())
     {
      var sourceSha=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes((string)Get(node,"FullPath")))).Replace("-","");
      if((string)row["fileName"]!=originalName||(string)properties["FileName"]!=originalName||Path.GetFileName((string)row["storageRelativePath"])!=originalName
        ||(string)row["sha256"]!=sourceSha||(string)properties["SourceFileSha256"]!=sourceSha)
       throw new Exception("整批提交未保留原图档名称、内容或源文件校验值");
     }
    }
    if(mode=="confirm")foreach(var item in submitted)
    {
     var properties=(Dictionary<string,object>)item.Value["properties"];
     if((string)properties["SourceFileSha256"]!=(string)item.Value["sha256"]
      ||(string)properties["SourceFileSha256"]==(string)Get(versions[item.Key],"Sha256"))
      throw new Exception("变更的3D文件提交时，旧版本属性覆盖了本次源文件哈希");
    }
    if(mode=="pending-model-save-from-later-properties")
    {
     string expectedHash;
     using(var sha=SHA256.Create())expectedHash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(LaterPropertyMemoryPayload))).Replace("-","");
     var row=submitted[(Guid)Get(nodes[1],"DocumentId")];
     var properties=(Dictionary<string,object>)row["properties"];
     if(!string.Equals((string)row["sha256"],expectedHash,StringComparison.OrdinalIgnoreCase)
        ||!string.Equals((string)properties["SourceFileSha256"],expectedHash,StringComparison.OrdinalIgnoreCase)
        ||(string)properties["全局/物料名称"]!=LaterPropertyMemoryName)
      throw new Exception("子装配体版本仍使用检查期间变化之前的内容或属性");
    }
    if(mode.StartsWith("memory-"))foreach(var row in submitted.Values){var props=(Dictionary<string,object>)row["properties"];if((string)props["SourceFileSha256"]!=(string)row["sha256"])throw new Exception("旧源文件被误标为已提交的当前内容");if((string)props["全局/物料名称"]!="当前内存属性"||(string)props["UPLM.CustomPropertiesRead"]!="1")throw new Exception("未保存的内存属性未随当前副本完整提交");}
    value=new{batchId=payload["batchId"],results=nodes.Select(n=>{var requestData=submitted[(Guid)Get(n,"DocumentId")];return new{document=Document(n,true),version=new{id=Guid.NewGuid(),drawingVersion="V2",revision=new{display="W2"},sha256=requestData["sha256"],fileLength=requestData["fileLength"],storageRelativePath=requestData["storageRelativePath"],propertySnapshot=requestData["properties"]},versionCreated=true};}).ToArray()};
    if(mode=="changed-after-commit")File.AppendAllText((string)Get(nodes[0],"FullPath")," later-after-commit");
    if(mode=="chunked-final-sync")File.AppendAllText((string)Get(nodes.Last(),"FullPath")," later-after-commit");
   }
   else throw new Exception("Unexpected gate API request "+route);
   return new HttpResponseMessage(status){Content=new StringContent(json.Serialize(value))};
  }
 }
}
