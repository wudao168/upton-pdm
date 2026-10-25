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
 static async Task LinkedDrawingDiscardScenario()
 {
  foreach(var mode in new[]{"loaded","unloaded","acquire-discard","dirty-total","dirty-drawing","saved-drawing","other-session","other-user",
   "shared-model-editing","old-version","wrong-binding","part-denied","drawing-denied","unconfirmed-release","relations-failed","changed-during-release"})
  using(var fixture=new DiscardPathFixture())
  using(var pane=(Control)New("PdmTaskPaneControl","联动放弃工程图回归"))
  {
   var plugin=fixture.Plugin;var project=(Guid)Get(fixture.Manifest,"ProjectId");var session=(Guid)Field(plugin,"checkoutSessionId");
   SetField(plugin,"currentProjectId",project);SetField(plugin,"taskPaneControl",pane);pane.CreateControl();
   var folder=Path.GetDirectoryName(fixture.CurrentPath);var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var drawing=LinkedNode(folder,"ASSOCIATED-DIFFERENT-NAME.SLDDRW","Drawing");
   var drawingPath=(string)Get(drawing,"FullPath");var drawingId=(Guid)Get(drawing,"DocumentId");var partId=(Guid)Get(fixture.Node,"DocumentId");
   var version=LinkedVersion(drawing,LinkedSnapshot(drawing));var drawingVersion=(Guid)Get(version,"Id");var hash=(string)Get(version,"Sha256");
   var identity=Type("PdmDocumentIdentityStore");
   identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{drawingPath,mode=="wrong-binding"?Guid.NewGuid():drawingId,project,drawingVersion,"W3",hash,new FileInfo(drawingPath).Length});
   ((IList)Get(root,"Children")).Add(fixture.Node);((IList)Get(fixture.Node,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(fixture.Node);
   for(var index=0;index<212;index++){var node=fixture.AddWorkingPart("UNTOUCHED-"+index+".SLDPRT");File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.ReadOnly);((IList)Get(root,"Children")).Add(node);}
   File.SetAttributes((string)Get(root,"FullPath"),FileAttributes.ReadOnly);SetField(plugin,"currentTree",root);
   var memoryDirty=mode=="dirty-drawing";var drawingReadOnly=mode=="acquire-discard";var mutation=new List<string>();
   if(mode=="acquire-discard")File.SetAttributes(drawingPath,FileAttributes.ReadOnly);
   if(mode=="saved-drawing")File.AppendAllText(drawingPath,"independent drawing edit");
   var before=Directory.GetFiles(folder).ToDictionary(path=>path,path=>Tuple.Create(WorkingReuseHash(path),File.GetAttributes(path)));
   var appType=plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"用户当前配置":throw new Exception(call.MethodName)).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var part=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return fixture.CurrentPath;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return fixture.MemoryReadOnly;case "SetReadOnlyState":fixture.MemoryReadOnly=(bool)call.Args[0];return true;case "get_ConfigurationManager":return manager;default:mutation.Add(call.MethodName);throw new Exception("Part mutation "+call.MethodName);}
   }).GetTransparentProxy();
   var total=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Get(root,"FullPath"):call.MethodName=="IsOpenedReadOnly"?true:call.MethodName=="get_ConfigurationManager"?manager:call.MethodName=="GetSaveFlag"?mode=="dirty-total":throw new Exception("Total assembly must remain unchanged: "+call.MethodName)).GetTransparentProxy();
   var nativeDrawing=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return drawingPath;case "GetTitle":return Path.GetFileName(drawingPath);case "GetType":return 3;case "GetSaveFlag":return memoryDirty;case "IsOpenedReadOnly":return drawingReadOnly;
     case "SetReadOnlyState":drawingReadOnly=(bool)call.Args[0];return true;default:mutation.Add(call.MethodName);throw new Exception("Drawing saved, reopened or activated: "+call.MethodName);}
   }).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){case "GetDocuments":return mode=="unloaded"?new[]{total,part}:new[]{total,part,nativeDrawing};case "get_ActiveDoc":return total;case "GetDocumentDependencies2":if((string)call.Args[0]!=fixture.CurrentPath)throw new Exception("Unchanged drawing should not re-read or rebase its model references");return new string[0];default:mutation.Add(call.MethodName);throw new Exception("Discard closed/reopened user windows: "+call.MethodName);}
   }).GetTransparentProxy());
   var oldPath=Path.Combine(fixture.DirectoryPath,"View","old",Path.GetFileName(drawingPath));File.WriteAllText(oldPath,"unrelated old user's drawing modification");
   var record=New("LocalEditDraft");Set(record,"DocumentId",drawingId);Set(record,"ProjectId",project);Set(record,"SessionId",session);Set(record,"SourcePath",drawingPath);Set(record,"BaseVersionId",drawingVersion);Set(record,"BaseSha256",hash);
   DiscardCall(fixture.Journal,"Save",record);((IDictionary)Field(plugin,"localEdits"))[drawingId]=record;
   var oldRecord=New("LocalEditDraft");Set(oldRecord,"DocumentId",drawingId);Set(oldRecord,"ProjectId",project);Set(oldRecord,"SessionId",Guid.NewGuid());Set(oldRecord,"SourcePath",oldPath);Set(oldRecord,"BaseVersionId",drawingVersion);Set(oldRecord,"BaseSha256",hash);DiscardCall(fixture.Journal,"Save",oldRecord);
   var modelDocument=New("DocumentDto");Set(modelDocument,"Id",partId);Set(modelDocument,"ProjectId",project);Set(modelDocument,"Kind",1);Set(modelDocument,"FileName","PART.SLDPRT");Set(modelDocument,"CheckedOutBy","tester");Set(modelDocument,"CheckoutSessionId",session);
   var drawingDocument=New("DocumentDto");Set(drawingDocument,"Id",drawingId);Set(drawingDocument,"ProjectId",project);Set(drawingDocument,"Kind",2);Set(drawingDocument,"FileName",Get(drawing,"FileName"));Set(drawingDocument,"CheckedOutBy",mode=="other-user"?"another-engineer":"tester");Set(drawingDocument,"CheckoutSessionId",mode=="other-session"||mode=="other-user"?Guid.NewGuid():session);
   if(mode=="acquire-discard"){Set(drawingDocument,"CheckedOutBy",null);Set(drawingDocument,"CheckoutSessionId",null);}
   Set(drawing,"CheckedOutBy",Get(drawingDocument,"CheckedOutBy"));Set(drawing,"CheckoutSessionId",Get(drawingDocument,"CheckoutSessionId"));
   var other=New("DocumentDto");Set(other,"Id",Guid.NewGuid());Set(other,"ProjectId",project);Set(other,"Kind",1);Set(other,"CheckedOutBy","tester");Set(other,"CheckoutSessionId",session);
   var relation=New("DocumentModelDrawingRelationDto");Set(relation,"ModelDocumentId",partId);Set(relation,"DrawingDocumentId",drawingId);
   var handler=new LinkedDiscardHandler(fixture.Manifest,modelDocument,drawingDocument,other,relation,version,mode,()=>{memoryDirty=true;File.AppendAllText(drawingPath," later edit");});
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://discard-physical.invalid/")})
   {
    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(fixture.Client,"httpClient",http);SetField(fixture.Client,"controlledOpenHttpClient",http);
    Set(fixture.Node,"CheckedOutBy","tester");Set(fixture.Node,"CheckoutSessionId",session);
    if(mode=="acquire-discard")
    {
     await (Task)DiscardCall(plugin,"AcquireLinkedDrawingPermissionsAsync",project,new[]{partId},folder,CancellationToken.None);
     if(handler.DrawingCheckouts!=1||drawingReadOnly||(File.GetAttributes(drawingPath)&FileAttributes.ReadOnly)!=0)throw new Exception("Real linked acquire did not unlock drawing");
    }
    var failed=false;
    try{await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",new object[]{DiscardNodes(fixture.Node)});}catch(InvalidOperationException){failed=true;}
    if(failed!=(mode=="part-denied"))throw new Exception("Wrong overall discard result "+mode);
    var released=new[]{"loaded","unloaded","acquire-discard","dirty-total","changed-during-release"}.Contains(mode);
    var normalReleased=released&&mode!="changed-during-release";
    if(handler.DrawingDiscards!=(released||mode=="drawing-denied"||mode=="unconfirmed-release"?1:0))throw new Exception("Linked drawing was not released or an unsafe release occurred "+mode+" count="+handler.DrawingDiscards);
    if(!string.IsNullOrWhiteSpace((string)Get(drawingDocument,"CheckedOutBy"))==released)throw new Exception("Server drawing ownership was not verified "+mode);
    if(drawingReadOnly!=(normalReleased&&mode!="unloaded")||((File.GetAttributes(drawingPath)&FileAttributes.ReadOnly)!=0)!=normalReleased)throw new Exception("Drawing native/file permission mismatch "+mode);
    if(mutation.Count!=0||handler.FileDownloads!=0||handler.OtherMutations!=0)throw new Exception("Discard changed user windows, uploaded or downloaded files "+mode+" native="+string.Join(",",mutation)+" downloads="+handler.FileDownloads+" requests="+handler.OtherMutations+" "+handler.LastOtherRequest);
    foreach(var pair in before)
    {
     if(pair.Key==drawingPath&&mode=="changed-during-release"){if(!File.ReadAllText(pair.Key).EndsWith(" later edit")||!memoryDirty)throw new Exception("Late drawing edit was discarded");continue;}
     var expected=normalReleased&&pair.Key==drawingPath||mode!="part-denied"&&pair.Key==fixture.CurrentPath?pair.Value.Item2|FileAttributes.ReadOnly:pair.Value.Item2;
     if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||(File.GetAttributes(pair.Key)&FileAttributes.ReadOnly)!=(expected&FileAttributes.ReadOnly))throw new Exception("Unselected file or drawing content changed "+mode+" "+pair.Key);
    }
    var records=fixture.Records();if((bool)Get(records.Single(entry=>(Guid)Get(entry,"DocumentId")==drawingId&&(Guid)Get(entry,"SessionId")==session),"Closed")!=normalReleased)throw new Exception("Current drawing recovery record state incorrect "+mode);
    if((bool)Get(records.Single(entry=>(Guid)Get(entry,"DocumentId")==drawingId&&(Guid)Get(entry,"SessionId")!=(Guid)Get(record,"SessionId")),"Closed")||File.ReadAllText(oldPath)!="unrelated old user's drawing modification")throw new Exception("Another drawing copy or session record was retired "+mode);
    var reminder=((Label)Field(pane,"checkoutReminder")).Text;
    var warned=!normalReleased&&!new[]{"other-session","other-user","part-denied"}.Contains(mode);
    if(warned&&(!reminder.Contains("配套工程图")||!reminder.Contains((string)Get(drawing,"FileName"))&&mode!="relations-failed"))throw new Exception("Partial discard lacks a clear drawing-specific reason "+mode+" "+reminder);
    if(normalReleased&&(Get(drawing,"CheckedOutBy")!=null||Get(drawing,"CheckoutSessionId")!=null))throw new Exception("Visible drawing tree retained old ownership "+mode);
    fixture.AssertOldUntouched();Console.WriteLine("PASS: linked drawing discard "+mode+"; 215-file context, no window reopen, original bytes and unrelated recovery records protected");
   }
  }
 }

 static Array DiscardNodes(params object[] nodes){var array=Array.CreateInstance(Type("CadTreeNode"),nodes.Length);for(var i=0;i<nodes.Length;i++)array.SetValue(nodes[i],i);return array;}
 sealed class LinkedDiscardHandler:HttpMessageHandler
 {
  readonly object manifest,model,drawing,other,relation,version;readonly string mode;readonly Action lateEdit;
  public int DrawingDiscards,DrawingCheckouts,FileDownloads,OtherMutations;public string LastOtherRequest;
  public LinkedDiscardHandler(object manifest,object model,object drawing,object other,object relation,object version,string mode,Action lateEdit){this.manifest=manifest;this.model=model;this.drawing=drawing;this.other=other;this.relation=relation;this.version=version;this.mode=mode;this.lateEdit=lateEdit;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;var isDrawing=path.Contains(Get(drawing,"Id").ToString());object value;
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/versions"))
   {
    if(isDrawing){if(mode=="old-version")Set(version,"Id",Guid.NewGuid());value=new[]{version};}
    else{var file=((IList)Get(manifest,"Files"))[0];value=new[]{new{id=Get(file,"VersionId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength")}};}
   }
   else if(path.EndsWith("/checkout")&&isDrawing)
   {DrawingCheckouts++;Set(drawing,"CheckedOutBy","tester");Set(drawing,"CheckoutSessionId",Get(model,"CheckoutSessionId"));value=drawing;}
   else if(path.EndsWith("/discard-checkout"))
   {
    if(isDrawing)DrawingDiscards++;
    if(mode=="part-denied"&&!isDrawing||mode=="drawing-denied"&&isDrawing)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"回归模拟拒绝释放权限\"}")});
    value=isDrawing?drawing:model;
    if(!(mode=="unconfirmed-release"&&isDrawing)){Set(value,"CheckedOutBy",null);Set(value,"CheckoutSessionId",null);}
    if(mode=="changed-during-release"&&isDrawing)lateEdit();
   }
   else if(path.EndsWith("/open-manifest")){if(isDrawing)throw new Exception("An unchanged linked drawing must not build/rebase a model context");value=manifest;}
   else if(path.EndsWith("/document-relations"))
   {
    if(mode=="relations-failed")throw new HttpRequestException("simulated network failure");
    if(mode=="shared-model-editing"){var second=New("DocumentModelDrawingRelationDto");Set(second,"ModelDocumentId",Get(other,"Id"));Set(second,"DrawingDocumentId",Get(drawing,"Id"));value=new[]{relation,second};}else value=new[]{relation};
   }
   else if(path.EndsWith("/documents"))value=new[]{model,drawing,other};
   else if(path.EndsWith("/file")){FileDownloads++;throw new Exception("Discard downloaded an unchanged file");}
   else{OtherMutations++;LastOtherRequest=request.Method+" "+path;throw new Exception("Unexpected request "+LastOtherRequest);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
