using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void ArchiveProjectConfirmUiScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  object Project(string code,int? count,Guid? parent=null,bool submit=true){var p=New("ProjectDto");Set(p,"Id",Guid.NewGuid());Set(p,"Code",code);Set(p,"Name","界面验证项目");Set(p,"DocumentCount",count);Set(p,"CanReadContent",true);Set(p,"CanSubmitArchive",submit);Set(p,"ParentProjectId",parent);return p;}
  var main=Project("P700016",2);var id=(Guid)Get(main,"Id");var empty=Project("P700016-1",0,id);var existing=Project("P700016-2",800,id);var other=Project("P700016-3",5,id);var unknown=Project("P700016-4",null,id);var denied=Project("P700016-5",3,id,false);
  var list=Array.CreateInstance(Type("ProjectDto"),6);var values=new[]{main,empty,existing,other,unknown,denied};for(var i=0;i<values.Length;i++)list.SetValue(values[i],i);
  void Screenshot(Form form,string name){using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(output,name+".png"));}}
  using(var form=(Form)New("ProjectBrowserDialog",list,(Guid?)Get(existing,"Id"),true,true)){
   form.Opacity=0;form.ShowInTaskbar=false;form.Show();Application.DoEvents();
   var confirm=(Button)Field(form,"confirm");var checkbox=(CheckBox)Field(form,"existingProjectConfirmation");var rows=(ListView)Field(form,"childProjects");
   void Select(object project){TreeCall(form,"SelectProjectItem",rows,(Guid?)Get(project,"Id"));Application.DoEvents();}
   Check(checkbox.Visible&&!checkbox.Checked&&!confirm.Enabled,"已有图档项目默认必须二次勾选");
   TreeCall(form,"ConfirmProjectSelection");Check(form.DialogResult!=DialogResult.OK,"按钮、回车及双击共用的确认入口不能绕过勾选");
   var cancel=ArchiveControls(form).OfType<Button>().Single(b=>b.Text=="取消");
   Check(checkbox.RectangleToScreen(checkbox.ClientRectangle).Right<=cancel.RectangleToScreen(cancel.ClientRectangle).Left,"二次确认位于取消按钮左侧");Screenshot(form,"project-existing-unchecked");
   checkbox.Checked=true;Application.DoEvents();Check(confirm.Enabled,"勾选二次确认后才允许确认选择");Screenshot(form,"project-existing-checked");
   Select(other);Check(!checkbox.Checked&&!confirm.Enabled,"切换其他已有图档项目会清空勾选");
   checkbox.Checked=true;Select(empty);Check(!checkbox.Visible&&!checkbox.Checked&&confirm.Enabled,"空项目无需二次确认且不继承之前的勾选");Screenshot(form,"project-empty");
   Select(existing);Check(!confirm.Enabled&&!checkbox.Checked,"切回原项目也必须重新确认");
   Select(denied);checkbox.Checked=true;Check(!confirm.Enabled,"二次勾选不会绕过项目存档权限");
   Select(unknown);Check(checkbox.Visible&&!checkbox.Checked&&!confirm.Enabled,"数量未知时不误当作空项目绕过确认");
   Select(existing);form.MinimumSize=Size.Empty;form.ClientSize=new Size(460,650);TreeCall(form,"ConfigureInlineLayout");Application.DoEvents();
   Screenshot(form,"project-narrow");
   File.WriteAllText(Path.Combine(output,"narrow-bounds.txt"),"Form="+form.ClientRectangle+"; checkbox="+form.RectangleToClient(checkbox.RectangleToScreen(checkbox.ClientRectangle))+"; confirm="+form.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle)));
   Check(form.ClientRectangle.Contains(form.RectangleToClient(checkbox.RectangleToScreen(checkbox.ClientRectangle)))&&form.ClientRectangle.Contains(form.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle))),"窄侧栏中二次确认和操作按钮完整显示");Screenshot(form,"project-narrow");
   form.Close();
  }
  using(var form=(Form)New("ProjectBrowserDialog",list,(Guid?)Get(existing,"Id"),true)){
   form.Opacity=0;form.ShowInTaskbar=false;form.Show();Application.DoEvents();Check(((Button)Field(form,"confirm")).Enabled&&!((CheckBox)Field(form,"existingProjectConfirmation")).Visible,"工作区浏览等非存档项目选择保留原操作");form.Close();
  }
  var records=new List<object>();
  foreach(var count in new int?[]{0,800,null})foreach(var size in new[]{new Size(560,420),new Size(780,700)}){
   Set(existing,"DocumentCount",count);
   using(var form=(Form)New("ProjectAdmissionConfirmationDialog",existing,main,1194,null)){
    form.Opacity=0;form.ShowInTaskbar=false;form.ClientSize=size;form.Show();Application.DoEvents();
    var details=ArchiveControls(form).OfType<TableLayoutPanel>().Single(p=>p.ColumnCount==3&&p.RowCount==10);
    var current=(Label)details.GetControlFromPosition(1,8);var added=(Label)details.GetControlFromPosition(1,9);
    Check(current.Text==(count.HasValue?count+" 个":"待核对")&&added.Text=="1194 个","现有数量及新增数量按行独立显示："+count+" / "+size.Width);
    Check(current.ForeColor==Color.FromArgb(230,126,34)&&added.ForeColor==current.ForeColor&&current.Font.Bold&&added.Font.Bold&&Math.Abs(current.Font.SizeInPoints-10.8F)<0.01F,"两行数量橙色加粗且字号加大20%："+count+" / "+size.Width);
    var buttons=ArchiveControls(form).OfType<Button>().ToArray();
    Check(buttons.Length==3&&buttons.All(b=>b.Width==105&&b.Height==42&&Math.Abs(b.Font.SizeInPoints-12.6F)<0.01F&&TextRenderer.MeasureText(b.Text,b.Font).Width<=b.ClientSize.Width),"三个归属按钮及文字均为原70%，文字完整："+size.Width);
    var execute=buttons.Single(b=>b.Text=="确认归属");var confirm=buttons.Single(b=>b.Text=="确认");Check(!execute.Enabled,"确认项目之前归属操作仍禁用");
    Screenshot(form,"admission-"+(count.HasValue?count.ToString():"unknown")+"-"+size.Width+"-before");confirm.PerformClick();Application.DoEvents();Check(execute.Enabled&&confirm.Text=="已确认"&&!confirm.Enabled,"原确认流程和按钮状态保留");
    Screenshot(form,"admission-"+(count.HasValue?count.ToString():"unknown")+"-"+size.Width+"-after");
    records.Add(new{Existing=count,Added=1194,Width=size.Width,CountFont=added.Font.SizeInPoints,ButtonFont=execute.Font.SizeInPoints});form.Close();
   }
  }
  foreach(var title in new[]{"确认存档","取消存档"})using(var button=new Button{Text=title}){
   var active=title=="确认存档"?Color.FromArgb(21,126,77):Color.FromArgb(230,126,34);var gray=Color.FromArgb(224,224,224);
   Type("ArchiveCommandAppearance").GetMethod("Apply",Flags).Invoke(null,new object[]{button,(Color?)active,(Color?)gray});
   button.Enabled=false;Check(button.BackColor==gray,title+"不可点击时显示灰底");button.Enabled=true;Check(button.BackColor==active,title+"重新可用后恢复操作颜色");
   using(var parent=new Panel()){parent.Controls.Add(button);parent.Enabled=false;Check(button.BackColor==gray,title+"因父页面禁用时也显示灰底");parent.Enabled=true;Check(button.BackColor==active,title+"父页面恢复后颜色同步恢复");parent.Controls.Remove(button);}
  }
  File.WriteAllText(Path.Combine(output,"render-summary.json"),new JavaScriptSerializer().Serialize(records));
 }
}
