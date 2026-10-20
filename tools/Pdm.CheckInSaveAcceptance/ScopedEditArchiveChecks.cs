using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task ScopedEditArchiveScenario()
 {
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var directory=Path.Combine(Path.GetTempPath(),"pdm-scoped-archive-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var source=Path.Combine(directory,"USER.SLDPRT");File.WriteAllText(source,"unsaved editor baseline");
  var document=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?source:call.MethodName=="GetSaveFlag"?true:throw new Exception("Unexpected mutation "+call.MethodName)).GetTransparentProxy();
  var opens=0;SetField(plugin,"application",new Proxy(appType,call=>{
   if(call.MethodName=="GetDocuments")return new[]{document};if(call.MethodName=="get_ActiveDoc")return document;
   opens++;throw new Exception("Archive preparation opened a closed CAD document: "+call.MethodName);
  }).GetTransparentProxy());
  var itemType=Type("BatchOperationItem");var items=Array.CreateInstance(itemType,56);
  for(var i=0;i<56;i++)
  {
   var node=New("CadTreeNode");Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),i==0?1:2));Set(node,"FullPath",i==0?source:Path.Combine(directory,"DRAWING-"+i+".SLDDRW"));Set(node,"FileName",i==0?"USER.SLDPRT":"DRAWING-"+i+".SLDDRW");items.SetValue(New("BatchOperationItem",node,0),i);
  }
  var task=(Task)Type("PdmAddin").GetMethod("PreloadBatchCheckInDocumentsAsync",Flags).Invoke(plugin,new object[]{items,null,CancellationToken.None});await task;
  if(opens!=0||((IList)Get(task,"Result")).Count!=0||File.ReadAllText(source)!="unsaved editor baseline")throw new Exception("Batch preparation accumulated documents or changed user content");
  Console.WriteLine("PASS: 55 closed drawings stay closed during preparation and the user's pending editor remains untouched");

  var owner=New("ControlledOpenManifestDto");var requested=New("ControlledOpenManifestDto");var project=Guid.NewGuid();var rootId=Guid.NewGuid();var subId=Guid.NewGuid();var partId=Guid.NewGuid();var rootVersion=Guid.NewGuid();
  var files=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
  foreach(var pair in new[]{Tuple.Create(rootId,"ROOT.SLDASM"),Tuple.Create(subId,"SUB.SLDASM"),Tuple.Create(partId,"PART.SLDPRT")})
  {var file=New("ControlledOpenFileDto");Set(file,"DocumentId",pair.Item1);Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",pair.Item2);Set(file,"RelativePath",pair.Item2);Set(file,"Revision","W1");files.Add(file);}
  Set(owner,"ProjectId",project);Set(owner,"RootDocumentId",rootId);Set(owner,"RootVersionId",rootVersion);Set(owner,"RootRelativePath","ROOT.SLDASM");Set(owner,"Files",files);
  var selected=New("ControlledOpenFileDto");Set(selected,"DocumentId",partId);Set(selected,"FileName","PART.SLDPRT");Set(selected,"RelativePath","PART.SLDPRT");Set(selected,"VersionId",Guid.NewGuid());Set(selected,"Revision","W2");Set(selected,"IsRoot",true);
  var requestedFiles=(IList)Activator.CreateInstance(Property(requested,"Files").PropertyType);requestedFiles.Add(selected);Set(requested,"Files",requestedFiles);Set(requested,"ProjectId",project);Set(requested,"RootDocumentId",partId);
  var merged=Type("PdmAddin").GetMethod("MergeEditContext",Flags).Invoke(null,new[]{owner,requested});var result=((IEnumerable)Get(merged,"Files")).Cast<object>().ToArray();
  if((Guid)Get(merged,"RootDocumentId")!=rootId||(Guid)Get(merged,"RootVersionId")!=rootVersion||result.Length!=3||result.Count(file=>(bool)Get(file,"IsRoot"))!=1
   ||(string)Get(result.Single(file=>(Guid)Get(file,"DocumentId")==partId),"Revision")!="W2"||((IEnumerable)Get(owner,"Files")).Cast<object>().Any(file=>(string)Get(file,"Revision")!="W1"))throw new Exception("Edit context changed parent version or mutated the immutable preview");
  var dirty=false;var closeCount=0;var loaded=new List<object>();
  foreach(var pair in new[]{Tuple.Create(rootId,"ROOT.SLDASM"),Tuple.Create(subId,"SUB.SLDASM"),Tuple.Create(partId,"PART.SLDPRT")})
  {
   var path=Path.Combine(directory,pair.Item2);File.WriteAllText(path,"original bytes");
   loaded.Add(new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return path;if(call.MethodName=="GetTitle")return pair.Item2;
    if(call.MethodName=="GetType")return pair.Item1==partId?1:2;if(call.MethodName=="GetSaveFlag")return dirty&&pair.Item1==rootId;
    if(call.MethodName=="IsOpenedReadOnly")return false;throw new Exception("Unexpected model operation "+call.MethodName);
   }).GetTransparentProxy());
  }
  SetField(plugin,"application",new Proxy(appType,call=>{if(call.MethodName=="GetDocuments")return loaded.ToArray();closeCount++;throw new Exception("Validation closed or edited a document: "+call.MethodName);}).GetTransparentProxy());
  var validate=Type("PdmAddin").GetMethod("ValidateEditContextSwitch",Flags);validate.Invoke(plugin,new[]{merged,(object)Path.Combine(directory,"working")});
  if(closeCount!=0)throw new Exception("Context validation mutated native documents");
  Console.WriteLine("PASS: Open clean parent and subassembly are part of read-only edit context without changing their versions or acquiring their permissions");
  using(var cacheClient=(IDisposable)New("PdmApiClient","http://isolated-cache.invalid/"))
  {
   var workspace=New("ControlledWorkspaceManager",cacheClient,Path.Combine(directory,"cache"));
   var readOnly=Type("ControlledWorkspaceManager").GetMethod("GetReadOnlyDirectory",Flags);
   if((string)readOnly.Invoke(workspace,new[]{owner,(object)false})==(string)readOnly.Invoke(workspace,new[]{merged,(object)false})
      ||(string)readOnly.Invoke(workspace,new[]{owner,(object)true})!=(string)readOnly.Invoke(workspace,new[]{merged,(object)true}))throw new Exception("Child update reused an immutable latest cache or changed a historical cache location");
  }
  Console.WriteLine("PASS: Same parent version with a newer child uses a separate immutable latest cache; historical cache stays pinned");
  dirty=true;var blocked=false;try{validate.Invoke(plugin,new[]{merged,(object)Path.Combine(directory,"working")});}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){blocked=error.InnerException.Message.Contains("ROOT.SLDASM");}
  if(!blocked||closeCount!=0)throw new Exception("Unsaved parent editor was not preserved");
  Console.WriteLine("PASS: Actual unsaved parent conflict is identified before native switching or server checkout");
 }
}
