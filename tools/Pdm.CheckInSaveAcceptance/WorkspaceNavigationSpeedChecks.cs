using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task WorkspaceNavigationSpeedScenario(Form host,string output)
 {
  host.ClientSize=new System.Drawing.Size(640,1080);
  Directory.CreateDirectory(output);
  var modern=Type("PdmAddin").GetMethod("GetNavigationLatestVersionsAsync",Flags)!=null;
  void Check(bool passed,string message){TreeCheck(passed,message);Console.WriteLine("PASS: "+message);}
  using(var handler=new NavigationSpeedHandler())
  using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
  using(var pane=(Control)New("PdmTaskPaneControl",""))
  using(var lifetime=new CancellationTokenSource())
  {
   var plugin=New("PdmAddin");
   ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
   SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);SetField(plugin,"taskPaneControl",pane);
   host.Controls.Add(pane);pane.CreateControl();pane.Size=new System.Drawing.Size(720,900);
   var workspace=(Control)Field(pane,"projectDocuments");
   var selector=Field(workspace,"projectSelector");
   var projects=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ProjectDto")));
   foreach(var id in new[]{handler.Project,handler.OtherProject}){var project=New("ProjectDto");Set(project,"Id",id);Set(project,"Code",id==handler.Project?"P-SPEED":"P-OTHER");Set(project,"Name","隔离性能验证");projects.Add(project);}
   TreeCall(workspace,"SetProjects",projects);TreeCall(selector,"SelectProject",handler.Project);
   ((TabControl)Field(pane,"tabs")).SelectedTab=((TabControl)Field(pane,"tabs")).TabPages.Cast<TabPage>().Single(tab=>tab.Text=="工作区");
   var tree=(TreeView)Field(workspace,"tree");
   var timer=Stopwatch.StartNew();
   var load=(Task)TreeCall(plugin,"LoadProjectTreeAsync",handler.Project);
   await TreeWait(()=>tree.Nodes.Count>0,"项目清单未显示");
   var first=timer.ElapsedMilliseconds;
   if(modern)Check(!handler.ReferenceFinished&&handler.VersionCalls==0,"项目清单先于慢结构请求显示，首屏不读取独立装配版本");
   await load;
   var ready=timer.ElapsedMilliseconds;
   if(modern)
   {
    Check(handler.VersionCalls==0,"未展开的独立装配不请求内部结构");
    var row=WorkspaceRows(tree.Nodes,workspace).Single(item=>Equals(Get(item.Tag,"DocumentId"),handler.Second));
    tree.SelectedNode=row;row.Expand();row.Expand();
    await TreeWait(()=>handler.VersionCalls==1,"展开装配没有读取结构");
    await TreeWait(()=>((ISet<Guid>)Field(workspace,"pendingStructures")).Count==0,"独立装配未完成加载");
    var rows=WorkspaceRows(tree.Nodes,workspace).ToArray();
    row=rows.Single(item=>Equals(Get(item.Tag,"DocumentId"),handler.Second));
    await TreeWait(()=>row.IsExpanded,"读取完成后装配没有展开");
    Check(handler.VersionCalls==1&&row.Nodes.Cast<TreeNode>().Any(item=>Equals(Get(item.Tag,"DocumentId"),handler.Part)),"重复展开只发一个请求，内部层级完整且自动展开");
    Check(rows.Count(item=>Equals(Get(item.Tag,"DocumentId"),handler.Part))==1,"补齐结构后独立零件归位，无重复图档");
    Check(Equals(Get(tree.SelectedNode.Tag,"DocumentId"),handler.Second),"补齐结构保留当前所选装配");
    var before=handler.VersionCalls;row.Collapse();row.Expand();Check(handler.VersionCalls==before,"已加载装配再次展开不重复查询");
    var ids=rows.Where(item=>Get(item.Tag,"DocumentId")!=null).Select(item=>(Guid)Get(item.Tag,"DocumentId")).Distinct().ToArray();
    Check(new[]{handler.First,handler.Second,handler.Part,handler.Drawing}.All(ids.Contains),"两个一级结构、工程图和内部零件均可选择");
    var latest1=(Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Second,CancellationToken.None);
    var latest2=(Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Second,CancellationToken.None);
    await Task.WhenAll(latest1,latest2);Check(handler.VersionCalls==before,"工作区与设计树共用有效的最新版本请求");
    var canceled=new CancellationTokenSource();canceled.Cancel();var didCancel=false;
    try{await (Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Second,canceled.Token);}catch(OperationCanceledException){didCancel=true;}
    canceled.Dispose();Check(didCancel,"缓存命中仍尊重调用方取消");
   }
   var repeatedTimer=Stopwatch.StartNew();await (Task)TreeCall(plugin,"LoadProjectTreeAsync",handler.Project);var repeated=repeatedTimer.ElapsedMilliseconds;
   if(modern)
   {
    Check(handler.DocumentsCalls==1,"短时间重复选择项目复用已取得的清单");
    handler.DelayFirst=true;
    ((IDictionary)Field(plugin,"projectDocumentsCache")).Clear();
    var old=(Task)TreeCall(plugin,"LoadProjectTreeAsync",handler.Project);
    await TreeWait(()=>handler.FirstWaiting,"旧项目请求没有进入慢响应");
    TreeCall(selector,"SelectProject",handler.OtherProject);
    await (Task)TreeCall(plugin,"LoadProjectTreeAsync",handler.OtherProject);await old;
    Check(handler.FirstCanceled&&Equals(Get(Field(workspace,"root"),"DocumentId"),handler.OtherDocument),"切换项目取消旧请求，旧响应不能覆盖新项目");
    Check(((Label)Field(workspace,"loadingStatus")).Text.Contains("就绪"),"工作区显示中文加载阶段和耗时");
    TreeCall(pane,"SetTreeLoadingStatus","正在核对实际设计树");
    await TreeWait(()=>((Label)Field(pane,"workspaceOperationStatus")).Text.Contains("用时")
      &&!((Label)Field(pane,"workspaceOperationStatus")).Text.Contains("用时 00:00:00"),"加载计时未推进");
    Check(((Label)Field(pane,"workspaceOperationStatus")).Text.Contains("正在加载设计树"),"设计树加载阶段持续显示已用时间");
    TreeCall(pane,"SetTreeLoadingStatus",string.Empty);
    Check(!(bool)Get(Field(pane,"workspaceElapsedTimer"),"Enabled")&&!((Control)Field(pane,"workspaceOperationProgress")).Visible,"加载结束停止计时并隐藏运行进度，空闲状态不误报准备中");
    TreeCall(plugin,"CancelProjectNavigation");
   }
   using(var bitmap=new System.Drawing.Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"workspace.png"));}
   File.WriteAllText(Path.Combine(output,"timings.json"),new JavaScriptSerializer().Serialize(new{Modern=modern,FirstVisibleMs=first,PrimaryReadyMs=ready,RepeatedReadyMs=repeated,handler.DocumentsCalls,handler.VersionCalls,Scope="隔离HTTP延迟对照；不包含真实SolidWorks原生打开耗时"}));
   Console.WriteLine("TIMING: first-visible="+first+"ms primary-ready="+ready+"ms repeated="+repeated+"ms");
   lifetime.Cancel();
  }
  if(modern)await NavigationVersionReadsScenario(output);
 }

 static async Task NavigationVersionReadsScenario(string output)
 {
  void Check(bool passed,string message){TreeCheck(passed,message);Console.WriteLine("PASS: "+message);}
  using(var handler=new NavigationVersionHandler())
  using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
  using(var lifetime=new CancellationTokenSource())
  {
   var plugin=New("PdmAddin");((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
   SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
   var path=Path.Combine(output,"版本匹配样件.SLDPRT");File.WriteAllText(path,"latest-content");
   var node=New("CadTreeNode");Set(node,"DocumentId",handler.Document);Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   var nodes=Array.CreateInstance(Type("CadTreeNode"),1);nodes.SetValue(node,0);
   string Hash(){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
   async Task<object[]> Read()
   {
    var task=(Task)TreeCall(plugin,"GetNavigationTreeVersionsAsync",handler.Document,nodes,new Dictionary<string,string>{{path,Hash()}},CancellationToken.None);
    await task;return ((IEnumerable)Get(task,"Result")).Cast<object>().ToArray();
   }
   handler.LatestHash=Hash();File.WriteAllText(path,"old-content");handler.OldHash=Hash();File.WriteAllText(path,"latest-content");
   var latest=await Read();Check(latest.Length==1&&handler.LatestCalls==1&&handler.HistoryCalls==0,"本地匹配最新版时只读取一条版本，不拉取整个历史");
   await Read();Check(handler.LatestCalls==1,"重复状态核对复用已验证的最新版本");
   File.WriteAllText(path,"old-content");var old=await Read();Check(old.Length==2&&handler.HistoryCalls==1,"旧本地内容按需取完整历史，保留历史版本识别");
   IDictionary Histories()=> (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),typeof(IReadOnlyList<>).MakeGenericType(Type("DocumentVersionDto"))));
   Array Versions(object[] values){var array=Array.CreateInstance(Type("DocumentVersionDto"),values.Length);for(var index=0;index<values.Length;index++)array.SetValue(values[index],index);return array;}
   var histories=Histories();histories.Add(handler.Document,Versions(old));
   var current=(string)TreeCall(plugin,"DetermineCurrentRevision",node,histories,new Dictionary<string,string>{{path,Hash()}});
   Check(current=="A1","按完整历史正确识别旧版，未把旧图档误判成本地修改");
   var document=New("DocumentDto");Set(document,"Id",handler.Document);Set(document,"StoredVersionCount",3);var revision=New("RevisionDto");Set(revision,"Display","A3");Set(document,"Revision",revision);
   var documents=Array.CreateInstance(Type("DocumentDto"),1);documents.SetValue(document,0);
   TreeCall(plugin,"InvalidateChangedVersionHistories",documents);TreeCall(plugin,"RememberNavigationVersions",documents);
   handler.LatestRevision="A3";File.WriteAllText(path,"latest-content");latest=await Read();
   Check(handler.LatestCalls==2&&(string)Get(Get(latest[0],"Revision"),"Display")=="A3","服务器版本变化使缓存失效，重新取得最新版本");
   ((IDictionary)Field(plugin,"navigationVersions")).Clear();
   using(var canceled=new CancellationTokenSource())
   {
    var waiter=(Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Document,canceled.Token);
    var survivor=(Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Document,CancellationToken.None);
    canceled.Cancel();var stopped=false;try{await waiter;}catch(OperationCanceledException){stopped=true;}await survivor;
    Check(stopped&&handler.LatestCalls==3,"在途相同请求合并，取消一个等待者不影响其他等待者");
   }
   var drawing=New("CadTreeNode");var drawingId=Guid.NewGuid();Set(drawing,"DocumentId",drawingId);Set(drawing,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
   var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"Children",Activator.CreateInstance(snapshot.GetType().GetProperty("Children").PropertyType));var reference=New("DocumentReferenceNodeDto");Set(reference,"DocumentId",handler.Document);var before=New("RevisionDto");Set(before,"Display","A1");Set(reference,"Revision",before);((IList)Get(snapshot,"Children")).Add(reference);
   var drawingVersion=New("DocumentVersionDto");Set(drawingVersion,"ReferenceSnapshot",snapshot);
   histories=Histories();histories.Add(handler.Document,Versions(latest));histories.Add(drawingId,Versions(new[]{drawingVersion}));
   var all=Array.CreateInstance(Type("CadTreeNode"),2);all.SetValue(node,0);all.SetValue(drawing,1);
   await (Task)TreeCall(plugin,"CompleteDrawingNavigationHistoriesAsync",all,histories,CancellationToken.None);
   Check(handler.HistoryCalls==2&&((IEnumerable)histories[handler.Document]).Cast<object>().Count()==2,"工程图引用旧模型时按需补齐模型历史，保留真实更新判断");
   Set(node,"CurrentRevision","A3");
   Check((bool)TreeCall(plugin,"HasPendingDrawingModelChange",node,snapshot,histories),"模型内容确有变更时仍提示工程图待更新");
   SetField(plugin,"authenticatedUsername","另一个用户");await (Task)TreeCall(plugin,"GetNavigationLatestVersionsAsync",handler.Document,CancellationToken.None);
   Check(handler.LatestCalls==4,"切换账号不复用原账号的版本查询");
   lifetime.Cancel();
  }
 }

 sealed class NavigationVersionHandler:HttpMessageHandler
 {
  public readonly Guid Document=Guid.NewGuid(),LatestVersion=Guid.NewGuid(),OldVersion=Guid.NewGuid();
  public string LatestHash,OldHash,LatestRevision="A2";public int LatestCalls,HistoryCalls;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   await Task.Delay(80,token);var latest=request.RequestUri.Query.Contains("take=1");
   if(latest)Interlocked.Increment(ref LatestCalls);else Interlocked.Increment(ref HistoryCalls);
   object Version(Guid id,string revision,string hash)=>new{Id=id,Revision=new{Display=revision},Sha256=hash,PropertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",hash}}};
   var versions=latest?new[]{Version(LatestVersion,LatestRevision,LatestHash)}:new[]{Version(LatestVersion,LatestRevision,LatestHash),Version(OldVersion,"A1",OldHash)};
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(versions))};
  }
 }

 sealed class NavigationSpeedHandler:HttpMessageHandler
 {
  public readonly Guid Project=Guid.NewGuid(),OtherProject=Guid.NewGuid(),OtherDocument=Guid.NewGuid(),First=Guid.NewGuid(),Second=Guid.NewGuid(),Part=Guid.NewGuid(),Drawing=Guid.NewGuid();
  public int VersionCalls,DocumentsCalls;
  public volatile bool ReferenceFinished,DelayFirst,FirstWaiting,FirstCanceled;
  object Node(Guid id,string name,int kind,params object[] children)=>new{DocumentId=id,FileName=name+(kind==0?".SLDASM":kind==1?".SLDPRT":".SLDDRW"),DisplayName=name,Kind=kind,Children=children};
  object Document(Guid id,string name,int kind)=>new{Id=id,ProjectId=Project,Name=name,DrawingNumber=name,FileName=name+(kind==0?".SLDASM":kind==1?".SLDPRT":".SLDDRW"),Kind=kind,StoredVersionCount=1,Revision=new{Display="A"}};
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object payload;
   if(path.EndsWith("/document-operation-rules"))payload=new{DisabledRuleIds=Array.Empty<string>()};
   else if(path.Contains(OtherProject.ToString()))
    payload=path.EndsWith("/documents")?(object)new[]{new{Id=OtherDocument,ProjectId=OtherProject,Name="新项目图档",Kind=1,StoredVersionCount=0}}:path.EndsWith("/reference-tree")?Node(OtherDocument,"新项目图档",1):Array.Empty<object>();
   else if(path.EndsWith("/documents"))
   {
    Interlocked.Increment(ref DocumentsCalls);
    if(DelayFirst){FirstWaiting=true;try{await Task.Delay(10000,token);}catch(OperationCanceledException){FirstCanceled=true;throw;}}
    await Task.Delay(60,token);
    payload=new[]{Document(First,"第一套结构",0),Document(Second,"第二套结构",0),Document(Part,"内部零件",1),Document(Drawing,"配套工程图",2)};
   }
   else if(path.EndsWith("/document-relations"))payload=new[]{new{ModelDocumentId=Second,DrawingDocumentId=Drawing}};
   else if(path.EndsWith("/reference-tree")){await Task.Delay(400,token);ReferenceFinished=true;payload=Node(First,"第一套结构",0);}
   else if(path.EndsWith("/versions")){Interlocked.Increment(ref VersionCalls);await Task.Delay(800,token);payload=new[]{new{Id=Guid.NewGuid(),ReferenceSnapshot=Node(Second,"第二套结构",0,Node(Part,"内部零件",1))}};}
   else throw new InvalidOperationException("意外的性能验证请求："+path);
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(payload))};
  }
 }
}
