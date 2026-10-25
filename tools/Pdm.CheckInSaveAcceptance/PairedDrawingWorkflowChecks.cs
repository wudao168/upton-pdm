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
 static async Task PairedDrawingWorkflowScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-paired-workflow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in new[]{"fresh","owned","owned-unsaved","owned-saved","drawing-active","stale-binding","foreign-editor","checkout-denied","version-race","native-unlock-failed","unknown-saved-change"})
   {
    var folder=Path.Combine(directory,"View",mode);Directory.CreateDirectory(folder);
    var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
    var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var part=LinkedNode(folder,"7000093.101.03-02.SLDPRT","Part");var drawing=LinkedNode(folder,"7000093.101.03-02.SLDDRW","Drawing");
    ((IList)Get(root,"Children")).Add(part);((IList)Get(part,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(part);
    for(var index=0;index<212;index++)((IList)Get(root,"Children")).Add(LinkedNode(folder,"UNTOUCHED-"+index+".SLDPRT","Part"));
    var versions=new Dictionary<Guid,object>{{(Guid)Get(root,"DocumentId"),LinkedVersion(root,LinkedSnapshot(root))},{(Guid)Get(drawing,"DocumentId"),LinkedVersion(drawing,LinkedSnapshot(drawing))}};
    var path=(string)Get(drawing,"FullPath");var version=versions[(Guid)Get(drawing,"DocumentId")];var sha=WorkingReuseHash(path);
    var written=(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Get(drawing,"DocumentId"),project,mode=="stale-binding"?Guid.NewGuid():Get(version,"Id"),"A",sha,new FileInfo(path).Length});
    if(!written)throw new Exception("Could not create isolated drawing binding");
    var doc=New("DocumentDto");Set(doc,"Id",Get(drawing,"DocumentId"));Set(doc,"ProjectId",project);Set(doc,"FileName",Get(drawing,"FileName"));Set(doc,"Kind",2);Set(doc,"CheckedOutAt",new DateTime(2026,10,24,4,8,12,DateTimeKind.Utc));
    var owned=mode.StartsWith("owned");
    if(owned){Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);}
    if(mode=="foreign-editor"){Set(doc,"CheckedOutBy","another-engineer");Set(doc,"CheckoutSessionId",Guid.NewGuid());}
    var dirty=mode=="owned-unsaved";var readOnly=!owned;var nativeCalls=new List<string>();
    if(mode=="owned-saved"||mode=="unknown-saved-change")File.AppendAllText(path,"saved drawing edits");
    foreach(var file in Directory.GetFiles(folder))File.SetAttributes(file,FileAttributes.ReadOnly);
    if(owned)File.SetAttributes(path,FileAttributes.Normal);
    var before=Directory.GetFiles(folder).ToDictionary(file=>file,file=>Tuple.Create(WorkingReuseHash(file),File.GetAttributes(file)));
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var parent=new Proxy(nativeType,call=>call.MethodName=="GetPathName"?Get(root,"FullPath"):throw new Exception("Parent was inspected or mutated: "+call.MethodName)).GetTransparentProxy();
    var native=new Proxy(nativeType,call=>{
     switch(call.MethodName)
     {
      case "GetPathName":return path;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return readOnly;
      case "SetReadOnlyState":nativeCalls.Add("readonly:"+call.Args[0]);if(mode=="native-unlock-failed"&&!(bool)call.Args[0])return false;readOnly=(bool)call.Args[0];return true;
      default:throw new Exception("Drawing was saved, closed, reopened or activated: "+call.MethodName);
     }
    }).GetTransparentProxy();
    SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{parent,native}:throw new Exception("Native open/close or activation: "+call.MethodName)).GetTransparentProxy());
    SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);SetField(plugin,"currentTree",mode=="drawing-active"?drawing:root);
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://paired-drawing.invalid/"))
    using(var handler=new PairedDrawingHandler(doc,versions,session,mode))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://paired-drawing.invalid/")})
    using(var pane=(Control)New("PdmTaskPaneControl","工程图获取回归"))
    {
     pane.CreateControl();SetField(plugin,"taskPaneControl",pane);
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     var scope=(string)Get(client,"EditJournalScope")+"|tester";SetField(plugin,"editJournalScope",scope);SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),scope));
     if(owned)Type("PdmAddin").GetMethod("RememberLocalEdit",Flags).Invoke(plugin,new[]{drawing,doc});
     Exception failure=null;
     try{await (Task)Type("PdmAddin").GetMethod("AcquireExplicitlyFromPlmAsync",Flags).Invoke(plugin,new object[]{LinkedItems(drawing),project,null});}
     catch(Exception error){failure=error;}
     var shouldFail=new[]{"stale-binding","foreign-editor","checkout-denied","version-race","native-unlock-failed","unknown-saved-change"}.Contains(mode);
     if((failure!=null)!=shouldFail)throw new Exception("Unexpected targeted acquire result "+mode,failure);
     if(shouldFail&&!failure.Message.Any(character=>character>='\u4e00'&&character<='\u9fff'))throw new Exception("Targeted failure lost its Chinese explanation "+mode,failure);
     if(handler.OtherRequests.Count>0)throw new Exception("Targeted acquire rebuilt a manifest, downloaded or wrote another document: "+string.Join(",",handler.OtherRequests));
     var rollback=mode=="version-race"||mode=="native-unlock-failed";
     if(handler.Discards!=(rollback?1:0)||handler.Checkouts!=(owned||mode=="stale-binding"||mode=="unknown-saved-change"?0:1))throw new Exception("Unexpected checkout/rollback count "+mode+" "+handler.Checkouts+"/"+handler.Discards);
     foreach(var pair in before)
     {
      var expected=pair.Key==path&&!shouldFail?pair.Value.Item2&~FileAttributes.ReadOnly:pair.Value.Item2;
      if(expected==0)expected=FileAttributes.Normal;
      if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||File.GetAttributes(pair.Key)!=expected)throw new Exception("Acquisition rewrote a file or unlocked an unrelated file "+mode+" "+pair.Key);
     }
     if(dirty!=(mode=="owned-unsaved")||readOnly!=shouldFail)throw new Exception("Native drawing dirty or readonly state was lost "+mode);
     if(owned&&nativeCalls.Count>0)throw new Exception("Already editable own drawing should not change native state");
     if(((IDictionary)Field(plugin,"controlledOpenManifests")).Count!=0)throw new Exception("Test must reproduce acquisition without a cached assembly context");
     Console.WriteLine("PASS: paired drawing acquisition "+mode+"; 215 file hashes protected; dirty parent never saved/inspected; no close/reopen/download");
    }
   }
   var metadata=New("PdmAddin");SetField(metadata,"refreshSuppressionDepth",1);
   var begin=Type("PdmAddin").GetMethod("BeginMetadataRefresh",Flags);var cancel=Type("PdmAddin").GetMethod("CancelMetadataRefresh",Flags);var refresh=Type("PdmAddin").GetMethod("RefreshMetadataAsync",Flags);
   var first=(CancellationToken)begin.Invoke(metadata,null);var second=(CancellationToken)begin.Invoke(metadata,null);cancel.Invoke(metadata,null);
   if(!first.IsCancellationRequested||!second.IsCancellationRequested)throw new Exception("Superseded metadata work was not cancelled");
   using(first.Register(()=>{}))using(second.Register(()=>{})){}
   Console.WriteLine("PASS: cancelled overlapping metadata tasks retain usable tokens until their completion");
   await (Task)refresh.Invoke(metadata,new object[]{Guid.NewGuid(),first});await (Task)refresh.Invoke(metadata,new object[]{Guid.NewGuid(),second});
   if(((IDictionary)Field(metadata,"metadataRefreshOwners")).Count!=0)throw new Exception("Completed metadata owner leaked");
   using(var external=new CancellationTokenSource()){await (Task)refresh.Invoke(metadata,new object[]{Guid.NewGuid(),external.Token});using(external.Token.Register(()=>{})){};}
   Console.WriteLine("PASS: completed metadata sources are released and external cancellation sources remain owned by their caller");
   await PairedDrawingPlanScenario(directory);
   await PairedDrawingUpdateScenario(directory);
  }
  finally
  {
   bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected fixture path");Directory.Delete(directory,true);
  }
 }
 static async Task PairedDrawingPlanScenario(string directory)
 {
  foreach(var mode in new[]{"model-only","drawing-only-unsaved-model"})
  {
   var folder=Path.Combine(directory,"plan-"+mode);Directory.CreateDirectory(folder);var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
   SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
   var model=LinkedNode(folder,"PART.SLDPRT","Part");var drawing=LinkedNode(folder,"PART.SLDDRW","Drawing");((IList)Get(model,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(model);
   var versions=new Dictionary<Guid,object>{{(Guid)Get(model,"DocumentId"),LinkedVersion(model,LinkedSnapshot(model))},{(Guid)Get(drawing,"DocumentId"),LinkedVersion(drawing,LinkedSnapshot(drawing))}};
   File.AppendAllText((string)Get(model,"FullPath"),"real model geometry change");Set(model,"WorkState",Enum.Parse(Type("CadWorkState"),"PendingCheckIn"));Set(drawing,"RequiresModelUpdate",true);
   var otherFolder=Path.Combine(folder,"other");Directory.CreateDirectory(otherFolder);var otherModel=LinkedNode(otherFolder,"PART.SLDPRT","Part");Set(otherModel,"DocumentId",Get(model,"DocumentId"));
   var otherDrawing=LinkedNode(otherFolder,"OTHER.SLDDRW","Drawing");((IList)Get(otherDrawing,"ReferencedModels")).Add(otherModel);((IList)Get(model,"Children")).Add(otherDrawing);SetField(plugin,"currentTree",model);
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?Array.Empty<object>():throw new Exception("Unexpected archive native mutation "+call.MethodName)).GetTransparentProxy());
   using(var handler=new LinkedArchiveHandler(new[]{model,drawing},versions,project,session,new List<string>(),false))
   using(var client=(IDisposable)New("PdmApiClient","http://linked-qa.invalid/"))
   {
    LinkedClient(plugin,client,handler);Exception failure=null;object plan=null;
    try{var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{mode=="model-only"?LinkedItems(model):LinkedItems(drawing),null,CancellationToken.None});await task;plan=Get(task,"Result");}
    catch(InvalidOperationException error){failure=error;}
    if(mode=="model-only")
    {
     if(failure!=null)throw failure;var planned=((IEnumerable)Get(plan,"Items")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
     if(planned.Length!=2||!planned.Contains(model)||!planned.Contains(drawing)||planned.Contains(otherDrawing)||(int)Get(plan,"SkippedFiles")!=0)throw new Exception("Selected model did not bring only its physical paired drawing into archive, or skip count became negative");
    }
    else if(failure==null||!failure.Message.Contains("尚未上传或升版"))throw new Exception("Drawing alone was allowed to archive against an unsubmitted dirty model");
    Console.WriteLine("PASS: paired archive planning "+mode+"; unrelated physical copy not selected; no upload/version calls");
   }
  }
 }
 static async Task PairedDrawingUpdateScenario(string directory)
 {
  foreach(var mode in new[]{"success","view-rebuild-failed","drawing-save-failed"})
  {
   var folder=Path.Combine(directory,"update-"+mode);Directory.CreateDirectory(folder);var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
   var model=LinkedNode(folder,"PART.SLDPRT","Part");var drawing=LinkedNode(folder,"PART.SLDDRW","Drawing");var parent=LinkedNode(folder,"UNSELECTED.SLDASM","Assembly");
   ((IList)Get(model,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(model);
   SetField(plugin,"currentTree",model);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
   Set(model,"CheckedOutBy","tester");Set(model,"CheckoutSessionId",session);Set(drawing,"CheckedOutBy","tester");Set(drawing,"CheckoutSessionId",session);Set(drawing,"RequiresModelUpdate",true);
   var versions=new Dictionary<Guid,object>{{(Guid)Get(model,"DocumentId"),LinkedVersion(model,LinkedSnapshot(model))},{(Guid)Get(drawing,"DocumentId"),LinkedVersion(drawing,LinkedSnapshot(drawing))}};
   File.AppendAllText((string)Get(model,"FullPath"),"modified geometry");var modelSha=WorkingReuseHash((string)Get(model,"FullPath"));var parentSha=WorkingReuseHash((string)Get(parent,"FullPath"));
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var nativePart=new SheetReferenceModelProxy(modelType,false,call=>call.MethodName=="GetPathName"?Get(model,"FullPath"):call.MethodName=="GetSaveFlag"?false:call.MethodName=="IsOpenedReadOnly"?false:throw new Exception("Unrequested model save: "+call.MethodName)).GetTransparentProxy();
   var nativeParent=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Get(parent,"FullPath"):throw new Exception("Unselected parent access: "+call.MethodName)).GetTransparentProxy();
   var view=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>call.MethodName=="get_ReferencedDocument"?nativePart:call.MethodName=="get_ReferencedConfiguration"?"Default":throw new Exception("Unexpected view mutation "+call.MethodName)).GetTransparentProxy();
   var sheet=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Sheet",true),call=>call.MethodName=="GetName"?"SHEET1":throw new Exception(call.MethodName)).GetTransparentProxy();
   var saves=0;var sheets=new List<string>();var dirty=false;
   var nativeDrawing=new SheetReferenceModelProxy(modelType,true,call=>{
    switch(call.MethodName)
    {
     case "GetType":return 3;case "GetPathName":return Get(drawing,"FullPath");case "GetTitle":return "PART.SLDDRW";case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return false;
     case "GetViews":return new object[]{new object[]{null,view}};case "GetCurrentSheet":return sheet;case "GetSheetNames":return new[]{"SHEET1","SHEET2"};
     case "ActivateSheet":sheets.Add((string)call.Args[0]);return true;
     case "ForceRebuild3":dirty=true;return mode!="view-rebuild-failed";
     case "Save3":
      saves++;if(mode=="drawing-save-failed")return false;
      File.AppendAllText((string)Get(drawing,"FullPath"),"updated views for current model");dirty=false;return true;
     default:throw new Exception("Unexpected native drawing mutation "+call.MethodName);
    }
   }).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    switch(call.MethodName)
    {
     case "GetType":return appType;
     case "GetDocuments":return new[]{nativeParent,nativePart,nativeDrawing};case "get_ActiveDoc":return nativeParent;
     case "ActivateDoc3":call.Args[3]=0;return (string)call.Args[0]=="PART.SLDDRW"?nativeDrawing:nativeParent;
     default:throw new Exception("Unexpected native window operation "+call.MethodName);
    }
   }).GetTransparentProxy();
   SetField(plugin,"application",app);SetField(plugin,"scanner",New("SolidWorksReferenceTreeScanner",app));
   using(var handler=new LinkedArchiveHandler(new[]{model,drawing},versions,project,session,new List<string>(),false))
   using(var client=(IDisposable)New("PdmApiClient","http://linked-qa.invalid/"))
   {
    LinkedClient(plugin,client,handler);var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(model),null,CancellationToken.None});await task;var plan=Get(task,"Result");Exception failure=null;
    try{await (Task)Type("PdmAddin").GetMethod("UpdateLinkedDrawingsAsync",Flags).Invoke(plugin,new object[]{plan,project,CancellationToken.None});}catch(InvalidOperationException error){failure=error;}
    if(mode=="success")
    {
     if(failure!=null)throw failure;
     if(saves!=1||dirty||!sheets.Contains("SHEET2")||Get(drawing,"DrawingUpdatePendingReason")!=null||Convert.ToString(Get(drawing,"WorkState"))!="PendingCheckIn")throw new Exception("Paired drawing did not rebuild all sheets and save exactly once into a submittable state");
    }
    else if(failure==null||!failure.Message.Contains("尚未上传或升版")||string.IsNullOrWhiteSpace((string)Get(drawing,"DrawingUpdatePendingReason")))throw new Exception("Failed paired drawing update did not stop batch or retain diagnosis");
    if(WorkingReuseHash((string)Get(model,"FullPath"))!=modelSha||WorkingReuseHash((string)Get(parent,"FullPath"))!=parentSha)throw new Exception("Paired drawing update changed an unselected file");
    Console.WriteLine("PASS: paired drawing native API proxy update "+mode+"; all sheets rebuilt before archive, model/parent bytes retained; no upload before successful update");
   }
  }
 }
 sealed class PairedDrawingHandler:HttpMessageHandler
 {
  readonly object document;readonly Dictionary<Guid,object> versions;readonly Guid session;readonly string mode;
  public int Checkouts,Discards;public readonly List<string> OtherRequests=new List<string>();
  public PairedDrawingHandler(object document,Dictionary<Guid,object> versions,Guid session,string mode){this.document=document;this.versions=versions;this.session=session;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object body;var status=HttpStatusCode.OK;
   if(path.EndsWith("/document-operation-rules"))body=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/documents"))body=new[]{document};
   else if(path.EndsWith("/versions"))
   {
    if(request.RequestUri.Query!="?take=1")throw new Exception("Drawing acquisition fetched full histories");
    var id=versions.Keys.Single(key=>path.Contains(key.ToString()));body=new[]{versions[id]};
   }
   else if(path.EndsWith("/checkout"))
   {
    Checkouts++;
    if(mode=="foreign-editor"||mode=="checkout-denied"){status=HttpStatusCode.Conflict;body=new{message="图档正在由其他人编辑"};}
    else
    {
     Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);body=document;
     if(mode=="version-race")Set(versions[(Guid)Get(document,"Id")],"Id",Guid.NewGuid());
    }
   }
   else if(path.EndsWith("/discard-checkout")){Discards++;Set(document,"CheckedOutBy",null);Set(document,"CheckoutSessionId",null);body=document;}
   else{OtherRequests.Add(path);throw new Exception("Unexpected target acquire request "+path);}
   return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(new JavaScriptSerializer().Serialize(body))});
  }
 }
}
