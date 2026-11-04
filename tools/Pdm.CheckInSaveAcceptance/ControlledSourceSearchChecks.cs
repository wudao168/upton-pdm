using System;
using System.Collections;
using System.IO;
using System.Reflection;

internal static partial class Program
{
 static void ControlledSourceSearchScenario(string directory)
 {
  foreach(var mode in new[]{"controlled", "old-source", "wrong-path", "changed-target", "unreadable", "outside-scope", "partial-rollback"})
  {
   var folder=Path.Combine(directory,mode);Directory.CreateDirectory(folder);
   var root=Path.Combine(folder,"H620.SLDPRT");var step=Path.Combine(folder,"H620_外观.STEP");
   var part=Path.Combine(folder,"PART.SLDPRT");var old=Path.Combine(folder,"old","H620_外观.STEP");
   File.WriteAllText(root,"native content");File.WriteAllText(step,"server step");File.WriteAllText(part,"native part");
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());
   var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
   foreach(var path in new[]{root,step,part})
   {
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());
    Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));
    Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);files.Add(file);
   }
   Set(manifest,"Files",files);var rootHash=WorkingReuseHash(root);var partHash=WorkingReuseHash(part);var searched=0;
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>{
    if(call.MethodName=="GetDocuments")return new object[0];
    if(call.MethodName=="GetDocumentDependencies2")
    {
     if((string)call.Args[0]!=root)return new string[0];
     if(!(bool)call.Args[2])return mode=="partial-rollback"?new[]{"part",Path.Combine(folder,"old","PART.SLDPRT"),"source",old}:new[]{"source",old};
     searched++;if(mode=="unreadable")throw new IOException("search failed");
     if(mode=="changed-target")File.WriteAllText(step,"unexpected bytes");
     return new[]{"source",mode=="old-source"?old:mode=="wrong-path"?Path.Combine(folder,"other","H620_外观.STEP"):step};
    }
    if(call.MethodName=="ReplaceReferencedDocument")
    {
     if((string)call.Args[0]!=root)throw new Exception("Unexpected owner");
     if(Path.GetExtension((string)call.Args[1]).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)) {File.AppendAllText(root,"partial rewrite");return true;}
     return false;
    }
    throw new Exception("Search fallback must not open/save/activate: "+call.MethodName);
   }).GetTransparentProxy();
   IDisposable boundary=mode=="outside-scope"?null:(IDisposable)Activator.CreateInstance(Type("ControlledOpenBoundary"),Flags,null,new object[]{folder,manifest},null);
   var blocked=false;
   try {TreeCallStatic("ControlledReferenceMaterializer","Prepare",app,root,manifest);}
   catch(TargetInvocationException error)when(error.InnerException is IOException){blocked=true;}
   finally{boundary?.Dispose();}
   var success=mode=="controlled"||mode=="partial-rollback";
   if(blocked==success||WorkingReuseHash(root)!=rootHash||WorkingReuseHash(part)!=partHash
      || mode=="outside-scope"&&searched!=0||Directory.GetFiles(folder,".ref-*.bak").Length!=0)
    throw new Exception("Wrong bounded source outcome: "+mode);
   Console.WriteLine("PASS: "+mode+" preserves native bytes and only accepts verified bounded source resolution");
  }
 }
}
