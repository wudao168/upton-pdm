using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
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
 static async Task WorkspaceCacheRecoveryScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);
  foreach(var mode in new[]{"old-writable","old-readonly","current-cache","modified","receipt","receipt-other-company","receipt-later-edit","receipt-pending-edit",
   "backup","backup-later-edit","backup-tampered","backup-manifest-changed","late-disk-edit","late-open","late-new-file","binding-write-failed","retained-edit"})
  using(var f=new WorkspaceRecoveryFixture(output,mode=="retained-edit"||mode=="current-cache"))
  {
   void Check(bool value,string message){TreeCheck(value,message);Console.WriteLine("PASS: "+mode+"："+message);}
   var beforePart=WorkingReuseHash(f.PartPath);var expected=(string)Get(f.RootFile,"Sha256");
   var modified=new[]{"modified","receipt","receipt-other-company","receipt-later-edit","receipt-pending-edit","backup","backup-later-edit","backup-tampered","backup-manifest-changed","retained-edit"}.Contains(mode);
   if(modified)File.AppendAllText(f.Path," local saved modification");
   if(mode=="old-readonly")File.SetAttributes(f.Path,FileAttributes.ReadOnly);
   if(mode.StartsWith("receipt"))
   {
    var version=f.Version();
    Check((bool)TreeCallStatic("ConfirmedArchiveSource","Record",f.Root,f.Scope,f.Path,f.Id,f.Project,version,WorkingReuseHash(f.Path)),"已提交源内容与服务器版本分开记录");
    if(mode=="receipt-other-company")TreeCall(f.Client,"SetActiveCompany",Guid.NewGuid());
    if(mode=="receipt-later-edit")File.AppendAllText(f.Path," after archive");
    if(mode=="receipt-pending-edit")Set(f.Manager,"PendingLocalEdits",new[]{f.Id});
   }
   if(mode=="retained-edit")((IDictionary)Get(f.Manifest,"RetainedWorkingHashes")).Add(f.Id,WorkingReuseHash(f.Path));
   var before=WorkingReuseHash(f.Path);var oldBinding=f.Binding();
   var originalBytes=File.ReadAllBytes(f.Path);
   if(mode=="late-disk-edit")f.Handler.DuringDownload=()=>File.AppendAllText(f.Path," late edit");
   if(mode=="late-open")Set(f.Manager,"RefreshProtectedPaths",new Func<Task<IReadOnlyCollection<string>>>(()=>Task.FromResult<IReadOnlyCollection<string>>(new[]{f.Path})));
   if(mode=="late-new-file"){File.Delete(f.Path);f.Handler.DuringDownload=()=>File.WriteAllText(f.Path,"new local file");}
   if(mode=="binding-write-failed")f.Handler.DuringDownload=()=>{var blocker=System.IO.Path.Combine(f.Root,"binding-blocker");File.WriteAllText(blocker,"blocked");SetFieldStatic("PdmDocumentIdentityStore","bindingDirectory",blocker);};
   Exception failure=null;object backup=null;
   try
   {
    if(mode.StartsWith("backup"))
    {
     try{await f.Prepare();throw new Exception("Expected cache conflict");}
     catch(Exception error) when(error.GetType().Name=="WorkspaceCacheConflictException")
     {
      Check(((IEnumerable)Get(error,"Files")).Cast<object>().Single().GetType().GetProperty("FileName").GetValue(((IEnumerable)Get(error,"Files")).Cast<object>().Single()).Equals("ROOT.SLDASM"),"冲突列明实际文件");
      if(mode=="backup-later-edit")File.AppendAllText(f.Path," after confirmation");
      backup=DiscardCall(f.Manager,"BackupWorkingConflicts",f.Manifest,error,CancellationToken.None);
     }
     if(mode=="backup-tampered")File.AppendAllText(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"ROOT.SLDASM"),"tampered");
     if(mode=="backup-manifest-changed")Set(f.RootFile,"VersionId",Guid.NewGuid());
     await (Task)DiscardCall(f.Manager,"PrepareBackedUpWorkingCopyAsync",f.Manifest,new Guid[0],backup,CancellationToken.None);
    }
    else await f.Prepare(mode=="retained-edit"?new[]{f.Id}:new Guid[0]);
   }
   catch(Exception error){failure=error;}
   f.ResetBindings();
   var success=new[]{"old-writable","old-readonly","current-cache","receipt","backup","retained-edit"}.Contains(mode);
   File.WriteAllText(System.IO.Path.Combine(output,mode+".txt"),failure?.ToString()??"success");
   Check((failure==null)==success,success?"按选定版本完成缓存准备":"差异或失败被保护，未盲目覆盖");
   if(success)
   {
    Check(WorkingReuseHash(f.Path)==(mode=="retained-edit"?before:expected),"正确复用、更新或保留本地编辑内容");
    Check(f.Handler.Downloads==(mode=="current-cache"||mode=="retained-edit"?0:1),"只获取需要更新的文件，不整套重复下载");
    if(backup!=null)Check(WorkingReuseHash(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"ROOT.SLDASM"))==before
     &&File.Exists(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"workspace-recovery.json")),"原文件和原版本记录保留在已核验恢复备份中");
    if(mode=="receipt")
    {
     File.SetAttributes(f.Path,FileAttributes.Normal);File.WriteAllBytes(f.Path,originalBytes);
     Check(TreeCall(f.Manager,"FindWorkingConflicts",f.Manifest,new Guid[0])!=null,"同步后的旧回执不能掩盖新的回退修改");
    }
   }
   else
   {
    var preserved=mode=="late-new-file"?File.ReadAllText(f.Path)=="new local file":mode=="late-disk-edit"?File.ReadAllText(f.Path).EndsWith(" late edit")
     :mode=="backup-later-edit"?File.ReadAllText(f.Path).EndsWith(" after confirmation"):WorkingReuseHash(f.Path)==before;
    Check(preserved,"文件、后续修改及原绑定保持可恢复");
    Check(Equals(Get(f.Binding(),"VersionId"),Get(oldBinding,"VersionId")),"失败未把旧内容标记成新版本");
   }
   Check(WorkingReuseHash(f.PartPath)==beforePart&&f.Handler.OtherMutations==0,"未更新的零件与服务器业务数据不受影响");
  }
  await WorkspaceConflictUiScenario(host,output);
  await WorkspaceRecoveryCoordinatorScenario(host,output);
  await WorkspaceArchiveReceiptScenario(output);
 }

 static async Task WorkspaceConflictUiScenario(Form host,string output)
 {
  host.ClientSize=new Size(440,1000);
  using(var f=new WorkspaceRecoveryFixture(output,false))
  using(var pane=(Control)New("PdmTaskPaneControl","工作区冲突隔离验证"))
  {
   host.Controls.Add(pane);pane.CreateControl();File.AppendAllText(f.Path," local edit");
   var conflict=TreeCall(f.Manager,"FindWorkingConflicts",f.Manifest,new Guid[0]);
   foreach(var choice in new[]{"cancel","latest","restore","canceled-token"})
   using(var token=new CancellationTokenSource())
   {
    var before=WorkingReuseHash(f.Path);var forms=Application.OpenForms.Count;
    TreeCall(pane,"SetWorkspaceOperationState",true,"正在核对工作区");
    var task=(Task)TreeCall(pane,"ResolveWorkspaceCacheConflictAsync",conflict,choice=="restore",true,"基准不同，暂不能续接",token.Token);
    var workspace=(Control)Field(pane,"projectDocuments");
    await TreeWait(()=>WorkspaceConflictControl(workspace)!=null,"冲突处理页面未显示");
    var content=WorkspaceConflictControl(workspace);var tabs=(TabControl)Field(pane,"tabs");
    TreeCheck(tabs.SelectedTab.Text=="工作区"&&content.Enabled&&Application.OpenForms.Count==forms,"冲突在工作区内显示且可以操作，不新增窗口");
    Console.WriteLine("PASS: conflict-ui "+choice+"：工作区列表和按钮可用，没有额外弹窗");
    var grid=(DataGridView)Field(content,"files");TreeCheck(grid.Rows.Count==1&&grid.Columns.Count==3,"冲突文件和版本未列明");
    if(choice=="latest")
    {
     host.ClientSize=new Size(300,900);await Task.Delay(20);
     using(var bitmap=new Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(System.IO.Path.Combine(output,"conflict-narrow.png"));}
    }
    if(choice=="canceled-token")token.Cancel();else ((Button)Field(content,choice=="latest"?"latest":choice=="restore"?"restore":"cancel")).PerformClick();
    try{await task;TreeCheck(Get(task,"Result").ToString()==(choice=="latest"?"BackupAndGetLatest":choice=="restore"?"RestoreEditing":"Cancel"),"冲突按钮结果错误");}
    catch(OperationCanceledException){if(choice!="canceled-token")throw;}
    TreeCall(pane,"SetWorkspaceOperationState",false,"");
    TreeCheck(WorkspaceConflictControl(workspace)==null&&WorkingReuseHash(f.Path)==before,"选择页面关闭时误改文件");
    Console.WriteLine("PASS: conflict-ui "+choice+"：退出后页面和按钮状态恢复，文件不变");
   }
  }
 }

 static Control WorkspaceConflictControl(Control parent)
 {
  if(parent.GetType().Name=="WorkspaceCacheConflictControl")return parent;
  foreach(Control child in parent.Controls){var found=WorkspaceConflictControl(child);if(found!=null)return found;}return null;
 }

 static async Task WorkspaceRecoveryCoordinatorScenario(Form host,string output)
 {
  foreach(var choice in new[]{"cancel","backup","backup-retained","restore","checkout-denied","other-session"})
  using(var f=new WorkspaceRecoveryFixture(output,choice!="cancel"&&choice!="backup"))
  using(var pane=(Control)New("PdmTaskPaneControl","工作区恢复流程隔离验证"))
  using(var lifetime=new CancellationTokenSource())
  {
   host.Controls.Add(pane);pane.CreateControl();File.AppendAllText(f.Path," unsubmitted work");var before=WorkingReuseHash(f.Path);
   var plugin=New("PdmAddin");SetField(plugin,"apiClient",f.Client);SetField(plugin,"controlledWorkspace",f.Manager);SetField(plugin,"taskPaneControl",pane);SetField(plugin,"lifetime",lifetime);SetField(plugin,"authenticatedUsername","tester");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?Array.Empty<object>():throw new Exception("恢复不应开关、保存CAD图档："+call.MethodName)).GetTransparentProxy());
   var journal=New("EditDraftJournal",System.IO.Path.Combine(f.Root,"journal"),"fixture");SetField(plugin,"editJournal",journal);SetField(plugin,"editJournalScope",(string)Get(f.Client,"EditJournalScope")+"|tester");
   string originalDraft=null;
   if(choice.StartsWith("backup"))
   {
    originalDraft=System.IO.Path.Combine(f.Root,"old-draft.SLDASM");File.WriteAllText(originalDraft,"older unsaved recovery content");
    var entry=New("LocalEditDraft");Set(entry,"DocumentId",f.Id);Set(entry,"ProjectId",f.Project);Set(entry,"SessionId",Guid.NewGuid());Set(entry,"SourcePath",f.Path);
    Set(entry,"BaseVersionId",Get(f.Binding(),"VersionId"));Set(entry,"BaseSha256",Get(f.Binding(),"Sha256"));Set(entry,"DraftPath",originalDraft);Set(entry,"DraftSha256",WorkingReuseHash(originalDraft));Set(entry,"RestoreOnOpen",true);Set(entry,"SavedAtUtc",DateTime.UtcNow);
    TreeCall(journal,"Save",entry);((IDictionary)Field(plugin,"localEdits")).Add(f.Id,entry);
   }
   f.Handler.Session=(Guid)Field(plugin,"checkoutSessionId");f.Handler.DenyCheckout=choice=="checkout-denied";f.Handler.ForeignLease=choice=="other-session";
   TreeCall(pane,"SetWorkspaceOperationState",true,"正在核对工作区");
   var editable=new HashSet<Guid>();var created=new List<Guid>();
   if(choice=="backup-retained")
   {
    ((IDictionary)Get(f.Manifest,"RetainedWorkingHashes")).Add(f.Id,before);((ISet<Guid>)Get(f.Manifest,"ResumedDocumentIds")).Add(f.Id);editable.Add(f.Id);
    File.AppendAllText(f.PartPath," another local modification");
   }
   var task=(Task)DiscardCall(plugin,"PrepareWorkspaceWithRecoveryAsync",f.Manifest,editable,created);
   await TreeWait(()=>WorkspaceConflictControl((Control)Field(pane,"projectDocuments"))!=null,"恢复处理页面未显示");
   var content=WorkspaceConflictControl((Control)Field(pane,"projectDocuments"));
   ((Button)Field(content,choice=="cancel"?"cancel":choice.StartsWith("backup")?"latest":"restore")).PerformClick();
   Exception failure=null;try{await task;}catch(Exception error){failure=error;}
   if(choice=="checkout-denied"||choice=="other-session")
    TreeCheck(failure!=null&&WorkingReuseHash(f.Path)==before&&f.Handler.Downloads==0&&((IDictionary)Field(plugin,"localEdits")).Count==0,"恢复越过权限或编辑会话校验");
   else
   {
    if(failure!=null)throw new Exception("coordinator "+choice,failure);
    if(choice=="cancel")TreeCheck(Get(task,"Result")==null&&f.Handler.Downloads==0&&f.Handler.Checkouts==0&&WorkingReuseHash(f.Path)==before,"取消打开误改文件或权限");
    if(choice.StartsWith("backup"))
    {
     var backup=Get(f.Manifest,"CacheBackup");TreeCheck(WorkingReuseHash(f.Path)==Get(f.RootFile,"Sha256").ToString()&&f.Handler.Downloads==(choice=="backup-retained"?2:1)&&f.Handler.Checkouts==0
      &&File.Exists(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"edit-recovery.json"))
      &&WorkingReuseHash(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"ROOT.SLDASM"))==before,"一键备份与读取PLM最新未闭环");
     var entries=((IEnumerable)TreeCall(journal,"ReadDocument",f.Id)).Cast<object>().ToArray();
     TreeCheck(entries.All(entry=>(bool)Get(entry,"Closed")&&!(bool)Get(entry,"RestoreOnOpen"))
      &&File.ReadAllText(originalDraft)=="older unsaved recovery content"
      &&Directory.GetFiles(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),"drafts")).Length==1,"备份后旧恢复记录会再次覆盖新版，或旧草稿未完整保留");
     TreeCheck(((IDictionary)Get(f.Manifest,"RetainedWorkingHashes")).Count==0&&((ISet<Guid>)Get(f.Manifest,"ResumedDocumentIds")).Count==0&&editable.Count==0,"打开PLM最新时仍混入本机保留修改");
    }
    if(choice=="restore")TreeCheck(WorkingReuseHash(f.Path)==before&&f.Handler.Downloads==0&&f.Handler.Checkouts==1&&editable.Contains(f.Id)
     &&((IDictionary)Field(plugin,"localEdits")).Contains(f.Id)&&((IDictionary)Get(f.Manifest,"RetainedWorkingHashes")).Contains(f.Id),"恢复未保留内容、记录或正确编辑权限");
   }
   TreeCall(pane,"SetWorkspaceOperationState",false,"");Console.WriteLine("PASS: coordinator "+choice+"：工作区选择、备份、权限及实际文件更新流程一致");
   host.Controls.Remove(pane);
  }
 }

 static async Task WorkspaceArchiveReceiptScenario(string output)
 {
  foreach(var loaded in new[]{true,false})
  using(var f=new WorkspaceRecoveryFixture(output,false))
  {
   File.AppendAllText(f.Path," submitted source");var sourceHash=WorkingReuseHash(f.Path);var version=f.Version();
   var upload=System.IO.Path.Combine(f.Root,"upload","ROOT.SLDASM");Directory.CreateDirectory(System.IO.Path.GetDirectoryName(upload));File.WriteAllBytes(upload,f.Handler.Bytes[f.Id]);
   var job=Activator.CreateInstance(Type("PdmAddin").GetNestedType("ArchiveLocalSyncJob",Flags));
   foreach(var pair in new Dictionary<string,object>{{"Path",f.Path},{"DocumentId",f.Id},{"ProjectId",f.Project},{"WorkspaceRoot",f.Root},{"ProjectCode","QA-CACHE"},
    {"Version",version},{"UploadCopyPath",upload},{"SourceHash",sourceHash},{"SyncIdentity",true},{"MemorySnapshot",true},{"Scope",Get(f.Client,"EditJournalScope")},{"User","tester"},{"ProtectedCachePaths",loaded?new[]{f.Path}:new string[0]}})SetField(job,pair.Key,pair.Value);
   var result=DiscardCallStatic("PdmAddin","RunArchiveLocalSyncJob",job);
   TreeCheck(WorkingReuseHash(f.Path)==(loaded?sourceHash:(string)Get(version,"Sha256")),"存档后同步处理了正在打开的源文件，或没有更新已关闭缓存");
   if(loaded)
   {
    TreeCheck((string)Get(f.Binding(),"Sha256")!=(string)Get(version,"Sha256"),"旧源文件被冒充新版本");
    await f.Prepare();
    TreeCheck(WorkingReuseHash(f.Path)==(string)Get(version,"Sha256"),"重启后提交回执未能消除旧源缓存误报");
   }
   TreeCheck(Field(result,"JournalError")==null,"回执同步错误");
   Console.WriteLine("PASS: archive-source loaded="+loaded+"：保留打开窗口的源字节，关闭缓存同步版本并在下次打开复用持久回执");
  }
 }

 static object DiscardCallStatic(string type,string method,params object[] args)
 {try{return Type(type).GetMethod(method,Flags).Invoke(null,args);}catch(System.Reflection.TargetInvocationException error){throw error.InnerException??error;}}
 static void SetFieldStatic(string type,string field,object value)=>Type(type).GetField(field,Flags).SetValue(null,value);

 sealed class WorkspaceRecoveryFixture:IDisposable
 {
  public readonly string Root,Path,PartPath,Scope;public readonly Guid Id=Guid.NewGuid(),Project=Guid.NewGuid();
  public readonly object Client,Manager,Manifest,RootFile;public readonly WorkspaceRecoveryHandler Handler;
  readonly object originalBindings,originalRoot,originalScope;readonly System.Reflection.FieldInfo bindingField,rootField,scopeField;
  public WorkspaceRecoveryFixture(string output,bool current,string rootName="ROOT.SLDASM",string partName="UNCHANGED.SLDPRT")
  {
   Root=System.IO.Path.Combine(System.IO.Path.GetFullPath(output),Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(Root);
   var store=Type("PdmDocumentIdentityStore");bindingField=store.GetField("bindingDirectory",Flags);rootField=store.GetField("workspaceRootResolver",Flags);scopeField=store.GetField("isolatedReadScope",Flags);
   originalBindings=bindingField.GetValue(null);originalRoot=rootField.GetValue(null);originalScope=scopeField.GetValue(null);ResetBindings();rootField.SetValue(null,new Func<string>(()=>Root));scopeField.SetValue(null,true);
   Manifest=New("ControlledOpenManifestDto");Set(Manifest,"Id",Guid.NewGuid());Set(Manifest,"ProjectId",Project);Set(Manifest,"ProjectCode","QA-CACHE");Set(Manifest,"RootDocumentId",Id);Set(Manifest,"RootRelativePath",rootName);
   var files=(IList)Activator.CreateInstance(Property(Manifest,"Files").PropertyType);Set(Manifest,"Files",files);
   var old=System.Text.Encoding.UTF8.GetBytes("old archived root");var latest=current?old:System.Text.Encoding.UTF8.GetBytes("latest archived root");
   RootFile=FileItem(rootName,Id,latest);files.Add(RootFile);Set(Manifest,"RootVersionId",Get(RootFile,"VersionId"));Set(Manifest,"RootRevision","W2");
   var part=FileItem(partName,Guid.NewGuid(),System.Text.Encoding.UTF8.GetBytes("unchanged part"));files.Add(part);
   Handler=new WorkspaceRecoveryHandler(Manifest);Handler.Bytes[Id]=latest;Handler.Bytes[(Guid)Get(part,"DocumentId")]=System.Text.Encoding.UTF8.GetBytes("unchanged part");
   Client=New("PdmApiClient","http://workspace-cache.invalid/");((HttpClient)Field(Client,"httpClient")).Dispose();((HttpClient)Field(Client,"controlledOpenHttpClient")).Dispose();
   var http=new HttpClient(Handler){BaseAddress=new Uri("http://workspace-cache.invalid/")};http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(Client,"httpClient",http);SetField(Client,"controlledOpenHttpClient",http);TreeCall(Client,"SetActiveCompany",Guid.NewGuid());
   var login=New("LoginResponseDto");Set(login,"AccessToken","fixture");Set(login,"Username","tester");Set(login,"ExpiresAt",DateTimeOffset.UtcNow.AddHours(8).ToString("o"));TreeCall(Client,"StartRenewableSession",login);
   Scope=Client.GetType().GetProperty("SourceReceiptScope",Flags)==null?(string)Get(Client,"EditJournalScope")+"|tester":(string)Get(Client,"SourceReceiptScope");
   Manager=New("ControlledWorkspaceManager",Client,Root);var directory=(string)TreeCall(Manager,"UseRecoveryWorkingDirectory",Manifest);Directory.CreateDirectory(directory);
   Path=System.IO.Path.Combine(directory,rootName);PartPath=System.IO.Path.Combine(directory,partName);File.WriteAllBytes(Path,old);File.WriteAllBytes(PartPath,Handler.Bytes[(Guid)Get(part,"DocumentId")]);
   store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{Path,Id,Project,current?Get(RootFile,"VersionId"):Guid.NewGuid(),"W1",WorkingReuseHash(Path),(long)old.Length});
   store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{PartPath,Get(part,"DocumentId"),Project,Get(part,"VersionId"),"W1",Get(part,"Sha256"),Get(part,"FileLength")});
  }
  object FileItem(string name,Guid id,byte[] bytes)
  {
   var seed=System.IO.Path.Combine(Root,"seed");File.WriteAllBytes(seed,bytes);var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Revision","W2");Set(file,"Sha256",WorkingReuseHash(seed));Set(file,"FileLength",(long)bytes.Length);File.Delete(seed);return file;
  }
  public object Version(){var version=New("DocumentVersionDto");Set(version,"Id",Get(RootFile,"VersionId"));Set(version,"Sha256",Get(RootFile,"Sha256"));Set(version,"FileLength",Get(RootFile,"FileLength"));var revision=New("RevisionDto");Set(revision,"Display","W2");Set(version,"Revision",revision);return version;}
  public object Binding(){var args=new object[]{Path,null};if(!(bool)Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args))throw new Exception("fixture binding missing");return args[1];}
  public Task Prepare(Guid[] editable=null)=>(Task)DiscardCall(Manager,"PreparePlmWorkingCopyAsync",Manifest,editable??new Guid[0],CancellationToken.None);
  public void ResetBindings()=>bindingField.SetValue(null,System.IO.Path.Combine(Root,"bindings"));
  public void Dispose(){((IDisposable)Client).Dispose();bindingField.SetValue(null,originalBindings);rootField.SetValue(null,originalRoot);scopeField.SetValue(null,originalScope);}
 }

 sealed class WorkspaceRecoveryHandler:HttpMessageHandler
 {
  readonly object manifest;public readonly Dictionary<Guid,byte[]> Bytes=new Dictionary<Guid,byte[]>();public int Downloads,OtherMutations;public Action DuringDownload;
  public Guid Session;public int Checkouts;public bool DenyCheckout,ForeignLease;readonly Dictionary<Guid,object> documents=new Dictionary<Guid,object>();
  public WorkspaceRecoveryHandler(object manifest){this.manifest=manifest;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(request.RequestUri.AbsolutePath.EndsWith("/file"))
   {Downloads++;DuringDownload?.Invoke();var id=Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[3]);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes[id])});}
   if(request.RequestUri.AbsolutePath.EndsWith("/open-manifest"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(manifest))});
   object Document(object file)
   {
    var id=(Guid)Get(file,"DocumentId");if(documents.TryGetValue(id,out var found))return found;
    var doc=New("DocumentDto");Set(doc,"Id",id);Set(doc,"ProjectId",Get(manifest,"ProjectId"));Set(doc,"FileName",Get(file,"FileName"));
    if(ForeignLease){Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",Guid.NewGuid());}
    documents[id]=doc;return doc;
   }
   if(request.RequestUri.AbsolutePath.EndsWith("/documents"))
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(((IEnumerable)Get(manifest,"Files")).Cast<object>().Select(Document).ToArray()))});
   if(request.RequestUri.AbsolutePath.EndsWith("/versions"))
   {
    var id=Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[3]);var file=((IEnumerable)Get(manifest,"Files")).Cast<object>().Single(item=>Equals(Get(item,"DocumentId"),id));
    var revision=New("RevisionDto");Set(revision,"Display",Get(file,"Revision"));var version=New("DocumentVersionDto");Set(version,"Id",Get(file,"VersionId"));Set(version,"Sha256",Get(file,"Sha256"));Set(version,"FileLength",Get(file,"FileLength"));Set(version,"Revision",revision);
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(new[]{version}))});
   }
   if(request.RequestUri.AbsolutePath.EndsWith("/checkout"))
   {
    Checkouts++;if(DenyCheckout)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("没有编辑权限")});
    var id=Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[3]);var doc=Document(((IEnumerable)Get(manifest,"Files")).Cast<object>().Single(file=>Equals(Get(file,"DocumentId"),id)));
    Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",Session);Set(doc,"CheckoutMachine",Environment.MachineName);Set(doc,"CheckedOutAt",DateTime.UtcNow);
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(doc))});
   }
   OtherMutations++;throw new Exception("Unexpected business mutation "+request.RequestUri.AbsolutePath);
  }
 }
}
