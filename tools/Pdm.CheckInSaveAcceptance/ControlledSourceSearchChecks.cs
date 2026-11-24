using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

internal static partial class Program
{
 static void ControlledSourceSearchScenario(string directory)
 {
  directory=Path.GetFullPath(directory);
  foreach(var mode in new[]{"controlled", "old-source", "wrong-path", "changed-target", "unreadable", "outside-scope", "partial-rollback",
   "working-copy-controlled", "working-copy-old-source", "working-copy-changed-target",
   "working-copy-native-replacement-stale-controlled", "working-copy-native-replacement-stale-old-source"})
  {
   var folder=Path.Combine(directory,mode);Directory.CreateDirectory(folder);
   var root=Path.Combine(folder,"H620.SLDPRT");var step=Path.Combine(folder,"H620_外观.STEP");
   var part=Path.Combine(folder,"PART.SLDPRT");var old=Path.Combine(folder,"old","H620_外观.STEP");
   File.WriteAllText(root,"native content");File.WriteAllText(step,"server step");File.WriteAllText(part,"native part");
   var staleAfterReplace=mode.Contains("native-replacement-stale");
   var expectedSource=staleAfterReplace?part:step;
   if(staleAfterReplace)
   {
    old=Path.Combine(folder,"old",Path.GetFileName(part));
    Directory.CreateDirectory(Path.GetDirectoryName(old));
    File.WriteAllText(old,"previous working copy");
   }
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());
   var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
   foreach(var path in new[]{root,step,part})
   {
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());
    Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));
    Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);files.Add(file);
   }
   Set(manifest,"Files",files);var rootHash=WorkingReuseHash(root);var partHash=WorkingReuseHash(part);var searched=0;
   var preparingWorkingCopy=mode.StartsWith("working-copy-",StringComparison.Ordinal);
   var previousDirectory=Path.Combine(folder,"old");var currentDirectory=previousDirectory;var searchFolders=previousDirectory;
   var preferences=new Dictionary<int,bool>();
   int? searchRules=null, toolboxSearch=null;
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>{
    if(call.MethodName=="GetDocuments")return new object[0];
    if(preparingWorkingCopy)
    {
     if(call.MethodName=="GetCurrentWorkingDirectory")return currentDirectory;
     if(call.MethodName=="SetCurrentWorkingDirectory"){currentDirectory=(string)call.Args[0];return true;}
     if(call.MethodName=="GetSearchFolders")return searchFolders;
     if(call.MethodName=="SetSearchFolders"){searchFolders=(string)call.Args[1];return true;}
     if(call.MethodName=="GetUserPreferenceToggle")
     {
      var preference=(int)call.Args[0];
      if(!preferences.TryGetValue(preference,out var value))
      {
       if(searchRules.HasValue)toolboxSearch=preference;else searchRules=preference;
       preferences[preference]=value=toolboxSearch==preference;
      }
      return value;
     }
     if(call.MethodName=="SetUserPreferenceToggle"){preferences[(int)call.Args[0]]=(bool)call.Args[1];return true;}
    }
    if(call.MethodName=="GetDocumentDependencies2")
    {
     if((string)call.Args[0]!=root)return new string[0];
     if(!(bool)call.Args[2])return mode=="partial-rollback"?new[]{"part",Path.Combine(folder,"old","PART.SLDPRT"),"source",old}:new[]{"source",old};
     searched++;if(mode=="unreadable")throw new IOException("search failed");
     if(preparingWorkingCopy&&(currentDirectory!=folder||searchFolders!=folder||!preferences[searchRules.Value]||preferences[toolboxSearch.Value]))
      throw new Exception("Working-copy search did not enter the same bounded native context");
     if(mode.EndsWith("changed-target",StringComparison.Ordinal))File.WriteAllText(step,"unexpected bytes");
     return new[]{"source",mode.EndsWith("old-source",StringComparison.Ordinal)?old:mode=="wrong-path"?Path.Combine(folder,"other","H620_外观.STEP"):expectedSource};
    }
    if(call.MethodName=="ReplaceReferencedDocument")
    {
     if((string)call.Args[0]!=root)throw new Exception("Unexpected owner");
     if(Path.GetExtension((string)call.Args[1]).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)) {File.AppendAllText(root,"partial rewrite");return true;}
     return false;
    }
    throw new Exception("Search fallback must not open/save/activate: "+call.MethodName);
   }).GetTransparentProxy();
   IDisposable boundary=mode=="outside-scope"||preparingWorkingCopy?null:(IDisposable)Activator.CreateInstance(Type("ControlledOpenBoundary"),Flags,null,new object[]{folder,manifest},null);
   var blocked=false;
   try {TreeCallStatic("ControlledReferenceMaterializer",preparingWorkingCopy?"PrepareWorkingCopyBeforeOpen":"Prepare",app,root,manifest);}
   catch(TargetInvocationException error)when(error.InnerException is IOException){blocked=true;}
   finally{boundary?.Dispose();}
   var success=mode=="controlled"||mode=="partial-rollback"||mode.EndsWith("-controlled",StringComparison.Ordinal);
   if(blocked==success||WorkingReuseHash(root)!=rootHash||WorkingReuseHash(part)!=partHash
      || mode=="outside-scope"&&searched!=0||Directory.GetFiles(folder,".ref-*.bak").Length!=0)
    throw new Exception("Wrong bounded source outcome: "+mode);
   if(preparingWorkingCopy&&(searched!=1||currentDirectory!=previousDirectory||searchFolders!=previousDirectory
      ||preferences[searchRules.Value]||!preferences[toolboxSearch.Value]))
    throw new Exception("Working-copy search settings or directory did not return to their original state: "+mode);
   Console.WriteLine("PASS: "+mode+" preserves native bytes and only accepts verified bounded source resolution");
   if(mode=="working-copy-native-replacement-stale-controlled")
    VerifyNativeFallbackPreload(folder,root,part,old,manifest,appType);
  }
 }

 static void VerifyNativeFallbackPreload(string directory,string root,string part,string old,object manifest,System.Type appType)
 {
  var preload=Type("ControlledReferenceMaterializer").GetMethod("PreloadVerifiedNativeSources",Flags);
  var openType=preload.GetParameters()[3].ParameterType;
  var modelType=openType.GetGenericArguments()[2];
  var pathParameter=Expression.Parameter(typeof(string),"path");
  var kindParameter=Expression.Parameter(typeof(int),"kind");
  foreach(var mode in new[]{"exact", "already-open", "outside-scope", "wrong-result", "editable-result",
   "changed-target", "changed-on-open", "loaded-old", "loads-old"})
  {
   var opened=0;
   var modelPath=mode=="wrong-result"||mode=="loaded-old"?old:part;
   var model=new Proxy(modelType,call=>{
    if(call.MethodName=="GetPathName")return modelPath;
    if(call.MethodName=="IsOpenedReadOnly")return mode!="editable-result";
    throw new Exception("Preload must only verify loaded identity: "+call.MethodName);
   }).GetTransparentProxy();
   var conflictingModel=new Proxy(modelType,call=>call.MethodName=="GetPathName"?old:
    throw new Exception("Unexpected call on conflicting model: "+call.MethodName)).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    if(call.MethodName=="GetDocuments")return mode=="loaded-old"||mode=="already-open"?new[]{model}:
     mode=="loads-old"&&opened>0?new[]{model,conflictingModel}:Array.Empty<object>();
    throw new Exception("Unexpected preload native call: "+call.MethodName);
   }).GetTransparentProxy();
   Func<string,int,object> open=(path,kind)=>{
    if(path!=part||kind!=1)throw new Exception("Only the verified native part may open");
    opened++;
    if(mode=="changed-on-open")File.WriteAllText(part,"part changed during native open");
    return model;
   };
   var opener=Expression.Lambda(openType,Expression.Convert(
    Expression.Invoke(Expression.Constant(open),pathParameter,kindParameter),modelType),pathParameter,kindParameter).Compile();
   var original=File.ReadAllText(part);
   if(mode=="changed-target")File.WriteAllText(part,"unverified part");
   using var boundary=mode=="outside-scope"?null:(IDisposable)Activator.CreateInstance(
    Type("ControlledOpenBoundary"),Flags,null,new object[]{directory,manifest},null);
   var blocked=false;
   try {preload.Invoke(null,new[]{app,root,manifest,opener});}
   catch(TargetInvocationException error)when(error.InnerException is IOException||error.InnerException is InvalidOperationException){blocked=true;}
   finally {if(mode=="changed-target"||mode=="changed-on-open")File.WriteAllText(part,original);}
   var mustOpen=mode=="exact"||mode=="wrong-result"||mode=="editable-result"||mode=="changed-on-open"||mode=="loads-old";
   var mustBlock=mode!="exact"&&mode!="already-open";
   if(blocked!=mustBlock||opened!=(mustOpen?1:0)||File.ReadAllText(old)!="previous working copy")
    throw new Exception("Unexpected native preload outcome: "+mode);
   Console.WriteLine("PASS: native source preload "+mode);
  }
 }
}
