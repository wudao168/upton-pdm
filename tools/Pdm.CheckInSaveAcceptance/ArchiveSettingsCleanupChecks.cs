using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveSettingsCleanupScenario(Form host,string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var cleaner=Type("ArchiveTemporaryCopyCleaner");var rootField=cleaner.GetField("directory",Flags);var previous=rootField.GetValue(null);
  object Clean(Func<bool> busy)=>cleaner.GetMethod("Clean",Flags).Invoke(null,new object[]{busy});
  string Copy(string root,string name="PART.SLDPRT"){
   var path=Path.Combine(root,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),name);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,new byte[8192]);return path;
  }
  try
  {
   foreach(var mode in new[]{"normal","busy","becomes-busy","locked","readonly","nested","unknown"})
   {
    var folder=Path.Combine(output,mode+"-"+Guid.NewGuid().ToString("N"));var root=Path.Combine(folder,"checkin");rootField.SetValue(null,root);
    var copy=Copy(root);var outside=Path.Combine(folder,"original.SLDPRT");File.WriteAllText(outside,"source remains intact");
    if(mode=="readonly")File.SetAttributes(copy,FileAttributes.ReadOnly);
    if(mode=="nested")Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(copy),"unknown-child"));
    var unknown=Path.Combine(root,"not-an-owned-copy","important.txt");if(mode=="unknown"){Directory.CreateDirectory(Path.GetDirectoryName(unknown));File.WriteAllText(unknown,"keep");}
    var calls=0;Func<bool> busy=()=>mode=="busy"||mode=="becomes-busy"&&++calls>=2;
    object result=null;Exception failure=null;
    using(var held=mode=="locked"?new FileStream(copy,FileMode.Open,FileAccess.Read,FileShare.Read):null)
     try{result=Clean(busy);}catch(TargetInvocationException error){failure=error.InnerException;}
    var delete=mode=="normal"||mode=="unknown";
    Check(File.Exists(copy)!=delete,mode+"：仅删除可确认且未使用的临时副本");
    Check(File.ReadAllText(outside)=="source remains intact",mode+"：原图和工作区目录不受影响");
    if(delete)Check((int)Get(result,"DeletedFiles")==1&&(long)Get(result,"FreedBytes")==8192,"清理数量及释放字节按实际删除统计");
    if(mode=="busy"||mode=="becomes-busy")Check(failure is InvalidOperationException,"操作进行中或清理前新操作开始时拒绝删除");
    if(mode=="locked"||mode=="readonly"||mode=="nested")Check((int)Get(result,"SkippedItems")>0,"被占用、只读及未知子目录保留并计入跳过");
    if(mode=="readonly"){Check((File.GetAttributes(copy)&FileAttributes.ReadOnly)!=0,"不改写只读文件属性");File.SetAttributes(copy,FileAttributes.Normal);}
    if(mode=="unknown")Check(File.ReadAllText(unknown)=="keep","非插件GUID结构中的文件保留");
   }
   var missing=Path.Combine(output,"missing-"+Guid.NewGuid().ToString("N"));rootField.SetValue(null,missing);var empty=Clean(()=>false);
   Check((int)Get(empty,"DeletedFiles")==0&&!Directory.Exists(missing),"目录不存在时不创建或删除其他位置");
   var activeRoot=Path.Combine(output,"active-plugin-"+Guid.NewGuid().ToString("N"));rootField.SetValue(null,activeRoot);var activeCopy=Copy(activeRoot);
   var addin=New("PdmAddin");SetField(addin,"checkInOperationInProgress",1);Exception busyFailure=null;
   try{await (Task)TreeCall(addin,"CleanArchiveTemporaryCopiesAsync");}catch(Exception error){busyFailure=error;}
   Check(busyFailure is InvalidOperationException&&File.Exists(activeCopy),"插件真实操作标记阻止清理活动存档副本");
   var linkRoot=Path.Combine(output,"links-"+Guid.NewGuid().ToString("N"));var external=Path.Combine(linkRoot,"outside");Directory.CreateDirectory(external);var sentinel=Path.Combine(external,"source.SLDPRT");File.WriteAllText(sentinel,"keep source");
   var rootPath=Path.Combine(linkRoot,"checkin");var document=Path.Combine(rootPath,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(document);var link=Path.Combine(document,Guid.NewGuid().ToString("N"));
   var command="New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+external.Replace("'","''")+"' | Out-Null";
   using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\PowerShell\7\pwsh.exe","-NoProfile -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden}))
   {if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new Exception("目录链接回归准备失败");}
   rootField.SetValue(null,rootPath);var linked=Clean(()=>false);
   Check(File.ReadAllText(sentinel)=="keep source"&&Directory.Exists(link)&&(int)Get(linked,"SkippedItems")>0,"不沿目录链接删除包外原图");
   rootField.SetValue(null,link);Exception linkFailure=null;try{Clean(()=>false);}catch(TargetInvocationException error){linkFailure=error.InnerException;}
   Check(linkFailure is InvalidOperationException&&File.ReadAllText(sentinel)=="keep source","临时根目录自身为链接时停止清理");
   Directory.Delete(link,false);
  }
  finally{rootField.SetValue(null,previous);}
  var constructor=Type("PluginSettingsDialog").GetConstructors(Flags).Single();
  var arguments=constructor.GetParameters().Select(parameter=>{
   if(!typeof(Delegate).IsAssignableFrom(parameter.ParameterType))return Activator.CreateInstance(parameter.ParameterType,true);
   var invoke=parameter.ParameterType.GetMethod("Invoke");object value;
   if(invoke.ReturnType.IsGenericType&&invoke.ReturnType.GetGenericTypeDefinition()==typeof(Task<>)){
    var resultType=invoke.ReturnType.GetGenericArguments()[0];value=typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null,new[]{Activator.CreateInstance(resultType,true)});
   }else value=Activator.CreateInstance(invoke.ReturnType,true);
   return (object)Expression.Lambda(parameter.ParameterType,Expression.Constant(value,invoke.ReturnType),invoke.GetParameters().Select(parameter=>Expression.Parameter(parameter.ParameterType))).Compile();
  }).ToArray();
  using(var dialog=(Form)constructor.Invoke(arguments))
  {
   dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show(host);
   var property=dialog.GetType().GetProperty("CleanTemporaryCopies",Flags);var resultType=property.PropertyType.GetMethod("Invoke").ReturnType.GetGenericArguments()[0];
   var completion=Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType));var task=Get(completion,"Task");
   property.SetValue(dialog,Expression.Lambda(property.PropertyType,Expression.Constant(task,property.PropertyType.GetMethod("Invoke").ReturnType)).Compile());
   var button=(Button)Field(dialog,"cleanupCopiesButton");var status=(Label)Field(dialog,"cleanupCopiesStatus");
   var cleanup=(Task)TreeCall(dialog,"CleanTemporaryCopiesAsync");
   Check(!button.Enabled&&!((Button)Field(dialog,"saveButton")).Enabled,"清理期间按钮禁用，防止重复操作");
   var result=Activator.CreateInstance(resultType);Set(result,"DeletedFiles",3);Set(result,"FreedBytes",1048576L);Set(result,"SkippedItems",1);
   completion.GetType().GetMethod("SetResult").Invoke(completion,new[]{result});await cleanup;
   Check(button.Enabled&&status.Text.Contains("3")&&status.Text.Contains("1.00 MB")&&status.Text.Contains("跳过"),"设置页显示清理数量、释放空间和跳过提示");
   Check(button.Size==((Button)Field(dialog,"saveButton")).Size&&dialog.Font.Size==9F,"沿用现有按钮尺寸及插件字体");
   var bounds=dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
   Check(dialog.ClientRectangle.Contains(bounds),"清理按钮完整显示在设置页内");
   using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"settings-cleanup.png"));}
   dialog.Close();
  }
 }
}
