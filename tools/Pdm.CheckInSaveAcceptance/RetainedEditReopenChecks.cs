using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
 static async Task RetainedEditReopenScenario(string output,string realDraft=null)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var savedBindings=bindings.GetValue(null);var savedResolver=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  try
  {
   foreach(var mode in new[]{"old-session","same-session","annotation-only","different-user","different-machine","server-version-changed","wrong-path","unknown-source","old-process-running","discard-pending","released-lease","checkout-refused","manifest-changed","partial-retry",
    "closed-preserved","closed-cache-missing","closed-partial-cache","closed-drawing-only","closed-wrong-version","closed-wrong-user","closed-other-machine","closed-corrupt-draft","closed-newer-source","closed-checkout-refused","closed-partial-retry","closed-manifest-changed","closed-live-owner","closed-discarded"}.Concat(realDraft==null?new string[0]:new[]{"closed-real-draft"}))
   {
    var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);var plugin=New("PdmAddin");var session=(Guid)Field(plugin,"checkoutSessionId");var oldSession=mode=="same-session"?session:Guid.NewGuid();var project=Guid.NewGuid();
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","QA-RESUME");Set(manifest,"RootRelativePath","ROOT.SLDASM");
    var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    var bytes=Encoding.UTF8.GetBytes("immutable server native bytes");var seed=Path.Combine(folder,"seed");File.WriteAllBytes(seed,bytes);var hash=WorkingReuseHash(seed);File.Delete(seed);
    foreach(var name in new[]{"ROOT.SLDASM","PART.SLDPRT","DRAWING.SLDDRW"}){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Revision","W2");Set(file,"Configuration","Default");Set(file,"Sha256",hash);Set(file,"FileLength",(long)bytes.Length);files.Add(file);}
    Set(manifest,"Files",files);Set(files[0],"IsRoot",true);Set(manifest,"RootDocumentId",Get(files[0],"DocumentId"));Set(manifest,"RootVersionId",Get(files[0],"VersionId"));
    var machine=(string)Field(plugin,"checkoutMachineName");var previousTime=new DateTime(2026,10,27,10,37,11,DateTimeKind.Utc);var documents=new List<object>();
    foreach(var index in new[]{1,2}){var document=New("DocumentDto");Set(document,"Id",Get(files[index],"DocumentId"));Set(document,"ProjectId",project);Set(document,"FileName",Get(files[index],"FileName"));Set(document,"Kind",index==1?1:2);Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",oldSession);Set(document,"CheckoutMachine",machine);Set(document,"CheckedOutAt",previousTime);documents.Add(document);}
    var handler=new RetainedEditHandler(manifest,documents,bytes,session,machine,mode);
    using(var client=(IDisposable)New("PdmApiClient","http://retained.invalid/"))using(var http=new HttpClient(handler){BaseAddress=new Uri("http://retained.invalid/")})using(var lifetime=new CancellationTokenSource())
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
     var manager=New("ControlledWorkspaceManager",client,folder);var canonical=Path.Combine(folder,"View","QA-RESUME","装配工作区","plm-"+((Guid)Get(manifest,"RootDocumentId")).ToString("N"));
     ((IDictionary)Field(manager,"serverWorkingDirectories")).Add(Get(manifest,"RootDocumentId"),canonical);
     var prepared=(Task)TreeCall(manager,"PrepareWorkingCopyAsync",manifest,new Guid[0],CancellationToken.None);await prepared;var rootPath=(string)Get(prepared,"Result");
     var scope=(string)Get(client,"EditJournalScope")+"|tester";var journal=New("EditDraftJournal",Path.Combine(folder,"journal"),scope);
     var savedEntries=new List<object>();
     SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",manager);SetField(plugin,"lifetime",lifetime);SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"editJournal",journal);SetField(plugin,"editJournalScope",scope);
     foreach(var index in new[]{1,2})
     {
      var file=files[index];var entry=New("LocalEditDraft");Set(entry,"DocumentId",Get(file,"DocumentId"));Set(entry,"ProjectId",project);Set(entry,"SessionId",oldSession);Set(entry,"CheckedOutAt",previousTime);Set(entry,"BaseVersionId",Get(file,"VersionId"));Set(entry,"BaseSha256",hash);Set(entry,"SourcePath",Path.Combine(canonical,(string)Get(file,"FileName")));Set(entry,"ProcessId",999999999);Set(entry,"ProcessStartedUtcTicks",1L);
      if(index==1&&mode=="wrong-path")Set(entry,"SourcePath",Path.Combine(folder,"original","PART.SLDPRT"));
      if(index==1&&mode=="discard-pending")Set(entry,"DiscardPending",true);
      if(index==1&&mode=="old-process-running")using(var process=Process.GetCurrentProcess()){Set(entry,"ProcessId",process.Id);Set(entry,"ProcessStartedUtcTicks",process.StartTime.ToUniversalTime().Ticks);}
      if(index!=1||mode!="unknown-source"){TreeCall(journal,"Save",entry);savedEntries.Add(entry);}
     }
     var edited=Path.Combine(canonical,mode=="annotation-only"||mode=="closed-drawing-only"?"DRAWING.SLDDRW":"PART.SLDPRT");File.SetAttributes(edited,FileAttributes.Normal);if(mode=="closed-real-draft")File.Copy(realDraft,edited,true);else File.AppendAllText(edited," saved user feature or annotation edit");var changedHash=WorkingReuseHash(edited);
     if(mode.StartsWith("closed-"))
     {
      foreach(var entry in savedEntries){Set(entry,"Closed",true);Set(entry,"DiskSha256",WorkingReuseHash((string)Get(entry,"SourcePath")));if((string)Get(entry,"SourcePath")==edited)TreeCall(journal,"Preserve",entry,edited,true);TreeCall(journal,"Save",entry);}
      foreach(var document in documents){Set(document,"CheckedOutBy",null);Set(document,"CheckoutSessionId",null);Set(document,"CheckedOutAt",null);Set(document,"CheckoutMachine",null);}
      var changedEntry=savedEntries.Single(entry=>(string)Get(entry,"SourcePath")==edited);
      if(mode=="closed-wrong-version")Set(files[1],"VersionId",Guid.NewGuid());
      if(mode=="closed-wrong-user"){Set(documents[0],"CheckedOutBy","another-user");Set(documents[0],"CheckoutSessionId",Guid.NewGuid());}
      if(mode=="closed-other-machine"){Set(documents[0],"CheckedOutBy","tester");Set(documents[0],"CheckoutSessionId",oldSession);Set(documents[0],"CheckoutMachine","OTHER-PC");}
      if(mode=="closed-corrupt-draft")File.AppendAllText((string)Get(changedEntry,"DraftPath"),"corrupted recovery");
      if(mode=="closed-newer-source"){File.AppendAllText(edited," newer unsnapshotted edit");changedHash=WorkingReuseHash(edited);}
      if(mode=="closed-live-owner")using(var process=Process.GetCurrentProcess()){Set(changedEntry,"ProcessId",process.Id);Set(changedEntry,"ProcessStartedUtcTicks",process.StartTime.ToUniversalTime().Ticks);TreeCall(journal,"Save",changedEntry);}
      if(mode=="closed-discarded"){Set(changedEntry,"DiscardPending",true);TreeCall(journal,"Save",changedEntry);}
      if(mode=="closed-cache-missing"||mode=="closed-real-draft"||mode=="closed-partial-cache")foreach(var file in files){var path=Path.Combine(canonical,(string)Get(file,"FileName"));if(mode=="closed-partial-cache"&&path==edited)continue;File.SetAttributes(path,FileAttributes.Normal);File.Delete(path);}
      var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
      SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new object[0]:throw new Exception("恢复前不得操作SolidWorks模型："+call.MethodName)).GetTransparentProxy());
     }
     if(mode=="different-user")Set(documents[0],"CheckedOutBy","another-user");if(mode=="different-machine")Set(documents[0],"CheckoutMachine","ANOTHER-PC");
     if(mode=="released-lease"){Set(documents[0],"CheckedOutBy",null);Set(documents[0],"CheckoutSessionId",null);}
     if(mode=="server-version-changed")Set(files[1],"VersionId",Guid.NewGuid());
     handler.Downloads=0;Exception failure=null;object result=null;
     try
     {
      var resume=Type("PdmAddin").GetMethod("ResumeRetainedWorkingCopyAsync",Flags);
      if(resume!=null){var task=(Task)resume.Invoke(plugin,new[]{manifest});try{await task;}catch(Exception)when(mode.EndsWith("partial-retry")){handler.AllowRetry=true;task=(Task)resume.Invoke(plugin,new[]{manifest});await task;}result=Get(task,"Result");}
      var ids=result==null?new Guid[0]:((IEnumerable)result).Cast<object>().Select(document=>(Guid)Get(document,"Id")).ToArray();
      var taskPrepare=(Task)TreeCall(manager,"PrepareWorkingCopyAsync",manifest,ids,CancellationToken.None);await taskPrepare;
      Check((string)Get(taskPrepare,"Result")==rootPath,mode+"：在原固定工作目录续接，不另建整套副本");
     }
     catch(Exception error){failure=error is System.Reflection.TargetInvocationException wrapped?wrapped.InnerException:error;}
     var success=new[]{"old-session","same-session","annotation-only","partial-retry","closed-preserved","closed-cache-missing","closed-partial-cache","closed-drawing-only","closed-partial-retry","closed-real-draft"}.Contains(mode);
     Check((failure==null)==success,mode+"：只续接同账号、同电脑、原版本且旧进程已退出的既有权限"+(failure==null?"":"；"+failure.Message));
     Check(WorkingReuseHash(edited)==changedHash,mode+"：保存的特征或标注修改始终保留");
     var expectedDownloads=mode=="closed-cache-missing"||mode=="closed-real-draft"?3:mode=="closed-partial-cache"?2:0;
     Check(handler.Downloads==expectedDownloads&&handler.UnexpectedWrites==0,mode+"：只补齐缺少的服务器基准，不提交版本或丢弃草稿；download="+handler.Downloads+" unexpected="+handler.LastUnexpected);
     if(success)
     {
      var expectedLeases=mode=="closed-drawing-only"?1:2;
      Check(handler.Checkouts==(mode=="same-session"?0:mode.EndsWith("partial-retry")?3:expectedLeases),mode+"：仅恢复需要的零件和配套工程图权限");
      Check(((IDictionary)Field(plugin,"localEdits")).Count==expectedLeases,mode+"：当前会话恢复记录已持久保存");
      Check((string)TreeCall(manager,"ApplyWorkingPermissions",manifest,((IEnumerable)result).Cast<object>().Select(document=>(Guid)Get(document,"Id")).ToArray(),false)==rootPath
       &&WorkingReuseHash(edited)==changedHash,mode+"：显式获取编辑权限时也接受已核对工作副本，不覆盖或重建目录");
      var arguments=new object[]{edited,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,arguments);
      Check((string)Get(arguments[1],"Sha256")==hash&&(string)Get(arguments[1],"ResolvedReferenceSha256")!=changedHash,mode+"：原服务器指纹不变，不把业务修改登记为引用调整");
      var file=files[mode=="annotation-only"||mode=="closed-drawing-only"?2:1];var materializer=Type("ControlledReferenceMaterializer");
      Check(!(bool)materializer.GetMethod("Matches",Flags).Invoke(null,new[]{(object)edited,file}),mode+"：原生打开范围之外仍识别为待提交修改");
      using(var retained=(IDisposable)New("ControlledWorkingCopyScope",rootPath,manifest))
      {
       Check((bool)materializer.GetMethod("Matches",Flags).Invoke(null,new[]{(object)edited,file}),mode+"：只在已核对的续接打开范围内接受保存的修改");
       var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
       var app=new Proxy(appType,call=>call.MethodName=="GetDocuments"?new object[0]:call.MethodName=="GetDocumentDependencies2"?((string)call.Args[0]==edited?throw new Exception("不得重定位或读取已修改图档的旧引用"):new string[0]):throw new Exception("不得调用模型改写接口："+call.MethodName)).GetTransparentProxy();
       materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{app,(object)rootPath,manifest});
       Check(WorkingReuseHash(edited)==changedHash,mode+"：引用准备不改写已保存的用户修改");
      }
      Check(!(bool)materializer.GetMethod("Matches",Flags).Invoke(null,new[]{(object)edited,file}),mode+"：原生打开结束后立即恢复严格服务器校验");
      var json=new JavaScriptSerializer().Serialize(manifest);Check(!json.Contains("RetainedWorkingHashes")&&!json.Contains("ResumedDocumentIds"),mode+"：本机续接许可不进入服务器清单或缓存索引");
      var forged=json.Substring(0,json.Length-1)+",\"RetainedWorkingHashes\":{\""+Get(file,"DocumentId")+"\":\""+changedHash+"\"},\"ResumedDocumentIds\":[\""+Get(file,"DocumentId")+"\"]}";
      var untrusted=new JavaScriptSerializer().Deserialize(forged,manifest.GetType());
      Check(((IDictionary)Get(untrusted,"RetainedWorkingHashes")).Count==0&&((IEnumerable)Get(untrusted,"ResumedDocumentIds")).Cast<object>().Count()==0,mode+"：服务器响应不能注入本地修改放行许可");
      if(mode=="closed-real-draft")Check(WorkingReuseHash(realDraft)==changedHash&&new FileInfo(edited).Length==new FileInfo(realDraft).Length,"真实草稿的隔离副本恢复后逐字节校验一致，原恢复文件未改动");
      if(mode=="old-session"||mode=="annotation-only"||mode=="closed-cache-missing"||mode=="closed-drawing-only")
      {
       var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
       var readOnlyOption=(int)Enum.Parse(System.Reflection.Assembly.Load("SolidWorks.Interop.swconst").GetType("SolidWorks.Interop.swconst.swOpenDocOptions_e",true),"swOpenDocOptions_ReadOnly");
       var loaded=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);var nativeReadonly=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);var active="";var nativeOpens=0;
       var partPath=Path.Combine(canonical,"PART.SLDPRT");var drawingPath=Path.Combine(canonical,"DRAWING.SLDDRW");
       var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
       object ModelCall(System.Runtime.Remoting.Messaging.IMethodCallMessage call,string path,int kind)
       {
        switch(call.MethodName){case "GetHashCode":return path.GetHashCode();case "Equals":return loaded.TryGetValue(path,out var same)&&ReferenceEquals(same,call.Args[0]);case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return kind;case "GetSaveFlag":return path==edited;case "IsOpenedReadOnly":return !nativeReadonly.ContainsKey(path)||nativeReadonly[path];
         case "SetReadOnlyState":nativeReadonly[path]=(bool)call.Args[0];return true;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("不得保存、重建或改写原模型："+call.MethodName);}
       }
       var part=new SheetReferenceModelProxy(modelType,false,call=>ModelCall(call,partPath,1)).GetTransparentProxy();
       var component=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?part:call.MethodName=="GetPathName"?partPath:call.MethodName=="get_IsVirtual"?false:call.MethodName=="get_Name2"?"PART":throw new Exception(call.MethodName)).GetTransparentProxy();
       var total=new DiscardAssemblyProxy(modelType,call=>call.MethodName=="GetComponents"?new[]{component}:ModelCall(call,rootPath,2)).GetTransparentProxy();
       var view=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>call.MethodName=="get_ReferencedDocument"?part:call.MethodName=="GetReferencedModelName"?partPath:throw new Exception(call.MethodName)).GetTransparentProxy();
       var drawing=new SheetReferenceModelProxy(modelType,true,call=>call.MethodName=="GetViews"?new object[]{new object[]{view,view}}:ModelCall(call,drawingPath,3)).GetTransparentProxy();
       var search=false;var app=new Proxy(appType,call=>{
        switch(call.MethodName){case "GetDocuments":return loaded.Values.ToArray();case "get_ActiveDoc":return loaded.TryGetValue(active,out var current)?current:null;
         case "OpenDoc6":nativeOpens++;active=(string)call.Args[0];nativeReadonly[active]=((int)call.Args[2]&readOnlyOption)!=0;if(active==rootPath){loaded[rootPath]=total;loaded[partPath]=part;nativeReadonly[partPath]=false;return total;}if(active==drawingPath){loaded[drawingPath]=drawing;return drawing;}throw new Exception("不得打开其他目录的来源");
         case "GetDocumentDependencies2":return new string[0];case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;
         case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;default:throw new Exception("不得关闭旧窗口或改写原文件："+call.MethodName);}
       }).GetTransparentProxy();SetField(plugin,"application",app);
       TreeCall(plugin,"RememberControlledOpenManifest",manifest,canonical);
       var opened=TreeCall(plugin,"OpenControlledReadOnlyDocumentCore",rootPath,2,"",manifest,false,false);
       Check(ReferenceEquals(opened,total)&&nativeOpens==1&&loaded.Count==2,mode+"：原生打开阶段复用原目录并正常载入总装与零件");
       Check(nativeReadonly[partPath]==(mode=="closed-drawing-only")&&nativeReadonly[rootPath],mode+"：只恢复必要图档权限，总装继续只读");
       Check(WorkingReuseHash(edited)==changedHash,mode+"：原生加载及权限恢复后修改内容不变");
       var requested=new JavaScriptSerializer().Deserialize(new JavaScriptSerializer().Serialize(manifest),manifest.GetType());var requestedFiles=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
       foreach(var item in new[]{files[2],files[1]}){var copy=new JavaScriptSerializer().Deserialize(new JavaScriptSerializer().Serialize(item),item.GetType());Set(copy,"IsRoot",item==files[2]);requestedFiles.Add(copy);}
       Set(requested,"Files",requestedFiles);Set(requested,"RootDocumentId",Get(files[2],"DocumentId"));Set(requested,"RootVersionId",Get(files[2],"VersionId"));Set(requested,"RootRelativePath","DRAWING.SLDDRW");
       var opening=(Task)TreeCall(plugin,"TryOpenDrawingInLoadedContextAsync",requested,true);await opening;
       Check((bool)Get(opening,"Result")&&nativeOpens==2&&loaded.Count==3,mode+"：随后打开配套工程图也复用同一模型工作区");
       Check(WorkingReuseHash(edited)==changedHash,mode+"：配套工程图打开不丢失零件特征或已保存标注");
      }
     }
    }
   }
  }
  finally{bindings.SetValue(null,savedBindings);resolver.SetValue(null,savedResolver);}
 }
 sealed class RetainedEditHandler:HttpMessageHandler
 {
  readonly object manifest;readonly List<object> documents;readonly byte[] bytes;readonly Guid session;readonly string machine,mode;
  internal int Downloads,Checkouts,UnexpectedWrites;internal bool AllowRetry;internal string LastUnexpected;
  internal RetainedEditHandler(object manifest,List<object> documents,byte[] bytes,Guid session,string machine,string mode){this.manifest=manifest;this.documents=documents;this.bytes=bytes;this.session=session;this.machine=machine;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;var json=new JavaScriptSerializer();
   if(path.EndsWith("/file")){Downloads++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}
   if(path.EndsWith("/documents"))value=documents;
   else if(path.EndsWith("/document-relations"))value=new[]{new{modelDocumentId=Get(documents[0],"Id"),drawingDocumentId=Get(documents[1],"Id")}};
   else if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/versions")){var id=Guid.Parse(path.Split('/')[3]);var file=((IEnumerable)Get(manifest,"Files")).Cast<object>().Single(item=>(Guid)Get(item,"DocumentId")==id);value=new[]{new{id=Get(file,"VersionId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength")}};}
   else if(path.EndsWith("/checkout"))
   {
    Checkouts++;var id=Guid.Parse(path.Split('/')[3]);var document=documents.Single(item=>(Guid)Get(item,"Id")==id);
    if(mode.EndsWith("checkout-refused")||mode.EndsWith("partial-retry")&&!AllowRetry&&Checkouts==2)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent("{\"message\":\"会话续接暂未完成\"}")});
    Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);Set(document,"CheckedOutAt",new DateTime(2026,10,27,11,0,0,DateTimeKind.Utc));Set(document,"CheckoutMachine",machine);value=document;
   }
   else if(path.EndsWith("/open-manifest")){value=manifest;if(mode.EndsWith("manifest-changed")){value=json.Deserialize(json.Serialize(manifest),manifest.GetType());Set(((IList)Get(value,"Files"))[1],"VersionId",Guid.NewGuid());}}
   else{UnexpectedWrites++;LastUnexpected=path;throw new Exception("Unexpected business API "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(value))});
  }
 }
}
