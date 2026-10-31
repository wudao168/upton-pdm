using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int LayerWindowStyle(IntPtr window,int index);
 [DllImport("user32.dll",EntryPoint="GetWindow")]static extern IntPtr LayerWindow(IntPtr window,uint command);
 [DllImport("user32.dll",EntryPoint="GetTopWindow")]static extern IntPtr LayerTop(IntPtr window);
 [DllImport("user32.dll",EntryPoint="SetWindowPos")]static extern bool LayerRaise(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll",EntryPoint="ShowWindow")]static extern bool LayerShow(IntPtr window,int command);
 [DllImport("user32.dll",EntryPoint="IsWindowVisible")]static extern bool LayerVisible(IntPtr window);
 [DllImport("user32.dll",EntryPoint="GetDlgItem")]static extern IntPtr LayerButton(IntPtr window,int id);
 [DllImport("user32.dll",EntryPoint="PostMessageW")]static extern bool LayerPost(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll",EntryPoint="SetForegroundWindow")]static extern bool LayerActivate(IntPtr window);
 static int LayerDepth(IntPtr handle){var depth=0;for(var window=LayerTop(IntPtr.Zero);window!=IntPtr.Zero;window=LayerWindow(window,2),depth++)if(window==handle)return depth;return int.MaxValue;}
 static async Task DialogOwnerLayerScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);
  host.ClientSize=new Size(400,300);
  host.ShowInTaskbar=true;
  using(var pane=new Panel{Dock=DockStyle.Fill})
  using(var foreign=new Form{Text="层级测试：其他应用模拟",Opacity=0,ShowInTaskbar=true,Size=new Size(250,150)})
  {
   host.Controls.Add(pane);pane.CreateControl();foreign.Show();
   var records=new List<object>();
   void Check(bool passed,string message){TreeCheck(passed,message);Console.WriteLine("PASS: "+message);}
   void Raise(IntPtr handle){if(!LayerRaise(handle,new IntPtr(-2),0,0,0,0,0x53))throw new Exception("无法设置隔离测试窗口顺序");LayerActivate(handle);}
   async Task Inspect(IntPtr handle,bool topMost,string name,IntPtr expectedOwner=default)
   {
    Check(!topMost&&(LayerWindowStyle(handle,-20)&8)==0,name+"：没有系统级置顶样式");
    Check(LayerWindow(handle,4)==(expectedOwner==IntPtr.Zero?host.Handle:expectedOwner),name+"：原生所属窗口符合界面线程要求");
    Raise(foreign.Handle);
    await Task.Delay(100);
    Check(LayerDepth(foreign.Handle)<LayerDepth(handle),name+"：其他应用窗口能显示在弹窗前面");
    Raise(host.Handle);
    await Task.Delay(100);
    Check(LayerDepth(handle)<LayerDepth(host.Handle),name+"：所属主窗口回到前台时弹窗仍在其上方");
    records.Add(new{Name=name,TopMost=topMost,Owner=LayerWindow(handle,4).ToInt64()});
   }
   var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{pane.Handle,CancellationToken.None});
   await (Task)Get(session,"Ready");
   try
   {
    await Task.Delay(100);
    var archiveHandle=ArchiveUi(session,()=>ArchiveWindow(session).Handle);
    await Inspect(archiveHandle,ArchiveUi(session,()=>ArchiveWindow(session).TopMost),"存档窗口（独立界面线程）",(IntPtr)Get(session,"PresentationOwnerHandle"));
    Check((IntPtr)Get(session,"OwnerHandle")==host.Handle,"存档窗口逻辑归属主窗口，原生所属窗口留在独立界面线程");
    Raise(foreign.Handle);TreeCall(session,"SetStage","正在检查结构，后台保持完整过程");await Task.Delay(800);
    Check(LayerDepth(foreign.Handle)<LayerDepth(archiveHandle),"后台进度和计时更新不会把存档窗口抬到其他应用上方");
    TreeCall(session,"Complete","本次检查完成。",false,false);await Task.Delay(150);
    Check(LayerDepth(foreign.Handle)<LayerDepth(archiveHandle),"结果显示仍保持在其他应用之后");
    LayerShow(host.Handle,6);await Task.Delay(400);
    Check(!LayerVisible(archiveHandle),"所属主窗口最小化时存档窗口随之隐藏");
    LayerShow(host.Handle,9);await Task.Delay(400);
    Check(LayerVisible(archiveHandle),"恢复所属主窗口时存档窗口随之恢复");
   }
   finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
   Check(IsWindowEnabled(host.Handle),"关闭存档窗口后恢复所属主窗口的可操作状态");
   var root=New("CadTreeNode");Set(root,"FileName","层级验证.SLDASM");Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
   var emptyProjects=Array.CreateInstance(Type("ProjectDto"),0);
   using(var progress=(Form)New("BatchProgressDialog",1))
   using(var note=(Form)New("ChangeNoteDialog","层级验证.SLDASM"))
   using(var scope=(Form)New("ArchiveScopeReviewDialog",Array.Empty<string[]>(),1,0))
   using(var batch=(Form)New("BatchOperationDialog",root,LinkedItems(root),emptyProjects,null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
   using(var property=(Form)New("BatchPropertyEditDialog",Array.CreateInstance(Type("BatchPropertyEditItem"),0),null,0,Enum.Parse(Type("PropertyOperationMode"),"BatchEdit"),null,null,null,null,null,null,null,null,host.Handle))
   {
    foreach(var item in new[]{new{Form=progress,Name="进度窗口"},new{Form=note,Name="变更说明"},new{Form=scope,Name="存档范围确认"},new{Form=batch,Name="图档选择"},new{Form=property,Name="属性窗口"}})
    {
     item.Form.Opacity=0;item.Form.ShowInTaskbar=false;item.Form.Show(pane);await Task.Delay(100);await Inspect(item.Form.Handle,item.Form.TopMost,item.Name);item.Form.Hide();
    }
   }
   Exception failure=null;var seen=false;
   using(var timer=new System.Windows.Forms.Timer{Interval=20})
   {
    timer.Tick+=(_,__)=>
    {
     var prompt=FindWindow("#32770","层级回归确认");if(prompt==IntPtr.Zero||LayerWindow(prompt,4)!=host.Handle)return;
     try
     {
      seen=true;
      Check((LayerWindowStyle(prompt,-20)&8)==0,"确认提示没有系统级置顶样式");
      Check(LayerWindow(prompt,4)==host.Handle,"确认提示归属主窗口，保持应用内模态");
      Check((LayerWindowStyle(LayerButton(prompt,2),-16)&0xF)==1,"确认提示保留取消为默认按钮");
     }catch(Exception error){failure=error;}
     finally{timer.Stop();LayerPost(prompt,0x111,new IntPtr(2),IntPtr.Zero);}
    };
    timer.Start();
    var result=(DialogResult)Type("TopMostMessageBox").GetMethod("Show",Flags).Invoke(null,new object[]{pane,"确认仅在所属应用内显示。","层级回归确认",MessageBoxButtons.OKCancel,MessageBoxIcon.Information,MessageBoxDefaultButton.Button2});
    if(failure!=null)throw failure;
    Check(seen&&result==DialogResult.Cancel,"确认提示正常返回取消结果");
   }
   var geometry=Type("DialogWindowSizing").GetMethod("CenteredBounds",Flags);
   foreach(var area in new[]{new Rectangle(0,0,1920,1040),new Rectangle(-1920,0,1920,1040),new Rectangle(1920,-1080,1280,1000)})
   {
    var bounds=(Rectangle)geometry.Invoke(null,new object[]{area,new Size(3000,2000)});
    Check(area.Contains(bounds)&&Math.Abs(bounds.Left+bounds.Right-area.Left-area.Right)<=1&&Math.Abs(bounds.Top+bounds.Bottom-area.Top-area.Bottom)<=1,"保持所属显示器工作区居中并适配超大窗口："+area.Location);
   }
   File.WriteAllText(Path.Combine(output,"owner-layer.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(records));
  }
 }
}
