using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;

internal static partial class Program
{
 static void HistoricalOwnerScenario()
 {
  var snapshot=Type("PdmAddin").GetNestedType("LoadedDocumentSnapshot",BindingFlags.NonPublic);
  foreach(var mode in new[]{"clean","dirty-owner","editable-owner","readonly-transient"})
  {
   var names=new[]{"PART.SLDPRT","SUB.SLDASM","ROOT.SLDASM","VIEW.SLDDRW","OTHER.SLDASM"};
   var all=Array.CreateInstance(snapshot,names.Length);
   for(var i=0;i<names.Length;i++)all.SetValue(Activator.CreateInstance(snapshot,Flags,null,new object[]{names[i],names[i],i==0?1:i==3?3:2,i==2&&(mode=="dirty-owner"||mode=="readonly-transient"),i==2&&mode=="readonly-transient"},null),i);
   var conflicts=Array.CreateInstance(snapshot,1);conflicts.SetValue(all.GetValue(0),0);
   var references=new Dictionary<string,string[]>{{names[1],new[]{names[0]}},{names[2],new[]{names[1]}},{names[3],new[]{names[2]}}};
   var readOnly=new HashSet<string>(names.Where(name=>mode!="editable-owner"||name!="ROOT.SLDASM"),StringComparer.OrdinalIgnoreCase);
   var rejected=false;
   try
   {
    var result=((IEnumerable)Type("PdmAddin").GetMethod("ExpandReadOnlyReferenceOwners",Flags).Invoke(null,new object[]{all,conflicts,references,readOnly})).Cast<object>().Select(item=>(string)Get(item,"Path")).ToArray();
    if(!result.SequenceEqual(new[]{"VIEW.SLDDRW","ROOT.SLDASM","SUB.SLDASM","PART.SLDPRT"}))throw new Exception("Owner windows must close before the referenced models; unrelated windows must stay");
   }
   catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
   if(rejected!=(mode=="dirty-owner"||mode=="editable-owner"))throw new Exception("Incorrect historical read-only owner policy: "+mode);
   Console.WriteLine("PASS: historical reference owner closure "+mode);
  }
 }

 static void BusyControlledOpenScenario()
 {
  var queuedPlugin=New("PdmAddin");var queuedProject=Guid.NewGuid();var queuedDocument=Guid.NewGuid();
  var queue=Type("PdmAddin").GetMethod("QueueControlledOpen",Flags);
  var queuedMode=Enum.Parse(Type("ControlledOpenMode"),"LatestReadOnly");
  var requestArgs=new object[]{queuedProject,queuedDocument,queuedMode,null,null,null};
  queue.Invoke(queuedPlugin,requestArgs);queue.Invoke(queuedPlugin,requestArgs);
  if(((ICollection)Field(queuedPlugin,"queuedControlledOpens")).Count!=1)throw new Exception("Repeated busy open was not coalesced");
  requestArgs[1]=Guid.NewGuid();queue.Invoke(queuedPlugin,requestArgs);
  if(((ICollection)Field(queuedPlugin,"queuedControlledOpens")).Count!=2)throw new Exception("Distinct queued open was dropped");
  Console.WriteLine("PASS: repeated busy requests coalesce while distinct PLM targets remain queued");
  foreach(var flag in new[]{"openOperationInProgress","workspaceOperationInProgress"})
  {
   var plugin=New("PdmAddin");using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))using(var listener=(IDisposable)New("SolidWorksOpenRequestListener"))
   {
    ((HttpClient)Field(client,"httpClient")).DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
    SetField(plugin,"apiClient",client);SetField(plugin,"controlledOpenListener",listener);SetField(plugin,flag,1);
    var request=New("SolidWorksOpenRequest");Set(request,"Mode","LatestReadOnly");SetField(plugin,"pendingControlledOpenRequest",request);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeCalls=0;
    SetField(plugin,"application",new Proxy(appType,call=>{nativeCalls++;throw new Exception("Busy request reentered native CAD");}).GetTransparentProxy());
    Type("PdmAddin").GetMethod("TryStartControlledOpenRequest",Flags).Invoke(plugin,null);
    if(nativeCalls!=0||Field(plugin,"pendingControlledOpenRequest")!=request)throw new Exception("Busy open request must stay queued without blocking the running operation");
    Console.WriteLine("PASS: controlled UI request waits for "+flag);
   }
  }
 }
}
