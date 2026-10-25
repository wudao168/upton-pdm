using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task PendingIndexCompletionScenario()
 {
  foreach(var mode in new[]{"part","pure-drawing","assembly-root","assembly-child","suppressed-restored"})
  foreach(var state in new[]{"completed-now","still-pending","already-complete"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-index-completion-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var kind=mode=="part"?"Part":mode=="pure-drawing"?"Drawing":"Assembly";
   var node=LinkedNode(directory,"ROOT"+(kind=="Part"?".SLDPRT":kind=="Drawing"?".SLDDRW":".SLDASM"),kind);
   if(kind=="Drawing")Set(node,"DrawingViewsRead",true);
   Set(node,"Revision","A");Set(node,"WorkState",Enum.Parse(Type("CadWorkState"),"PendingCheckIn"));
   object affected=node;
   if(kind=="Assembly")
   {
    var child=LinkedNode(directory,"CHILD.SLDPRT","Part");((IList)Get(node,"Children")).Add(child);
    if(mode=="assembly-child"||mode=="suppressed-restored")affected=child;
    if(mode=="suppressed-restored")Set(child,"Status",Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));
   }
   var snapshot=LinkedSnapshot(node);var affectedSnapshot=affected==node?snapshot:((IList)Get(snapshot,"Children"))[0];
   if(mode=="suppressed-restored")Set(affectedSnapshot,"Status",(int)Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));
   Set(affectedSnapshot,"StructureIndexPending",state!="already-complete");Set(affected,"StructureIndexPending",state=="still-pending");
   var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",node);
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new object[0]:call.MethodName=="get_ActiveDoc"?null:throw new Exception("Unexpected native mutation: "+call.MethodName)).GetTransparentProxy());
   var version=LinkedVersion(node,snapshot);
   using(var handler=new PendingIndexHandler(version))
   using(var client=(IDisposable)New("PdmApiClient","http://index-fixture.invalid/"))
   {
    LinkedClient(plugin,client,handler);
    var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(node),null,CancellationToken.None});await task;
    var plan=Get(task,"Result");var count=((IEnumerable)Get(plan,"Items")).Cast<object>().Count();
    if(count!=(state=="completed-now"?1:0))throw new Exception("Index completion was skipped or unchanged index repeated a version: "+mode+"/"+state);
    if((bool)Get(node,"LatestStructureIndexPending")!=(state!="already-complete")||(bool)Get(affected,"StructureIndexPending")!=(state=="still-pending"))throw new Exception("Server index state overwrote local scan state");
    // Existing bytes can be reused, but a completed index must not take the
    // unchanged-reference shortcut before the new frozen snapshot is prepared.
    if(state=="completed-now"&&kind=="Part")
    {
     var cache=(IDictionary)Field(plugin,"documentVersionsCache");
     var versions=Array.CreateInstance(Type("DocumentVersionDto"),1);versions.SetValue(version,0);
     var entry=Activator.CreateInstance(Type("PdmAddin").GetNestedType("DocumentVersionsCacheEntry",Flags),Flags,null,new object[]{versions,DateTime.UtcNow.AddMinutes(5)},null);
     cache.Add(Get(node,"DocumentId"),entry);Set(node,"WorkState",Enum.Parse(Type("CadWorkState"),"None"));
     task=(Task)Type("PdmAddin").GetMethod("TryBuildUnchangedArchivePlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(node),null,CancellationToken.None});await task;
     if(Get(task,"Result")!=null)throw new Exception("Fast no-change path prevented pending-index recovery");
    }
    {
     var preflight=((IDictionary)Get(plan,"Preflights"))[(string)Get(node,"FullPath")];
     task=(Task)Type("PdmAddin").GetMethod("PrepareBatchCheckInFileAsync",Flags).Invoke(plugin,new object[]{node,CancellationToken.None,null,preflight,null,null});await task;
     var prepared=Get(task,"Result");
     if((Get(prepared,"StoredFile")==null)!=(state=="completed-now"))throw new Exception("Upload preparation discarded the completed index: "+mode+"/"+state);
     Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new[]{Get(prepared,"UploadCopyPath")});
    }
    if(state=="completed-now")
    {
     Set(affectedSnapshot,"StructureIndexPending",false);
     Type("PdmAddin").GetMethod("ApplyLatestVersion",Flags).Invoke(null,new[]{node,version});
     task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(node),null,CancellationToken.None});await task;
     if(((IEnumerable)Get(Get(task,"Result"),"Items")).Cast<object>().Any())throw new Exception("Completed index generated another no-change submission: "+mode);
    }
   }
   Console.WriteLine("PASS: index completion plan and no-change retry "+mode+"/"+state);
  }
  foreach(var known in new[]{false,true})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-index-read-count-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var node=LinkedNode(directory,"PART.SLDPRT","Part");var version=LinkedVersion(node,LinkedSnapshot(node));
   Set(node,"LatestStoredSha256",Get(version,"Sha256"));Set(node,"LatestVersionSha256",Get(version,"Sha256"));
   if(known)Set(node,"LatestStructureIndexPending",false);
   var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");
   using(var handler=new PendingIndexHandler(version))
   using(var client=(IDisposable)New("PdmApiClient","http://index-fixture.invalid/"))
   {
    LinkedClient(plugin,client,handler);
    var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(node),null,CancellationToken.None});await task;
    if(((IEnumerable)Get(Get(task,"Result"),"Items")).Cast<object>().Any()||handler.Reads!=(known?0:1))throw new Exception("Normal unchanged part added requests or was resubmitted");
    if(!known&&!handler.LastQuery.Contains("take=1"))throw new Exception("Unknown index metadata unnecessarily fetched full history");
   }
   Console.WriteLine("PASS: normal cached part adds no requests; unknown index reads latest only known="+known);
  }
  ReusedPendingIndexScenario();
 }
 static void ReusedPendingIndexScenario()
 {
  foreach(var scope in new[]{"root","child","nested","complete"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-reused-index-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var node=LinkedNode(directory,"SUB.SLDASM","Assembly");Set(node,"Revision","A");
   var child=LinkedNode(directory,"CHILD.SLDASM","Assembly");
   var nested=LinkedNode(directory,"NESTED.SLDPRT","Part");
   ((IList)Get(child,"Children")).Add(nested);((IList)Get(node,"Children")).Add(child);
   var snapshot=LinkedSnapshot(node);var archivedChild=((IList)Get(snapshot,"Children"))[0];
   if(scope!="complete")Set(scope=="root"?snapshot:scope=="child"?archivedChild:((IList)Get(archivedChild,"Children"))[0],"StructureIndexPending",true);
   var version=LinkedVersion(node,snapshot);
   ((IList)Get(node,"Children")).Clear();Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));
   Set(node,"SuppressedArchiveVersion",version);Set(node,"SuppressedArchiveSha256",Get(version,"Sha256"));
   if(!(bool)Type("SuppressedAssemblyArchive").GetMethod("CanReuse",Flags).Invoke(null,new[]{node}))throw new Exception("Fixture did not reuse the archived suppressed assembly");
   var completed=Type("PdmAddin").GetMethod("ArchiveIndexWasCompleted",Flags);
   if((bool)completed.Invoke(null,new[]{snapshot,node}))throw new Exception("Reusing an unread pending index was mistaken for completion: "+scope);
   var request=Type("PdmApiClient").GetMethod("ToRequestNode",Flags).Invoke(null,new object[]{node,true,null});
   bool Pending(object value)=> (bool)Get(value,"structureIndexPending")||((IEnumerable)Get(value,"children")).Cast<object>().Any(Pending);
   if(Pending(request)!=(scope!="complete"))throw new Exception("Reused pending state was lost from the archived snapshot: "+scope);
   // A parent also needs to retain pending state while its suppressed child is reused.
   var parent=LinkedNode(directory,"ROOT.SLDASM","Assembly");Set(parent,"Revision","A");((IList)Get(parent,"Children")).Add(node);
   var parentSnapshot=LinkedSnapshot(parent,false);((IList)Get(parentSnapshot,"Children")).Add(snapshot);
   if((bool)completed.Invoke(null,new[]{parentSnapshot,parent}))throw new Exception("Parent mistook reused child index for completion: "+scope);
   request=Type("PdmApiClient").GetMethod("ToRequestNode",Flags).Invoke(null,new object[]{parent,true,null});
   if(Pending(request)!=(scope!="complete"))throw new Exception("Parent submission lost reused child pending state: "+scope);
   // A genuine fresh expansion can complete it; the old reusable snapshot must not
   // make pending sticky after the current hierarchy has actually been read.
   ((IList)Get(node,"Children")).Add(child);
   if((bool)completed.Invoke(null,new[]{snapshot,node})!=(scope!="complete"))throw new Exception("Freshly expanded suppressed index could not complete: "+scope);
   request=Type("PdmApiClient").GetMethod("ToRequestNode",Flags).Invoke(null,new object[]{parent,true,null});
   if(Pending(request))throw new Exception("Fresh current hierarchy inherited stale pending state: "+scope);
   Console.WriteLine("PASS: reused suppressed snapshot retains pending until actual expansion "+scope);
  }
  foreach(var mode in new[]{"virtual-child","drawing-model"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-embedded-index-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var root=LinkedNode(directory,mode=="virtual-child"?"ROOT.SLDASM":"ROOT.SLDDRW",mode=="virtual-child"?"Assembly":"Drawing");
   if(mode=="drawing-model")Set(root,"DrawingViewsRead",true);
   var child=LinkedNode(directory,"CHILD.SLDPRT","Part");
   if(mode=="virtual-child")Set(child,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
   ((IList)Get(root,mode=="virtual-child"?"Children":"ReferencedModels")).Add(child);
   var snapshot=LinkedSnapshot(root);Set(snapshot,"StructureIndexPending",true);
   var completed=Type("PdmAddin").GetMethod("ArchiveIndexWasCompleted",Flags);
   Set(child,"StructureIndexPending",true);
   if((bool)completed.Invoke(null,new[]{snapshot,root}))throw new Exception("Incomplete embedded/view model was ignored: "+mode);
   Set(child,"StructureIndexPending",false);
   if(!(bool)completed.Invoke(null,new[]{snapshot,root}))throw new Exception("Completed embedded/view model remained pending: "+mode);
   Console.WriteLine("PASS: pending completion includes embedded virtual hierarchy and drawing views "+mode);
  }
 }
 sealed class PendingIndexHandler:HttpMessageHandler
 {
  readonly object version;public int Reads;public string LastQuery;
  public PendingIndexHandler(object version){this.version=version;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(request.Method!=HttpMethod.Get||!request.RequestUri.AbsolutePath.EndsWith("/versions"))throw new Exception("Unexpected index fixture request: "+request);
   Reads++;LastQuery=request.RequestUri.Query;
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(new[]{version}))});
  }
 }
}
