using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static Dictionary<Guid,object[]> archiveRuleVersions;
 static int archiveRuleDelay,archiveRuleActive,archiveRuleMax;
 static async Task<IReadOnlyList<TVersion>> ArchiveRuleVersionLoader<TNode,TVersion>(TNode node,CancellationToken token)
 {
  var active=Interlocked.Increment(ref archiveRuleActive);archiveRuleMax=Math.Max(archiveRuleMax,active);
  try
  {
   if(archiveRuleDelay>0)await Task.Delay(archiveRuleDelay,token);
   var id=(Guid)Get(node,"DocumentId");
   return archiveRuleVersions.TryGetValue(id,out var versions)?versions?.Cast<TVersion>().ToArray():null;
  }
  finally{Interlocked.Decrement(ref archiveRuleActive);}
 }

 static async Task ArchiveSelectionRulesScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  var nodeType=Type("CadTreeNode");var itemType=Type("BatchOperationItem");var dialogType=Type("BatchOperationDialog");
  object Node(string name,string kind="Part")
  {
   var path=Path.Combine(folder,name);File.WriteAllText(path,"archived:"+name);
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"DisplayName",name);
   Set(node,"DocumentId",Guid.NewGuid());Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"Configuration","Default");Set(node,"Quantity",1);
   Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"LatestStructureIndexPending",false);Set(node,"NativeFileScopeVerified",true);
   Set(node,"LatestStoredSha256",WorkingReuseHash(path));Set(node,"LatestVersionSha256",WorkingReuseHash(path));Set(node,"CurrentRevision","W1");Set(node,"LatestRevision","W1");return node;
  }
  object Snapshot(object node)
  {
   var result=New("DocumentReferenceNodeDto");
   foreach(var key in new[]{"DocumentId","FileName","DisplayName","Configuration","Quantity"})Set(result,key,Get(node,key));
   Set(result,"Kind",Convert.ToInt32(Get(node,"Kind")));Set(result,"Status",Convert.ToInt32(Get(node,"Status")));
   var children=(IList)Activator.CreateInstance(Property(result,"Children").PropertyType);
   foreach(var child in ((IList)Get(node,"Children")).Cast<object>().Where(child=>Convert.ToString(Get(child,"Kind"))!="Drawing"))children.Add(Snapshot(child));
   Set(result,"Children",children);return result;
  }
  object Version(object node)
  {
   var version=New("DocumentVersionDto");Set(version,"Id",Guid.NewGuid());Set(version,"Sha256",WorkingReuseHash((string)Get(node,"FullPath")));
   Set(version,"FileLength",new FileInfo((string)Get(node,"FullPath")).Length);Set(version,"ReferenceSnapshot",Snapshot(node));
   var revision=New("RevisionDto");Set(revision,"Display","W1");Set(version,"Revision",revision);Set(node,"OpenedVersionId",Get(version,"Id"));return version;
  }
  var root=Node("ROOT.SLDASM","Assembly");var nodes=new List<object>{root};
  for(var index=1;index<214;index++){var node=Node("PART-"+index+".SLDPRT");nodes.Add(node);((IList)Get(root,"Children")).Add(node);}
  archiveRuleVersions=nodes.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>new[]{Version(node)});
  File.AppendAllText((string)Get(nodes[1],"FullPath"),"|one actual change");
  var items=Array.CreateInstance(itemType,nodes.Count);
  for(var i=0;i<nodes.Count;i++)
  {
   var item=New("BatchOperationItem",nodes[i],i==0?0:1);
   if(i>0){var parents=Array.CreateInstance(nodeType,1);parents.SetValue(root,0);itemType.GetMethod("AddAncestors",Flags).Invoke(item,new object[]{parents});}
   items.SetValue(item,i);
  }
  var projects=Array.CreateInstance(Type("ProjectDto"),0);
  Form Dialog(IReadOnlyCollection<string> initial=null)
  {
   var dialog=(Form)New("BatchOperationDialog",root,items,projects,null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),initial,null,null,false,false,0);
   dialog.TopMost=false;dialog.ShowInTaskbar=false;dialog.Opacity=0;
   var loader=dialogType.GetProperty("LoadArchiveVersionsAsync",Flags);
   if(loader!=null)
   {
    var method=typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(nodeType,Type("DocumentVersionDto"));
    loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,method));
   }
   return dialog;
  }
  object[] Selected(Form dialog)=>((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
  void Check(bool condition,string description){if(!condition)throw new Exception(description);Console.WriteLine("PASS: "+description);}
  async Task Analyze(Form dialog,bool select=true)
  {
   var method=dialogType.GetMethod("AnalyzeArchiveSelectionAsync",Flags);
   if(method==null){dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{true});Check(Selected(dialog).Length==1,"214-file unchanged records must not remain selectable");return;}
   await (Task)method.Invoke(dialog,new object[]{select});
  }
  string State(Form dialog,object node)=>(string)dialogType.GetMethod("StateText",Flags).Invoke(dialog,new[]{node,(object)"tester"});
  bool Allowed(Form dialog,object node)=>(bool)dialogType.GetMethod("CanSelectNode",Flags).Invoke(dialog,new[]{node});
  using(var dialog=Dialog())
  {
   archiveRuleDelay=1;archiveRuleMax=0;
   await Analyze(dialog);
   Check(Selected(dialog).Length==1&&ReferenceEquals(Selected(dialog)[0],nodes[1]),"214-file analysis selects only one real change; no unchanged ancestor or sibling");
   Check(!Allowed(dialog,root)&&State(dialog,root).Contains("未变更，无需提交"),"unchanged parent is disabled despite its changed child");
   dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{true});
   Check(Selected(dialog).Length==1,"select-all excludes all 213 unchanged documents");
   var tree=(TreeView)Field(dialog,"files");
   Check(tree.Nodes[0].StateImageIndex==0,"unchanged parent checkbox does not inherit its child's checked state");
   dialogType.GetMethod("ToggleNode",Flags).Invoke(dialog,new object[]{tree.Nodes[0]});
   Check(Selected(dialog).Length==1,"clicking an unchanged parent does not cascade into children");
   Check(archiveRuleMax>1&&archiveRuleMax<=8,"archive metadata analysis is bounded to eight requests and stays asynchronous");
   dialog.TopLevel=false;host.ClientSize=dialog.ClientSize;host.Controls.Add(dialog);dialog.Show();
   while((bool)Field(dialog,"archiveAnalysisInProgress"))await Task.Delay(5);
   using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"archive-selection-rules.png"));}
   Check(((Label)Field(dialog,"selectionSummary")).Text.Contains("存档1个，仅处理权限0个"),"summary separates submission from permission handling");

   Set(nodes[2],"CheckedOutBy","tester");await Analyze(dialog);
   Check(!Selected(dialog).Contains(nodes[2])&&Allowed(dialog,nodes[2])&&State(dialog,nodes[2]).Contains("仅释放权限"),"unchanged own checkout is optional permission-only work and is not automatically selected");
   dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{true});
   Check(Selected(dialog).Length==2&&((Label)Field(dialog,"selectionSummary")).Text.Contains("存档1个，仅处理权限1个"),"select-all counts unchanged checkout separately without declaring it modified");
   var disposition=(ComboBox)Field(dialog,"editDisposition");disposition.SelectedIndex=1;
   Check(!Allowed(dialog,nodes[2])&&!Selected(dialog).Contains(nodes[2]),"continue-editing does not submit or release an unchanged own checkout");
   disposition.SelectedIndex=2;
   Check(Allowed(dialog,nodes[2])&&State(dialog,nodes[2]).Contains("仅交接权限"),"handoff marks unchanged documents as permission-only without a version claim");
   Set(nodes[2],"CheckedOutBy",null);disposition.SelectedIndex=0;
   Set(nodes[3],"IsModifiedInSolidWorks",true);await Analyze(dialog);
   Check(!Allowed(dialog,nodes[3])&&!Selected(dialog).Contains(nodes[3])&&State(dialog,nodes[3]).Contains("待核对"),"unsaved native flags remain pending instead of becoming confirmed changes");
   Set(nodes[3],"IsModifiedInSolidWorks",false);
   var known=archiveRuleVersions[(Guid)Get(nodes[4],"DocumentId")];archiveRuleVersions[(Guid)Get(nodes[4],"DocumentId")]=null;await Analyze(dialog);
   Check(!Allowed(dialog,nodes[4])&&State(dialog,nodes[4]).Contains("待核对"),"missing server evidence remains pending and cannot be selected");
   archiveRuleVersions[(Guid)Get(nodes[4],"DocumentId")]=known;
   var noVersions=archiveRuleVersions[(Guid)Get(nodes[5],"DocumentId")];archiveRuleVersions[(Guid)Get(nodes[5],"DocumentId")]=new object[0];await Analyze(dialog);
   Check(Allowed(dialog,nodes[5])&&Selected(dialog).Contains(nodes[5])&&State(dialog,nodes[5]).Contains("首次存档"),"registered documents with no stored version remain valid first archives");
   archiveRuleVersions[(Guid)Get(nodes[5],"DocumentId")]=noVersions;
   Set(nodes[1],"CheckedOutBy","another-user");await Analyze(dialog);
   Check(!Allowed(dialog,nodes[1])&&State(dialog,nodes[1]).Contains("不能提交"),"another engineer's checkout stays disabled");Set(nodes[1],"CheckedOutBy",null);
   Set(nodes[1],"DrawingReviewLocked",true);await Analyze(dialog);
   Check(!Allowed(dialog,nodes[1]),"review-locked changed documents stay disabled");Set(nodes[1],"DrawingReviewLocked",false);
   archiveRuleDelay=20;
   var pending=(Task)dialogType.GetMethod("AnalyzeArchiveSelectionAsync",Flags).Invoke(dialog,new object[]{false});
   dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{false});await pending;
   Check(Selected(dialog).Length==0,"background analysis does not undo a user's clear selection");archiveRuleDelay=1;
   archiveRuleDelay=20;
   var previous=(Task)dialogType.GetMethod("AnalyzeArchiveSelectionAsync",Flags).Invoke(dialog,new object[]{true});
   ((RadioButton)Field(dialog,"acquire")).Checked=true;
   Check(((Button)Field(dialog,"execute")).Enabled,"switching to acquire cancels archive analysis without blocking the operation");
   ((RadioButton)Field(dialog,"checkIn")).Checked=true;
   await previous;
   while((bool)Field(dialog,"archiveAnalysisInProgress"))await Task.Delay(5);
   Check(Selected(dialog).Length==1&&Selected(dialog).Contains(nodes[1]),"a cancelled analysis cannot overwrite the next operation's selection");archiveRuleDelay=1;
  }
  File.AppendAllText((string)Get(nodes[6],"FullPath"),"|second change outside explicit scope");
  using(var dialog=Dialog(new[]{(string)Get(nodes[1],"FullPath")}))
  {
   await Analyze(dialog,false);
   Check(Selected(dialog).Length==1&&Selected(dialog).Contains(nodes[1]),"explicit single-document scope is not expanded to another changed file");
  }
  var analyzeNode=Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags);
  string Classify(object node,object version)
  {
   var versions=Array.CreateInstance(Type("DocumentVersionDto"),1);versions.SetValue(version,0);
   return Convert.ToString(analyzeNode.Invoke(null,new[]{node,"tester",(object)versions,WorkingReuseHash((string)Get(node,"FullPath"))}));
  }
  Set(nodes[7],"CurrentRevision","W99");
  Check(Classify(root,archiveRuleVersions[(Guid)Get(root,"DocumentId")][0])=="Unchanged","child version changes alone do not mark the parent assembly changed");
  Set(nodes[7],"Quantity",2);
  Check(Classify(root,archiveRuleVersions[(Guid)Get(root,"DocumentId")][0])=="Changed","actual component quantity change makes the assembly selectable");Set(nodes[7],"Quantity",1);
  var first=Node("FIRST.SLDPRT");Set(first,"DocumentId",null);
  Check(Classify(first,archiveRuleVersions[(Guid)Get(nodes[8],"DocumentId")][0])=="New","unregistered local files remain first-archive candidates");
  var drawing=Node("DRAWING.SLDDRW","Drawing");Set(drawing,"DrawingViewsRead",true);var drawingVersion=Version(drawing);
  Set(drawing,"RequiresModelUpdate",true);
  Check(Classify(drawing,drawingVersion)=="DrawingUpdate","verified paired drawing has an explicit update-before-archive state");
  Set(drawing,"DrawingUpdatePendingReason","关联模型尚未更新");
  Check(Classify(drawing,drawingVersion)=="DrawingUpdate","known paired drawing update remains actionable and is not mislabeled as unknown change");
  ((IList)Get(drawing,"ReferencedModels")).Add(nodes[1]);((IList)Get(nodes[1],"Children")).Add(drawing);
  archiveRuleVersions[(Guid)Get(drawing,"DocumentId")]=new[]{drawingVersion};
  var unrelated=Node("UNRELATED.SLDDRW","Drawing");Set(unrelated,"DrawingViewsRead",true);Set(unrelated,"RequiresModelUpdate",true);
  ((IList)Get(unrelated,"ReferencedModels")).Add(nodes[6]);((IList)Get(root,"Children")).Add(unrelated);
  archiveRuleVersions[(Guid)Get(unrelated,"DocumentId")]=new[]{Version(unrelated)};
  var expanded=Array.CreateInstance(itemType,items.Length+2);Array.Copy(items,expanded,items.Length);
  expanded.SetValue(New("BatchOperationItem",drawing,2),items.Length);expanded.SetValue(New("BatchOperationItem",unrelated,1),items.Length+1);items=expanded;
  using(var dialog=Dialog(new[]{(string)Get(nodes[1],"FullPath")}))
  {
   await Analyze(dialog,false);
   Check(Selected(dialog).Length==2&&Selected(dialog).Contains(nodes[1])&&Selected(dialog).Contains(drawing),"explicit model archive includes its paired update drawing and excludes other changed models and drawings");
   Check(Allowed(dialog,drawing)&&State(dialog,drawing).Contains("存档时更新")&&((Label)Field(dialog,"selectionSummary")).Text.Contains("工程图待更新1个"),"paired drawing update has an enabled checkbox and a clear Chinese status and count");
   dialog.TopLevel=false;host.ClientSize=dialog.ClientSize;host.Controls.Add(dialog);dialog.Show();
   while((bool)Field(dialog,"archiveAnalysisInProgress"))await Task.Delay(5);
   dialogType.GetMethod("ToggleShowCheckedOnly",Flags).Invoke(dialog,null);((TreeView)Field(dialog,"files")).ExpandAll();
   using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"paired-drawing-selection.png"));}
   dialogType.GetMethod("SetAllChecked",Flags).Invoke(dialog,new object[]{false});await Analyze(dialog,false);
   Check(Selected(dialog).Length==0,"paired drawing analysis still respects a user's clear selection");
  }
  var closing=Dialog();archiveRuleDelay=50;
  var closingTask=(Task)dialogType.GetMethod("AnalyzeArchiveSelectionAsync",Flags).Invoke(closing,new object[]{true});closing.Dispose();await closingTask;
  Check(!((Button)Field(closing,"execute")).Enabled,"closing an analyzing dialog cancels safely without restoring selections or enabling execution");
 }
}
