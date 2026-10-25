using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task AssemblyNoopDiscardScenario(string capture)
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-asm-discard-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in new[]{"old-addresses","child-dirty","pending-retry","foreign-source","wrong-version","server-denied","unconfirmed-release","saved-edit","rewrite-failure","no-pending-proof"})
   {
    var folder=Path.Combine(directory,mode,"View");Directory.CreateDirectory(folder);
    var json=new JavaScriptSerializer();var manifest=json.Deserialize(File.ReadAllText(Path.Combine(capture,"real-working-manifest.json")),Type("ControlledOpenManifestDto"));
    var project=(Guid)Get(manifest,"ProjectId");var rootId=(Guid)Get(manifest,"RootDocumentId");var files=((IEnumerable)Get(manifest,"Files")).Cast<object>().ToArray();
    if(files.Length!=216)throw new Exception("真实样本范围不是216文件");
    foreach(var file in files)
    {
     var path=Path.Combine(folder,(string)Get(file,"RelativePath"));
     if((Guid)Get(file,"DocumentId")==rootId)File.Copy(Path.Combine(capture,"canonical-F0190002.10.SLDASM"),path);else File.WriteAllText(path,"isolated companion "+Get(file,"FileName"));
     Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);
     if(!(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")}))throw new Exception("无法创建隔离基准关联");
     File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    var rootFile=files.Single(file=>(Guid)Get(file,"DocumentId")==rootId);var rootPath=Path.Combine(folder,(string)Get(rootFile,"FileName"));var canonical=File.ReadAllBytes(rootPath);
    var pending=mode=="pending-retry"||mode=="no-pending-proof";var changed=mode=="saved-edit"||mode=="rewrite-failure";
    File.SetAttributes(rootPath,FileAttributes.Normal);if(changed)File.AppendAllText(rootPath," saved real engineering edit");
    var plugin=New("PdmAddin");var session=(Guid)Field(plugin,"checkoutSessionId");var root=New("CadTreeNode");
    Set(root,"DocumentId",rootId);Set(root,"FullPath",rootPath);Set(root,"FileName",Get(rootFile,"FileName"));Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(root,"WorkState",Enum.Parse(Type("CadWorkState"),"Editable"));Set(root,"CheckedOutBy","tester");Set(root,"CheckoutSessionId",session);
    SetField(plugin,"currentTree",root);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var rootLoaded=!pending;var rootReadOnly=false;var rootDirty=changed;var closes=0;var opens=0;var rewrites=0;var dependencyReads=0;var unexpected=new List<string>();var rewritten=false;
    var models=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);var componentModels=new List<Tuple<object,string>>();var components=new List<object>();
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var config=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"用户当前配置":throw new Exception(call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?config:throw new Exception(call.MethodName)).GetTransparentProxy();
    object ModelCall(System.Runtime.Remoting.Messaging.IMethodCallMessage call,string path,int kind,bool isRoot)
    {
     switch(call.MethodName){case "get_Visible":return isRoot;case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return kind;case "GetSaveFlag":return isRoot?rootDirty:mode=="child-dirty"&&path==componentModels.FirstOrDefault()?.Item2;
      case "IsOpenedReadOnly":return isRoot?rootReadOnly:!(mode=="child-dirty"&&path==componentModels.FirstOrDefault()?.Item2);case "get_ConfigurationManager":return manager;
      case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;case "GetComponents":return isRoot?components.ToArray():Array.Empty<object>();
      case "SetReadOnlyState":if(!isRoot)throw new Exception("放弃总装改动了其他组件权限");rootReadOnly=(bool)call.Args[0];return true;
      default:unexpected.Add(call.MethodName);throw new Exception("Unexpected native mutation "+call.MethodName);}
    }
    foreach(var file in files)
    {
     var path=Path.Combine(folder,(string)Get(file,"FileName"));var isRoot=(Guid)Get(file,"DocumentId")==rootId;
     var kind=Path.GetExtension(path).Equals(".SLDASM",StringComparison.OrdinalIgnoreCase)?2:Path.GetExtension(path).Equals(".SLDDRW",StringComparison.OrdinalIgnoreCase)?3:1;
     if(!isRoot&&mode=="foreign-source"&&componentModels.Count==0)path=Path.Combine(directory,"foreign",Path.GetFileName(path));
     object model=kind==2?new DiscardAssemblyProxy(modelType,call=>ModelCall(call,path,kind,isRoot)).GetTransparentProxy():new SheetReferenceModelProxy(modelType,kind==3,call=>call.MethodName=="GetViews"?Array.Empty<object>():ModelCall(call,path,kind,isRoot)).GetTransparentProxy();
     models.Add(path,model);
     if(!isRoot&&kind!=3)
     {
      componentModels.Add(Tuple.Create(model,path));
      components.Add(new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
       if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
       return call.MethodName=="GetModelDoc2"?model:call.MethodName=="GetPathName"?path:call.MethodName=="get_IsVirtual"?false:call.MethodName=="get_Name2"?Path.GetFileName(path):throw new Exception(call.MethodName);
      }).GetTransparentProxy());
     }
    }
    if(mode=="wrong-version")
    {
     var file=files.First(item=>(Guid)Get(item,"DocumentId")!=rootId);var path=Path.Combine(folder,(string)Get(file,"FileName"));
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Get(file,"DocumentId"),project,Guid.NewGuid(),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")});
    }
    // The stored-address check needs one stale direct dependency. Actual loaded
    // sources still cover all 216 files; do not simulate every deep file as a
    // direct root reference and repeatedly hash the 30 MB assembly for each one.
    var nativeFiles=files.Where(file=>(Guid)Get(file,"DocumentId")!=rootId&&!((string)Get(file,"FileName")).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
    SetField(plugin,"application",new Proxy(appType,call=>{
     switch(call.MethodName){case "GetDocuments":return pending?Array.Empty<object>():models.Where(pair=>rootLoaded||pair.Key!=rootPath).Select(pair=>pair.Value).ToArray();case "get_ActiveDoc":return rootLoaded?models[rootPath]:null;
      case "GetDocumentDependencies2":dependencyReads++;return nativeFiles.SelectMany(file=>new[]{(string)Get(file,"FileName"),Path.Combine(rewritten?folder:Path.Combine(directory,"old-stored-address"),(string)Get(file,"FileName"))}).ToArray();
      case "CloseDoc":if((string)call.Args[0]!=(string)Get(rootFile,"FileName"))throw new Exception("关闭了无关组件");closes++;rootLoaded=false;return null;
      case "OpenDoc6":if((string)call.Args[0]!=rootPath||(string)call.Args[3]!="用户当前配置")throw new Exception("重新打开了错误文件或丢失原配置");opens++;rootLoaded=true;rootReadOnly=true;rootDirty=false;return models[rootPath];
      case "ReplaceReferencedDocument":if((string)call.Args[0]!=rootPath)throw new Exception("引用改写越界");rewrites++;if(mode=="saved-edit"){rewritten=true;File.AppendAllText(rootPath,"|verified reference rewrite");}return true;
      default:unexpected.Add(call.MethodName);throw new Exception("Unexpected application mutation "+call.MethodName);}
    }).GetTransparentProxy());
    var before=files.Where(file=>(Guid)Get(file,"DocumentId")!=rootId).ToDictionary(file=>Path.Combine(folder,(string)Get(file,"FileName")),file=>Tuple.Create(WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName"))),File.GetAttributes(Path.Combine(folder,(string)Get(file,"FileName")))));
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://asm-discard.invalid/"))
    using(var handler=new AssemblyDiscardHandler(manifest,canonical,session,mode))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://asm-discard.invalid/")})
    using(var pane=(Control)New("PdmTaskPaneControl","总装未修改放弃回归"))
    {
     pane.CreateControl();http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
     SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);SetField(plugin,"taskPaneControl",pane);
     var scope=(string)Get(client,"EditJournalScope")+"|tester";var journal=New("EditDraftJournal",Path.Combine(folder,"journal"),scope);SetField(plugin,"editJournal",journal);SetField(plugin,"editJournalScope",scope);
     var entry=New("LocalEditDraft");Set(entry,"DocumentId",rootId);Set(entry,"ProjectId",project);Set(entry,"SessionId",session);Set(entry,"SourcePath",rootPath);Set(entry,"BaseVersionId",Get(rootFile,"VersionId"));Set(entry,"BaseSha256",Get(rootFile,"Sha256"));Set(entry,"ObservedOpen",mode!="no-pending-proof");Set(entry,"UnsavedChanges",changed);
     DiscardCall(journal,"Save",entry);((IDictionary)Field(plugin,"localEdits"))[rootId]=entry;
     if(pending){DiscardCall(journal,"MarkDiscarded",entry);Set(root,"WorkState",Enum.Parse(Type("CadWorkState"),"None"));}
     Exception failure=null;
     try{if(pending)await (Task)DiscardCall(plugin,"RestoreDiscardedWorkingPathsAsync",manifest,new[]{rootPath});else await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",DiscardNodes(root));}catch(Exception error){failure=error;}
     var shouldFail=new[]{"foreign-source","wrong-version","server-denied","unconfirmed-release","rewrite-failure","no-pending-proof"}.Contains(mode);
     if((failure!=null)!=shouldFail)throw new Exception("总装放弃结果异常 "+mode+" closes="+closes+" downloads="+handler.Downloads,failure);
     if(!changed&&!mode.Equals("no-pending-proof")&&(closes!=0||opens!=0||rewrites!=0||handler.Downloads!=0))throw new Exception("未操作总装仍被关闭、重新下载或改写引用 "+mode);
     if(mode=="old-addresses"||mode=="child-dirty"||mode=="pending-retry")
     {
      if(dependencyReads!=0||!File.ReadAllBytes(rootPath).SequenceEqual(canonical)||(!pending&&!rootReadOnly))throw new Exception("旧引用字符串仍触发恢复或原窗口未原位只读 "+mode);
     }
     if(mode=="saved-edit"&&(closes!=1||opens!=1||handler.Downloads!=1||rewrites==0||!rootReadOnly))throw new Exception("真实修改没有按原流程恢复及返回原总装窗口");
     if(mode=="old-addresses"||mode=="saved-edit")
     {
      var counts=new[]{closes,opens,handler.Downloads,handler.Discards,rewrites};
      await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",DiscardNodes(root));
      if(!counts.SequenceEqual(new[]{closes,opens,handler.Downloads,handler.Discards,rewrites})||!rootLoaded||!rootReadOnly)
       throw new Exception("总装第二次放弃再次关闭窗口或恢复文件 "+mode);
     }
     var records=((IEnumerable)DiscardCall(journal,"ReadAll")).Cast<object>().ToArray();var pendingState=(bool)Get(records.Single(),"DiscardPending");
     if(pendingState!=(shouldFail&&!new[]{"server-denied","unconfirmed-release"}.Contains(mode)))throw new Exception("待恢复记录被错误清除 "+mode);
     if(!pending&&!(mode=="server-denied"||mode=="unconfirmed-release")&&(string)Get(root,"WorkState").ToString()=="Editable")throw new Exception("服务器已释放权限仍显示可编辑");
     foreach(var pair in before)if(WorkingReuseHash(pair.Key)!=pair.Value.Item1||File.GetAttributes(pair.Key)!=pair.Value.Item2)throw new Exception("改动了其他组件文件 "+mode+" "+pair.Key);
     if(unexpected.Count!=0||handler.OtherWrites!=0)throw new Exception("额外原生操作或上传写入 "+mode+" "+string.Join(",",unexpected));
     if(failure!=null&&!failure.Message.Any(character=>character>='\u4e00'&&character<='\u9fff'))throw new Exception("错误提示未中文化 "+mode);
     Console.WriteLine("PASS: F019 assembly discard "+mode+"; actual 216-file manifest and canonical root replay, other files protected");
    }
   }
  }
  finally
  {
   bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(directory,true);
  }
 }

 sealed class AssemblyDiscardHandler:HttpMessageHandler
 {
  readonly object manifest;readonly byte[] canonical;readonly Guid session;readonly string mode;public int Downloads,OtherWrites,Discards;
  public AssemblyDiscardHandler(object manifest,byte[] canonical,Guid session,string mode){this.manifest=manifest;this.canonical=canonical;this.session=session;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/versions"))value=new[]{new{id=Get(manifest,"RootVersionId")}};
   else if(path.EndsWith("/documents"))value=new[]{new{id=Get(manifest,"RootDocumentId"),projectId=Get(manifest,"ProjectId"),kind=0,drawingVersion="V5",publishedVersion="A"}};
   else if(path.EndsWith("/discard-checkout"))
   {
    Discards++;
    if(mode=="server-denied")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"服务器尚未确认释放权限\"}")});
    value=new{id=Get(manifest,"RootDocumentId"),projectId=Get(manifest,"ProjectId"),kind=0,checkedOutBy=mode=="unconfirmed-release"?"tester":null,checkoutSessionId=mode=="unconfirmed-release"?(Guid?)session:null,drawingVersion="V5",publishedVersion="A"};
   }
   else if(path.EndsWith("/open-manifest"))value=manifest;
   else if(path.EndsWith("/document-relations"))value=new object[0];
   else if(path.EndsWith("/file")){Downloads++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(canonical)});}
   else{OtherWrites++;throw new Exception("Unexpected business request "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
