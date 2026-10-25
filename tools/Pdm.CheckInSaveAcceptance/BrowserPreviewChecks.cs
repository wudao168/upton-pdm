using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void WorkerPreviewStateScenario(string executable)
 {
  var worker=System.Reflection.Assembly.LoadFrom(Path.GetFullPath(executable));
  var update=worker.GetType("Program",true).GetMethod("PreviewState",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
  var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-browser-preview-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(directory);
  var corePath=Path.Combine(directory,"native-result.json");var previewPath=Path.Combine(directory,"native-previews.json");
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  string Hash(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
  var core=json.Serialize(new{Success=true,PreviewsPending=true,Files=new[]{new{Key="fixed",PreviewPath=(string)null}},Warnings=new string[0]});
  File.WriteAllText(corePath,core,new UTF8Encoding(false));var sha=Hash(File.ReadAllBytes(corePath));
  string Saved(string source)=>json.Serialize(new{Success=true,PreviewsPending=true,ArchiveSourceSha256=source,
   Files=new[]{new{Key="fixed",PreviewPath="_previews/saved.pdf",PreviewSha256="saved-hash",PreviewLength=123}},Warnings=new string[0]});
  Dictionary<string,object> Read()=>(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(previewPath));
  Dictionary<string,object> First(Dictionary<string,object> batch)=>(Dictionary<string,object>)((object[])batch["Files"])[0];
  try
  {
   File.WriteAllText(previewPath,Saved(sha));update.Invoke(null,new object[]{corePath,previewPath,"预览超时",true});var state=Read();
   if(!Equals(state["PreviewsPending"],true)||(string)First(state)["PreviewPath"]!="_previews/saved.pdf")throw new Exception("Timeout lost successful checkpoints");
   Console.WriteLine("PASS: preview timeout preserves successful files");
   update.Invoke(null,new object[]{corePath,previewPath,"自动重试已到上限",false});state=Read();
   if(!Equals(state["Success"],true)||!Equals(state["PreviewsPending"],false)||(string)First(state)["PreviewSha256"]!="saved-hash")throw new Exception("Preview failure replaced native success");
   Console.WriteLine("PASS: final preview failure preserves native success and partial previews");
   update.Invoke(null,new object[]{corePath,previewPath,null,true});state=Read();
   if(!Equals(state["PreviewsPending"],true)||Convert.ToInt64(First(state)["PreviewLength"])!=123)throw new Exception("Explicit retry lost its cache");
   Console.WriteLine("PASS: explicit retry keeps the verified cache");
   File.WriteAllText(previewPath,Saved("another-core"));update.Invoke(null,new object[]{corePath,previewPath,null,true});state=Read();
   if(First(state)["PreviewPath"]!=null||(string)state["ArchiveSourceSha256"]!=sha)throw new Exception("Another core checkpoint was reused");
   Console.WriteLine("PASS: stale core checkpoints are rejected");
   if(Hash(File.ReadAllBytes(corePath))!=sha)throw new Exception("Immutable core result changed");
   Console.WriteLine("PASS: all preview transitions preserve immutable core bytes");
   var prepare=worker.GetType("Program",true).GetMethod("PrepareInputs",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
   foreach(var same in new[]{true,false})
   {
    var input=Path.Combine(directory,"input-"+same);var work=Path.Combine(directory,"work-"+same);
    Directory.CreateDirectory(Path.Combine(input,"one"));Directory.CreateDirectory(Path.Combine(input,"two"));
    var first=Path.Combine(input,"one","PART.SLDPRT");var second=Path.Combine(input,"two","PART.SLDPRT");
    File.WriteAllText(first,"same bytes");File.WriteAllText(second,same?"same bytes":"other data");
    var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);Exception failure=null;
    try{prepare.Invoke(null,new object[]{input,work,hashes,paths});}catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
    if(same&&(failure!=null||hashes.Count!=1||paths.Count!=1||Directory.GetFiles(work).Length!=1))throw new Exception("Identical source copies were not deduplicated");
    if(!same&&!(failure is InvalidOperationException))throw new Exception("Different same-named files were accepted");
    if(File.ReadAllText(first)!="same bytes"||File.ReadAllText(second)!=(same?"same bytes":"other data"))throw new Exception("Uploaded originals changed");
    Console.WriteLine("PASS: browser same-name source comparison same="+same);
   }
   var nativeDocument=worker.GetType("Program",true).GetMethod("IsNativeDocument",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
   var supported=new[]{"ROOT.SLDASM","PART.SLDPRT","DRAWING.SLDDRW","MODEL.STEP","MODEL2.stp","equations.txt","table.xls","design.XLSX"};
   var companionInput=Path.Combine(directory,"input-companions");var companionWork=Path.Combine(directory,"work-companions");
   Directory.CreateDirectory(Path.Combine(companionInput,"__plm__"));
   var originalBytes=new Dictionary<string,byte[]>();var originalAttributes=new Dictionary<string,FileAttributes>();
   foreach(var name in supported)
   {
    var path=Path.Combine(companionInput,name=="MODEL2.stp"||name=="design.XLSX"?"__plm__":"",name);
    File.WriteAllText(path,name=="equations.txt"?"C:\\not-in-this-job\\unavailable.SLDPRT":"saved bytes "+name);
    File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.ReadOnly);
    originalBytes[path]=File.ReadAllBytes(path);originalAttributes[path]=File.GetAttributes(path);
   }
   File.WriteAllText(Path.Combine(companionInput,"ignored.pdf"),"not an archive companion");
   var outside=Path.Combine(directory,"outside.step");File.WriteAllText(outside,"outside job input");
   var companionHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var companionPaths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   prepare.Invoke(null,new object[]{companionInput,companionWork,companionHashes,companionPaths});
   if(companionHashes.Count!=8||companionPaths.Count!=8||Directory.GetFiles(companionWork).Length!=8)
    throw new Exception("Native/companion input set was not copied completely or unrelated files were included");
   foreach(var pair in originalBytes)
   {
    var name=Path.GetFileName(pair.Key);var copy=Path.Combine(companionWork,name);
    if(Hash(File.ReadAllBytes(copy))!=Hash(pair.Value)||companionHashes[name]!=Hash(pair.Value)
     ||(File.GetAttributes(copy)&FileAttributes.ReadOnly)!=0||!File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)
     ||File.GetAttributes(pair.Key)!=originalAttributes[pair.Key])throw new Exception("Companion copy changed source bytes/attributes or lost its fingerprint: "+name);
   }
   if(companionPaths["MODEL2.stp"]!="__plm__/MODEL2.stp"||companionPaths["design.XLSX"]!="__plm__/design.XLSX"
    ||File.Exists(Path.Combine(companionWork,"ignored.pdf"))||File.Exists(Path.Combine(companionWork,"outside.step")))
    throw new Exception("Companion inputs escaped the job scope or lost controlled-prefetch mapping");
   Console.WriteLine("PASS: five companion formats and controlled prefetch preserve original bytes/attributes and stay inside job input");
   foreach(var name in supported)
    if((bool)nativeDocument.Invoke(null,new object[]{name})!=new[]{".sldasm",".sldprt",".slddrw"}.Contains(Path.GetExtension(name).ToLowerInvariant()))
     throw new Exception("A companion file was accepted as a native root: "+name);
   if((bool)nativeDocument.Invoke(null,new object[]{"document.pdf"}))throw new Exception("Unsupported native root was accepted");
   Console.WriteLine("PASS: roots remain limited to native SolidWorks documents");
   foreach(var extension in new[]{".step",".stp",".txt",".xls",".xlsx"})
    foreach(var same in new[]{true,false})
    {
     var input=Path.Combine(directory,"duplicate-"+extension.Substring(1)+"-"+same);var work=Path.Combine(directory,"copy-"+extension.Substring(1)+"-"+same);
     Directory.CreateDirectory(Path.Combine(input,"upload"));Directory.CreateDirectory(Path.Combine(input,"__plm__"));
     var first=Path.Combine(input,"upload","COMPANION"+extension.ToUpperInvariant());var second=Path.Combine(input,"__plm__","companion"+extension);
     File.WriteAllText(first,"same bytes");File.WriteAllText(second,same?"same bytes":"other data");
     var beforeFirst=Hash(File.ReadAllBytes(first));var beforeSecond=Hash(File.ReadAllBytes(second));
     var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);Exception failure=null;
     try{prepare.Invoke(null,new object[]{input,work,hashes,paths});}catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
     if(same&&(failure!=null||hashes.Count!=1||paths.Count!=1||Directory.GetFiles(work).Length!=1))throw new Exception("Identical companion copies were not deduplicated: "+extension);
     if(!same&&!(failure is InvalidOperationException))throw new Exception("Same-length different companion content was accepted: "+extension);
     if(Hash(File.ReadAllBytes(first))!=beforeFirst||Hash(File.ReadAllBytes(second))!=beforeSecond)throw new Exception("Companion conflict resolution changed an input");
     Console.WriteLine("PASS: companion same-name content comparison extension="+extension+" same="+same);
    }
  }
  finally
  {
   if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-browser-preview-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   Directory.Delete(directory,true);
  }
 }
 static void BrowserPreviewRecoveryScenario()
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  string HashBytes(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
  foreach(var mode in new[]{"cached","legacy-cache","old-generator","active-document-v1","step-v2","config-v2","working-copy-changed","different-core","different-native-hash","preview-same-time-edit","preview-length","frozen-changed","outside-path"})
  {
   var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-browser-preview-"+Guid.NewGuid().ToString("N")));
   Directory.CreateDirectory(Path.Combine(directory,"_archive"));Directory.CreateDirectory(Path.Combine(directory,"_previews"));
   var native=Path.Combine(directory,"_archive","DRAWING.SLDDRW");var working=Path.Combine(directory,"DRAWING.SLDDRW");
   var preview=Path.Combine(directory,"_previews","drawing.pdf");var key=Guid.NewGuid();
   File.WriteAllText(native,"native immutable drawing");File.Copy(native,working);File.WriteAllText(preview,"%PDF-complete-preview");
   var nativeHash=HashBytes(File.ReadAllBytes(native));var previewHash=HashBytes(File.ReadAllBytes(preview));var previewTime=File.GetLastWriteTimeUtc(preview);
   object FileRecord(bool cached)=>new{Key=key,FileName="DRAWING.SLDDRW",RelativePath=mode=="outside-path"?"../outside.SLDDRW":"_archive/DRAWING.SLDDRW",
    Length=new FileInfo(native).Length,Sha256=cached&&mode=="different-native-hash"?new string('F',64):nativeHash,Root=new{configuration="Default"},Properties=new Dictionary<string,string>(),InputRelativePath="DRAWING.SLDDRW",
    PreviewPath=cached?"_previews/drawing.pdf":null,PreviewSha256=cached?previewHash:null,PreviewLength=cached?new FileInfo(preview).Length:0};
   var core=json.Serialize(new{Success=true,RootKey=key,Files=new[]{FileRecord(false)},Warnings=new string[0],PreviewsPending=true});
   var priorRecord=new Dictionary<string,object>{{"Success",true},{"RootKey",key},{"Files",new[]{FileRecord(true)}},{"Warnings",new string[0]},{"PreviewsPending",true},
    {"ArchiveSourceSha256",mode=="different-core"?new string('F',64):HashBytes(Encoding.UTF8.GetBytes(core))}};
   if(mode!="legacy-cache")priorRecord["PreviewGeneratorVersion"]=mode=="old-generator"?"invisible-document-v0":mode=="active-document-v1"?"active-document-v1":mode=="step-v2"?"active-document-step-v2":mode=="config-v2"?"active-document-config-v2":"active-document-config-v3";
   var prior=json.Serialize(priorRecord);
   if(mode=="working-copy-changed")File.WriteAllText(working,"later mutable bytes");
   if(mode=="preview-same-time-edit"){File.WriteAllText(preview,"%PDF-corrupt--preview");File.SetLastWriteTimeUtc(preview,previewTime);}
   if(mode=="preview-length")File.AppendAllText(preview,"truncated marker");
   if(mode=="frozen-changed")File.AppendAllText(native,"changed");
   var calls=new List<string>();var checkpoints=new List<string>();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>{calls.Add(call.MethodName);if(call.MethodName=="SetSearchFolders"||call.MethodName=="SetCurrentWorkingDirectory")return true;
    if(call.MethodName=="GetDocuments")return Array.Empty<object>();throw new IOException("QA forbids a native call when a cache is invalid: "+call.MethodName);}).GetTransparentProxy();
   try
   {
    string output=null;Exception failure=null;
    try{output=(string)Type("PdmAddin").GetMethod("ReadBrowserArchivePreviews",Flags).Invoke(null,new object[]{app,directory,core,prior,(Action<string>)(_=>{}),(Action<string>)(value=>checkpoints.Add(value)),true});}
    catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
    if(mode=="frozen-changed"||mode=="outside-path")
    {if(!(failure is IOException)||checkpoints.Count!=0)throw new Exception("Unverified frozen source was accepted: "+mode);}
    else
    {
     if(failure!=null)throw failure;
     var result=(Dictionary<string,object>)json.DeserializeObject(output);var file=(Dictionary<string,object>)((object[])result["Files"])[0];
     if(!Equals(result["PreviewGeneratorVersion"],"active-document-config-v3"))throw new Exception("Preview checkpoint has no current generator version");
     var reused=file["PreviewPath"]!=null;var expected=mode=="cached"||mode=="working-copy-changed";
     if(reused!=expected||Equals(result["PreviewsPending"],expected))throw new Exception("Cache/pending result wrong: "+mode);
     if(expected&&calls.Any(name=>name!="GetDocuments"&&name!="SetSearchFolders"&&name!="SetCurrentWorkingDirectory"))throw new Exception("Valid cache was regenerated");
     if(HashBytes(File.ReadAllBytes(working))!=nativeHash)throw new Exception("Preview read did not retain frozen native bytes");
     if(HashBytes(File.ReadAllBytes(native))!=nativeHash)throw new Exception("Frozen archive bytes changed");
     if(checkpoints.Count==0)throw new Exception("No durable preview checkpoint");
    }
    Console.WriteLine("PASS: browser preview recovery "+mode);
   }
   finally
   {
    if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-browser-preview-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
    foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
    Directory.Delete(directory,true);
   }
  }
  BrowserPreviewActiveDocumentScenario();
 }

 static void BrowserPreviewActiveDocumentScenario()
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  string Hash(string path){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
  foreach(var mode in new[]{"active-document","active-assembly","preloaded-switch","preloaded-same","preloaded-switch-false","preloaded-switch-stale","step-fallback","assembly-fallback","drawing-pdf","drawing-pdf-fallback","wrong-active-document","server-fault","disconnected"})
  {
   var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-browser-preview-"+Guid.NewGuid().ToString("N")));
   Directory.CreateDirectory(Path.Combine(directory,"_archive"));
   try
   {
    var comFault=mode=="server-fault"||mode=="disconnected";var drawing=mode.StartsWith("drawing-pdf");
    var preloaded=mode.StartsWith("preloaded-");var configurationMismatch=preloaded&&mode!="preloaded-same";
    var configurationFailure=mode=="preloaded-switch-false"||mode=="preloaded-switch-stale";
    var assemblyDocument=mode=="active-assembly"||mode=="assembly-fallback";var fallback=mode.EndsWith("-fallback");
    var names=comFault?new[]{"FIRST.SLDPRT","FAIL.SLDPRT","LAST.SLDPRT"}:new[]{drawing?"TARGET.SLDDRW":assemblyDocument?"TARGET.SLDASM":"TARGET.SLDPRT"};
    var records=names.Select(name=>{
     var frozen=Path.Combine(directory,"_archive",name);File.WriteAllText(frozen,"immutable native "+name);
     File.Copy(frozen,Path.Combine(directory,name));
     return new Dictionary<string,object>{{"Key",Guid.NewGuid()},{"FileName",name},{"RelativePath","_archive/"+name},{"Length",new FileInfo(frozen).Length},
      {"Sha256",Hash(frozen)},{"Root",new{configuration="0"}},{"Properties",new Dictionary<string,string>()},{"InputRelativePath",name}};
    }).ToArray();
    var core=json.Serialize(new{Success=true,RootKey=records[0]["Key"],Files=records,Warnings=new string[0],PreviewsPending=true});
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    var extensionType=interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true);
    var constants=System.Reflection.Assembly.Load("SolidWorks.Interop.swconst");
    var dontRebuild=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swRebuildOnActivation_e",true),"swDontRebuildActiveDoc"));
    var saveOptionsType=constants.GetType("SolidWorks.Interop.swconst.swSaveAsOptions_e",true);
    var silent=Convert.ToInt32(Enum.Parse(saveOptionsType,"swSaveAsOptions_Silent"));var copy=Convert.ToInt32(Enum.Parse(saveOptionsType,"swSaveAsOptions_Copy"));
    var notSupported=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swFileSaveError_e",true),"swFileSaveAsNotSupported"));
    var exports=new List<string>();var opens=new List<string>();var checkpoints=new List<string>();var switches=new List<string>();
    Dictionary<string,object> Read(string value)=>(Dictionary<string,object>)json.DeserializeObject(value);
    Dictionary<string,object>[] Files(string value)=>((IEnumerable)Read(value)["Files"]).Cast<Dictionary<string,object>>().ToArray();
    string Run(int session,string previous)
    {
     var visibility=false;var applicationVisible=false;var selected=true;var activeName="UNRELATED.SLDPRT";var activatedName="";var activeRead=false;
     var loaded=new Dictionary<string,object>();var activeModels=new Dictionary<string,object>();
     var foreign=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Path.Combine(directory,"UNRELATED.SLDPRT"):call.MethodName=="GetTitle"?"UNRELATED.SLDPRT"
      :throw new Exception("Foreign document must not be exported or changed: "+call.MethodName)).GetTransparentProxy();
     foreach(var name in names)
     {
      var captured=name;var configurationRead=false;var actualConfiguration=configurationMismatch?"180":"0";
      var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>{
       if(call.MethodName!="get_Name")throw new Exception("Unexpected preview configuration call: "+call.MethodName);
       configurationRead=true;return actualConfiguration;
      }).GetTransparentProxy();
      var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration
       :throw new Exception("Unexpected preview configuration manager call: "+call.MethodName)).GetTransparentProxy();
      var extension=new Proxy(extensionType,call=>{
       if(call.MethodName!="SaveAs")throw new Exception("Unexpected preview extension call: "+call.MethodName);
       if((int)call.Args[2]!=(silent|copy))throw new Exception("STEP and PDF must preserve Silent | Copy");
       if(!drawing&&(activeName!=captured||activatedName!=captured||!activeRead||selected))throw new Exception("Preview export preceded exact active-document verification or selection clearing");
       if(!drawing&&(!configurationRead||actualConfiguration!="0"))throw new Exception("Preview export did not verify the planned active configuration");
       configurationRead=false;
       exports.Add(session+":"+captured);var output=(string)call.Args[0];
       if(Path.GetExtension(output)!=(drawing?".pdf":".step"))throw new Exception("Preview export format did not match its native document");
       if(fallback&&exports.Count==1)
       {
        var arguments=call.Args;arguments[4]=notSupported;arguments[5]=0;
        return new System.Runtime.Remoting.Messaging.ReturnMessage(false,arguments,arguments.Length,call.LogicalCallContext,call);
       }
       if(session==1&&comFault&&captured=="FAIL.SLDPRT")
       {File.WriteAllText(output,"partial unusable STEP");throw new COMException("Simulated unavailable CAD session",unchecked((int)(mode=="server-fault"?0x80010105:0x80010108)));}
       File.WriteAllText(output,(drawing?"%PDF-complete ":"complete STEP ")+captured);call.Args[4]=0;call.Args[5]=0;return true;
      }).GetTransparentProxy();
      activeModels[captured]=new Proxy(modelType,call=>{
       if(call.MethodName=="GetPathName")return Path.Combine(directory,captured);
       if(call.MethodName=="GetTitle")return captured;
       if(call.MethodName=="ClearSelection2"){if(!Equals(call.Args[0],true))throw new Exception("Preview did not clear every selection");selected=false;return null;}
       if(call.MethodName=="get_ConfigurationManager")return manager;
       if(call.MethodName=="ShowConfiguration2")
       {
        if(!Equals(call.Args[0],"0"))throw new Exception("Preview selected a configuration other than its plan");
        switches.Add(captured);configurationRead=false;
        if(mode=="preloaded-switch-false")return false;
        if(mode!="preloaded-switch-stale")actualConfiguration="0";
        return true;
       }
       if(call.MethodName=="get_Extension")return extension;
       throw new Exception("Unexpected active preview document call: "+call.MethodName);
      }).GetTransparentProxy();
      if(preloaded)loaded[captured]=activeModels[captured];
     }
     var app=new Proxy(appType,call=>{
      switch(call.MethodName)
      {
       case "GetDocuments":return loaded.Values.ToArray();
       case "SetSearchFolders":case "SetCurrentWorkingDirectory":return true;
       case "GetAddInObject":return null;
       case "get_Visible":return applicationVisible;
       case "set_Visible":applicationVisible=(bool)call.Args[0];return null;
       case "GetDocumentVisible":return visibility;
       case "DocumentVisible":visibility=(bool)call.Args[0];return null;
       case "OpenDoc6":
        var path=(string)call.Args[0];var name=Path.GetFileName(path);
        if(!visibility||(int)call.Args[1]!=(drawing?3:assemblyDocument?2:1)||((int)call.Args[2]&3)!=3||(string)call.Args[3]!=(drawing?"":"0"))
         throw new Exception("Preview lacks a visible, read-only, exact-configuration open context");
        opens.Add(session+":"+name);call.Args[4]=0;call.Args[5]=0;
        if(preloaded)
        {
         var arguments=call.Args;arguments[4]=0;arguments[5]=128;
         return new System.Runtime.Remoting.Messaging.ReturnMessage(loaded[name],arguments,arguments.Length,call.LogicalCallContext,call);
        }
        if(drawing)return loaded[name]=activeModels[name];
        return loaded[name]=new Proxy(modelType,documentCall=>documentCall.MethodName=="GetTitle"?name
         :documentCall.MethodName=="GetPathName"?path:throw new Exception("Export must use the verified ActiveDoc: "+documentCall.MethodName)).GetTransparentProxy();
       case "ActivateDoc3":
        var title=(string)call.Args[0];
        if(!loaded.ContainsKey(title)||!Equals(call.Args[1],false)||(int)call.Args[2]!=dontRebuild)throw new Exception("Preview activated the wrong document or requested rebuild");
        if(activatedName!=title){selected=true;activeRead=false;}activatedName=title;call.Args[3]=0;
        activeName=mode=="wrong-active-document"?"UNRELATED.SLDPRT":title;return loaded[title];
       case "get_ActiveDoc":activeRead=true;return activeName=="UNRELATED.SLDPRT"?foreign:activeModels[activeName];
       case "CloseDoc":
        if(preloaded)throw new Exception("Preview attempted to close a preloaded document");
        if(!loaded.Remove((string)call.Args[0]))throw new Exception("Preview attempted to close an unrelated document");return null;
       default:throw new Exception("Unexpected preview application call: "+call.MethodName);
      }
     }).GetTransparentProxy();
     var output=(string)Type("PdmAddin").GetMethod("ReadBrowserArchivePreviews",Flags).Invoke(null,new object[]{app,directory,core,previous,(Action<string>)(_=>{}),(Action<string>)(value=>checkpoints.Add(value)),true});
     if(visibility||applicationVisible)throw new Exception("Preview changed document/application visibility after opening");
     if(loaded.Count!=(preloaded?names.Length:0))throw new Exception("Preview changed preloaded documents or failed to close its own target");
     return output;
    }
    var first=Run(1,null);var firstFiles=Files(first);
    if(mode=="wrong-active-document"||configurationFailure)
    {
     if(exports.Count!=0||firstFiles[0]["PreviewPath"]!=null||firstFiles[0]["PreviewError"]==null||!Equals(Read(first)["PreviewsPending"],true))
      throw new Exception("Mismatched ActiveDoc or configuration was exported instead of rejected");
    }
    else if(comFault)
    {
     if(!exports.SequenceEqual(new[]{"1:FIRST.SLDPRT","1:FAIL.SLDPRT"})||!opens.SequenceEqual(exports)
      ||firstFiles[0]["PreviewPath"]==null||firstFiles[1]["PreviewPath"]!=null||firstFiles[1]["PreviewError"]==null
      ||firstFiles[2]["PreviewPath"]!=null||firstFiles[2]["PreviewError"]!=null||!Equals(Read(first)["PreviewsPending"],true))
      throw new Exception("COM failure did not stop the current session while preserving prior success");
     var checkpointFiles=Files(checkpoints.Last());
     if(checkpointFiles[0]["PreviewPath"]==null||checkpointFiles[1]["PreviewError"]==null||checkpointFiles[2]["PreviewPath"]!=null)
      throw new Exception("COM failure checkpoint lost completed previews or failure state");
     var retried=Run(2,first);
     if(!exports.SequenceEqual(new[]{"1:FIRST.SLDPRT","1:FAIL.SLDPRT","2:LAST.SLDPRT","2:FAIL.SLDPRT"})||!opens.SequenceEqual(exports)
      ||Files(retried).Any(file=>file["PreviewPath"]==null||!Equals(file["PreviewConfiguration"],"0"))||!Equals(Read(retried)["PreviewsPending"],false))
      throw new Exception("Fresh session did not reuse success and process untouched documents before the prior failure");
    }
    else if(!exports.SequenceEqual(Enumerable.Repeat("1:"+names[0],fallback?2:1))||firstFiles[0]["PreviewPath"]==null
     ||!Equals(firstFiles[0]["PreviewConfiguration"],drawing?null:"0")||!Equals(Read(first)["PreviewsPending"],false))
     throw new Exception("Exact active-document preview did not complete: "+mode+"; exports="+string.Join(",",exports)+"; "+firstFiles[0]["PreviewError"]);
    if(switches.Count!=(configurationMismatch?1:0))throw new Exception("Preview did not limit configuration switching to an actual mismatch");
    foreach(var record in records)
     if(Hash(Path.Combine(directory,"_archive",(string)record["FileName"]))!=(string)record["Sha256"]||Hash(Path.Combine(directory,(string)record["FileName"]))!=(string)record["Sha256"])
      throw new Exception("Preview context handling changed native bytes");
    Console.WriteLine("PASS: browser preview active context "+mode);
   }
   finally
   {
    if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-browser-preview-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
    foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
    Directory.Delete(directory,true);
   }
  }
 }
}
