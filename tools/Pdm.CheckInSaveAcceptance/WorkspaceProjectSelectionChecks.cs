using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task WorkspaceProjectSelectionScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);host.ClientSize=new Size(440,1020);
  void Check(bool value,string message){TreeCheck(value,message);Console.WriteLine("PASS: "+message);}
  var projects=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("ProjectDto")));
  object Project(string code,Guid? parent=null,string name="隔离选择验证")
  {
   var value=New("ProjectDto");Set(value,"Id",Guid.NewGuid());Set(value,"Code",code);Set(value,"Name",name);
   Set(value,"ParentProjectId",parent);Set(value,"CanReadContent",true);Set(value,"CanSubmitArchive",true);Set(value,"DocumentCount",3);projects.Add(value);return value;
  }
  var main16=Project("P700016");var child16=Project("P700016-6",(Guid)Get(main16,"Id"));
  var main17=Project("P700017");var child17=Project("P700017-16",(Guid)Get(main17,"Id"),"玻璃装配");
  using(var pane=(Control)New("ProjectDocumentsControl"))
  {
   host.Controls.Add(pane);pane.CreateControl();TreeCall(pane,"SetProjects",projects);
   var selector=Field(pane,"projectSelector");var search=(TextBox)Field(selector,"selectedProject");
   search.Text="16";var picker=Field(pane,"inlineProjectBrowser");var mains=(ListBox)Field(picker,"mainProjects");
   var numericCorrect=mains.Items.Count==1&&Equals(Get(mains.Items[0],"Id"),Get(main16,"Id"));
   Console.WriteLine("OBSERVED: numeric16-main-count="+mains.Items.Count+"; expected=1");
   search.Text="P700017-16";
   var children=(ListView)Field(picker,"childProjects");
   Check(mains.Items.Count==1&&children.Items.Count==1&&Equals(Get(children.Items[0].Tag,"Id"),Get(child17,"Id")),"完整子项目号仍能准确查找主项目与归属");
   search.Text="玻璃";
   Check(mains.Items.Count==1&&children.Items.Count==1&&Equals(Get(children.Items[0].Tag,"Id"),Get(child17,"Id")),"文字搜索仍按归属名称匹配");
   search.Text="16";mains.SelectedItem=main16;
   var row=children.Items.Cast<ListViewItem>().Single(item=>Equals(Get(item.Tag,"Id"),Get(child16,"Id")));row.Selected=true;
   var callbacks=0;var insideNotification=false;var callbackInsideNotification=false;var pickerVisibleAtCallback=false;
   Action received=()=>{callbacks++;callbackInsideNotification|=insideNotification;pickerVisibleAtCallback|=((Control)Field(pane,"projectPickerHost")).Visible;};
   var selectedEvent=pane.GetType().GetEvent("ProjectSelected");
   var parameters=selectedEvent.EventHandlerType.GetMethod("Invoke").GetParameters().Select(p=>Expression.Parameter(p.ParameterType)).ToArray();
   selectedEvent.AddEventHandler(pane,Expression.Lambda(selectedEvent.EventHandlerType,Expression.Call(Expression.Constant(received),typeof(Action).GetMethod("Invoke")),parameters).Compile());
   var notify=typeof(Control).GetMethod("OnDoubleClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
   var timer=Stopwatch.StartNew();insideNotification=true;notify.Invoke(children,new object[]{EventArgs.Empty});insideNotification=false;
   var eventReturnedMs=timer.ElapsedMilliseconds;var disposedInsideNotification=children.IsDisposed;
   if(!children.IsDisposed)notify.Invoke(children,new object[]{EventArgs.Empty});
   await TreeWait(()=>callbacks>0,"双击归属未发送项目选择通知");
   Console.WriteLine("OBSERVED: disposed-inside-notification="+disposedInsideNotification+"; callback-inside-notification="+callbackInsideNotification+"; picker-visible-at-callback="+pickerVisibleAtCallback+"; event-return-ms="+eventReturnedMs);
   Check(callbacks==1&&Equals(Get(selector,"SelectedProjectId"),Get(child16,"Id")),"双击及连续重复通知仅确认一次指定归属");
   Check(Field(pane,"inlineProjectBrowser")==null&&children.IsDisposed&&!host.IsDisposed,"双击后选择区释放，宿主及文档区保持可用");
   Check(((TreeView)Field(pane,"tree")).Nodes.Count==0,"只选择项目不会触发原生图档打开");
   search.Text=string.Empty;((Button)Field(selector,"browse")).PerformClick();picker=Field(pane,"inlineProjectBrowser");mains=(ListBox)Field(picker,"mainProjects");
   Check(mains.Items.Count==2,"清空搜索恢复全部主项目");
   ((Button)Field(picker,"cancel")).PerformClick();
   using(var bitmap=new Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(Path.Combine(output,"after-selection.png"));}
   Check(numericCorrect,"数字16不再因其他主项目的子项目序号16误匹配");
   Check(!disposedInsideNotification&&!callbackInsideNotification&&!pickerVisibleAtCallback,"原生双击事件返回后才释放列表，收起选择区后才加载清单");
  }
 }
}
