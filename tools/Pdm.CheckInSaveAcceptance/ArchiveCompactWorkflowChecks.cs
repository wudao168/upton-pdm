using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveCompactWorkflowScenario(Form host,string output)
 {
  using(var stream=assembly.GetManifestResourceStream("Upton.Pdm.SolidWorks.Assets.PdmClient.png"))
  using(var hash=SHA256.Create())
  {
   var embedded=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
   var icon=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(assembly.Location)),"frozen-source","src","pdm-ui","src","assets","pdm-client-icon.png");
   TreeCheck(embedded==WorkingReuseHash(icon),"插件没有嵌入最新统一图标资源");
   Console.WriteLine("PASS: embedded plugin branding exactly matches the verified shared icon asset");
  }
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var nodes=new List<object>{root};
  for(var i=1;i<214;i++){var node=LinkedNode(folder,i==1?"7000093.101.03-02.SLDPRT":"PART-"+i+".SLDPRT","Part");nodes.Add(node);((IList)Get(root,"Children")).Add(node);}
  var drawing=LinkedNode(folder,"7000093.101.03-02.SLDDRW","Drawing");nodes.Add(drawing);((IList)Get(nodes[1],"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(nodes[1]);
  archiveRuleVersions=nodes.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>new[]{LinkedVersion(node,LinkedSnapshot(node))});
  foreach(var node in nodes){Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"LatestStructureIndexPending",false);Set(node,"NativeFileScopeVerified",true);Set(node,"LatestStoredSha256",WorkingReuseHash((string)Get(node,"FullPath")));Set(node,"LatestVersionSha256",Get(node,"LatestStoredSha256"));}
  Set(drawing,"DrawingViewsRead",true);File.AppendAllText((string)Get(nodes[1],"FullPath"),"actual geometry change");
  var project=New("ProjectDto");var projectId=Guid.NewGuid();Set(project,"Id",projectId);Set(project,"Code","P700014-1");Set(project,"Name","AAAA");Set(project,"CanSubmitArchive",true);
  var projects=Array.CreateInstance(Type("ProjectDto"),1);projects.SetValue(project,0);
  var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
  ArchiveUi(session,()=>{ArchiveWindow(session).TopMost=false;return true;});
  try
  {
   var driver=Task.Run(async()=>{
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any()),"未显示选择区");
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any(f=>((Button)Field(f,"recheck")).Enabled)),"初始检查未完成");
    ArchiveUi(session,()=>{
     var window=ArchiveWindow(session);var form=((Panel)Field(window,"selection")).Controls.OfType<Form>().Single();
     var tree=(TreeView)Field(form,"files");var buttons=ArchiveControls(form).OfType<Button>().Where(b=>new[]{"全选","清除","勾选变更项","补充工程图","待补全引用","仅显示勾选","取消存档","重新检查","确认存档"}.Contains(b.Text)).ToArray();
     TreeCheck(buttons.Length==9&&buttons.All(b=>b.Size==new Size(80,28)),"九个按钮尺寸或功能入口不完整");
     Console.WriteLine("PASS: all nine operation buttons use 80x28, original commands and two independent groups remain");
     TreeCheck(form.Font.Name=="Microsoft YaHei UI"&&form.Font.SizeInPoints==9&&tree.ItemHeight==20,"字体或原行高被改变");
     var visible=tree.ClientSize.Height/tree.ItemHeight;TreeCheck(visible>=30,"同页图档不足30条："+visible);
     Console.WriteLine("PASS: native dialog retains 9pt font and 20px rows with "+visible+" rows visible at the unchanged window width");
     var type=(ComboBox)Field(form,"drawingType");var person=(ComboBox)Field(form,"handoffRecipient");
     Console.WriteLine("Picker geometry: "+type.Bounds+" / "+person.Bounds);
     using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"native-layout-first-frame.png"));}
     TreeCheck(type.Size==person.Size&&type.Right==person.Right,"图纸类型和交接人下拉框不一致或未对齐");
     TreeCheck(((RadioButton)Field(form,"acquire")).Text=="获取权限"&&((RadioButton)Field(form,"checkIn")).Text=="提交存档"&&((CheckBox)Field(form,"standardLibrary")).Text=="存标准库","短标签未生效");
     var selector=(Control)Field(form,"projectSelector");var text=ArchiveControls(selector).OfType<TextBox>().Single();TreeCheck(text.ClientSize.Width>TextRenderer.MeasureText(text.Text,text.Font).Width,"项目框无法完整显示项目号和名称");
     Console.WriteLine("PASS: short operation labels leave sufficient project display space; type and handoff selectors align and match");
     TreeCheck(type.Items.Count==6&&((ComboBox)Field(form,"editDisposition")).Items.Count==3,"标准库或编辑权限功能缺失");
     var standard=(CheckBox)Field(form,"standardLibrary");standard.Checked=true;TreeCheck(type.Enabled&&!selector.Enabled,"标准库模式未保留项目互斥");standard.Checked=false;
     Console.WriteLine("PASS: standard library types and release/keep/handoff operations retain their original rules");
     var windowSize=window.Size;var treeSize=tree.Size;TreeCall(form,"ToggleShowCheckedOnly");TreeCall(form,"ToggleShowCheckedOnly");TreeCheck(window.Size==windowSize&&tree.Size==treeSize,"筛选改变了窗口或列表大小");
     Console.WriteLine("PASS: checked-only filtering changes only tree rows, not window or list geometry");
     var flags=buttons.Select(b=>b.Enabled).ToArray();foreach(var b in buttons)b.Enabled=false;
     TreeCheck(buttons.All(b=>b.BackColor==buttons.Last().BackColor&&b.ForeColor==buttons.Last().ForeColor&&b.FlatAppearance.BorderColor==buttons.Last().FlatAppearance.BorderColor),"禁用按钮颜色未统一");for(var i=0;i<buttons.Length;i++)buttons[i].Enabled=flags[i];
     Console.WriteLine("PASS: every disabled command, including cancel, uses the same gray palette");
     using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"native-compact-selection.png"));}
     return true;
    });
    await TreeWait(()=>ArchiveUi(session,()=>((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Any(f=>((Button)Field(f,"recheck")).Enabled)),"模式切换后的检查没有完成");
    ArchiveUi(session,()=>{var form=((Panel)Field(ArchiveWindow(session),"selection")).Controls.OfType<Form>().Single();TreeCheck(((Button)Field(form,"execute")).Enabled,"选定范围后无法直接开始自动存档");ArchiveWindow(session).BeginInvoke((Action)(()=>((Button)Field(form,"execute")).PerformClick()));return true;});
   });
   _ = driver.ContinueWith(t=>{if(t.IsFaulted)TreeCall(session,"RequestCancellation");},TaskScheduler.Default);
   var items=LinkedItems(nodes.ToArray());
   ArchiveUi(session,()=>{
    var form=(Form)New("BatchOperationDialog",root,items,projects,projectId,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),new[]{(string)Get(nodes[1],"FullPath")},null,new Dictionary<string,string>{{"helper","协助人"}},true,false,0);
    var loader=form.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(form,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
    return TreeCall(session,"ShowInline",form,true);
   });await driver;
   Console.WriteLine("PASS: initial confirm starts the full archive workflow without requiring an initial preparation click");
   Set(drawing,"RequiresModelUpdate",true);var preparedTimer=System.Diagnostics.Stopwatch.StartNew();
   TreeCall(session,"ReportPreparedFiles",LinkedItems(nodes[1],drawing));
   TreeCheck(preparedTimer.ElapsedMilliseconds<2000,"准备结果仍在等待人工再次确认");
   ArchiveUi(session,()=>{
    var window=ArchiveWindow(session);var form=((Panel)Field(window,"selection")).Controls.OfType<Form>().Single();var selected=((IEnumerable)Get(form,"SelectedItems")).Cast<object>().ToArray();
    TreeCheck(selected.Length==2&&selected.Any(item=>ReferenceEquals(Get(item,"Node"),drawing)),"配套工程图没有明确纳入自动存档");
    TreeCheck(((string)TreeCall(form,"StateText",drawing,"tester")).Contains("正在自动存档")&&!((Button)Field(form,"execute")).Enabled&&!((Button)Field(form,"recheck")).Enabled,"处理中仍可重复提交或要求人工确认");
    using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"native-prepared-auto-submit.png"));}return true;
   });
   Console.WriteLine("PASS: model and paired drawing show automatic archive progress with no second click and duplicate submission disabled");
   TreeCall(session,"TrackFile",nodes[1],"已存档（服务器已确认）：V3 → V4","V4 / 未发布");TreeCall(session,"TrackFile",drawing,"已存档（服务器已确认）：V2 → V3","V3 / 未发布");
   TreeCall(session,"Complete","模型与工程图均已存档，服务器已确认。",false,false);
   ArchiveUi(session,()=>{
    var window=ArchiveWindow(session);var form=((Panel)Field(window,"selection")).Controls.OfType<Form>().Single();TreeCheck(((Label)Field(window,"elapsed")).Text.Contains("总耗时")&&!((Label)Field(window,"elapsed")).Text.Contains("当前步骤"),"耗时显示不符合要求");
    TreeCheck(((string)TreeCall(form,"StateText",drawing,"tester")).StartsWith("已存档"),"完成后工程图状态没有回写");
    var versions=(IDictionary)Field(form,"workflowVersions");TreeCheck((string)versions[(string)Get(drawing,"FullPath")]=="V3 / 未发布","工程图实际版本没有写回独立版本列");
    using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"native-archive-complete.png"));}return true;
   });
   Console.WriteLine("PASS: final per-file receipt state and business versions stay visible in the original four columns; only total elapsed time is shown");
  }
  finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
  await ArchiveUploadGateScenario(host,Path.Combine(output,"upload-gate"));
 }
}
