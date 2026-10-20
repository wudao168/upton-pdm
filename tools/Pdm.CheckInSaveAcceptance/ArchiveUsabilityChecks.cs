using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void ArchiveUsabilityScenario()
 {
  DrawingSaveGate();
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var directory=Path.Combine(Path.GetTempPath(),"pdm-drawing-sequence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var path=Path.Combine(directory,"700020.05.00-09.SLDPRT");File.WriteAllText(path,"unchanged saved model");
  var dirty=false;
  var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?dirty:throw new Exception("Unexpected model mutation "+call.MethodName)).GetTransparentProxy();
  SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{model}:throw new Exception(call.MethodName)).GetTransparentProxy());
  var reference=New("CadTreeNode");Set(reference,"FullPath",path);Set(reference,"FileName",Path.GetFileName(path));Set(reference,"Configuration","默认");Set(reference,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(reference,"DocumentId",Guid.NewGuid());Set(reference,"CurrentRevision","W1");
  var id=Get(reference,"DocumentId");
  foreach(var name in new[]{"05","07","08","09","09-retry"})
  {
   dirty=name.StartsWith("09",StringComparison.Ordinal);
   var drawing=New("CadTreeNode");Set(drawing,"FullPath",Path.Combine(directory,name+".SLDDRW"));Set(drawing,"FileName",name+".SLDDRW");Set(drawing,"Kind",Enum.ToObject(Type("CadDocumentKind"),2));((IList)Get(drawing,"ReferencedModels")).Add(reference);
   Type("PdmAddin").GetMethod("ValidateDrawingModelsSaved",Flags).Invoke(plugin,new object[]{drawing,new Dictionary<string,bool>{{path,false}}});
   var items=Array.CreateInstance(Type("BatchOperationItem"),1);items.SetValue(New("BatchOperationItem",drawing,0),0);
   Type("PdmAddin").GetMethod("ValidateBatchSavedState",Flags).Invoke(plugin,new object[]{items,new Dictionary<string,bool>{{path,false}},false});
   if(!Equals(Get(reference,"DocumentId"),id)||(string)Get(reference,"CurrentRevision")!="W1"||File.ReadAllText(path)!="unchanged saved model"||dirty!=name.StartsWith("09",StringComparison.Ordinal))throw new Exception("Drawing archive changed source identity, version, bytes or pending edits");
   Console.WriteLine("PASS: Sequential independent drawing "+name+" preserves referenced model/version and pending edits");
  }
  Set(reference,"IsModifiedInSolidWorks",true);
  var selected=Array.CreateInstance(Type("BatchOperationItem"),1);selected.SetValue(New("BatchOperationItem",reference,0),0);
  var blocked=false;
  try{Type("PdmAddin").GetMethod("ValidateBatchSavedState",Flags).Invoke(plugin,new object[]{selected,new Dictionary<string,bool>{{path,true}},false});}
  catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){blocked=error.InnerException.Message.Contains("700020.05.00-09");}
  if(!blocked||!dirty)throw new Exception("Selected model pending edits were not protected");
  Console.WriteLine("PASS: Selected model with pending edits still requires saving; reference-only model does not");
  foreach(var mode in new[]{"missing-manager","partial-names","native-read-error"})
  {
   var manager=mode=="missing-manager"?null:new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>
   {
    if(call.MethodName=="get_Count")return 2;
    if(call.MethodName=="GetNames"){if(mode=="native-read-error")throw new InvalidOperationException("native test failure");return new[]{"名称"};}
    if(call.MethodName=="Get6")return new ReturnMessage(2,new object[]{"名称",true,"连接件","连接件",true,false},6,call.LogicalCallContext,call);
    throw new Exception(call.MethodName);
   }).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="get_CustomPropertyManager"?manager:throw new Exception("Unexpected mutation "+call.MethodName)).GetTransparentProxy();
   var doc=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="get_Extension"?extension:throw new Exception("Unexpected mutation "+call.MethodName)).GetTransparentProxy();
   var node=New("CadTreeNode");Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),2));Set(node,"FileName","NEW.SLDDRW");
   var properties=(IReadOnlyDictionary<string,string>)Type("PdmAddin").GetMethod("ReadArchiveProperties",Flags).Invoke(plugin,new object[]{doc,node,null,null});
   if(mode=="partial-names"&&properties["全局/名称"]!="连接件")throw new Exception("Readable property value lost");
   Console.WriteLine("PASS: Native drawing retained when optional property indexing fails: "+mode);
  }
 }
}
