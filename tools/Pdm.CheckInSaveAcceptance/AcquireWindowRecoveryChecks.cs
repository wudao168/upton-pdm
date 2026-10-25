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
 static async Task AcquireWindowRecoveryScenario(bool failureOnly)
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-acquire-window-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindingField=identity.GetField("bindingDirectory",Flags);var rootField=identity.GetField("workspaceRootResolver",Flags);
  var oldBinding=bindingField.GetValue(null);var oldRoot=rootField.GetValue(null);
  bindingField.SetValue(null,Path.Combine(directory,"bindings"));rootField.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in failureOnly?new[]{"prepare-lock","native-open-failure","native-invalid-source","fresh-success","dirty-fallback"}:
    new[]{"child-clean","child-readonly-dirty","child-writable-dirty","child-server-changed","prepare-lock","native-open-failure","native-invalid-source","fresh-success","dirty-fallback"})
   {
    var folder=Path.Combine(directory,mode);var original=Path.Combine(folder,"View","QA","original");Directory.CreateDirectory(original);
    var childMode=mode.StartsWith("child-");var rootName=childMode||mode=="native-invalid-source"?"ROOT.SLDASM":"ROOT.SLDPRT";
    var bytes=Encoding.UTF8.GetBytes("immutable archived geometry "+mode);var rootPath=Path.Combine(original,rootName);var childPath=Path.Combine(original,"PART.SLDPRT");
    File.WriteAllBytes(rootPath,bytes);File.WriteAllBytes(childPath,bytes);var hash=WorkingReuseHash(rootPath);
    var plugin=New("PdmAddin");var project=Guid.NewGuid();var rootId=Guid.NewGuid();var childId=Guid.NewGuid();var rootVersion=Guid.NewGuid();var childVersion=Guid.NewGuid();
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","QA");
    Set(manifest,"RootDocumentId",rootId);Set(manifest,"RootVersionId",rootVersion);Set(manifest,"RootRelativePath",rootName);Set(manifest,"ForEdit",false);Set(manifest,"RootRevision","W1");
    var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    foreach(var isRoot in new[]{true,false})
    {
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",isRoot?rootId:childId);Set(file,"VersionId",isRoot?rootVersion:childVersion);
     Set(file,"FileName",isRoot?rootName:"PART.SLDPRT");Set(file,"RelativePath",Get(file,"FileName"));Set(file,"Configuration","Default");
     Set(file,"Revision","W1");Set(file,"Sha256",hash);Set(file,"FileLength",(long)bytes.Length);Set(file,"IsRoot",isRoot);files.Add(file);
     var path=Path.Combine(original,(string)Get(file,"FileName"));
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),"W1",hash,(long)bytes.Length});
     File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    Set(manifest,"Files",files);
    var contexts=(IDictionary)Field(plugin,"controlledOpenManifests");contexts.Add(original,manifest);
    var node=New("CadTreeNode");Set(node,"FullPath",childMode?childPath:rootPath);Set(node,"FileName",childMode?"PART.SLDPRT":rootName);
    Set(node,"DocumentId",childMode?childId:rootId);Set(node,"Configuration","Default");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));SetField(plugin,"currentTree",node);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var loaded=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);var readOnly=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
    var dirty=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var opened=new List<string>();var closed=new List<string>();
    var active=childMode?childPath:rootPath;var unrelated=Path.Combine(folder,"unrelated.SLDPRT");File.WriteAllText(unrelated,"unrelated unsaved user's file");dirty.Add(unrelated);
    if(mode=="child-readonly-dirty"||mode=="child-writable-dirty"||mode=="child-server-changed")dirty.Add(childPath);
    if(mode=="dirty-fallback")dirty.Add(rootPath);
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    object Model(string path)
    {
     if(loaded.TryGetValue(path,out var existing))return existing;
     readOnly[path]=path!=unrelated&&!(path==childPath&&mode=="child-writable-dirty");
     Func<System.Runtime.Remoting.Messaging.IMethodCallMessage,object> dispatch=call=>{
      switch(call.MethodName)
      {
       case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return path.EndsWith(".SLDASM")?2:1;
       case "GetHashCode":return path.GetHashCode();case "Equals":return ReferenceEquals(call.Args[0],loaded[path]);
       case "GetSaveFlag":return dirty.Contains(path);case "IsOpenedReadOnly":return readOnly[path];case "get_Visible":return path!=childPath||childMode;
       case "get_ConfigurationManager":return manager;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;
       case "SetReadOnlyState":readOnly[path]=(bool)call.Args[0];return true;
       case "GetComponents":
        var part=mode=="native-invalid-source"&&path!=rootPath?childPath:Path.Combine(Path.GetDirectoryName(path),"PART.SLDPRT");
        return new[]{new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),component=>{
         switch(component.MethodName){case "GetPathName":return part;case "get_Name2":return "PART-1";case "get_ReferencedConfiguration":return "Default";case "get_IsVirtual":return false;case "GetModelDoc2":return Model(part);case "GetChildren":return new object[0];case "GetSuppression":return 3;default:throw new Exception(component.MethodName);}
        }).GetTransparentProxy()};
       default:throw new Exception("Unexpected model mutation: "+call.MethodName);
      }
     };
     var proxy=path.EndsWith(".SLDASM")?new AssemblyProxy(modelType,dispatch).GetTransparentProxy():new Proxy(modelType,dispatch).GetTransparentProxy();
     loaded.Add(path,proxy);return proxy;
    }
    Model(rootPath);Model(childPath);Model(unrelated);
    FileStream sharingLock=null;var search="";var searchToggle=false;
    SetField(plugin,"application",new Proxy(appType,call=>{
     switch(call.MethodName)
     {
      case "GetDocuments":return loaded.Values.ToArray();case "get_ActiveDoc":return active!=null&&loaded.TryGetValue(active,out var value)?value:null;
      case "CloseDoc":
       var title=(string)call.Args[0];if(title==Path.GetFileName(unrelated)||string.IsNullOrWhiteSpace(title))throw new Exception("Unrelated window closed");
       foreach(var path in loaded.Keys.Where(path=>Path.GetFileName(path)==title).ToArray()){closed.Add(path);loaded.Remove(path);if(path==rootPath&&childMode)loaded.Remove(childPath);if(active==path)active=null;}return null;
      case "OpenDoc6":
       var target=(string)call.Args[0];opened.Add(target);
       if(mode=="native-open-failure"&&target!=rootPath&&target.EndsWith(rootName))throw new IOException("模拟原生打开失败");
       active=target;return Model(target);
      case "ActivateDoc3":active=loaded.Keys.Single(path=>Path.GetFileName(path)==(string)call.Args[0]);return loaded[active];
      case "GetDocumentDependencies2":return mode=="prepare-lock"&&((string)call.Args[0]).EndsWith(rootName)?new[]{"PART.SLDPRT",Path.Combine(folder,"old","PART.SLDPRT")}:new string[0];
      case "ReplaceReferencedDocument":sharingLock=new FileStream((string)call.Args[0],FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;
      case "GetCurrentWorkingDirectory":return "";case "GetSearchFolders":return search;
      case "SetCurrentWorkingDirectory":return true;case "SetSearchFolders":search=(string)call.Args[1];return true;
      case "GetUserPreferenceToggle":return searchToggle;case "SetUserPreferenceToggle":searchToggle=(bool)call.Args[1];return true;
      default:throw new Exception("Unexpected CAD operation: "+call.MethodName);
     }
    }).GetTransparentProxy());
    var session=(Guid)Field(plugin,"checkoutSessionId");
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://acquire-window.invalid/"))
    using(var handler=new AcquireWindowHandler(manifest,bytes,session,mode=="child-server-changed"))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://acquire-window.invalid/")})
    using(var pane=(System.Windows.Forms.Control)New("PdmTaskPaneControl","获取窗口回归"))
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
     SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,folder));SetField(plugin,"lifetime",lifetime);
     SetField(plugin,"taskPaneControl",pane);SetField(plugin,"authenticatedUsername","tester");
     SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),"fixture"));
     object originalDraft=null;
     if(!childMode)
     {
      originalDraft=New("LocalEditDraft");Set(originalDraft,"DocumentId",rootId);Set(originalDraft,"ProjectId",project);Set(originalDraft,"SessionId",Guid.NewGuid());Set(originalDraft,"SourcePath",rootPath);Set(originalDraft,"RestoreOnOpen",true);
      Type("EditDraftJournal").GetMethod("Save",Flags).Invoke(Field(plugin,"editJournal"),new[]{originalDraft});
      ((IDictionary)Field(plugin,"localEdits"))[rootId]=originalDraft;
     }
     var failed=false;bool success=false;
     try
     {
      Task task;
      if(childMode)
      {
       var selected=Array.CreateInstance(Type("BatchOperationItem"),1);selected.SetValue(New("BatchOperationItem",node,0),0);
       task=(Task)Type("PdmAddin").GetMethod("AcquireExplicitlyFromPlmAsync",Flags).Invoke(plugin,new object[]{selected,project,null});
      }
      else task=(Task)Type("PdmAddin").GetMethod("ExecuteControlledOpenAsync",Flags).Invoke(plugin,new object[]{project,rootId,Enum.Parse(Type("ControlledOpenMode"),"LatestEdit"),null,rootId,new[]{rootId},false});
      await task;success=childMode||((bool)Get(task,"Result"));
     }
     catch(InvalidOperationException){failed=true;}
     finally{sharingLock?.Dispose();}
     var blocked=mode=="child-writable-dirty"||mode=="child-server-changed";
     if(childMode)
     {
      if(blocked!=failed||success==blocked||closed.Count!=0||opened.Count!=0||active!=childPath||!loaded.ContainsKey(rootPath)||!loaded.ContainsKey(childPath))throw new Exception("Child window acquisition reopened/closed context: "+mode);
      if(!blocked&&(readOnly[childPath]||handler.Checkouts!=1||handler.Downloads!=0||!readOnly[rootPath]))throw new Exception("Wrong child permission scope: "+mode);
      if(blocked&&handler.Checkouts!=0)throw new Exception("Unsafe child acquired checkout: "+mode);
      if(mode=="child-readonly-dirty"&&!dirty.Contains(childPath))throw new Exception("Child unsaved geometry lost");
     }
     else
     {
      var expected=mode=="fresh-success";
      if(success!=expected||!loaded.ContainsKey(unrelated))throw new Exception("Unexpected controlled-open result: "+mode);
      if(!expected&&(!loaded.ContainsKey(rootPath)||active!=rootPath))throw new Exception("Original window not preserved/restored: "+mode);
      if((mode=="prepare-lock"||mode=="dirty-fallback")&&closed.Count!=0)throw new Exception("Window closed before preparation or dirty guard: "+mode);
      if(mode=="native-open-failure"&&(!closed.Contains(rootPath)||!opened.Contains(rootPath)))throw new Exception("Native failure did not restore original window; closed="+string.Join(";",closed)+"; opened="+string.Join(";",opened)+"; reason="+Field(plugin,"controlledOpenFailureMessage"));
      if(!expected&&handler.Discards!=1)throw new Exception("New checkout not rolled back: "+mode);
      if(!expected&&contexts.Count!=1)throw new Exception("Failed workspace retained as an active context: "+mode);
      if(!expected&&opened.Any(path=>path==childPath))throw new Exception("Hidden child was opened as an extra window: "+mode);
      var recorded=((IEnumerable)Type("EditDraftJournal").GetMethod("ReadAll",Flags).Invoke(Field(plugin,"editJournal"),null)).Cast<object>().Single(entry=>(Guid)Get(entry,"SessionId")== (Guid)Get(originalDraft,"SessionId"));
      if((bool)Get(recorded,"Closed")!=expected)throw new Exception("Failed open retired original recovery record: "+mode);
      if(expected)
      {
       var currentDraft=New("LocalEditDraft");Set(currentDraft,"DocumentId",rootId);Set(currentDraft,"ProjectId",project);Set(currentDraft,"SessionId",session);Set(currentDraft,"SourcePath",active);
       Type("EditDraftJournal").GetMethod("Save",Flags).Invoke(Field(plugin,"editJournal"),new[]{currentDraft});((IDictionary)Field(plugin,"localEdits"))[rootId]=currentDraft;
       Type("PdmAddin").GetMethod("RetireLocalRecoveryForPlmOpenCore",Flags).Invoke(plugin,new[]{manifest,(object)Path.GetDirectoryName(active)});
       var preserved=((IEnumerable)Type("EditDraftJournal").GetMethod("ReadAll",Flags).Invoke(Field(plugin,"editJournal"),null)).Cast<object>().Single(entry=>(Guid)Get(entry,"SessionId")==session);
       if((bool)Get(preserved,"Closed")||!((IDictionary)Field(plugin,"localEdits")).Contains(rootId))throw new Exception("Successful open retired its new recovery record");
      }
     }
     if(!File.ReadAllBytes(rootPath).SequenceEqual(bytes)||!File.ReadAllBytes(childPath).SequenceEqual(bytes)||File.ReadAllText(unrelated)!="unrelated unsaved user's file"||!dirty.Contains(unrelated))throw new Exception("Original/unrelated file changed: "+mode);
     Console.WriteLine("PASS: acquire-window "+mode+"; original bytes, unrelated dirty window and permission scope preserved");
    }
   }
  }
  finally
  {
   bindingField.SetValue(null,oldBinding);rootField.SetValue(null,oldRoot);
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   Directory.Delete(directory,true);
  }
 }

 sealed class AcquireWindowHandler:HttpMessageHandler
 {
  readonly object manifest;readonly byte[] bytes;readonly Guid session;readonly bool changed;readonly JavaScriptSerializer json=new JavaScriptSerializer();
  readonly Dictionary<Guid,object> documents=new Dictionary<Guid,object>();
  public int Checkouts,Downloads,Discards;
  public AcquireWindowHandler(object manifest,byte[] bytes,Guid session,bool changed)
  {
   this.manifest=manifest;this.bytes=bytes;this.session=session;this.changed=changed;
   foreach(var file in ((IList)Get(manifest,"Files")).Cast<object>())
   {
    var document=New("DocumentDto");Set(document,"Id",Get(file,"DocumentId"));Set(document,"ProjectId",Get(manifest,"ProjectId"));Set(document,"FileName",Get(file,"FileName"));Set(document,"CheckedOutAt",DateTime.UtcNow);documents.Add((Guid)Get(document,"Id"),document);
   }
  }
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object result;
   var id=path.Split('/').Select(part=>Guid.TryParse(part,out var parsed)?parsed:Guid.Empty).FirstOrDefault(value=>documents.ContainsKey(value));
   if(path.EndsWith("/file")){Downloads++;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
   if(path.EndsWith("/rules"))result=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/open-manifest"))
   {
    var body=json.Deserialize<Dictionary<string,object>>(await request.Content.ReadAsStringAsync());result=json.Deserialize(json.Serialize(manifest),manifest.GetType());Set(result,"ForEdit",(bool)body["forEdit"]);
    if(changed)Set(result,"RootVersionId",Guid.NewGuid());
   }
   else if(path.EndsWith("/documents"))result=documents.Values.ToArray();
   else if(path.EndsWith("/document-relations"))result=new object[0];
   else if(path.EndsWith("/versions"))
   {
    var file=((IList)Get(manifest,"Files")).Cast<object>().Single(item=>(Guid)Get(item,"DocumentId")==id);
    var child=((IList)Get(manifest,"Files")).Cast<object>().Single(item=>(Guid)Get(item,"DocumentId")!=(Guid)Get(manifest,"RootDocumentId"));
    var snapshot=new{documentId=id,fileName=Get(file,"FileName"),kind=((string)Get(file,"FileName")).EndsWith(".SLDASM")?2:1,configuration="Default",children=((string)Get(file,"FileName")).EndsWith(".SLDASM")?new object[]{new{documentId=Get(child,"DocumentId"),fileName=Get(child,"FileName"),kind=1,configuration="Default",children=new object[0]}}:new object[0]};
    result=new[]{new{id=Get(file,"VersionId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength"),referenceSnapshot=snapshot}};
   }
   else if(path.EndsWith("/checkout")){Checkouts++;result=documents[id];Set(result,"CheckedOutBy","tester");Set(result,"CheckoutSessionId",session);}
   else if(path.EndsWith("/discard-checkout")){Discards++;result=documents[id];Set(result,"CheckedOutBy",null);Set(result,"CheckoutSessionId",null);}
   else throw new Exception("Unexpected business request: "+path);
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(result))};
  }
 }
}
