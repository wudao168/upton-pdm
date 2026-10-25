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
using System.Windows.Forms;

internal static partial class Program
{
 static async Task LatestReadOnlyWorkingScenario(bool permissionOnly=false)
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-latest-working-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var previousBindings=bindings.GetValue(null);var previousRoot=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in permissionOnly?new[]{"LatestReadOnly-handler","LatestReadOnly-handler-structural","LatestReadOnly-handler-cache-missing"}:new[]{"LatestReadOnly","LatestReadOnly-owned","SpecificReadOnly","LatestReleased"})
   {
    var folder=Path.Combine(directory,mode);Directory.CreateDirectory(folder);
    var plugin=New("PdmAddin");var project=Guid.NewGuid();var rootId=Guid.NewGuid();var versionId=Guid.NewGuid();
    var bytes=Encoding.UTF8.GetBytes("immutable server baseline "+mode);var seed=Path.Combine(folder,"server-seed");File.WriteAllBytes(seed,bytes);var hash=WorkingReuseHash(seed);
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","QA-LATEST");Set(manifest,"RootDocumentId",rootId);Set(manifest,"RootVersionId",versionId);Set(manifest,"RootRelativePath","ROOT.SLDPRT");Set(manifest,"RootRevision","W1");Set(manifest,"ForEdit",false);
    var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    for(var i=0;i<214;i++)
    {
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",i==0?rootId:Guid.NewGuid());Set(file,"VersionId",i==0?versionId:Guid.NewGuid());Set(file,"FileName",i==0?"ROOT.SLDPRT":"PART-"+i+".SLDPRT");Set(file,"RelativePath",Get(file,"FileName"));Set(file,"Sha256",hash);Set(file,"FileLength",(long)bytes.Length);Set(file,"Configuration","Default");Set(file,"Revision","W1");Set(file,"IsRoot",i==0);files.Add(file);
    }
    Set(manifest,"Files",files);
    var document=New("DocumentDto");Set(document,"Id",rootId);Set(document,"ProjectId",project);Set(document,"FileName","ROOT.SLDPRT");Set(document,"CheckedOutAt",DateTime.UtcNow);
    var session=(Guid)Field(plugin,"checkoutSessionId");
    if(mode.EndsWith("owned")){Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);}
    var version=new{id=versionId,sha256=hash,fileLength=(long)bytes.Length,referenceSnapshot=new{documentId=rootId,fileName="ROOT.SLDPRT",kind=1,configuration="Default",children=new object[0]}};
    var handler=new LatestWorkingHandler(manifest,document,version,session,bytes);
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://latest-working.invalid/"))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://latest-working.invalid/")})
    using(var pane=(Control)New("PdmTaskPaneControl","最新只读工作副本回归"))
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
     SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,folder));SetField(plugin,"lifetime",lifetime);SetField(plugin,"taskPaneControl",pane);SetField(plugin,"authenticatedUsername","tester");
     SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),"fixture"));
     var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
     var openedPath="";var opens=0;var readOnly=true;var mutations=new List<string>();var searchRules=false;
     var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
     var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
     var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
      switch(call.MethodName){case "GetPathName":return openedPath;case "GetTitle":return "ROOT.SLDPRT";case "GetType":return 1;case "IsOpenedReadOnly":return readOnly;case "GetSaveFlag":return false;case "get_ConfigurationManager":return manager;case "SetReadOnlyState":mutations.Add("SetReadOnlyState:"+call.Args[0]);readOnly=(bool)call.Args[0];return true;default:throw new Exception("Unexpected model mutation "+call.MethodName);}
     }).GetTransparentProxy();
     SetField(plugin,"application",new Proxy(appType,call=>{
      switch(call.MethodName){case "GetDocuments":return opens==0?new object[0]:new[]{model};case "get_ActiveDoc":return opens==0?null:model;case "OpenDoc6":opens++;openedPath=(string)call.Args[0];if(((int)call.Args[2]&2)==0)throw new Exception("Latest open omitted native read-only option");return model;case "GetDocumentDependencies2":return new string[0];case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;case "GetUserPreferenceToggle":return searchRules;case "SetUserPreferenceToggle":searchRules=(bool)call.Args[1];return true;default:mutations.Add(call.MethodName);throw new Exception("Unexpected CAD mutation "+call.MethodName);}
     }).GetTransparentProxy());
     var openMode=mode.StartsWith("LatestReadOnly")?"LatestReadOnly":mode;
     var task=(Task)Type("PdmAddin").GetMethod("ExecuteControlledOpenAsync",Flags).Invoke(plugin,new object[]{project,rootId,Enum.Parse(Type("ControlledOpenMode"),openMode),mode=="SpecificReadOnly"?(object)versionId:null,null,null,false});
     await task;if(!(bool)Get(task,"Result"))throw new Exception("Controlled open preparation failed: "+mode);
     for(var i=0;i<200&&(int)Field(plugin,"openOperationInProgress")!=0;i++)await Task.Delay(10);
     var contexts=(IDictionary)Field(plugin,"controlledOpenManifests");if(contexts.Count!=1||opens!=1||(int)Field(plugin,"openOperationInProgress")!=0)throw new Exception("Initial native open did not finish exactly once: "+mode);
     var enumerator=contexts.GetEnumerator();enumerator.MoveNext();var context=enumerator.Entry;var prepared=Path.Combine((string)context.Key,"ROOT.SLDPRT");
     var latest=openMode=="LatestReadOnly";
     if(openedPath!=prepared||((string)context.Key).IndexOf(Path.DirectorySeparatorChar+"View"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)>=0!=latest)throw new Exception("Wrong working/snapshot route: "+mode+" "+prepared);
     if(handler.Checkouts!=0||handler.DocumentReads!=0||handler.ForEditRequests.Any(value=>value)||!readOnly||mutations.Count!=0)throw new Exception("Read-only open acquired or restored edit permissions: "+mode);
     foreach(var file in files.Cast<object>())
     {
      var path=Path.Combine((string)context.Key,(string)Get(file,"FileName"));
      if(WorkingReuseHash(path)!=hash||(File.GetAttributes(path)&FileAttributes.ReadOnly)==0)throw new Exception("A prepared file was changed or writable before acquire: "+mode);
     }
     Console.WriteLine("PASS: actual controlled-open entry "+mode+"; 214 verified read-only files, one native open, no checkout; route="+(latest?"working":"snapshot"));
     if(latest)
     {
      var node=New("CadTreeNode");Set(node,"FullPath",prepared);Set(node,"FileName","ROOT.SLDPRT");Set(node,"DocumentId",rootId);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(node,"Configuration","Default");SetField(plugin,"currentTree",node);
      var selected=Array.CreateInstance(Type("BatchOperationItem"),1);selected.SetValue(New("BatchOperationItem",node,0),0);
      var downloadsBefore=handler.Downloads;
      if(permissionOnly)
      {
       using(var refresh=new PermissionRefreshFixture(plugin,node,model,client,project,mode.EndsWith("cache-missing")))
       {
        if(mode.EndsWith("structural"))handler.OnCheckout=refresh.StructuralChange;
        Type("PdmAddin").GetMethod("OnCheckoutRequested",Flags).Invoke(plugin,new[]{(object)null,New("CadTreeNodeEventArgs",node)});
        for(var i=0;i<300&&(int)Field(plugin,"workspaceOperationInProgress")!=0;i++)await Task.Delay(10);
        refresh.AssertAfterHandler();
        if(handler.LimitedVersionReads!=2)throw new Exception("Stored-version existence checks fetched full histories");
       }
      }
      else{var acquire=(Task)Type("PdmAddin").GetMethod("AcquireExplicitlyFromPlmAsync",Flags).Invoke(plugin,new object[]{selected,project,null});await acquire;}
      if(opens!=1||openedPath!=prepared||readOnly||handler.Checkouts!=1||handler.Downloads!=downloadsBefore||mutations.Count!=1||mutations[0]!="SetReadOnlyState:False")throw new Exception("First permission acquisition reopened or replaced the loaded context: "+mode);
      foreach(var file in files.Cast<object>())
      {
       var path=Path.Combine((string)context.Key,(string)Get(file,"FileName"));var writable=(File.GetAttributes(path)&FileAttributes.ReadOnly)==0;
       if(WorkingReuseHash(path)!=hash||writable!=((Guid)Get(file,"DocumentId")==rootId))throw new Exception("Acquisition changed bytes or unlocked an unselected file: "+mode);
      }
      Console.WriteLine("PASS: actual first acquire after "+mode+"; same loaded document/path, no reopen/download/save, only selected file unlocked, other 213 hashes and read-only flags retained");
     }
    }
   }
  }
  finally
  {
   bindings.SetValue(null,previousBindings);resolver.SetValue(null,previousRoot);
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected QA path");Directory.Delete(directory,true);
  }
 }

 sealed class LatestWorkingHandler:HttpMessageHandler
 {
  readonly object manifest,document,version;readonly Guid session;readonly byte[] bytes;
  public int Checkouts,Downloads,DocumentReads,LimitedVersionReads;public Action OnCheckout;public readonly List<bool> ForEditRequests=new List<bool>();
  public LatestWorkingHandler(object manifest,object document,object version,Guid session,byte[] bytes){this.manifest=manifest;this.document=document;this.version=version;this.session=session;this.bytes=bytes;}
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;var json=new JavaScriptSerializer();object value;
   if(path.EndsWith("/file")){Downloads++;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/open-manifest"))
   {
    var body=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());var forEdit=(bool)body["forEdit"];ForEditRequests.Add(forEdit);
    value=json.Deserialize(json.Serialize(manifest),manifest.GetType());Set(value,"ForEdit",forEdit);
   }
   else if(path.EndsWith("/documents")){DocumentReads++;value=new[]{document};}
   else if(path.EndsWith("/versions")){if(request.RequestUri.Query=="?take=1")LimitedVersionReads++;value=new[]{version};}
   else if(path.EndsWith("/document-relations"))value=new object[0];
   else if(path.EndsWith("/checkout")){OnCheckout?.Invoke();Checkouts++;Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);value=document;}
   else throw new Exception("Unexpected business request "+path);
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(value))};
  }
 }
}
