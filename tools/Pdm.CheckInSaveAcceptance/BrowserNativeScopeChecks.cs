using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void BrowserNativeScopeScenario()
 {
  var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-browser-native-scope-"+Guid.NewGuid().ToString("N")));
  Directory.CreateDirectory(directory);
  var gate=Type("DocumentRuleGate");gate.GetMethod("Configure",Flags).Invoke(null,new object[]{Array.Empty<string>()});
  try
  {
   foreach(var listed in new[]{false,true})
   {
    var fixture=Path.Combine(directory,listed?"native-missing":"historical-missing");Directory.CreateDirectory(fixture);
    var drawing=LinkedNode(fixture,"DRAWING.SLDDRW","Drawing");var actual=LinkedNode(fixture,"ACTUAL.SLDPRT","Part");
    Set(drawing,"Revision","W1");Set(drawing,"CurrentRevision","W1");Set(actual,"Revision","W1");Set(actual,"CurrentRevision","W1");
    var drawingPath=(string)Get(drawing,"FullPath");var actualPath=(string)Get(actual,"FullPath");
    var missing=Path.Combine(fixture,"old-location","HISTORY.SLDASM");
    ((IList)Get(drawing,"ReferencedModels")).Add(actual);
    var inventory=listed?new[]{drawingPath,actualPath,missing}:new[]{drawingPath,actualPath};
    Type("NativeArchiveManifest").GetMethod("ConfirmFileScope",Flags).Invoke(null,new object[]{drawing,inventory});
    var drawingBytes=File.ReadAllBytes(drawingPath);var actualBytes=File.ReadAllBytes(actualPath);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
     call.MethodName=="GetPathName"?actualPath:throw new Exception("Unexpected model operation: "+call.MethodName)).GetTransparentProxy();
    object View(bool historical)=>new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>
     call.MethodName=="get_ReferencedDocument"?(historical?null:model):call.MethodName=="GetReferencedModelName"?(historical?missing:actualPath)
     :call.MethodName=="get_ReferencedConfiguration"?(historical?"":"Default"):throw new Exception("Unexpected view operation: "+call.MethodName)).GetTransparentProxy();
    var sheet=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>
     call.MethodName=="get_ReferencedDocument"?null:call.MethodName=="GetReferencedModelName"?Path.Combine(fixture,"SHEET-OLD-PROPERTY-SOURCE.SLDPRT")
     :call.MethodName=="get_ReferencedConfiguration"?"":throw new Exception("Unexpected sheet operation: "+call.MethodName)).GetTransparentProxy();
    var doc=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
     call.MethodName=="GetType"?3:call.MethodName=="GetViews"?new object[]{new object[]{sheet,View(false),View(true)}}
     :throw new Exception("Drawing scope update must not save/rebuild: "+call.MethodName)).GetTransparentProxy();
    var app=new Proxy(appType,call=>call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type)
     ?appType:throw new Exception("Drawing scope update must not call SolidWorks application: "+call.MethodName)).GetTransparentProxy();
    var scanner=Type("SolidWorksReferenceTreeScanner").GetConstructor(Flags,null,new[]{appType},null).Invoke(new[]{app});
    Type("SolidWorksReferenceTreeScanner").GetMethod("UpdateDrawingReferences",Flags).Invoke(scanner,new[]{doc,drawing});
    var references=((IEnumerable)Get(drawing,"ReferencedModels")).Cast<object>().ToArray();
    var historicalNode=references.Single(node=>(string)Get(node,"FullPath")==missing);
    if(references.Length!=2||(bool)Get(historicalNode,"IsOutsideNativeFileScope")==listed
     ||Get(historicalNode,"DocumentId")!=null||!string.IsNullOrEmpty((string)Get(historicalNode,"Configuration")))
     throw new Exception("View refresh changed scope classification or guessed an identity/configuration");
    if(references.Single(node=>(string)Get(node,"FullPath")==actualPath) is object current&& !Equals(Get(current,"DocumentId"),Get(actual,"DocumentId")))
     throw new Exception("Exact known model identity was lost during view refresh");
    object snapshot=null;Exception failure=null;
    try{snapshot=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{drawing,inventory});}
    catch(TargetInvocationException error){failure=error.InnerException;}
    if(listed)
    {
     if(!(bool)gate.GetMethod("IsEnabled",Flags).Invoke(null,new object[]{"B09"})||!(failure is InvalidOperationException)
      ||!failure.Message.Contains("以下存档所需文件尚未定位")||!failure.Message.Contains("HISTORY.SLDASM"))
      throw new Exception("A missing model explicitly listed by the native inventory was not blocked by B09",failure);
     Console.WriteLine("PASS: a newly read missing view model inside native scope still fails B09");
    }
    else
    {
     if(failure!=null)throw new Exception("Outside-scope view history blocked archive capture",failure);
     if(!(bool)Get(drawing,"StructureIndexPending"))throw new Exception("Skipped historical view did not retain pending structure index");
     var captured=(Dictionary<string,object>)snapshot.GetType().GetMethod("CreateRoot",Flags).Invoke(snapshot,null);
     var children=((IEnumerable)captured["children"]).Cast<Dictionary<string,object>>().ToArray();
     if(!Equals(captured["structureIndexPending"],true)||children.Length!=1||(string)children[0]["fileName"]!="ACTUAL.SLDPRT"
      ||Guid.Parse(Convert.ToString(children[0]["documentId"]))!=(Guid)Get(actual,"DocumentId"))
      throw new Exception("Captured drawing did not preserve only its exact known model and pending index");
     if(File.Exists(missing)||Get(historicalNode,"DocumentId")!=null)throw new Exception("Historical model was materialized or registered");
     Console.WriteLine("PASS: refreshed outside-scope view history stays diagnostic/pending and is excluded from captured archive without guessed identity");
    }
    if(!File.ReadAllBytes(drawingPath).SequenceEqual(drawingBytes)||!File.ReadAllBytes(actualPath).SequenceEqual(actualBytes))
     throw new Exception("Read-only scope regression changed source bytes");
   }
  }
  finally
  {
   gate.GetMethod("Configure",Flags).Invoke(null,new object[]{Array.Empty<string>()});
   if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-browser-native-scope-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
   Directory.Delete(directory,true);
  }
 }

 static void BrowserCompanionPreviewScenario()
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  string Hash(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
  foreach(var mode in new[]{"companions-only","cached-native-model","damaged-step","damaged-txt","damaged-xlsx","damaged-native-model"})
  {
   var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-browser-companion-preview-"+Guid.NewGuid().ToString("N")));
   Directory.CreateDirectory(Path.Combine(directory,"_archive"));Directory.CreateDirectory(Path.Combine(directory,"_previews"));
   try
   {
    var names=new List<string>{"SOURCE.STEP","EQUATIONS.TXT","DESIGN.XLSX"};
    if(mode=="cached-native-model"||mode=="damaged-native-model")names.Add("MODEL.SLDPRT");
    var records=new List<Dictionary<string,object>>();var savedHashes=new Dictionary<string,string>();
    foreach(var name in names)
    {
     var frozen=Path.Combine(directory,"_archive",name);File.WriteAllText(frozen,"frozen original "+name);
     var hash=Hash(File.ReadAllBytes(frozen));savedHashes[name]=hash;
     records.Add(new Dictionary<string,object>{{"Key",Guid.NewGuid()},{"FileName",name},{"RelativePath","_archive/"+name},
      {"Length",new FileInfo(frozen).Length},{"Sha256",hash},{"Root",new{kind=name.EndsWith(".SLDPRT")?1:5,configuration="Default"}},
      {"Properties",new Dictionary<string,string>()},{"InputRelativePath",name},{"PreviewPath",null},{"PreviewSha256",null},{"PreviewLength",0},{"PreviewError",null}});
    }
    var core=json.Serialize(new{Success=true,RootKey=records[0]["Key"],Files=records,Warnings=new string[0],PreviewsPending=true});
    var coreHash=Hash(Encoding.UTF8.GetBytes(core));var corePath=Path.Combine(directory,"native-result.json");File.WriteAllText(corePath,core,new UTF8Encoding(false));
    string previous=null;
    if(names.Contains("MODEL.SLDPRT"))
    {
     var preview=Path.Combine(directory,"_previews","model.step");File.WriteAllText(preview,"verified cached 3D preview");
     var cached=records.Select(record=>new Dictionary<string,object>(record)).ToArray();
     var item=cached.Single(record=>(string)record["FileName"]=="MODEL.SLDPRT");
     item["PreviewPath"]="_previews/model.step";item["PreviewLength"]=new FileInfo(preview).Length;item["PreviewSha256"]=Hash(File.ReadAllBytes(preview));
     item["PreviewConfiguration"]="0";
     previous=json.Serialize(new{Success=true,RootKey=records[0]["Key"],Files=cached,Warnings=new string[0],PreviewsPending=true,ArchiveSourceSha256=coreHash,PreviewGeneratorVersion="active-document-config-v3"});
    }
    string damaged=mode=="damaged-step"?"SOURCE.STEP":mode=="damaged-txt"?"EQUATIONS.TXT":mode=="damaged-xlsx"?"DESIGN.XLSX":mode=="damaged-native-model"?"MODEL.SLDPRT":null;
    if(damaged!=null)
    {
     var path=Path.Combine(directory,"_archive",damaged);var bytes=File.ReadAllBytes(path);bytes[0]^=1;File.WriteAllBytes(path,bytes);
    }
    var frozenBefore=names.ToDictionary(name=>name,name=>Hash(File.ReadAllBytes(Path.Combine(directory,"_archive",name))));
    var calls=new List<string>();var checkpoints=new List<string>();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
    var app=new Proxy(appType,call=>{
     calls.Add(call.MethodName);
     if(call.MethodName=="GetDocuments")return Array.Empty<object>();
     if(call.MethodName=="SetSearchFolders"||call.MethodName=="SetCurrentWorkingDirectory")return true;
     throw new IOException("No CAD open/save is allowed for companions or a verified 3D cache: "+call.MethodName);
    }).GetTransparentProxy();
    string output=null;Exception failure=null;
    try{output=(string)Type("PdmAddin").GetMethod("ReadBrowserArchivePreviews",Flags).Invoke(null,new object[]{app,directory,core,previous,(Action<string>)(_=>{}),(Action<string>)(value=>checkpoints.Add(value)),true});}
    catch(TargetInvocationException error){failure=error.InnerException;}
    if(calls.Any(name=>name!="GetDocuments"&&name!="SetSearchFolders"&&name!="SetCurrentWorkingDirectory"))throw new Exception("Companion/cached preview attempted native document access");
    if(damaged!=null)
    {
     if(!(failure is IOException)||!failure.Message.Contains("原生固定副本校验失败")||!failure.Message.Contains(damaged)||checkpoints.Count!=0)
      throw new Exception("Corrupt frozen bytes were not rejected before preview checkpoints: "+mode,failure);
    }
    else
    {
     if(failure!=null)throw failure;
     var result=(Dictionary<string,object>)json.DeserializeObject(output);
     var files=((IEnumerable)result["Files"]).Cast<Dictionary<string,object>>().ToArray();
     if(!Equals(result["Success"],true)||!Equals(result["PreviewsPending"],false)||((IEnumerable)result["Warnings"]).Cast<object>().Any()||files.Length!=names.Count)
      throw new Exception("Companion files created a retry/warning or changed native success");
     foreach(var file in files)
     {
      var name=(string)file["FileName"];
      if(name=="MODEL.SLDPRT")
      {
       if((string)file["PreviewPath"]!="_previews/model.step"||Convert.ToInt64(file["PreviewLength"])<=0||!Equals(file["PreviewConfiguration"],"0"))throw new Exception("Verified native 3D preview cache or actual configuration was lost");
      }
      else if(file["PreviewPath"]!=null||file["PreviewSha256"]!=null||Convert.ToInt64(file["PreviewLength"])!=0||file["PreviewError"]!=null)
       throw new Exception("Other file was assigned a synthetic preview or failure");
      if(Hash(File.ReadAllBytes(Path.Combine(directory,name)))!=savedHashes[name])throw new Exception("Working file was not restored from verified frozen bytes");
     }
     if(checkpoints.Count==0)throw new Exception("Durable preview phase checkpoint was lost");
    }
    if(Hash(File.ReadAllBytes(corePath))!=coreHash||names.Any(name=>Hash(File.ReadAllBytes(Path.Combine(directory,"_archive",name)))!=frozenBefore[name]))
     throw new Exception("Preview handling changed immutable core or frozen files");
    Console.WriteLine("PASS: native/Other preview scope "+mode);
   }
   finally
   {
    if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-browser-companion-preview-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
    foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
    Directory.Delete(directory,true);
   }
  }
 }
}
