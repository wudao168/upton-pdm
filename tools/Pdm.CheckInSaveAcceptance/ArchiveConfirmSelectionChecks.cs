using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveConfirmSelectionScenario(Form host,string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var nodes=new List<object>{root};
  for(var i=0;i<214;i++){var node=LinkedNode(folder,"PART-"+i+".SLDPRT","Part");nodes.Add(node);((IList)Get(root,"Children")).Add(node);}
  archiveRuleVersions=nodes.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>new[]{LinkedVersion(node,LinkedSnapshot(node))});archiveRuleDelay=0;
  foreach(var node in nodes){Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"NativeFileScopeVerified",true);}
  var part=nodes[1];var permission=nodes[2];Set(part,"CheckedOutBy","tester");Set(permission,"CheckedOutBy","tester");
  var path=(string)Get(part,"FullPath");var original=File.ReadAllText(path);File.AppendAllText(path," actual engineering edit");
  using(var dialog=(Form)New("BatchOperationDialog",root,LinkedItems(nodes.ToArray()),Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,true,false,0))
  {
   var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
   dialog.TopMost=false;dialog.TopLevel=false;host.ClientSize=new Size(1240,850);host.Controls.Add(dialog);dialog.Show();
   await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
   await TreeWait(()=>!(bool)Field(dialog,"archiveAnalysisInProgress"),"初始分析没有结束");
   var execute=(Button)Field(dialog,"execute");var tree=(TreeView)Field(dialog,"files");var clicks=0;execute.Click+=(_,__)=>clicks++;
   int Count()=>((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Count();
   TreeNode Row(object node)=>tree.Nodes.Cast<TreeNode>().SelectMany(CheckedNodes).First(item=>item.Tag!=null&&ReferenceEquals(Get(item.Tag,"Model"),node));
   void Check(bool ok,string message){TreeCheck(ok,message);Console.WriteLine("PASS: "+message);}
   void Save(string name){using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
   TreeCall(dialog,"SetAllChecked",false);
   Check(Count()==0&&!execute.Enabled,"215文件清空勾选后确认存档立即禁用");
   Check(execute.BackColor==Color.FromArgb(242,244,247)&&execute.ForeColor==Color.FromArgb(145,155,168),"零勾选使用统一禁用灰色");
   execute.PerformClick();Check(clicks==0&&dialog.DialogResult!=DialogResult.OK,"禁用按钮不能触发点击或提交结果");
   var size=dialog.Size;TreeCall(dialog,"ToggleShowCheckedOnly");
   Check(!execute.Enabled&&tree.Nodes.Count==1&&tree.Nodes[0].Text=="尚未勾选图档"&&dialog.Size==size,"仅显示勾选的空列表保持禁用及原窗口大小");Save("empty-selection-disabled");
   TreeCall(dialog,"ToggleShowCheckedOnly");TreeCall(dialog,"ToggleNode",Row(part));
   Check(Count()==1&&execute.Enabled,"勾选可处理图档立即启用确认存档；selected="+Count()+" enabled="+execute.Enabled+" allowed="+TreeCall(dialog,"CanSelectNode",part)+" state="+TreeCall(dialog,"StateText",part,"tester"));Save("one-selection-enabled");
   TreeCall(dialog,"ToggleNode",Row(part));Check(Count()==0&&!execute.Enabled,"取消最后一项勾选立即禁用");
   TreeCall(dialog,"SetAllChecked",true);Check(Count()>0&&execute.Enabled,"全选有效图档恢复确认按钮");
   TreeCall(dialog,"SetAllChecked",false);Check(Count()==0&&!execute.Enabled,"全选后清除仍保持禁用");
   await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",true);Check(Count()==1&&execute.Enabled,"勾选变更项分析完成后有有效范围才启用");
   TreeCall(dialog,"SetAllChecked",false);archiveRuleDelay=15;var analysis=(Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
   Check(!execute.Enabled,"重新分析期间确认按钮保持禁用");await analysis;archiveRuleDelay=0;
   Check(Count()==0&&!execute.Enabled,"重新检查保留零勾选时不会再次启用");
   File.WriteAllText(path,original);await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",true);
   Check(Count()==0&&!execute.Enabled,"全部未变更且零勾选时分析结束仍禁用");
   TreeCall(dialog,"ToggleNode",Row(permission));Check(Count()==1&&execute.Enabled,"仅释放未变更图档权限的有效勾选仍可确认");
   ((ComboBox)Field(dialog,"editDisposition")).SelectedIndex=1;
   Check(Count()==0&&!execute.Enabled,"继续编辑剔除仅权限项后自动禁用");
   ((ComboBox)Field(dialog,"editDisposition")).SelectedIndex=0;
   ((CheckBox)Field(dialog,"standardLibrary")).Checked=true;
   Check(Count()>0&&execute.Enabled,"标准库存档自动包含完整结构时保留确认功能");
   ((CheckBox)Field(dialog,"standardLibrary")).Checked=false;await TreeWait(()=>!(bool)Field(dialog,"archiveAnalysisInProgress"),"退出标准库后分析没有结束");
   Check(Count()==0&&!execute.Enabled,"返回项目存档后零勾选不残留启用状态");
   ((RadioButton)Field(dialog,"acquire")).Checked=true;TreeCall(dialog,"SetAllChecked",false);
   Check(!execute.Enabled&&execute.Text=="获取权限","获取权限模式零勾选同样禁用执行");
   TreeCall(dialog,"ToggleNode",Row(part));Check(Count()==1&&execute.Enabled,"获取权限模式选择有效图档正常启用");
   TreeCall(dialog,"SetWorkflowBusy");Check(!execute.Enabled,"已有勾选进入执行阶段后确认按钮仍禁用");
   TreeCall(dialog,"FinishWorkflow");TreeCall(dialog,"UpdateWorkflowButtons");Check(!execute.Enabled,"完成状态不会因残留勾选重新启用确认");
  }
 }
}
