using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void OperationRuleScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-formal-rules-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  try
  {
   object Node(string path,string kind="Assembly")
   {
    var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));
    Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"InstancePath",Path.GetFileName(path));return node;
   }
   var rootPath=Path.Combine(folder,"ROOT.SLDASM");var partPath=Path.Combine(folder,"PART.SLDPRT");
   File.WriteAllText(rootPath,"native assembly");File.WriteAllText(partPath,"native part");
   var root=Node(rootPath);var part=Node(partPath,"Part");((IList)Get(root,"Children")).Add(part);
   Set(root,"ArchiveReadError","结构接口暂不可读");
   var confirm=Type("NativeArchiveManifest").GetMethod("ConfirmFileScope",Flags);
   confirm.Invoke(null,new object[]{root,new[]{rootPath,partPath}});
   Type("PdmApiClient").GetMethod("ValidateArchiveReferenceCompleteness",Flags).Invoke(null,new[]{root});
   if(!(bool)Get(root,"StructureIndexPending")||(bool)Get(root,"HasBlockingIssue"))throw new Exception("Partial index must not block valid package files");
   Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{root,new[]{rootPath,partPath}});
   var missing=Node(Path.Combine(folder,"OLD.SLDPRT"),"Part");Set(missing,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));
   ((IList)Get(root,"Children")).Add(missing);confirm.Invoke(null,new object[]{root,new[]{rootPath,partPath}});
   if(!(bool)Get(missing,"IsOutsideNativeFileScope"))throw new Exception("Outside-package historical address was made mandatory");
   var items=((IEnumerable)Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{root})).Cast<object>().ToArray();
   if(items.Length!=2)throw new Exception("Historical address entered registration scope");
   File.Delete(partPath);
   var blocked=false;
   try{Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{root,new[]{rootPath,partPath}});}
   catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}
   if(!blocked)throw new Exception("A required package file disappeared but archive did not fail");
   Console.WriteLine("PASS: B05/B09 package scope permits partial indexing, omits outside history and still rejects lost package bytes");

   Directory.CreateDirectory(Path.Combine(folder,"old"));File.WriteAllText(partPath,"same bytes");
   var duplicate=Path.Combine(folder,"old","PART.SLDPRT");File.WriteAllText(duplicate,"same bytes");
   var aliasesRoot=Node(rootPath);var a=Node(partPath,"Part");var b=Node(duplicate,"Part");
   ((IList)Get(aliasesRoot,"Children")).Add(a);((IList)Get(aliasesRoot,"Children")).Add(b);
   Type("ArchiveFileAliases").GetMethod("Normalize",Flags).Invoke(null,new[]{aliasesRoot});
   if((string)Get(a,"FullPath")!=(string)Get(b,"FullPath")||!File.Exists(duplicate))throw new Exception("Equal-content alias normalization failed or removed source");
   Set(b,"FullPath",duplicate);File.WriteAllText(duplicate,"different bytes");
   Type("ArchiveFileAliases").GetMethod("Normalize",Flags).Invoke(null,new[]{aliasesRoot});
   if((string)Get(a,"FullPath")== (string)Get(b,"FullPath"))throw new Exception("Different files silently merged");
   Console.WriteLine("PASS: D01 equal-content paths merge without deleting sources; real content conflicts stay distinct");

   var copy=Path.Combine(folder,"upload.copy");File.WriteAllText(copy,"frozen bytes");
   var hash=(string)Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new object[]{copy});
   File.WriteAllText(rootPath,"later local edit");
   var frozen=Type("CheckInUploadHashes").GetMethod("ReadFrozen",Flags);
   frozen.Invoke(null,new object[]{copy,hash,hash,CancellationToken.None});
   File.WriteAllText(copy,"tampered upload");blocked=false;
   try{frozen.Invoke(null,new object[]{copy,hash,hash,CancellationToken.None});}
   catch(TargetInvocationException e){blocked=e.InnerException is IOException;}
   if(!blocked||File.ReadAllText(rootPath)!="later local edit")throw new Exception("Frozen upload integrity or later local edit protection failed");
   Console.WriteLine("PASS: E03 fixed upload accepts later source edits while rejecting tampered upload copies");

   var oldCache=Path.Combine(folder,"snapshot");Directory.CreateDirectory(oldCache);
   File.WriteAllText(Path.Combine(oldCache,"ROOT.SLDASM"),"local changes");
   File.WriteAllText(Path.Combine(oldCache,"NEW.SLDDRW"),"unsubmitted drawing");
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());
   var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);var entry=New("ControlledOpenFileDto");
   File.WriteAllText(copy,"server bytes");hash=(string)Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new object[]{copy});
   Set(entry,"FileName","ROOT.SLDASM");Set(entry,"RelativePath","ROOT.SLDASM");Set(entry,"Sha256",hash);files.Add(entry);Set(manifest,"Files",files);
   var choose=Type("ControlledWorkspaceManager").GetMethod("SelectReadOnlyTarget",Flags);
   var fresh=(string)choose.Invoke(null,new object[]{oldCache,manifest});
   if(fresh==oldCache||!File.Exists(Path.Combine(oldCache,"NEW.SLDDRW")))throw new Exception("Modified cache or unsubmitted drawing was overwritten");
   Directory.CreateDirectory(fresh);File.Copy(copy,Path.Combine(fresh,"ROOT.SLDASM"));
   if((string)choose.Invoke(null,new object[]{oldCache,manifest})!=fresh)throw new Exception("Verified fresh snapshot was not reused");
   Console.WriteLine("PASS: F11/I10 changed cache and new drawing are preserved while a verified isolated snapshot can reopen and be reused");

   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   foreach(var embedded in new[]{false,true})
   {
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var outsidePath=Path.Combine(folder,"outside","CHILD.SLDPRT");
    var child=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?outsidePath
     :call.MethodName=="GetType"?1:call.MethodName=="get_Extension"?extension:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?child
     :call.MethodName=="get_IsVirtual"?embedded:call.MethodName=="get_Name2"?"CHILD-1":call.MethodName=="GetPathName"?outsidePath:throw new Exception(call.MethodName)).GetTransparentProxy();
    var parent=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?rootPath
     :call.MethodName=="GetType"?2:call.MethodName=="GetComponents"?new[]{component}:call.MethodName=="get_Extension"?extension
     :call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var app=new Proxy(appType,call=>throw new Exception("Unexpected native mutation: "+call.MethodName)).GetTransparentProxy();
    var rootOnly=New("ControlledOpenManifestDto");var rootFiles=(IList)Activator.CreateInstance(Property(rootOnly,"Files").PropertyType);
    var rootFile=New("ControlledOpenFileDto");Set(rootFile,"FileName",Path.GetFileName(rootPath));Set(rootFile,"RelativePath",Path.GetFileName(rootPath));Set(rootFile,"IsRoot",true);rootFiles.Add(rootFile);Set(rootOnly,"Files",rootFiles);
    blocked=false;
    try{Type("PdmAddin").GetMethod("ValidateHistoricalControlledOpenReferences",Flags).Invoke(null,new[]{parent,(object)rootPath,rootOnly,app});}
    catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}
    if(blocked==embedded)throw new Exception("Parent-only package must allow embedded content and reject actual outside ordinary components");
    Console.WriteLine("PASS: F20 parent-only package loaded-source validation embedded="+embedded);
   }
  }
  finally{Directory.Delete(folder,true);}
 }
}
