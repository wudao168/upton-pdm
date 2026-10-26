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
 static async Task ArchiveStatusLayoutScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");Set(root,"DocumentId",null);
  var nodes=Enumerable.Range(1,70).Select(index=>{var node=LinkedNode(folder,"零件-"+index+".SLDPRT","Part");Set(node,"DocumentId",null);((IList)Get(root,"Children")).Add(node);return node;}).Prepend(root).ToArray();
  void Check(bool condition,string message){TreeCheck(condition,message);Console.WriteLine("PASS: "+message);}
  Rectangle Outer(Control control)=>control.Parent.RectangleToScreen(control.Bounds);
  void Save(Form form,string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
  async Task<object> Start()
  {
   var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
   ArchiveUi(session,()=>{ArchiveWindow(session).TopMost=false;return true;});return session;
  }
  async Task Close(object session){ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
  var session=await Start();
  try
  {
   ArchiveUi(session,()=>{var window=ArchiveWindow(session);var state=window.GetType().GetField("resultState",Flags);Check(state!=null,"顶部信息区包含独立的存档结果显示");var fields=(Control[])Field(window,"informationFields");var header=(Control)Field(window,"informationHeader");Check(fields.All(field=>field.Visible&&header.ClientRectangle.Contains(field.Bounds))&&((Label[])Field(window,"informationLabels")).All(label=>label.Visible&&label.Width>=48),"图档尚未读取时，四行信息及标题也完整显示");return true;});
   var driver=Task.Run(async()=>{
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any()),"存档列表未显示");
    ArchiveUi(session,()=>{
     var window=ArchiveWindow(session);var form=((Panel)Field(window,"selection")).Controls.OfType<Form>().Single();var tree=(TreeView)Field(form,"files");
     var fields=(Control[])Field(window,"informationFields");var labels=(Label[])Field(window,"informationLabels");var history=(Control)Field(window,"history");
     void LayoutCheck(string name)
     {
      window.PerformLayout();form.PerformLayout();
      var left=(Rectangle[])TreeCall(form,"InformationRows",(Control)Field(window,"informationHeader"));
      Check(fields.Length==4&&labels.Select(label=>label.Text).SequenceEqual(new[]{"处理信息","当前图档","存档结果","当前进度"}),name+"：四行标题与确认稿一致");
      Check(Enumerable.Range(0,3).All(i=>fields[i].Top==left[i].Top&&fields[i].Height==left[i].Height),name+"：前三行分别对齐归属项目、变更说明和全选行");
      Check(fields.All(field=>field.Width==fields[0].Width&&field.Height==fields[0].Height)&&fields[3].Top>=fields[2].Bottom+4,name+"：进度条与显示框同宽同高，并位于结果下方");
      Check(Outer(labels[0]).Left==Outer(history).Left&&Outer(fields[0]).Right==Outer(history).Right,name+"：四行整体左右边缘对齐过程记录框");
      Check(fields[3].Bottom<=((Label)Field(window,"historyTitle")).Top,name+"：进度行和过程记录标题互不遮挡");
      Check(tree.Font.SizeInPoints==9&&tree.ItemHeight==20&&tree.ClientSize.Height/tree.ItemHeight>=30,name+"：保留9pt字号与20px行高，一屏不少于30条明细");
     }
     LayoutCheck("默认窗口");Save(window,"status-running");
     window.Size=window.MinimumSize;LayoutCheck("最小窗口");Save(window,"status-minimum");
     var panel=(Panel)Field(window,"resultPanel");var state=(Label)Field(window,"resultState");
     Check(panel.BackColor==Color.FromArgb(255,243,205)&&state.Text.Contains("运行中"),"运行中的单一结果区域显示黄色");
     TreeCall(window,"Report","正在读取组件结构",(string)Get(nodes[1],"FileName"),7,20);var progress=(Control)Field(window,"progress");
     Check(progress.Text.Contains("35%")&&progress.Text.Contains("7 / 20"),"明确数量的进度显示真实百分比与条数");
     Check(((Label)Field(window,"file")).Text==(string)Get(nodes[1],"FileName")&&((Label)Field(window,"file")).Visible,"当前图档使用持续可见的独立显示框");
     TreeCall(window,"Report","正在核对引用范围",null,0,0);
     Check((bool)Get(progress,"IsIndeterminate")&&!progress.Text.Contains("%"),"数量未知阶段显示处理中，不虚报百分比");
     TreeCall(window,"Report","正在准备上传",(string)Get(nodes[2],"FileName"),7,20);Save(window,"status-counted-progress");
     ((Button)Field(form,"operationCancel")).PerformClick();return true;
    });
   });
   _=driver.ContinueWith(task=>{if(task.IsFaulted)TreeCall(session,"RequestCancellation");},TaskScheduler.Default);
   ArchiveUi(session,()=>{var form=(Form)New("BatchOperationDialog",root,LinkedItems(nodes),Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0);return TreeCall(session,"ShowInline",form,true);});await driver;
   TreeCall(session,"Complete","服务器已确认存档完成。",false,false);
   ArchiveUi(session,()=>{var window=ArchiveWindow(session);Check(((Panel)Field(window,"resultPanel")).BackColor==Color.FromArgb(228,244,232)&&((Label)Field(window,"resultState")).Text.Contains("成功"),"服务器成功结果显示绿色");Check(((Control)Field(window,"progress")).Text.Contains("100%"),"成功结束时进度到100%");Check(((Label)Field(window,"elapsed")).Parent==Field(window,"resultPanel")&&((Label)Field(window,"elapsed")).Text.StartsWith("总耗时"),"总耗时位于同一结果区域右侧");Save(window,"status-success");return true;});
  }
  finally{await Close(session);}
  session=await Start();
  try
  {
   TreeCall(session,"ReportFile",3,10,"失败样件.SLDPRT","正在提交样件");TreeCall(session,"Fail","存档失败，请检查图档权限。");
   ArchiveUi(session,()=>{var window=ArchiveWindow(session);Check(((Panel)Field(window,"resultPanel")).BackColor==Color.FromArgb(251,231,231)&&((Label)Field(window,"resultState")).Text.Contains("失败"),"失败结果显示红色");Check(((Control)Field(window,"progress")).Text.Contains("30%")&&!((Control)Field(window,"progress")).Text.Contains("100%"),"失败保留实际进度，不伪报100%");Save(window,"status-failure");return true;});
  }
  finally{await Close(session);}
  session=await Start();
  try
  {
   Set(session,"WorkflowStarted",true);TreeCall(session,"Complete","服务器已完成，等待清理。",false,false);
   ArchiveUi(session,()=>{Check(((Label)Field(ArchiveWindow(session),"resultState")).Text.Contains("运行中"),"清理结束前保持运行中，提前收到服务器结果不误报完成");return true;});
   TreeCall(session,"FinishIfRunning");ArchiveUi(session,()=>{Check(((Label)Field(ArchiveWindow(session),"resultState")).Text.Contains("成功"),"清理后保留原有成功结果");return true;});
  }
  finally{await Close(session);}
  session=await Start();
  try
  {
   var notices=string.Join("\r\n",Enumerable.Range(1,12).Select(index=>"引用提示：来源-"+index+".STEP；原有关联保留"));
   TreeCall(session,"Complete",notices,false,true);
   ArchiveUi(session,()=>{var window=ArchiveWindow(session);
    Check(((Label)Field(window,"resultState")).Text=="✓ 成功（有引用提示）","引用提示显示为存档成功，不误报失败");
    Check(((Label)Field(window,"stage")).Text=="存档成功（有引用提示）","处理信息明确区分有提示的成功结果");
    Check(((Control)Field(window,"history")).Text.Contains("来源-12.STEP"),"全部引用提示保留在过程记录，不只显示前八项");
    Save(window,"status-reference-warnings");return true;});
  }
  finally{await Close(session);}
  using(var pane=(Control)New("PdmTaskPaneControl",""))
  {
   var version=New("DocumentVersionDto");var snapshot=New("DocumentReferenceNodeDto");
   Set(snapshot,"ReferenceWarnings",new System.Collections.Generic.List<string>{"来源：Z:\\旧源.STEP；未找到原件；原有关联保留"});
   Set(version,"ReferenceSnapshot",snapshot);Set(version,"ChangeNote","手动修改零件");
   var versions=Array.CreateInstance(Type("DocumentVersionDto"),1);versions.SetValue(version,0);
   host.Controls.Add(pane);pane.CreateControl();TreeCall(pane,"ShowVersions",Guid.NewGuid(),"零件.SLDPRT",versions);
   var list=(ListView)Field(pane,"versionList");
   Check(list.Items[0].SubItems[4].Text.Contains("引用提示 1 项"),"重新读取的版本记录显示引用提示数量");
   Check(list.ShowItemToolTips&&list.Items[0].ToolTipText.Contains("Z:\\旧源.STEP"),"版本记录可查看持久化的原始来源路径和提示");
  }
 }
}
