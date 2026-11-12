using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task DrawingCheckoutLatestScenario(string output)
 {
  Directory.CreateDirectory(output);
  foreach(var mode in new[]{"loaded-clean","duplicate-close-event","isolated-copy","unloaded-clean","disk-edit","memory-edit","readonly-memory-edit","late-memory-edit","late-disk-edit",
   "bad-binding","new-reference","corrupt-download","prepare-failed","reload-modified","wrong-close-event","foreign-model","wrong-second-sheet"})
  using(var fixture=new DiscardPathFixture())
  {
   void Check(bool value,string message){TreeCheck(value,message);Console.WriteLine("PASS: "+mode+"："+message);}
   var plugin=fixture.Plugin;var project=(Guid)Get(fixture.Manifest,"ProjectId");var session=(Guid)Field(plugin,"checkoutSessionId");
   var folder=Path.GetDirectoryName(fixture.CurrentPath);var drawing=fixture.AddWorkingPart("DRAWING.SLDDRW");Set(drawing,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
   var total=fixture.AddWorkingPart("TOP.SLDASM");Set(total,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
   var path=(string)Get(drawing,"FullPath");var id=(Guid)Get(drawing,"DocumentId");var oldFile=((IEnumerable)Get(fixture.Manifest,"Files")).Cast<object>().Single(file=>Equals(Get(file,"DocumentId"),id));
   var old=LinkedVersion(drawing,LinkedSnapshot(drawing));Set(old,"Id",Get(oldFile,"VersionId"));
   var revision=New("RevisionDto");Set(revision,"Display","W1");Set(old,"Revision",revision);
   var bytes=Encoding.UTF8.GetBytes("new archived drawing bytes "+mode);var seed=Path.Combine(folder,"server.bin");File.WriteAllBytes(seed,bytes);var latestHash=WorkingReuseHash(seed);File.Delete(seed);
   var latest=New("DocumentVersionDto");Set(latest,"Id",Guid.NewGuid());Set(latest,"Sha256",latestHash);Set(latest,"FileLength",(long)bytes.Length);
   revision=New("RevisionDto");Set(revision,"Display","W2");Set(latest,"Revision",revision);
   var requested=New("ControlledOpenManifestDto");Set(requested,"Id",Guid.NewGuid());Set(requested,"ProjectId",project);Set(requested,"RootDocumentId",id);Set(requested,"RootVersionId",Get(latest,"Id"));Set(requested,"RootRelativePath","DRAWING.SLDDRW");
   var latestFile=New("ControlledOpenFileDto");Set(latestFile,"DocumentId",id);Set(latestFile,"VersionId",Get(latest,"Id"));Set(latestFile,"FileName","DRAWING.SLDDRW");Set(latestFile,"RelativePath","DRAWING.SLDDRW");Set(latestFile,"Revision","W2");Set(latestFile,"Sha256",latestHash);Set(latestFile,"FileLength",(long)bytes.Length);
   if(mode=="isolated-copy")
   {
    var isolation=New("NativeFileIsolationDto");Set(isolation,"ReferenceSnapshotId",Guid.NewGuid());Set(isolation,"Sha256",latestHash);Set(isolation,"Length",(long)bytes.Length);
    Set(latestFile,"NativeIsolation",isolation);Set(latestFile,"Sha256",new string('A',64));Set(latestFile,"FileLength",999L);Set(latest,"Sha256",Get(latestFile,"Sha256"));Set(latest,"FileLength",999L);
   }
   var files=(IList)Activator.CreateInstance(Property(requested,"Files").PropertyType);Set(requested,"Files",files);files.Add(latestFile);files.Add(((IList)Get(fixture.Manifest,"Files"))[0]);
   if(mode=="new-reference"){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"FileName","NEW-PART.SLDPRT");files.Add(file);}
   var document=New("DocumentDto");Set(document,"Id",id);Set(document,"ProjectId",project);Set(document,"Kind",(int)Get(drawing,"Kind"));Set(document,"FileName","DRAWING.SLDDRW");Set(document,"Revision",Get(latest,"Revision"));
   var identity=Type("PdmDocumentIdentityStore");
   if(mode=="bad-binding")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Guid.NewGuid(),project,Get(old,"Id"),"W1",Get(old,"Sha256"),Get(old,"FileLength")});
   if(mode=="disk-edit")File.AppendAllText(path," saved local annotations");
   File.SetAttributes(path,FileAttributes.ReadOnly);File.SetAttributes(fixture.CurrentPath,FileAttributes.ReadOnly);File.SetAttributes((string)Get(total,"FullPath"),FileAttributes.ReadOnly);
   var partHash=WorkingReuseHash(fixture.CurrentPath);var totalHash=WorkingReuseHash((string)Get(total,"FullPath"));var originalHash=WorkingReuseHash(path);
   var dirty=mode=="memory-edit"||mode=="readonly-memory-edit";var readOnly=mode!="memory-edit";var reloading=false;var reloads=0;var modelsSaved=0;var receivedOptions=-1;Delegate closing=null;
   var appType=plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var constants=System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(plugin.GetType().Assembly.Location),"SolidWorks.Interop.swconst.dll"));
   var reloadReason=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swFileCloseNotifyReason_e"),"swFileCloseNotifyReason_CloseForReload"));
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
   var part=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return fixture.CurrentPath;case "GetTitle":return "PART.SLDPRT";case "GetSaveFlag":return true;case "GetType":return 1;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("不得操作模型："+call.MethodName);}
   }).GetTransparentProxy();
   var totalModel=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Get(total,"FullPath"):call.MethodName=="GetTitle"?"TOP.SLDASM":call.MethodName=="GetSaveFlag"?true:throw new Exception("不得操作总装："+call.MethodName)).GetTransparentProxy();
   var viewType=interop.GetType("SolidWorks.Interop.sldworks.View",true);
   object View(bool foreign)=>new AssemblyProxy(viewType,call=>call.MethodName=="get_ReferencedDocument"?(foreign?null:part):call.MethodName=="GetReferencedModelName"?(foreign?Path.Combine(folder,"outside","PART.SLDPRT"):fixture.CurrentPath):throw new Exception(call.MethodName)).GetTransparentProxy();
   var native=new SheetReferenceModelProxy(modelType,true,call=>{
    switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return "DRAWING.SLDDRW";case "GetType":return 3;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return readOnly;
     case "SetReadOnlyState":readOnly=(bool)call.Args[0];return true;
     case "GetViews":return new object[]{new object[]{View(false),View(false)},new object[]{View(false),View(mode=="wrong-second-sheet")}};
     default:modelsSaved++;throw new Exception("不得保存或改写原图："+call.MethodName);}
   }).GetTransparentProxy();
   var foreignModel=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Path.Combine(folder,"outside","PART.SLDPRT"):throw new Exception(call.MethodName)).GetTransparentProxy();
   var search=false;var nativeCalls=new List<string>();
   SetField(plugin,"application",new AssemblyProxy(appType,call=>{
    nativeCalls.Add(call.MethodName+"; closed="+reloading);
    switch(call.MethodName){case "GetDocuments":return new[]{part,totalModel}.Concat(mode=="foreign-model"?new[]{foreignModel}:Array.Empty<object>()).Concat(!reloading&&mode!="unloaded-clean"?new[]{native}:Array.Empty<object>()).ToArray();
     case "get_ActiveDoc":return native;
     case "add_FileCloseNotify":closing=(Delegate)call.Args[0];return null;case "remove_FileCloseNotify":closing=null;return null;
     case "CloseAndReopen2":
      reloads++;receivedOptions=(int)call.Args[1];if(mode=="reload-modified"){call.Args[2]=null;return 2;}
      reloading=true;closing.DynamicInvoke(mode=="wrong-close-event"?Path.Combine(folder,"OTHER.SLDDRW"):path,reloadReason);
      if(mode=="duplicate-close-event")closing.DynamicInvoke(path,reloadReason);reloading=false;
      var values=call.Args;values[2]=native;return new System.Runtime.Remoting.Messaging.ReturnMessage(0,values,call.ArgCount,call.LogicalCallContext,call);
     case "GetDocumentDependencies2":if(mode=="prepare-failed")throw new IOException("隔离注入：原生引用读取失败");return Array.Empty<string>();
     case "ReplaceReferencedDocument":return false;
     case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;
     case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
     default:throw new Exception("不得切换或关闭模型："+call.MethodName);}
   }).GetTransparentProxy());
   ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,fixture.Manifest);
   SetField(plugin,"currentTree",drawing);
   var handler=new DrawingGetHandler(document,latest,old,requested,mode=="corrupt-download"?new byte[]{1,2}:bytes,session,()=>{
    if(mode=="late-memory-edit")dirty=true;
    if(mode=="late-disk-edit"){File.SetAttributes(path,FileAttributes.Normal);File.AppendAllText(path," new disk edit");}
   });
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://drawing-get.invalid/")})
   {
    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
    ((HttpClient)Field(fixture.Client,"httpClient")).Dispose();SetField(fixture.Client,"httpClient",http);SetField(fixture.Client,"controlledOpenHttpClient",http);
    SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",fixture.Client,fixture.DirectoryPath));
    var selected=Array.CreateInstance(Type("BatchOperationItem"),1);selected.SetValue(New("BatchOperationItem",drawing,0),0);
    Exception failure=null;try{await (Task)DiscardCall(plugin,"TryAcquireDrawingWorkingCopiesAsync",selected,project);}catch(Exception error){failure=error;}
    var success=mode=="loaded-clean"||mode=="duplicate-close-event"||mode=="isolated-copy"||mode=="unloaded-clean";
    File.WriteAllText(Path.Combine(output,mode+".txt"),failure?.ToString()??"success");
    File.WriteAllLines(Path.Combine(output,mode+"-calls.txt"),nativeCalls);
    Check((failure==null)==success,success?"旧版副本成功取得最新版并获取权限":"异常或修改状态安全停止，返回中文说明");
    if(success)
    {
     Check(WorkingReuseHash(path)==latestHash&&handler.Checkouts==1&&handler.Downloads==1,"仅取回一份工程图且仅检出该工程图");
     var args=new object[]{path,null};Check((bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args)&&Equals(Get(args[1],"VersionId"),Get(latest,"Id")),"本地版本记录更新为服务器新版");
     Check(reloads==(mode=="unloaded-clean"?0:1)&&(receivedOptions<0||(receivedOptions&2)==0),"专用重载不使用丢弃修改选项，未打开的工程图无需重载");
     await (Task)DiscardCall(plugin,"TryAcquireDrawingWorkingCopiesAsync",selected,project);
     Check(handler.Downloads==1&&reloads==(mode=="unloaded-clean"?0:1),"再次获取复用当前新版，不反复下载或重载");
    }
    else
    {
     Check(handler.Checkouts==0&&failure.Message.Any(c=>c>='\u4e00'&&c<='\u9fff'),"失败时不占用编辑权限，提示使用中文");
     var preserved=mode=="late-disk-edit"?File.ReadAllText(path).EndsWith(" new disk edit"):mode=="wrong-second-sheet"?WorkingReuseHash(path)==latestHash:WorkingReuseHash(path)==originalHash;
     Check(preserved,"本地修改或原副本保留，引用验证失败的新副本保持只读");
     var args=new object[]{path,null};Check((bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args)
      &&Equals(Get(args[1],"VersionId"),mode=="wrong-second-sheet"?Get(latest,"Id"):Get(old,"Id")),"失败后的版本记录与保留文件对应，不冒充检出成功");
    }
    Check(WorkingReuseHash(fixture.CurrentPath)==partHash&&WorkingReuseHash((string)Get(total,"FullPath"))==totalHash&&modelsSaved==0
     &&(File.GetAttributes(fixture.CurrentPath)&FileAttributes.ReadOnly)!=0&&(File.GetAttributes((string)Get(total,"FullPath"))&FileAttributes.ReadOnly)!=0,"模型与总装的内容、只读属性、未保存状态均未操作");
    Check(closing==null,"工程图专用重载事件已解除，避免重复回调");
    fixture.AssertOldUntouched();
   }
  }
 }

 sealed class DrawingGetHandler:HttpMessageHandler
 {
  readonly object document,latest,old,manifest;readonly byte[] bytes;readonly Guid session;readonly Action duringDownload;
  public int Checkouts,Downloads;
  public DrawingGetHandler(object document,object latest,object old,object manifest,byte[] bytes,Guid session,Action duringDownload)
  {this.document=document;this.latest=latest;this.old=old;this.manifest=manifest;this.bytes=bytes;this.session=session;this.duringDownload=duringDownload;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/versions"))value=new[]{latest,old};
   else if(path.EndsWith("/open-manifest"))value=manifest;
   else if(path.EndsWith("/documents"))value=new[]{document};
   else if(path.EndsWith("/file")){Downloads++;duringDownload();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}
   else if(path.EndsWith("/checkout")){Checkouts++;Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);Set(document,"CheckedOutAt",DateTime.UtcNow);value=document;}
   else if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=Array.Empty<string>()};
   else throw new Exception("Unexpected business request "+path);
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
