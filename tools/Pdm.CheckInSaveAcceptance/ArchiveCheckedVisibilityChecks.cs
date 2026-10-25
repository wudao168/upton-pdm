using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveCheckedVisibilityScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  object Node(string name,string kind="Assembly")
  {
   var node=LinkedNode(folder,name,kind);Set(node,"DocumentId",null);Set(node,"DisplayName",name);return node;
  }
  void Child(object parent,object child)=>((IList)Get(parent,"Children")).Add(child);
  var root=Node("ROOT.SLDASM");var first=Node("LEVEL-1.SLDASM");var second=Node("LEVEL-2.SLDASM");
  var part=Node("SELECTED.SLDPRT","Part");var drawing=Node("SELECTED.SLDDRW","Drawing");
  var owner=Node("EMBEDDED-OWNER.SLDASM");var embedded=Node("EMBEDDED.SLDASM");var virtualPart=Node("VIRTUAL.SLDPRT","Part");
  Set(embedded,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));Set(virtualPart,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
  Child(root,first);Child(first,second);Child(second,part);Child(part,drawing);Child(root,owner);Child(owner,embedded);Child(embedded,virtualPart);
  var repeated=New("CadTreeNode");foreach(var key in new[]{"FullPath","FileName","DisplayName","Kind"})Set(repeated,key,Get(part,key));Child(owner,repeated);
  var permission=LinkedNode(folder,"PERMISSION.SLDPRT","Part");Set(permission,"CheckedOutBy","tester");
  Set(permission,"StoredVersionStateKnown",true);Set(permission,"HasStoredVersion",true);Set(permission,"NativeFileScopeVerified",true);
  Set(permission,"LatestStoredSha256",WorkingReuseHash((string)Get(permission,"FullPath")));Child(root,permission);
  archiveRuleVersions=new Dictionary<Guid,object[]>{{(Guid)Get(permission,"DocumentId"),new[]{LinkedVersion(permission,LinkedSnapshot(permission))}}};archiveRuleDelay=0;
  var items=LinkedItems(root,first,second,part,drawing,owner,permission,repeated);
  var paths=new[]{part,drawing,owner}.Select(node=>(string)Get(node,"FullPath")).ToArray();
  Form Dialog()
  {
   var dialog=(Form)New("BatchOperationDialog",root,items,Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),paths,null,null,false,false,0);
   dialog.TopMost=false;dialog.ShowInTaskbar=false;dialog.Opacity=0;
   var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);
   loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
   return dialog;
  }
  IEnumerable<TreeNode> Nodes(TreeView tree)=>tree.Nodes.Cast<TreeNode>().SelectMany(CheckedNodes);
  TreeNode Find(TreeView tree,object model)=>Nodes(tree).Single(node=>node.Tag!=null&&ReferenceEquals(Get(node.Tag,"Model"),model));
  void Check(bool condition,string description){TreeCheck(condition,description);Console.WriteLine("PASS: "+description);}
  void Save(Form form,string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
  using(var dialog=Dialog())
  {
   dialog.TopLevel=false;dialog.Dock=DockStyle.Fill;dialog.MinimumSize=Size.Empty;host.ClientSize=new Size(820,840);host.Controls.Add(dialog);dialog.Show();
   await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
   await TreeWait(()=>!(bool)Field(dialog,"archiveAnalysisInProgress"),"初始分析没有完成");
   var tree=(TreeView)Field(dialog,"files");Find(tree,first).Expand();Find(tree,second).Collapse();Find(tree,owner).Collapse();
   TreeCall(dialog,"ToggleShowCheckedOnly");Save(dialog,"checked-only");
   Check(new[]{part,drawing,repeated}.All(model=>CheckedParents(Find(tree,model)).All(parent=>parent.IsExpanded)),"仅显示勾选后，四层图档及重复实例的路径完整展开");
   Check(new[]{root,first,second}.All(model=>Find(tree,model).StateImageIndex==0)&&Find(tree,root).ToolTipText.Contains("未勾选"),"定位父级保持未勾选状态，并明确说明仅显示层级");
   var summary=(Label)Field(dialog,"selectionSummary");
   Check(summary.Text.Contains("已勾选 3 个图档")&&summary.Text.Contains("存档3个，仅处理权限0个"),"计数按独立文件去重，不包含定位父级和嵌入式组件");
   Check(Find(tree,embedded).IsExpanded&&((Label)Field(dialog,"selectionHint")).Text.Contains("2个嵌入式组件实例随父装配保存"),"嵌入层级完整显示，随父装配保存的说明保留");
   Find(tree,first).Collapse();TreeCall(dialog,"RefreshFileTreePresentation");
   Check(CheckedParents(Find(tree,drawing)).All(parent=>parent.IsExpanded),"筛选中的重新刷新不会把已勾选图档藏入折叠层级");
   TreeCall(dialog,"ToggleShowCheckedOnly");
   Check(Find(tree,first).IsExpanded&&!Find(tree,second).IsExpanded&&!Find(tree,owner).IsExpanded,"显示全部恢复筛选前每个实例的展开状态");
   TreeCall(dialog,"ToggleNode",Find(tree,permission));TreeCall(dialog,"ToggleShowCheckedOnly");
   Check(summary.Text.Contains("已勾选 4 个图档")&&summary.Text.Contains("存档3个，仅处理权限1个"),"手动勾选未变更图档后，独立更新权限处理数量");
   archiveRuleDelay=40;var analysis=(Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
   Check(summary.Text.Contains("已勾选 4 个图档")&&((Label)Field(dialog,"selectionHint")).Text.Contains("正在核对"),"分析进度独立显示，不覆盖固定勾选数量");
   await analysis;archiveRuleDelay=0;
   Check(summary.Text.Contains("已勾选 4 个图档")&&CheckedParents(Find(tree,drawing)).All(parent=>parent.IsExpanded),"重新检查完成后计数及筛选展开仍然正确");
   TreeCall(dialog,"SetAllChecked",false);
   Check(summary.Text.Contains("已勾选 0 个图档")&&tree.Nodes.Count==1&&tree.Nodes[0].Text=="尚未勾选图档","清除后同时显示零计数和明确的空状态");
   TreeCall(dialog,"ToggleShowCheckedOnly");TreeCall(dialog,"SetAllChecked",true);TreeCall(dialog,"ToggleShowCheckedOnly");
   Check(summary.Text.Contains("已勾选 7 个图档")&&Nodes(tree).Where(node=>node.Nodes.Count>0).All(node=>node.IsExpanded),"全选后去重计数正确，全部筛选层级自动展开");
  }
  var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
  try
  {
   var driver=Task.Run(async()=>{
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any()),"存档选择区未显示");
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any(form=>!(bool)Field(form,"archiveAnalysisInProgress"))),"存档分析未完成");
    ArchiveUi(session,()=>{
     var window=ArchiveWindow(session);window.TopMost=false;window.Opacity=0;
     var form=((Panel)Field(window,"selection")).Controls.OfType<Form>().Single();form.Opacity=1;
     var list=(Control)Field(form,"fileDetails");var process=(Control)Field(window,"processRecords");var summary=(Label)Field(form,"selectionSummary");
     void LayoutCheck(string label)
     {
      window.PerformLayout();form.PerformLayout();
      var left=list.RectangleToScreen(list.ClientRectangle);var right=process.RectangleToScreen(process.ClientRectangle);
      Console.WriteLine(label+"容器："+left+" / "+right);
      Check(Math.Abs(left.Top-right.Top)<=1&&Math.Abs(left.Bottom-right.Bottom)<=1,label+"：左右图档和过程容器上下边缘对齐");
      var needed=TextRenderer.MeasureText(summary.Text,summary.Font,new Size(summary.Width,10000),TextFormatFlags.WordBreak).Height+summary.Padding.Vertical;
      Check(summary.Visible&&summary.Height>=needed&&summary.Bottom<=list.Top,label+"：勾选数量在图档上方完整显示");
     }
     LayoutCheck("默认窗口");Save(window,"aligned-default");
     window.Size=window.MinimumSize;TreeCall(window,"Report","正在处理当前图档",(string)Get(part,"FileName"),1,3);LayoutCheck("最小窗口和当前文件提示");
     window.Size=new Size(1500,1000);form.Font=new Font("Microsoft YaHei UI",12F);form.Scale(new SizeF(1.25F,1.25F));LayoutCheck("放大字体与窗口");Save(window,"aligned-scaled");
     ((Button)Field(form,"operationCancel")).PerformClick();return true;
    });
   });
   _=driver.ContinueWith(task=>{if(task.IsFaulted)TreeCall(session,"RequestCancellation");},TaskScheduler.Default);
   ArchiveUi(session,()=>TreeCall(session,"ShowInline",Dialog(),true));await driver;
  }
  finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
 }
 static IEnumerable<TreeNode> CheckedNodes(TreeNode root)
 {
  yield return root;foreach(TreeNode child in root.Nodes)foreach(var nested in CheckedNodes(child))yield return nested;
 }
 static IEnumerable<TreeNode> CheckedParents(TreeNode node)
 {
  for(var parent=node.Parent;parent!=null;parent=parent.Parent)yield return parent;
 }
}
