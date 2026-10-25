using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static object SpeedManifest(string rootName,byte[] bytes)
 {
  string hash;using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
  var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",Guid.NewGuid());Set(manifest,"ProjectCode","QA-SPEED");Set(manifest,"RootDocumentId",Guid.NewGuid());Set(manifest,"RootVersionId",Guid.NewGuid());Set(manifest,"RootRelativePath",rootName);Set(manifest,"ForEdit",false);
  var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
  for(var i=0;i<214;i++)
  {
   var file=New("ControlledOpenFileDto");Set(file,"DocumentId",i==0?Get(manifest,"RootDocumentId"):Guid.NewGuid());Set(file,"VersionId",i==0?Get(manifest,"RootVersionId"):Guid.NewGuid());Set(file,"FileName",i==0?rootName:"PART-"+i+".SLDPRT");Set(file,"RelativePath",Get(file,"FileName"));Set(file,"Sha256",hash);Set(file,"FileLength",(long)bytes.Length);Set(file,"Revision","W1");Set(file,"IsRoot",i==0);files.Add(file);
  }
  Set(manifest,"Files",files);return manifest;
 }

 static void ControlledNativeFirstScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-native-first-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var previousBinding=binding.GetValue(null);var previousRoot=resolver.GetValue(null);
  binding.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in new[]{"verified","fallback","fallback-fails","preloaded-mismatch","same-title","historical","disabled-rule","legacy-warning"})
   {
    Type("DocumentRuleGate").GetMethod("Configure",Flags).Invoke(null,new object[]{mode=="disabled-rule"||mode=="legacy-warning"?new[]{"F20"}:new string[0]});
    var folder=mode=="historical"?Path.Combine(directory,".uplm","Snapshots",mode,"ReadOnly"):Path.Combine(directory,"View",mode);Directory.CreateDirectory(folder);
    var bytes=Encoding.UTF8.GetBytes("canonical native content");var manifest=SpeedManifest("ROOT.SLDASM",bytes);var files=((IList)Get(manifest,"Files")).Cast<object>().ToArray();
    foreach(var file in files)
    {
     var p=Path.Combine(folder,(string)Get(file,"FileName"));File.WriteAllBytes(p,bytes);File.SetAttributes(p,FileAttributes.ReadOnly);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)p,Get(file,"DocumentId"),Get(manifest,"ProjectId"),Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
    }
    var rootPath=Path.Combine(folder,"ROOT.SLDASM");var partPath=Path.Combine(folder,"PART-1.SLDPRT");var otherPath=Path.Combine(directory,"user-draft.SLDPRT");File.WriteAllText(otherPath,"unselected dirty user draft");
    var externalPath=Path.Combine(directory,"external","PART-1.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(externalPath));File.WriteAllBytes(externalPath,bytes);
    var preloaded=mode=="preloaded-mismatch";var wrong=mode=="fallback"||mode=="fallback-fails"||preloaded||mode=="same-title"||mode=="legacy-warning";
    var loaded=preloaded;var opens=0;var closes=0;var scans=0;var replacements=0;var search="";var rules=false;
    var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    object Part(string path)=>new Proxy(modelType,call=>{
     switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return 1;case "GetSaveFlag":return path==otherPath;case "IsOpenedReadOnly":return path!=otherPath;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("Unselected model must not change: "+call.MethodName);}
    }).GetTransparentProxy();
    var other=Part(otherPath);
    object Component(object file)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
     var path=Path.Combine(folder,(string)Get(file,"FileName"));var first=path==partPath;
     switch(call.MethodName){case "GetPathName":return path;case "get_Name2":return Path.GetFileNameWithoutExtension(path);case "get_IsVirtual":return false;case "GetModelDoc2":return first?Part(wrong?externalPath:partPath):null;default:throw new Exception("Unexpected component operation "+call.MethodName);}
    }).GetTransparentProxy();
    var components=files.Skip(1).Select(Component).ToArray();
    var root=new DiscardAssemblyProxy(modelType,call=>{
     switch(call.MethodName){case "GetPathName":return rootPath;case "GetTitle":return "ROOT.SLDASM";case "GetType":return 2;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return true;case "GetComponents":return components;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("Unexpected root operation "+call.MethodName);}
    }).GetTransparentProxy();
    var conflicting=new Proxy(modelType,call=>call.MethodName=="GetPathName"?otherPath:call.MethodName=="GetTitle"?"ROOT.SLDASM":throw new Exception("Conflicting document touched")).GetTransparentProxy();
    object active=loaded?root:other;
    SetField(plugin,"application",new Proxy(appType,call=>{
     switch(call.MethodName){
      case "GetDocuments":return new[]{mode=="same-title"?conflicting:other}.Concat(loaded?new[]{root}:new object[0]).ToArray();
      case "get_ActiveDoc":return active;
      case "OpenDoc6":if((string)call.Args[0]!=rootPath)throw new Exception("Unexpected document open");opens++;loaded=true;active=root;return root;
      case "CloseDoc":if((string)call.Args[0]!="ROOT.SLDASM")throw new Exception("Closed unselected draft");closes++;loaded=false;active=other;return null;
      case "GetDocumentDependencies2":scans++;return (string)call.Args[0]==rootPath&&wrong?new[]{"PART-1.SLDPRT",externalPath}:new string[0];
      case "ReplaceReferencedDocument":if((string)call.Args[0]!=rootPath||(string)call.Args[1]!=externalPath||(string)call.Args[2]!=partPath)throw new Exception("Reference rewrite escaped root");replacements++;File.AppendAllText(rootPath,"|rebased");if(mode!="fallback-fails"&&mode!="legacy-warning")wrong=false;return true;
      case "GetCurrentWorkingDirectory":return "";
      case "GetSearchFolders":return search;
      case "SetCurrentWorkingDirectory":return true;
      case "SetSearchFolders":search=(string)call.Args[1];return true;
      case "GetUserPreferenceToggle":return rules;
      case "SetUserPreferenceToggle":rules=(bool)call.Args[1];return true;
      default:throw new Exception("Unexpected native operation "+call.MethodName);
     }
    }).GetTransparentProxy());
    var failed=false;try{var opened=DiscardCall(plugin,"OpenControlledReadOnlyDocument",rootPath,2,"Default",manifest);if(opened!=root)throw new Exception("Wrong returned document");}catch(InvalidOperationException){failed=true;}catch(IOException){failed=true;}
    var shouldFail=preloaded||mode=="same-title"||mode=="fallback-fails";
    if(failed!=shouldFail||search!=""||rules||File.ReadAllText(otherPath)!="unselected dirty user draft")throw new Exception("Native-first validation/scope failure "+mode);
    if(mode=="verified"&&(scans!=0||opens!=1||closes!=0||replacements!=0))throw new Exception("Verified native open performed an unnecessary 214-file prepass");
    if(mode=="fallback"&&(scans!=214||opens!=2||closes!=1||replacements!=1))throw new Exception("Reference mismatch did not run one bounded materialization retry");
    if(mode=="fallback-fails"&&(scans!=214||opens!=2||closes!=2))throw new Exception("Invalid fallback was accepted or retried indefinitely");
    if((preloaded||mode=="same-title")&&(closes!=0||scans!=0))throw new Exception("Pre-existing or same-title user window was closed");
    if(mode=="historical"&&(scans!=214||opens!=1||closes!=0))throw new Exception("Historical open used the fast path");
    if(mode=="disabled-rule"&&(scans!=0||opens!=1||closes!=0))throw new Exception("Verified fast open depended on optional precheck settings");
    if(mode=="legacy-warning"&&(scans!=214||opens!=2||closes!=1))throw new Exception("Fast-path failure changed the configured legacy fallback policy");
    foreach(var file in files.Skip(1)){var p=Path.Combine(folder,(string)Get(file,"FileName"));if(!File.ReadAllBytes(p).SequenceEqual(bytes)||(File.GetAttributes(p)&FileAttributes.ReadOnly)==0)throw new Exception("Native-first changed an unselected file");}
    Console.WriteLine("PASS: 214-file controlled native-first "+mode+"; dependencyScans="+scans+" opens="+opens+" closes="+closes+"; configured source validation and unselected draft retained");
   }
  }
  finally
  {
   Type("DocumentRuleGate").GetMethod("Configure",Flags).Invoke(null,new object[]{new string[0]});binding.SetValue(null,previousBinding);resolver.SetValue(null,previousRoot);
   foreach(var p in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(p,FileAttributes.Normal);
   if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected QA path");Directory.Delete(directory,true);
  }
 }

 static async Task ControlledDownloadConcurrencyScenario()
 {
  foreach(var mode in new[]{"success","failure","cancel"})
  {
   var folder=Path.Combine(Path.GetTempPath(),"pdm-download-speed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   var bytes=Encoding.UTF8.GetBytes("verified concurrent bytes");var manifest=SpeedManifest("ROOT.SLDPRT",bytes);
   using(var cancellation=new CancellationTokenSource())
   using(var handler=new ControlledSpeedDownloadHandler(bytes,mode,cancellation))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://speed.invalid/")})
   using(var client=(IDisposable)New("PdmApiClient","http://speed.invalid/"))
   {
    SetField(client,"httpClient",http);var workspace=New("ControlledWorkspaceManager",client,folder);string result=null;var failed=false;
    try{var task=(Task)Type("ControlledWorkspaceManager").GetMethod("PrepareReadOnlyAsync",Flags).Invoke(workspace,new object[]{manifest,false,cancellation.Token});await task;result=(string)Get(task,"Result");}catch(HttpRequestException){failed=true;}catch(OperationCanceledException){failed=true;}
    if(failed!=(mode!="success")||handler.Active!=0||handler.Maximum>4||handler.Maximum<2)throw new Exception("Unbounded, sequential or unfinished download batch: "+mode);
    if(mode=="success")
    {
     if(handler.Starts!=214||!File.Exists(result))throw new Exception("Concurrent batch missing files");
     foreach(var file in ((IList)Get(manifest,"Files")).Cast<object>()){var p=Path.Combine(Path.GetDirectoryName(result),(string)Get(file,"FileName"));if(!File.ReadAllBytes(p).SequenceEqual(bytes)||(File.GetAttributes(p)&FileAttributes.ReadOnly)==0)throw new Exception("Parallel transfer bypassed validation/read-only state");}
    }
    else if(Directory.GetFiles(folder,"*.SLDPRT",SearchOption.AllDirectories).Length!=0)throw new Exception("Failed/cancelled batch published incomplete files");
    Console.WriteLine("PASS: 214-file bounded download "+mode+"; maximum="+handler.Maximum+" activeAtReturn=0; hashes/read-only/publication checks retained");
   }
   foreach(var p in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))File.SetAttributes(p,FileAttributes.Normal);
   if(!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected QA path");Directory.Delete(folder,true);
  }
 }

 sealed class ControlledSpeedDownloadHandler:HttpMessageHandler
 {
  readonly byte[] bytes;readonly string mode;readonly CancellationTokenSource cancellation;public int Active,Maximum,Starts;
  public ControlledSpeedDownloadHandler(byte[] bytes,string mode,CancellationTokenSource cancellation){this.bytes=bytes;this.mode=mode;this.cancellation=cancellation;}
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(request.Method!=HttpMethod.Get||!request.RequestUri.AbsolutePath.EndsWith("/file"))throw new Exception("Unexpected business request");
   var active=Interlocked.Increment(ref Active);int prior;do{prior=Maximum;if(prior>=active)break;}while(Interlocked.CompareExchange(ref Maximum,active,prior)!=prior);
   var count=Interlocked.Increment(ref Starts);
   try{if(mode=="cancel"&&count==6)cancellation.Cancel();await Task.Delay(15,token);if(mode=="failure"&&count==6)throw new HttpRequestException("模拟传输失败");return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
   finally{Interlocked.Decrement(ref Active);}
  }
 }
}
