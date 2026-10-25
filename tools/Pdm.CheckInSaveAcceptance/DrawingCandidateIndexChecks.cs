using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task DrawingCandidateIndexScenario(string check)
 {
  foreach(var state in new[]{"candidate-pending","candidate-complete","views-completed","views-still-pending","views-complete-repeat","changed-pending","dirty-pending"})
  {
   var directory=Path.Combine(Path.GetTempPath(),"pdm-drawing-candidate-index-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   var node=LinkedNode(directory,"UNRELATED.SLDDRW","Drawing");Set(node,"Revision","W1");
   var viewsRead=state.StartsWith("views-",StringComparison.Ordinal);
   var candidate=!viewsRead;
   var stillPending=state=="views-still-pending";
   var serverPending=state!="candidate-complete"&&state!="views-complete-repeat";
   var changed=state=="changed-pending"||state=="dirty-pending";
   Set(node,"DrawingScopePaths",candidate?new HashSet<string>(StringComparer.OrdinalIgnoreCase):null);
   Set(node,"StructureIndexPending",stillPending);
   Set(node,"DrawingViewsRead",viewsRead);Set(node,"LatestStructureIndexPending",serverPending);
   var snapshot=LinkedSnapshot(node);Set(snapshot,"StructureIndexPending",serverPending);
   var version=LinkedVersion(node,snapshot);
   if(state=="changed-pending")File.AppendAllText((string)Get(node,"FullPath"),"actual drawing edit");
   if(state=="dirty-pending")
   {Set(node,"IsModifiedInSolidWorks",true);Set(node,"WorkState",Enum.Parse(Type("CadWorkState"),"ModifiedUnsaved"));}
   var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",node);
   if(check!="snapshot")
   using(var handler=new PendingIndexHandler(version))
   using(var client=(IDisposable)New("PdmApiClient","http://candidate-index-fixture.invalid/"))
   {
    LinkedClient(plugin,client,handler);
    var task=(Task)Type("PdmAddin").GetMethod("BuildBatchCheckInPlanAsync",Flags).Invoke(plugin,new object[]{LinkedItems(node),null,CancellationToken.None});await task;
    var count=((IEnumerable)Get(Get(task,"Result"),"Items")).Cast<object>().Count();
    if(count!=(state=="views-completed"||changed?1:0))throw new Exception("Unread drawing candidate was mistaken for a completed index, or actual change/completion was lost: "+state);
    Console.WriteLine("PASS: drawing candidate plan requires actual view read "+state);
   }
   if(check!="plan")
   {
    var expected=stillPending||candidate&&serverPending;
    var request=Type("PdmApiClient").GetMethod("ToRequestNode",Flags).Invoke(null,new object[]{node,true,null});
    if((bool)Get(request,"structureIndexPending")!=expected)throw new Exception("Drawing candidate pending state was lost from request: "+state);
    var captured=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{node,null});
    var frozen=(IDictionary)captured.GetType().GetMethod("CreateRoot",Flags).Invoke(captured,null);
    if((bool)frozen["structureIndexPending"]!=expected)throw new Exception("Drawing candidate pending state was lost from frozen snapshot: "+state);
    Console.WriteLine("PASS: drawing candidate request and frozen snapshot retain pending "+state);
   }
  }
 }
}
