using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task WorkspaceInlineProjectScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);host.ClientSize=new Size(440,1020);
  void Check(bool value,string message){TreeCheck(value,message);Console.WriteLine("PASS: "+message);}
  var projects=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("ProjectDto")));
  object Project(string code,Guid? parent=null)
  {
   var value=New("ProjectDto");Set(value,"Id",Guid.NewGuid());Set(value,"Code",code);Set(value,"Name","隔离项目选择");
   Set(value,"ParentProjectId",parent);Set(value,"CanReadContent",true);Set(value,"CanSubmitArchive",true);Set(value,"DocumentCount",3);projects.Add(value);return value;
  }
  var main=Project("QA-MAIN");var first=Project("QA-MAIN-1",(Guid)Get(main,"Id"));var second=Project("QA-MAIN-2",(Guid)Get(main,"Id"));
  using(var pane=(Control)New("ProjectDocumentsControl"))
  {
   host.Controls.Add(pane);pane.CreateControl();var selector=Field(pane,"projectSelector");
   ((Button)Field(selector,"browse")).PerformClick();
   var early=Field(pane,"inlineProjectBrowser");Check(((ListBox)Field(early,"mainProjects")).Items.Count==0,"启动前项目尚未返回的选择区可正常打开");
   TreeCall(pane,"SetProjects",projects);
   Check(ReferenceEquals(early,Field(pane,"inlineProjectBrowser"))&&((ListBox)Field(early,"mainProjects")).Items.Count==1,"项目清单延迟返回时立即刷新已打开的选择区");
   ((ListBox)Field(early,"mainProjects")).SelectedIndex=0;
   Check(((ListView)Field(early,"childProjects")).Items.Count==3,"延迟加载后主项目及图档归属都可选择");
   ((Button)Field(early,"cancel")).PerformClick();TreeCall(selector,"SelectProject",Get(first,"Id"));
   var searchBox=(TextBox)Field(selector,"selectedProject");
   Check(!searchBox.ReadOnly&&searchBox.TabStop,"顶部原只读项目框改为可输入搜索框");
   Check(Type("ProjectDocumentsControl").GetField("openEdit",Flags)==null,"工作区移除获取编辑按钮");
   var browseButton=(Button)Field(selector,"browse");var latestButton=(Button)Field(pane,"openLatest");
   var browseBounds=browseButton.RectangleToScreen(browseButton.ClientRectangle);var latestBounds=latestButton.RectangleToScreen(latestButton.ClientRectangle);
   Check(latestBounds.Left>browseBounds.Right&&latestBounds.Top==browseBounds.Top,"打开最新与选择项目同行并位于右侧");
   var root=LinkedNode(output,"ROOT.SLDASM","Assembly");TreeCall(pane,"SetTree",root);
   Check(!((Control)Field(pane,"selectedState")).Visible&&!((Control)Field(pane,"workspaceLocation")).Visible,"图档模式删除版本与本地位置说明区域");
   var formCount=Application.OpenForms.Count;((Button)Field(selector,"browse")).PerformClick();await Task.Delay(60);
   var picker=Field(pane,"inlineProjectBrowser");var mainList=(ListBox)Field(picker,"mainProjects");var children=(ListView)Field(picker,"childProjects");
   var hostPanel=(Control)Field(pane,"projectPickerHost");
   Check(picker!=null&&hostPanel.Visible&&Application.OpenForms.Count==formCount,"选择项目在常驻工作区内显示，不打开新窗口");
   var upper=mainList.RectangleToScreen(mainList.ClientRectangle);var lower=children.RectangleToScreen(children.ClientRectangle);
   Check(upper.Bottom<lower.Top&&upper.Left==lower.Left,"主项目在上、图档归属在下，两个列表上下排列");
   Check(!((Button)Field(pane,"openLatest")).Enabled,"选择归属期间不执行旧图档的打开");
   using(var bitmap=new Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(Path.Combine(output,"select-project.png"));}
   ((Button)Field(picker,"cancel")).PerformClick();
   Check(Field(pane,"inlineProjectBrowser")==null&&Equals(Get(selector,"SelectedProjectId"),Get(first,"Id"))&&ReferenceEquals(Field(pane,"root"),root),"取消内嵌选择保留原项目与图档列表");
   Check(!host.IsDisposed,"内嵌取消不会关闭宿主窗口");
   ((Button)Field(selector,"browse")).PerformClick();await Task.Delay(40);picker=Field(pane,"inlineProjectBrowser");children=(ListView)Field(picker,"childProjects");
   searchBox.Text="QA-MAIN-2";
   Check(children.Items.Count==1&&Equals(Get(children.Items[0].Tag,"Id"),Get(second,"Id")),"顶部搜索按项目号筛选图档归属");
   var row=children.Items.Cast<ListViewItem>().Single(item=>Equals(Get(item.Tag,"Id"),Get(second,"Id")));row.Selected=true;
   ((Button)Field(picker,"confirm")).PerformClick();
   await TreeWait(()=>Field(pane,"inlineProjectBrowser")==null,"归属选择未完成");
   Check(Field(pane,"inlineProjectBrowser")==null&&Equals(Get(selector,"SelectedProjectId"),Get(second,"Id")),"确认归属后两框收起并选择指定子项目");
   Check(Field(pane,"root")==null,"新项目按既有通知加载图档，不继续显示旧项目结构");
   TreeCall(pane,"SetTree",root);await Task.Delay(30);
   Check(!((Control)Field(pane,"projectPickerHost")).Visible,"确认后原区域切回图档结构列表");
   using(var bitmap=new Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(Path.Combine(output,"documents.png"));}
   searchBox.Text="ROOT.SLDASM";
   Check(Field(pane,"inlineProjectBrowser")!=null&&((ListBox)Field(Field(pane,"inlineProjectBrowser"),"mainProjects")).Items.Count==0
       &&ReferenceEquals(Field(pane,"root"),root),"选完项目后搜索仍只针对项目，不过滤或改变图档结构");
   ((Button)Field(Field(pane,"inlineProjectBrowser"),"cancel")).PerformClick();searchBox.Text=string.Empty;
   host.ClientSize=new Size(300,900);((Button)Field(selector,"browse")).PerformClick();await Task.Delay(40);picker=Field(pane,"inlineProjectBrowser");
   var button=(Button)Field(picker,"confirm");var area=(Control)Field(pane,"projectPickerHost");
   Check(area.RectangleToScreen(area.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)),"300像素窄窗格仍可点击确认选择");
   using(var bitmap=new Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(Path.Combine(output,"narrow-project.png"));}
   ((Button)Field(picker,"cancel")).PerformClick();
   var store=Type("PdmDocumentIdentityStore");store.GetMethod("ConfigureBrowserReadScope",Flags).Invoke(null,new object[]{output});
   var directory=Path.Combine(output,"View","QA-MAIN-2");var id=Guid.NewGuid();var version=Guid.NewGuid();
   foreach(var folder in new[]{"装配工作区","图档缓存"})
   {
    var target=Path.Combine(directory,folder,"PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(target));File.WriteAllText(target,"same controlled bytes");
    if(!(bool)store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{target,id,Get(second,"Id"),version,"W1",WorkingReuseHash(target),(long?)new FileInfo(target).Length}))throw new Exception("Fixture binding failed");
   }
   var paths=TreeCallStatic("ProjectDocumentsControl","ReadLocalWorkspacePaths",directory,Get(second,"Id"),CancellationToken.None);
   SetField(pane,"localPaths",paths);var node=New("CadTreeNode");Set(node,"DocumentId",id);
   Check((string)TreeCall(pane,"LocalStateText",node)=="多个本地副本","同一图档多份缓存显示多个本地副本，不再显示未下载");
   Set(node,"DocumentId",Guid.NewGuid());Check((string)TreeCall(pane,"LocalStateText",node)=="未下载","真正没有缓存时仍显示未下载");
  }
 }
}
