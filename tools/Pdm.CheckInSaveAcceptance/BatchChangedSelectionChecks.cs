using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static void BatchChangedSelectionScenario(string evidenceDirectory)
 {
  var local=BatchSelectionRead(Path.Combine(evidenceDirectory,"binding-hash-audit.json"));
  var server=BatchSelectionRead(Path.Combine(evidenceDirectory,"server-local-current-fingerprint-audit.json"));
  var cad=BatchSelectionRead(Path.Combine(evidenceDirectory,"current-cad-evidence.json"));
  var dependencies=BatchSelectionRead(Path.Combine(evidenceDirectory,"eight-dependency-content-audit-v2.json"));
  var localRows=BatchSelectionRows(local,"Rows").ToDictionary(row=>(string)row["FileName"],StringComparer.OrdinalIgnoreCase);
  var serverRows=BatchSelectionRows(server,"rows").ToArray();
  var dirtyPaths=new HashSet<string>(BatchSelectionRows(cad,"DocumentsBefore").Where(row=>(bool)row["Dirty"])
   .Select(row=>(string)row["Path"]),StringComparer.OrdinalIgnoreCase);
  var dialogType=Type("BatchOperationDialog");
  var analyze=Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags);
  bool HasChange(object node)=>Convert.ToString(analyze.Invoke(null,new[]{node,"tester",null,BatchSelectionHash((string)Get(node,"FullPath"))}))=="Changed";
  bool HasUnconfirmed(object node)=>(bool)Get(node,"IsModifiedInSolidWorks")
   && (bool)Type("PdmAddin").GetMethod("NodeMatchesLatestFile",Flags).Invoke(null,new[]{node,BatchSelectionHash((string)Get(node,"FullPath"))});
  var historicalOwner=dialogType.GetMethod("ExistingArchivedReferenceOwner",Flags);
  var requiresRegistration=dialogType.GetMethod("RequiresNewDocumentProjectConfirmation",Flags);
  var nodes=new List<object>();var changed=0;var relocated=0;var selected=0;var unsavedCount=0;
  foreach(var row in serverRows)
  {
   var baseline=localRows[(string)row["fileName"]];var path=(string)baseline["FullPath"];
   if(!(bool)row["versionMatches"]||BatchSelectionHash(path)!=(string)row["localActualSha256"])
    throw new Exception("Read-only evidence changed; do not claim a replay: "+path);
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));
   Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),path.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?"Assembly":path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?"Drawing":"Part"));
   Set(node,"DocumentId",Guid.Parse((string)row["documentId"]));Set(node,"OpenedVersionId",Guid.Parse((string)row["currentServerVersionId"]));
   Set(node,"LatestStoredSha256",(string)row["serverStoredSha256"]);Set(node,"LatestVersionSha256",(string)row["effectiveLatestVersionSha256"]);
   Set(node,"CurrentRevision",(string)row["currentRevision"]);Set(node,"LatestRevision",(string)row["currentRevision"]);
   var actual=HasChange(node);
   var expected=!(bool)row["matchesServerStored"]&&!(bool)row["matchesServerSource"]&&!(bool)row["matchesResolved"];
   if(actual!=expected)throw new Exception("Stored/source/verified relocated baseline disagree in selection: "+path);
   if(actual)changed++;
   if((bool)row["matchesResolved"]&&!(bool)row["matchesServerStored"]&&!(bool)row["matchesServerSource"])relocated++;
   Set(node,"IsModifiedInSolidWorks",dirtyPaths.Contains(path));
   if(HasChange(node))selected++;
   if(HasUnconfirmed(node))unsavedCount++;
   nodes.Add(node);
  }
  if(nodes.Count!=214||changed!=1||relocated!=52||selected!=1||unsavedCount!=2)
   throw new Exception("Actual evidence totals differ: files="+nodes.Count+" changed="+changed+" relocated="+relocated+" selected="+selected+" unsaved="+unsavedCount);
  Console.WriteLine("PASS: actual 214-file replay keeps one disk change, rejects 52 relocated false changes, retains two unsaved candidates separately");

  var relocatedNode=nodes.First(node=>Path.GetExtension((string)Get(node,"FullPath")).Equals(".SLDDRW",StringComparison.OrdinalIgnoreCase));
  var originalId=Get(relocatedNode,"DocumentId");Set(relocatedNode,"DocumentId",Guid.NewGuid());
  if(!HasChange(relocatedNode))throw new Exception("Relocated baseline accepted another document identity");
  Set(relocatedNode,"DocumentId",originalId);
  var originalStored=Get(relocatedNode,"LatestStoredSha256");Set(relocatedNode,"LatestStoredSha256",new string('F',64));
  if(!HasChange(relocatedNode))throw new Exception("Relocated baseline accepted a different current archive hash");
  Set(relocatedNode,"LatestStoredSha256",originalStored);
  var unchangedPart=nodes.First(node=>Path.GetExtension((string)Get(node,"FullPath")).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase)
   && !HasChange(node));
  Set(unchangedPart,"CheckedOutBy","tester");Set(unchangedPart,"IsModifiedInSolidWorks",true);
  if(HasChange(unchangedPart)||!HasUnconfirmed(unchangedPart))
   throw new Exception("Unsaved editable content must remain pending until saved and verified");
  Set(unchangedPart,"IsModifiedInSolidWorks",false);Set(unchangedPart,"CheckedOutBy",null);
  Console.WriteLine("PASS: wrong document or archive hash cannot reuse relocated identity; unsaved content stays pending without automatic submission");

  var owner=nodes.Single(node=>(string)Get(node,"FileName")=="SS5Y-8位电磁阀座.SLDASM");
  var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"DocumentId",Get(owner,"DocumentId"));
  var version=New("DocumentVersionDto");Set(version,"ReferenceSnapshot",snapshot);
  Set(owner,"SuppressedArchiveVersion",version);
  var ownerHash=BatchSelectionHash((string)Get(owner,"FullPath"));
  Set(owner,"SuppressedArchiveSha256",ownerHash);Set(owner,"SuppressedLocalStructureSha256",ownerHash);
  var rows=BatchSelectionRows(dependencies,"rows").ToArray();var pendingItems=new List<object>();
  foreach(var row in rows)
  {
   var node=New("CadTreeNode");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   Set(node,"FullPath",(string)row["path"]);Set(node,"FileName",(string)row["fileName"]);
   var item=New("BatchOperationItem",node,1);var ancestors=Array.CreateInstance(Type("CadTreeNode"),1);ancestors.SetValue(owner,0);
   Type("BatchOperationItem").GetMethod("AddAncestors",Flags).Invoke(item,new object[]{ancestors});
   if(!ReferenceEquals(historicalOwner.Invoke(null,new[]{item}),owner)
    ||(bool)requiresRegistration.Invoke(null,new object[]{item,Guid.Parse("945f4d78-183a-4a71-85d2-7cfc9dbd2bcc"),null}))
    throw new Exception("An old valve dependency was silently treated as a new document: "+row["fileName"]);
   ((IList)Get(owner,"Children")).Add(node);pendingItems.Add(item);
  }
  if(pendingItems.Count!=8)throw new Exception("Expected the eight actual valve dependencies");
  var sample=pendingItems[0];var child=Get(sample,"Node");
  Set(owner,"IsModifiedInSolidWorks",true);
  if(historicalOwner.Invoke(null,new[]{sample})!=null)throw new Exception("Dirty parent falsely proves an old embedded reference");
  Set(owner,"IsModifiedInSolidWorks",false);Set(owner,"SuppressedLocalStructureSha256","different");
  if(historicalOwner.Invoke(null,new[]{sample})!=null)throw new Exception("Changed parent bytes falsely prove an old reference");
  Set(owner,"SuppressedLocalStructureSha256",ownerHash);Set(child,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
  if(historicalOwner.Invoke(null,new[]{sample})!=null)throw new Exception("New peer drawing incorrectly classified as an old assembly member");
  Set(child,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(child,"DocumentId",Guid.NewGuid());
  if(historicalOwner.Invoke(null,new[]{sample})!=null)throw new Exception("Registered reference must retain its own identity");
  Set(child,"DocumentId",null);Set(snapshot,"DocumentId",Guid.NewGuid());
  if(historicalOwner.Invoke(null,new[]{sample})!=null)throw new Exception("Different archived parent identity accepted");
  Set(snapshot,"DocumentId",Get(owner,"DocumentId"));
  Console.WriteLine("PASS: all eight actual old dependencies are separate from new registration; dirty parent, wrong parent identity, changed bytes and new drawings cannot borrow that classification");

  var items=Array.CreateInstance(Type("BatchOperationItem"),pendingItems.Count+1);
  items.SetValue(New("BatchOperationItem",owner,0),0);for(var i=0;i<pendingItems.Count;i++)items.SetValue(pendingItems[i],i+1);
  var projects=Array.CreateInstance(Type("ProjectDto"),0);
  using(var dialog=(Form)New("BatchOperationDialog",owner,items,projects,null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
  {
   if(((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Any(item=>pendingItems.Contains(item)))throw new Exception("Old references automatically checked on entry");
   dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{true});
   if(((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Any(item=>pendingItems.Contains(item)))throw new Exception("Select-all silently admits unconfirmed old references");
   var summary=(string)dialogType.GetMethod("SelectionCountText",Flags).Invoke(dialog,null);
   if(!summary.Contains("8 项历史已有引用待补全"))throw new Exception("Historical gap is hidden from the selection summary");
   var tree=(TreeView)Field(dialog,"files");
   if(tree.Nodes.Count!=1||tree.Nodes[0].Nodes.Count!=8)throw new Exception("Historical references removed from the visible structure");
   Console.WriteLine("PASS: dialog preserves eight tree entries and explicit summary, without default/select-all admission");
  }
  var root=nodes.Single(node=>(string)Get(node,"FileName")=="CW0030002.10.SLDASM");
  foreach(var node in nodes.Where(node=>node!=root))((IList)Get(root,"Children")).Add(node);
  var allItems=Array.CreateInstance(Type("BatchOperationItem"),nodes.Count+pendingItems.Count);
  for(var i=0;i<nodes.Count;i++)allItems.SetValue(New("BatchOperationItem",nodes[i],i==0?0:1),i);
  for(var i=0;i<pendingItems.Count;i++)allItems.SetValue(pendingItems[i],nodes.Count+i);
  using(var dialog=(Form)New("BatchOperationDialog",root,allItems,projects,null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
  {
   var task=(Task)dialogType.GetMethod("SelectChangedItemsAsync",Flags).Invoke(dialog,new object[]{"tester"});
   var deadline=DateTime.UtcNow.AddSeconds(30);
   while(!task.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
   if(!task.IsCompleted)throw new Exception("Change selection did not finish in the hidden test dialog");
   task.GetAwaiter().GetResult();
   var summary=((Label)Field(dialog,"selectionSummary")).Text;
   if(!summary.Contains("存档1个，仅处理权限0个")||!summary.Contains("待核对")||!summary.Contains("8 项历史已有引用待补全")
    ||((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Count()!=1)
    throw new Exception("Actual selection summary misrepresents saved/unsaved/historical scope: "+summary);
   Console.WriteLine("PASS: actual hidden-dialog selection summary: "+summary);
  }
 }

 static Dictionary<string,object> BatchSelectionRead(string path)=>new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
 static IEnumerable<Dictionary<string,object>> BatchSelectionRows(Dictionary<string,object> data,string key)=>((IEnumerable)data[key]).Cast<Dictionary<string,object>>();
 static string BatchSelectionHash(string path)
 {
  using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
  using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
 }
}
