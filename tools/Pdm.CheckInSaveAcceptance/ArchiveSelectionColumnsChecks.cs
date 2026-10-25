using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static Task ArchiveSelectionColumnsScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);var preference=Path.Combine(output,"columns.json");
  if(File.Exists(preference))File.Delete(preference);
  var field=Type("BatchOperationDialog").GetField("FileColumnSettingsPath",Flags);var previous=field?.GetValue(null);field?.SetValue(null,preference);
  var root=New("CadTreeNode");Set(root,"FileName","ROOT.SLDASM");Set(root,"FullPath",Path.Combine(output,"ROOT.SLDASM"));
  var models=Enumerable.Range(0,70).Select(i=>{var node=New("CadTreeNode");Set(node,"FileName","文件-"+i+".SLDPRT");Set(node,"FullPath",Path.Combine(output,"文件-"+i+".SLDPRT"));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));((System.Collections.IList)Get(root,"Children")).Add(node);return node;}).Prepend(root).ToArray();
  Form Dialog()=> (Form)New("BatchOperationDialog",root,LinkedItems(models),Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"AcquireLatestAndCheckout"),null,null,null,false,false,0);
  int[] Columns(Form form){var tree=(TreeView)Field(form,"files");var values=new object[]{tree.ClientSize.Width,0,0};form.GetType().GetMethod("GetFileDetailColumns",Flags).Invoke(form,values);return new[]{(int)values[1],(int)values[2]};}
  void Check(bool passed,string text){TreeCheck(passed,text);Console.WriteLine("PASS: "+text);}
  int[] saved;
  try
  {
   host.ClientSize=new Size(1000,850);
   using(var form=Dialog())
   {
    form.TopMost=false;form.TopLevel=false;form.FormBorderStyle=FormBorderStyle.None;form.MinimumSize=Size.Empty;form.Dock=DockStyle.Fill;host.Controls.Add(form);form.Show();
    var tree=(TreeView)Field(form,"files");var header=(Panel)Field(form,"filesHeader");var before=Columns(form);
    var offset=header.PointToClient(tree.PointToScreen(Point.Empty)).X;
    DesignHeaderMouse(header,"Move",before[0]+offset,MouseButtons.None);
    Check(header.Cursor==Cursors.VSplit,"存档表头分隔线显示列宽拖动光标");
    DesignHeaderMouse(header,"Down",before[0]+offset);DesignHeaderMouse(header,"Move",before[0]-50+offset);DesignHeaderMouse(header,"Up",before[0]-50+offset);
    var resized=Columns(form);Check(resized[0]==before[0]-50&&resized[1]==before[1],"拖动名称列边界只调整名称与版本列");
    DesignHeaderMouse(header,"Down",resized[1]+offset);DesignHeaderMouse(header,"Move",resized[1]-35+offset);DesignHeaderMouse(header,"Up",resized[1]-35+offset);saved=Columns(form);
    Check(saved[0]==resized[0]&&saved[1]==resized[1]-35&&File.Exists(preference),"版本与状态列可独立调节，松开后保存列宽");
    var settings=File.ReadAllText(preference);TreeCall(form,"ToggleShowCheckedOnly");TreeCall(form,"ToggleShowCheckedOnly");
    Check(Columns(form).SequenceEqual(saved)&&File.ReadAllText(preference)==settings,"筛选勾选图档不会重置列宽");
    host.ClientSize=new Size(760,850);host.ClientSize=new Size(1000,850);
    Check(Columns(form).SequenceEqual(saved)&&File.ReadAllText(preference)==settings,"窗口缩窄再恢复不会覆盖记忆列宽");
    DesignHeaderMouse(header,"Down",saved[0]+offset);DesignHeaderMouse(header,"Move",saved[0]+10+offset);header.Capture=false;
    Check((int)Field(form,"draggingFileDivider")==0&&header.Cursor==Cursors.Default,"中断拖动后释放鼠标捕获并保存已调整宽度");saved=Columns(form);
    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"archive-selection-columns.png"));
     foreach(var x in saved){var head=form.PointToClient(header.PointToScreen(new Point(x+offset,10)));var row=form.PointToClient(tree.PointToScreen(new Point(x,tree.Nodes[0].Bounds.Top+tree.ItemHeight-2)));
      Check(head.X==row.X&&bitmap.GetPixel(head.X,head.Y).ToArgb()==Color.FromArgb(205,210,217).ToArgb()&&bitmap.GetPixel(row.X,row.Y).ToArgb()==Color.FromArgb(228,231,235).ToArgb(),"表头与内容的实际列分隔线对齐："+x);}}
    var style=TreeColumnWindowStyle(tree.Handle,-16);Check((style&0x8000)!=0&&(style&0x100000)==0&&(style&0x200000)!=0,"图档列固定横向位置并保留纵向滚动");
   }
   using(var form=Dialog())
   {
    form.TopLevel=false;form.TopMost=false;form.FormBorderStyle=FormBorderStyle.None;form.MinimumSize=Size.Empty;form.Dock=DockStyle.Fill;host.Controls.Add(form);form.Show();
    Check(Columns(form).SequenceEqual(saved),"重新打开存档窗口恢复上次列宽");
   }
   File.WriteAllText(preference,"invalid-json");using(var form=Dialog())Check((int)Field(form,"fileNameWidth")==0,"损坏的列宽偏好回退默认布局");
  }
  finally{field?.SetValue(null,previous);}
  return Task.CompletedTask;
 }
}
