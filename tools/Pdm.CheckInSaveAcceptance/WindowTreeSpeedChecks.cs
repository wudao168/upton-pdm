using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static object WindowModel(string path,int count,string configuration="Default")
 {
  var root=New("CadTreeNode");Set(root,"FullPath",path);Set(root,"FileName",Path.GetFileName(path));Set(root,"InstancePath",path);Set(root,"Configuration",configuration);Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(root,"DocumentId",Guid.NewGuid());Set(root,"DisplayName","窗口树");
  var groups=new List<object>();
  for(var i=1;i<count;i++)
  {
   var node=New("CadTreeNode");var group=i<15;var name=group?"GROUP-"+i+".SLDASM":"PART-"+(i%200)+".SLDPRT";
   Set(node,"FullPath",Path.Combine(Path.GetDirectoryName(path),name));Set(node,"FileName",name);Set(node,"DisplayName","节点"+i);Set(node,"Configuration",i%2==0?"C1":"C2");Set(node,"InstancePath",path+"/"+i);Set(node,"ComponentSelectionName","component-"+i);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),group?"Assembly":"Part"));Set(node,"DocumentId",Guid.NewGuid());
   if(group){groups.Add(node);((IList)Get(root,"Children")).Add(node);}else ((IList)Get(groups[(i-15)%groups.Count],"Children")).Add(node);
  }
  return root;
 }
 static async Task WindowTreeSpeedScenario(Form host)
 {
  host.ClientSize=new Size(800,1000);
  await WindowCachedContextScenario(host);
  await WindowViewStateScenario(host);
  await WindowBaseMetadataScenario(host);
  WindowPreviewFlagsScenario();
 }
 static async Task WindowReady(object pane,object root)
 {
  await TreeWait(()=>ReferenceEquals(Field(pane,"rootNode"),root)&&(Field(pane,"activeTreeBuildPlan") is object plan)&&!(bool)Get(plan,"UpdateOpen")
   &&((TreeView)Field(pane,"structureTree")).Nodes.Count>0,"窗口树未完成构建");
  await Task.Delay(30);
 }
 static async Task WindowCachedContextScenario(Form host)
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-window-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var oldBinding=binding.GetValue(null);var oldRoot=resolver.GetValue(null);
  binding.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   using(var pane=(Control)New("PdmTaskPaneControl","窗口缓存回归"))
   using(var client=(IDisposable)New("PdmApiClient","http://window-cache.invalid/"))
   {
    host.Controls.Add(pane);pane.CreateControl();var plugin=New("PdmAddin");SetField(plugin,"taskPaneControl",pane);SetField(plugin,"apiClient",client);
    var path=Path.Combine(directory,"ROOT.SLDASM");File.WriteAllText(path,"verified base bytes");var root=WindowModel(path,732);var part=WindowModel(Path.Combine(directory,"PART.SLDPRT"),1);Set(part,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
    var project=Guid.NewGuid();var version=Guid.NewGuid();var id=(Guid)Get(root,"DocumentId");var hash=WorkingReuseHash(path);
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,version,"W1",hash,new FileInfo(path).Length});
    Set(root,"OpenedVersionId",version);Set(root,"OpenedRevision","W1");Set(root,"CurrentRevision","W1");
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",version);Set(manifest,"RootRelativePath","ROOT.SLDASM");
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName","ROOT.SLDASM");Set(file,"RelativePath","ROOT.SLDASM");Set(file,"Revision","W1");Set(file,"Sha256",hash);Set(file,"FileLength",new FileInfo(path).Length);var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
    ((IDictionary)Field(plugin,"controlledOpenManifests"))[directory]=manifest;
    var key=path+"\nDefault";DiscardCall(plugin,"CacheActiveTree",key,root,project);SetField(plugin,"currentTree",part);SetField(plugin,"currentTreeCacheKey",Get(part,"FullPath")+"\nDefault");
    var timer=Stopwatch.StartNew();
    using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
    {
     bool restored;try{restored=(bool)DiscardCall(plugin,"TryRestoreActiveTreeFromCache",key);}catch(IOException error){throw new Exception("Window cache restore read locked CAD content",error);}
     if(!restored||!ReferenceEquals(Field(plugin,"currentTree"),root)||(Guid?)Get(root,"OpenedVersionId")!=version)throw new Exception("Cached version/context lost");
     await WindowReady(pane,root);
    }
    Console.WriteLine("PASS: 732-node document cache restored with CAD bytes exclusively locked; no CAD hash/rebind; elapsedMs="+timer.ElapsedMilliseconds);

    var appType=plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
    object Model(string p)=>new Proxy(modelType,call=>call.MethodName=="GetPathName"?p:call.MethodName=="get_ConfigurationManager"?
      new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),c=>c.MethodName=="get_ActiveConfiguration"?
       new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),n=>n.MethodName=="get_Name"?"Default":throw new Exception(n.MethodName)).GetTransparentProxy():throw new Exception(c.MethodName)).GetTransparentProxy():throw new Exception("Unexpected native scan "+call.MethodName)).GetTransparentProxy();
    var subPath=Path.Combine(directory,"SUB.SLDASM");var sub=WindowModel(subPath,1);((IList)Get(root,"Children")).Add(sub);
    var subKey=subPath+"\nDefault";DiscardCall(plugin,"CacheActiveTree",key,root,project);DiscardCall(plugin,"CacheActiveTree",subKey,sub,project);SetField(plugin,"currentTree",sub);SetField(plugin,"currentTreeCacheKey",subKey);
    SetField(plugin,"application",new Proxy(appType,c=>c.MethodName=="get_ActiveDoc"?Model(subPath):throw new Exception(c.MethodName)).GetTransparentProxy());
    SetField(plugin,"pendingTreeRefreshForceScan",0);DiscardCall(plugin,"OnAssemblyTreeChanged");DiscardCall(plugin,"CacheCurrentTreeContext");
    var cache=(IDictionary)Field(plugin,"activeTreeCache");if(cache.Contains(key)||cache.Contains(subKey))throw new Exception("Structural invalidation resurrected stale active/owning assembly trees");
    Console.WriteLine("PASS: nested assembly structural change invalidates owner and child; window switch cannot reinsert dirty tree");
    DiscardCall(plugin,"CacheActiveTree",key,root,project);SetField(plugin,"currentTree",root);SetField(plugin,"currentTreeCacheKey",key);SetField(plugin,"pendingTreeRefreshForceScan",0);
    DiscardCall(plugin,"OnFileOpened",(string)Get(part,"FullPath"));
    if((int)Field(plugin,"pendingTreeRefreshForceScan")!=0||!cache.Contains(key))throw new Exception("Opening another window forced current assembly scan");
    SetField(plugin,"pendingTreeRefreshForceScan",1);DiscardCall(plugin,"OnFileOpened",(string)Get(part,"FullPath"));if((int)Field(plugin,"pendingTreeRefreshForceScan")!=1)throw new Exception("File open erased a pending structural scan");
    Console.WriteLine("PASS: file-open invalidation is scoped to its document and preserves real forced scans");
    var alternative=WindowModel(path,1,"Alternative");Set(alternative,"DocumentId",id);DiscardCall(plugin,"CacheActiveTree",path+"\nAlternative",alternative,project);SetField(plugin,"pendingTreeRefreshForceScan",0);
    if(!(bool)DiscardCall(plugin,"TryRestoreActiveTreeFromCache",path+"\nAlternative")||!ReferenceEquals(Field(plugin,"currentTree"),alternative))throw new Exception("Configurations shared the same cached model");
    if((bool)DiscardCall(plugin,"TryRestoreActiveTreeFromCache",path+"\nNeverOpened"))throw new Exception("Unknown configuration reused another configuration");
    Console.WriteLine("PASS: document/configuration cache identities remain independent; unseen configuration requires scan");
   }
  }
  finally{binding.SetValue(null,oldBinding);resolver.SetValue(null,oldRoot);if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected QA path");Directory.Delete(directory,true);}
 }

 static async Task WindowViewStateScenario(Form host)
 {
  using(var pane=(Control)New("PdmTaskPaneControl","732节点窗口切换回归"))
  {
   host.Controls.Add(pane);pane.CreateControl();SetField(pane,"authenticatedUsername","tester");
   var root=WindowModel("H:\\QA-window\\ROOT.SLDASM",732);var other=WindowModel("H:\\QA-window\\OTHER.SLDASM",40);var alternate=WindowModel("H:\\QA-window\\ROOT.SLDASM",40,"Alternative");
   DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);var tree=(TreeView)Field(pane,"structureTree");
   DiscardCall(pane,"SelectByComponentName","component-345");var selected=tree.SelectedNode;var selectedIdentity=(string)Get(selected.Tag,"InstancePath");selected.Checked=true;
   var checkedPaths=(HashSet<string>)Field(pane,"checkedCheckInPaths");var checkedPath=(string)Get(selected.Tag,"FullPath");if(!checkedPaths.Contains(checkedPath))throw new Exception("Fixture selection missing");
   var generation=(int)Field(pane,"treeBuildGeneration");var originalRoot=tree.Nodes[0];var timer=Stopwatch.StartNew();
   for(var i=0;i<25;i++)DiscardCall(pane,"SetTree",root);
   if((int)Field(pane,"treeBuildGeneration")!=generation||tree.Nodes[0]!=originalRoot||tree.SelectedNode!=selected)throw new Exception("Unchanged tree metadata rebuilt UI nodes");
   Console.WriteLine("PASS: 25 unchanged 732-node updates retain TreeNode instances, selection and expansion; elapsedMs="+timer.ElapsedMilliseconds);
   var captureExpanded=Type("PdmTaskPaneControl").GetMethods(Flags).Single(method=>method.Name=="CaptureExpandedInstancePaths"&&method.GetParameters().Length==1);
   var expanded=(HashSet<string>)captureExpanded.Invoke(null,new object[]{tree.Nodes});
   tree.TopNode=selected.Parent;var topIdentity=(string)Get(tree.TopNode.Tag,"InstancePath");
   DiscardCall(pane,"SetTree",other);await WindowReady(pane,other);
   if(checkedPaths.Count!=0)throw new Exception("New document inherited another document's checked files");
   DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   var restoredExpanded=(HashSet<string>)captureExpanded.Invoke(null,new object[]{tree.Nodes});
   if((string)Get(tree.SelectedNode?.Tag,"InstancePath")!=selectedIdentity||!tree.SelectedNode.Checked||!checkedPaths.Contains(checkedPath)
    ||!expanded.IsSubsetOf(restoredExpanded)||(string)Get(tree.TopNode?.Tag,"InstancePath")!=topIdentity)throw new Exception("Document return lost selection, expansion, checked paths or scroll position");
   Console.WriteLine("PASS: assembly/other-window/assembly restores selected component, checked files, expanded ancestors and top visible row");
   DiscardCall(pane,"SetTree",alternate);await WindowReady(pane,alternate);if(checkedPaths.Count!=0)throw new Exception("Configuration inherited another configuration's selection");
   DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);if(!checkedPaths.Contains(checkedPath)||(string)Get(tree.SelectedNode?.Tag,"InstancePath")!=selectedIdentity)throw new Exception("Configuration return lost its own view state");
   Console.WriteLine("PASS: same-file configurations maintain separate checked/selected tree state");
   generation=(int)Field(pane,"treeBuildGeneration");Set(tree.SelectedNode.Tag,"WorkState",Enum.Parse(Type("CadWorkState"),"ModifiedUnsaved"));DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   if((int)Field(pane,"treeBuildGeneration")==generation||tree.SelectedNode.ForeColor!=Color.FromArgb(188,68,35))throw new Exception("No-change rendering cache hid a changed edit status");
   generation=(int)Field(pane,"treeBuildGeneration");var added=WindowModel("H:\\QA-window\\NEW.SLDPRT",1);((IList)Get(root,"Children")).Add(added);DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   if((int)Field(pane,"treeBuildGeneration")==generation)throw new Exception("UI tree skipped real topology change");
   Console.WriteLine("PASS: changed work state and added tree membership invalidate unchanged-render cache");
   var pendingModel=tree.SelectedNode.Tag;Set(pendingModel,"WorkState",Enum.Parse(Type("CadWorkState"),"None"));Set(pendingModel,"DrawingUpdatePendingReason","pending drawing update");
   SetField(pane,"activeHealthFilter",Enum.Parse(pane.GetType().GetField("activeHealthFilter",Flags).FieldType,"Pending"));DiscardCall(pane,"RebuildTree",string.Empty);await WindowReady(pane,root);
   Set(pendingModel,"DrawingUpdatePendingReason",string.Empty);DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   if(Field(pane,"activeHealthFilter").ToString()!="None"||tree.Nodes[0].Nodes.Count<2)throw new Exception("Status-only changes left an obsolete filtered tree on screen");
   Console.WriteLine("PASS: drawing-pending reason changes update filter membership and restore the full tree when the filter becomes empty");
   var search=(TextBox)Field(pane,"searchBox");search.Text="PART-145";await WindowReady(pane,root);
   DiscardCall(pane,"SetTree",other);await WindowReady(pane,other);generation=(int)Field(pane,"treeBuildGeneration");
   DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   if(search.Text!="PART-145"||(int)Field(pane,"treeBuildGeneration")!=generation+1)throw new Exception("Saved search triggered duplicate or incorrect tree rebuild");
   Console.WriteLine("PASS: document search is restored with one build rather than an extra TextChanged rebuild");
   DiscardCall(pane,"SetTree",other);DiscardCall(pane,"SetTree",root);DiscardCall(pane,"SetTree",other);DiscardCall(pane,"SetTree",root);await WindowReady(pane,root);
   if(!ReferenceEquals(tree.Nodes[0].Tag,root)||search.Text!="PART-145")throw new Exception("An old asynchronous build replaced the latest window");
   Console.WriteLine("PASS: rapid alternating window changes keep only the latest asynchronous tree build");
   DiscardCall(pane,"SetTree",other);await WindowReady(pane,other);var rebound=WindowModel("H:\\QA-window\\ROOT.SLDASM",732);
   DiscardCall(pane,"SetTree",rebound);await WindowReady(pane,rebound);if(checkedPaths.Contains(checkedPath))throw new Exception("Checked file selection crossed into a different PLM document at the same path");
   Console.WriteLine("PASS: cached checked paths are not restored onto a different document identity at the same physical path");
   for(var i=0;i<18;i++){var small=WindowModel("H:\\QA-window\\OTHER-"+i+".SLDASM",1);DiscardCall(pane,"SetTree",small);await WindowReady(pane,small);}
   if(((IDictionary)Field(pane,"treeViewStates")).Count>16)throw new Exception("Per-window UI view-state cache grew without bound");
   Console.WriteLine("PASS: view-state retention is bounded while alternating many documents");
  }
 }
 static async Task WindowBaseMetadataScenario(Form host)
 {
  using(var fixture=new TreeRefreshFixture(host))
  {
   await fixture.WaitForTree();
   var identity=Type("PdmDocumentIdentityStore");var field=identity.GetField("bindingDirectory",Flags);var previous=field.GetValue(null);
   var bindingPath=Path.Combine(Path.GetDirectoryName(fixture.Path),"bindings");field.SetValue(null,bindingPath);
   try
   {
    var version=Guid.NewGuid();var hash=WorkingReuseHash(fixture.Path);
    if(!(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{fixture.Path,Get(fixture.Root,"DocumentId"),fixture.Project,version,"W2",hash,new FileInfo(fixture.Path).Length}))throw new Exception("Fixture base binding failed");
    fixture.Handler.BlockSuffix="/document-relations";
    using(var locked=new FileStream(fixture.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
    {
     var refresh=(Task)DiscardCall(fixture.Plugin,"RefreshMetadataAsync",fixture.Project,System.Threading.CancellationToken.None);
     await TreeWait(()=>fixture.Handler.Blocked,"Base metadata did not reach guarded relation boundary");
     if((Guid?)Get(fixture.Root,"OpenedVersionId")!=version||(string)Get(fixture.Root,"OpenedRevision")!="W2"||fixture.NativeCalls!=0)throw new Exception("Cached base did not update from the physical binding without hashing/native scans");
     SetField(fixture.Plugin,"batchDialogInProgress",1);fixture.Handler.Resume.SetResult(true);await refresh;
    }
    Console.WriteLine("PASS: background base binding refresh reflects W2 while CAD bytes are locked, before guarded node/permission metadata update");
   }
   finally{field.SetValue(null,previous);}
  }
 }
 static void WindowPreviewFlagsScenario()
 {
  var settings=assembly.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore",true);var workspace=(string)settings.GetMethod("GetWorkspaceRoot",Flags).Invoke(null,null);
  var paths=new[]{Path.Combine(workspace,"View","QA","ROOT.SLDASM"),Path.Combine(workspace,".uplm","Snapshots","QA","Latest","W1","ROOT.SLDASM"),Path.Combine(workspace,".uplm","Snapshots","QA","ReadOnly","W1","ROOT.SLDASM"),Path.Combine(Path.GetTempPath(),"UPTON-PDM","history","QA","ROOT.SLDASM"),Path.Combine(Path.GetTempPath(),"outside-QA","ROOT.SLDASM")};
  foreach(var path in paths)
  {
   var plugin=New("PdmAddin");var root=WindowModel(path,1);var part=WindowModel(Path.Combine(Path.GetTempPath(),"outside-QA","PART.SLDPRT"),1);Set(part,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));var drawing=WindowModel(Path.Combine(Path.GetTempPath(),"outside-QA","DRAWING.SLDDRW"),1);Set(drawing,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(drawing);
   DiscardCall(plugin,"MarkReadOnlyPreview",root,false,false);
   var historical=(bool)Type("PdmAddin").GetMethod("IsHistoricalPreviewPath",Flags).Invoke(null,new object[]{path});
   var latest=!historical&&(bool)Type("PdmAddin").GetMethod("IsLatestReadOnlyPreviewPath",Flags).Invoke(null,new object[]{path});
   if((bool)Get(root,"IsHistoricalPreview")!=historical||(bool)Get(root,"IsLatestReadOnlyPreview")!=latest
    ||(bool)Get(part,"IsHistoricalPreview")!=historical||(bool)Get(part,"IsLatestReadOnlyPreview")!=latest
    ||(bool)Get(drawing,"IsHistoricalPreview")||(bool)Get(drawing,"IsLatestReadOnlyPreview"))throw new Exception("Batched preview classification changed historical/latest/drawing semantics");
  }
  Console.WriteLine("PASS: one-pass preview classification retains working/latest/historical/outside paths and independent linked-drawing semantics");
 }
}
