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
 static async Task ArchiveProjectPickerScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);var values=new List<object>();
  object Project(string code,string name,Guid? parent=null,bool allowed=true)
  {
   var project=New("ProjectDto");Set(project,"Id",Guid.NewGuid());Set(project,"Code",code);Set(project,"Name",name);Set(project,"ParentProjectId",parent);
   Set(project,"CanSubmitArchive",allowed);Set(project,"CanReadContent",true);Set(project,"DocumentCount",3);values.Add(project);return project;
  }
  object initial=null,target=null,forbidden=null;
  for(var i=0;i<48;i++)
  {
   var parent=Project("P"+(700000+i),"主项目验证"+i);
   for(var j=1;j<=4;j++)
   {
    var child=Project((string)Get(parent,"Code")+"-"+j,"搜索验收子项目"+i+"号"+j,(Guid)Get(parent,"Id"),!(i==27&&j==4));
    if(i==23&&j==2)initial=child;if(i==27&&j==2)target=child;if(i==27&&j==4)forbidden=child;
   }
  }
  var projects=Array.CreateInstance(Type("ProjectDto"),values.Count);for(var i=0;i<values.Count;i++)projects.SetValue(values[i],i);
  var path=Path.Combine(output,"未修改样件.SLDPRT");File.WriteAllText(path,"isolated project picker fixture");
  var root=New("CadTreeNode");Set(root,"FullPath",path);Set(root,"FileName",Path.GetFileName(path));Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
  var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
  void Check(bool passed,string text){TreeCheck(passed,text);Console.WriteLine("PASS: "+text);}
  Rectangle Outer(Control control)=>control.Parent.RectangleToScreen(control.Bounds);
  void Save(Form form,string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
  Form Batch()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Single();
  Form Picker()=>((Panel)Field(ArchiveWindow(session),"confirmation")).Controls.OfType<Form>().Single();
  async Task WaitPicker()=>await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"confirmation")).Controls.OfType<Form>().Any(form=>form.Visible)),"项目选择没有进入原存档窗口");
  void OpenPicker()
  {
   var window=ArchiveWindow(session);var selector=(Control)Field(Batch(),"projectSelector");
   window.BeginInvoke((Action)(()=>((Button)Field(selector,"browse")).PerformClick()));
  }
  try
  {
   var driver=Task.Run(async()=>{
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any()),"存档图档区域没有显示");
    ArchiveUi(session,()=>{var window=ArchiveWindow(session);window.TopMost=false;var frame=window.Height-window.ClientSize.Height;Check(window.ClientSize.Height==Math.Min(960,Screen.FromHandle(window.Handle).WorkingArea.Height-frame),"默认存档窗口加高100像素，并受所属显示器工作区约束");OpenPicker();return true;});
    await WaitPicker();
    ArchiveUi(session,()=>{
     var window=ArchiveWindow(session);var picker=Picker();var panel=(Panel)Field(window,"confirmation");var history=(TextBox)Field(window,"history");
     var search=(TextBox)Field(picker,"search");var main=(ListBox)Field(picker,"mainProjects");var children=(ListView)Field(picker,"childProjects");
     var confirm=(Button)Field(picker,"confirm");var cancel=(Button)picker.CancelButton;
     void LayoutCheck(string label)
     {
      window.PerformLayout();picker.PerformLayout();
      Check(search.Visible&&Outer(panel).Contains(Outer(search))&&search.Height>=24,label+"：项目号搜索框完整可见且不会被列表滚动藏起");
      var records=(Panel)Field(window,"processRecords");
      Check(Outer(main).Left==Outer(records).Left&&Outer(children).Right==Outer(records).Right,label+"：项目选择列表左右边缘与原过程记录区对齐");
      Check(Outer(cancel).Top-Math.Max(Outer(main).Bottom,Outer(children).Bottom)>=10&&Outer(panel).Contains(Outer(cancel))&&Outer(panel).Contains(Outer(confirm)),label+"：取消与确认按钮完整显示，并与列表保持间隙");
      Check(!history.Visible&&Outer(picker)==Outer(records),label+"：项目选择完整覆盖过程记录区，不残留日志条带");
      var columns=main.Parent.Parent;
      Check(Outer(main.Parent).Top==Outer(columns).Top&&Outer(main.Parent).Bottom==Outer(columns).Bottom
       &&Outer(children.Parent).Top==Outer(columns).Top&&Outer(children.Parent).Bottom==Outer(columns).Bottom,label+"：主项目和图档归属列表占满搜索栏与确认栏之间的全部高度");
     }
     Save(window,"project-picker-before-search");LayoutCheck("默认窗口");
     var information=(Control)Field(window,"information");
     Check(((Panel)Field(window,"selection")).Width==820&&information.ClientSize.Width-information.Padding.Horizontal==804,"右侧从483加宽至804像素，左侧宽度保持820像素");
     Check(children.Columns.Cast<ColumnHeader>().Sum(column=>column.Width)<=children.ClientSize.Width,"加宽后的项目选择仍可同时显示名称与三项状态列");
     var originalHandle=window.Handle;
     search.Text=(string)Get(target,"Code");
     Check(main.Items.Count==1&&children.Items.Count==1&&ReferenceEquals(children.Items[0].Tag,target)&&!confirm.Enabled,"按子项目号直接定位所属主项目和子项目，不自动确认");
     search.Text=(string)Get(target,"Name");Check(main.Items.Count==1&&children.Items.Count==1&&ReferenceEquals(children.Items[0].Tag,target),"项目名称搜索可用");
     search.Text="P700027-0";Check(main.Items.Count==1&&children.Items.Count==1&&ReferenceEquals(Get(children.Items[0].Tag,"ParentProjectId"),null),"主项目图档号的-0搜索可用");
     search.Text="没有匹配的项目";Check(main.Items.Count==0&&children.Items.Count==0&&!confirm.Enabled,"无匹配时清空候选并禁止确认");
     search.Text=(string)Get(forbidden,"Code");children.Items[0].Selected=true;Check(!confirm.Enabled,"搜索不能绕过项目存档权限");
     search.Text=string.Empty;Check(main.Items.Count==48,"清除搜索恢复全部项目");
     main.SelectedIndex=35;LayoutCheck("滚动主项目列表后");
     window.Size=window.MinimumSize;LayoutCheck("最小窗口");Save(window,"project-picker-minimum");
     window.ClientSize=new Size(1640,900);picker.Font=new Font("Microsoft YaHei UI",12F);LayoutCheck("放大字体");Save(window,"project-picker-large-font");
     Check(window.Handle==originalHandle&&!((Panel)Field(window,"selection")).Enabled,"项目浏览保持同一存档窗口并锁定左侧范围");
     cancel.PerformClick();return true;
    });
    await TreeWait(()=>ArchiveUi(session,()=>!((Panel)Field(ArchiveWindow(session),"confirmationHost")).Visible),"取消项目选择后未恢复原存档页");
    ArchiveUi(session,()=>{Check((Guid?)Get(Batch(),"SelectedProjectId")== (Guid)Get(initial,"Id")&&((Panel)Field(ArchiveWindow(session),"selection")).Enabled,"取消项目浏览保留原归属和图档范围");Check(((TextBox)Field(ArchiveWindow(session),"history")).Visible&&ArchiveHistory(session).Contains("等待确认：浏览选择项目"),"取消后恢复原过程记录，保留先前日志");OpenPicker();return true;});
    await WaitPicker();
    ArchiveUi(session,()=>{var picker=Picker();((TextBox)Field(picker,"search")).Text=(string)Get(target,"Code");var children=(ListView)Field(picker,"childProjects");children.Items[0].Selected=true;((Button)Field(picker,"confirm")).PerformClick();return true;});
    await TreeWait(()=>ArchiveUi(session,()=>!((Panel)Field(ArchiveWindow(session),"confirmationHost")).Visible),"确认项目选择后没有返回");
    ArchiveUi(session,()=>{
     Check((Guid?)Get(Batch(),"SelectedProjectId")==(Guid)Get(target,"Id"),"确认选择正确写回项目归属");
     Check(ArchiveHistory(session).Contains("确认结果：浏览选择项目"),"过程记录保留项目确认全过程");
     Check(((TextBox)Field(ArchiveWindow(session),"history")).Visible,"确认后恢复完整过程记录区");
     ((Button)Field(Batch(),"operationCancel")).PerformClick();return true;
    });
   });
   _=driver.ContinueWith(task=>{if(task.IsFaulted)TreeCall(session,"RequestCancellation");},TaskScheduler.Default);
   ArchiveUi(session,()=>{
    var form=(Form)New("BatchOperationDialog",root,LinkedItems(root),projects,Get(initial,"Id"),"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),new[]{path},null,null,false,false,0);
    return TreeCall(session,"ShowInline",form,true);
   });await driver;
  }
  finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
 }
}
