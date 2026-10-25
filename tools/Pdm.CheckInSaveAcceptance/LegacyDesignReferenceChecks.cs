using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void LegacyDesignReferenceScenario()
 {
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  foreach(var mode in new[]{"legacy-only","legacy-and-controlled","unknown-status"})
  {
   var paths=mode=="legacy-and-controlled"?new[]{@"C:\old\LEGACY.SLDASM",@"C:\controlled\CURRENT.SLDASM"}:new[]{@"C:\old\LEGACY.SLDASM"};
   var states=paths.Select(_=>mode=="unknown-status"?99:4).ToArray();
   var features=new object[paths.Length];var locked=new List<int>();
   for(var index=paths.Length-1;index>=0;index--)
   {
    var current=index;
    features[index]=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Feature",true),call=>{
     if(call.MethodName=="GetID")return current+1;
     if(call.MethodName=="get_Name")return "Feature"+current;
     if(call.MethodName=="GetFirstSubFeature")return null;
     if(call.MethodName=="GetNextFeature")return current+1<features.Length?features[current+1]:null;
     if(call.MethodName=="get_Is3DInterconnectFeature")return false;
     if(call.MethodName=="ListExternalFileReferences2")
     {
      var output=(object[])call.Args.Clone();output[0]=new[]{paths[current]};output[4]=new[]{states[current]};output[7]=0;output[8]="";
      return new ReturnMessage(null,output,output.Length,call.LogicalCallContext,call);
     }
     if(call.MethodName=="UpdateExternalFileReferences")
     {
      if((int)call.Args[2]!=2)throw new Exception("Native history must be locked, not broken");
      states[current]=1;locked.Add(current);return null;
     }
     throw new Exception("Unexpected feature mutation "+call.MethodName);
    }).GetTransparentProxy();
   }
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
    if(call.MethodName!="ListExternalFileReferences2")throw new Exception(call.MethodName);
    var output=(object[])call.Args.Clone();output[0]=paths;output[4]=states.ToArray();
    return new ReturnMessage(null,output,output.Length,call.LogicalCallContext,call);
   }).GetTransparentProxy();
   var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
    call.MethodName=="GetType"?1:call.MethodName=="get_Extension"?extension:call.MethodName=="FirstFeature"?features[0]
    :call.MethodName=="GetPathName"?@"C:\controlled\OWNER.SLDPRT":call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
   var controlled=new Dictionary<string,string>{{"CURRENT.SLDASM",@"C:\controlled\CURRENT.SLDASM"}};var notes=new List<string>();
   var changed=(bool)Type("NativeDesignDependencies").GetMethod("Relocate",Flags).Invoke(null,new object[]{model,controlled,notes});
   if(changed||locked.Count!=0||notes.Count!=0)throw new Exception("Opening must preserve native design links without locking or conversion: "+mode);
   if(states.Length>1&&states[1]!=4)throw new Exception("Existing controlled association was frozen");
   Console.WriteLine("PASS: legacy history "+mode+" causes no forced lock, break, conversion or geometry inspection");
  }
  PackAndGoArchiveScopeScenario();
  StartupIdleDeferralScenario();
  ArchiveContextOrderScenario();
 }

 static void StartupIdleDeferralScenario()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  var calls=new List<string>();
  var app=new Proxy(appType,call=>{
   calls.Add(call.MethodName);
   if(call.MethodName=="get_StartupProcessCompleted")return false;
   throw new Exception("Native document work ran before startup completed: "+call.MethodName);
  }).GetTransparentProxy();
  var addin=New("PdmAddin");SetField(addin,"application",app);
  Type("PdmAddin").GetMethod("OnSolidWorksIdle",Flags).Invoke(addin,null);
  if(calls.Count!=1||calls[0]!="get_StartupProcessCompleted")throw new Exception("Startup idle guard was not checked first");
  Console.WriteLine("PASS: startup idle defers native document scanning until startup completes");
 }

 static void ArchiveContextOrderScenario()
 {
  object Node(string name,string kind){var n=New("CadTreeNode");Set(n,"FullPath",@"C:\controlled\"+name);Set(n,"FileName",name);Set(n,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));return n;}
  var root=Node("ASSEMBLY.SLDASM","Assembly");var part=Node("PART.SLDPRT","Part");var context=Node("ASSEMBLY.SLDASM","Assembly");
  Set(context,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
  ((System.Collections.IList)Get(root,"Children")).Add(part);((System.Collections.IList)Get(part,"Children")).Add(context);
  var items=Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{root});
  var ordered=((System.Collections.IEnumerable)Type("PdmAddin").GetMethod("OrderBatchItemsForSave",Flags).Invoke(null,new[]{items})).Cast<object>().Select(item=>(string)Get(Get(item,"Node"),"FileName")).ToArray();
  if(!ordered.SequenceEqual(new[]{"PART.SLDPRT","ASSEMBLY.SLDASM"}))throw new Exception("A contextual parent was treated as its child's owned component");
  Console.WriteLine("PASS: assembly contextual back-reference preserves child-first save ordering without a false ownership cycle");
  Set(context,"Role",Enum.Parse(Type("CadReferenceRole"),"Component"));
  items=Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{root});
  var blocked=false;
  try{Type("PdmAddin").GetMethod("OrderBatchItemsForSave",Flags).Invoke(null,new[]{items});}
  catch(System.Reflection.TargetInvocationException error){blocked=error.InnerException is InvalidOperationException;}
  if(!blocked)throw new Exception("Real ownership cycles must remain blocked");
  Console.WriteLine("PASS: real component ownership cycle remains blocked");
 }
}
