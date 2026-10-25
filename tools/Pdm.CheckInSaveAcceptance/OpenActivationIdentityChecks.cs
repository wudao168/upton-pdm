using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void OpenActivationIdentityScenario()
 {
  var failures=new List<string>();
  foreach(var mode in new[]{"load-already-active","load-returned-wrong","known-name-conflict","loaded-unique","active-unique","zero-error-wrong-return","zero-error-active-other","generic-error-bitmask","rebuild-warning","dirty-target","null-zero"})
  {
   try
   {
    var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var targetPath=@"C:\qa\Snapshots\7000093.101.03-02.SLDPRT";var workingPath=@"C:\qa\Working\7000093.101.03-02.SLDPRT";var rootPath=@"C:\qa\Working\ROOT.SLDASM";
    var targetReadOnly=mode!="dirty-target";var wrongReadOnly=false;var wrongPermissionWrites=0;var targetPermissionWrites=0;var opens=0;var activations=0;
    object Model(string path)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName)
    {
     case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);
     case "IsOpenedReadOnly":return path==targetPath?targetReadOnly:path==workingPath?wrongReadOnly:true;
     case "GetSaveFlag":return path==targetPath&&mode=="dirty-target";
     case "SetReadOnlyState":if(path==workingPath){wrongPermissionWrites++;wrongReadOnly=(bool)call.Args[0];}else if(path==targetPath){targetPermissionWrites++;targetReadOnly=(bool)call.Args[0];}else throw new Exception("Unrelated root permissions changed");return true;
     default:throw new Exception("Opening must not save/rebuild/rename model: "+call.MethodName);
    }}).GetTransparentProxy();
    var target=Model(targetPath);var wrong=Model(workingPath);var root=Model(rootPath);object active=mode=="active-unique"?target:root;
    var loaded=new List<object>{root};
    if(!mode.StartsWith("load-")&&mode!="null-zero")loaded.Add(target);
    if(mode=="load-already-active"||mode=="load-returned-wrong"||mode=="known-name-conflict")loaded.Add(wrong);
    var app=new Proxy(appType,call=>
    {
     if(call.MethodName=="get_ActiveDoc")return active;
     if(call.MethodName=="GetDocuments")return loaded.ToArray();
     if(call.MethodName=="OpenDoc6")
     {
      opens++;if((string)call.Args[0]!=targetPath)throw new Exception("Open request changed target path");
      var args=(object[])call.Args.Clone();args[4]=0;args[5]=2;
      object returned=mode=="null-zero"?null:mode=="load-returned-wrong"?wrong:target;
      if(returned!=null){active=returned;if(!loaded.Exists(item=>ReferenceEquals(item,returned)))loaded.Add(returned);}
      return new ReturnMessage(returned,args,args.Length,call.LogicalCallContext,call);
     }
     if(call.MethodName=="ActivateDoc3")
     {
      activations++;if((bool)call.Args[1])throw new Exception("Activation must not rebuild by user preference");
      var args=(object[])call.Args.Clone();args[3]=mode=="generic-error-bitmask"?3:mode=="rebuild-warning"?2:0;
      object returned=mode=="load-already-active"||mode=="load-returned-wrong"||mode=="known-name-conflict"||mode=="zero-error-wrong-return"?wrong:target;
      active=mode=="zero-error-active-other"?root:returned;
      return new ReturnMessage(returned,args,args.Length,call.LogicalCallContext,call);
     }
     throw new Exception("Opening must not close or mutate another document: "+call.MethodName);
    }).GetTransparentProxy();SetField(plugin,"application",app);
    object result=null;Exception failure=null;
    try{result=Type("PdmAddin").GetMethod("OpenOrActivateDocumentOnSolidWorksThread",Flags).Invoke(plugin,new object[]{targetPath,1,"",true});}
    catch(TargetInvocationException error){failure=error.InnerException;}
    var succeeds=mode=="load-already-active"||mode=="loaded-unique"||mode=="active-unique"||mode=="rebuild-warning";
    if(mode=="dirty-target")
    {if(!(failure is InvalidOperationException)||targetPermissionWrites!=0||activations!=0)throw new Exception("Dirty target was modified or activated");}
    else if(failure!=null||succeeds&&!ReferenceEquals(result,target)||!succeeds&&result!=null)throw new Exception("Wrong open/activation result",failure);
    if(wrongPermissionWrites!=0||wrongReadOnly)throw new Exception("Wrong same-name working document was made read-only");
    if((mode=="load-already-active"||mode=="load-returned-wrong"||mode=="active-unique"||mode=="known-name-conflict")&&activations!=0)throw new Exception("Unsafe or redundant basename activation");
    if(mode=="known-name-conflict"&&!ReferenceEquals(active,root))throw new Exception("Conflicting window activation changed the current root");
    if((mode=="loaded-unique"||mode=="active-unique")&&opens!=0)throw new Exception("Existing target was unnecessarily reopened");
    if(!succeeds&&mode!="dirty-target")
    {
     var last=(string)Type("PluginDiagnosticLog").GetField("lastMessage",Flags).GetValue(null);
     if(last?.Contains("错误码：0")==true)throw new Exception("Zero status was displayed as the failure reason");
    }
    Console.WriteLine("PASS: exact document activation "+mode+"; no CAD save/rebuild/close/rename");
   }
   catch(Exception error){failures.Add(mode+": "+error.Message);Console.WriteLine("FAIL CASE: exact document activation "+mode+": "+error.Message);}
  }
  if(failures.Count>0)throw new Exception(string.Join("; ",failures));
 }
}
