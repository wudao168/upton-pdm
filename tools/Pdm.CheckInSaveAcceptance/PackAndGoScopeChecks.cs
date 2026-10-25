using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void PackAndGoArchiveScopeScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-pack-scope-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  try
  {
   var rootPath=Path.Combine(folder,"OWNER.SLDPRT");var packed=Path.Combine(folder,"PACKED.STEP");
   var missing=Path.Combine(folder,"OLD-MISSING.STEP");var outside=Path.Combine(folder,"OUTSIDE-EXISTS.STEP");
   foreach(var path in new[]{rootPath,packed,outside})File.WriteAllText(path,"saved content");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   var sources=new[]{packed,missing,outside};
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
    if(call.MethodName!="ListExternalFileReferences2")throw new Exception(call.MethodName);
    var output=(object[])call.Args.Clone();output[0]=sources;output[4]=new[]{5,99,3};
    return new ReturnMessage(null,output,output.Length,call.LogicalCallContext,call);
   }).GetTransparentProxy();
   var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
    call.MethodName=="GetType"?1:call.MethodName=="get_Extension"?extension:call.MethodName=="GetPathName"?rootPath
    :call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null
    :throw new Exception("Pack scope must not inspect or mutate feature geometry: "+call.MethodName)).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    switch(call.MethodName)
    {
     case "get_ActiveDoc":return model;
     case "GetCurrentWorkingDirectory":return folder;
     case "SetCurrentWorkingDirectory":return true;
     case "GetOpenDocumentByName":return string.Equals((string)call.Args[0],rootPath,StringComparison.OrdinalIgnoreCase)?model:null;
     case "ActivateDoc3":return model;
     default:throw new Exception("Unexpected native access outside Pack and Go scope: "+call.MethodName);
    }
   }).GetTransparentProxy();
   var root=New("CadTreeNode");Set(root,"FullPath",rootPath);Set(root,"FileName",Path.GetFileName(rootPath));Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   var inventory=new[]{rootPath,packed,missing};
   Type("NativeDesignDependencies").GetMethod("Collect",Flags).Invoke(null,new object[]{app,root,inventory});
   var children=((IEnumerable)Get(root,"Children")).Cast<object>().ToArray();
   if(children.Length!=1||(string)Get(children[0],"FullPath")!=packed)throw new Exception("Outside or missing history became a mandatory upload");
   var items=((IEnumerable)Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{root})).Cast<object>().ToArray();
   if(items.Length!=2)throw new Exception("Outside history entered registration or permission preparation");
   Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{root,inventory});
   Console.WriteLine("PASS: Pack and Go scope keeps packaged source, ignores missing history and even existing files outside package without geometry gates");

   var manifest=New("ControlledOpenManifestDto");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
   foreach(var path in new[]{rootPath,packed}){var file=New("ControlledOpenFileDto");Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));files.Add(file);}
   Set(manifest,"Files",files);
   Type("PdmAddin").GetMethod("ValidateControlledLoadedPaths",Flags).Invoke(null,new[]{model,(object)rootPath,manifest,app});
   Console.WriteLine("PASS: actual loaded-path validation does not resurrect outside-package design history as an open requirement");

   File.Delete(packed);
   var blocked=false;
   try{Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{root,inventory});}
   catch(TargetInvocationException error){blocked=error.InnerException is InvalidOperationException;}
   if(!blocked)throw new Exception("A real packaged file disappearing before upload was ignored");
   Console.WriteLine("PASS: actual packaged file loss remains an archive error");
  }
  finally{Directory.Delete(folder,true);}
 }
}
