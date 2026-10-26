using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using Environment=System.Environment;

internal static class NativeChecks
{
 const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
 static Assembly Plugin;static ISldWorks App;static string Stage;static object Addin;static int OwnedPid;static long OwnedStart;
 [STAThread] static int Main(string[] args)
 {
  if(args.Length<4){Console.Error.WriteLine("参数：候选插件DLL、验证模式、新隔离目录、样本或上下文文件。");return 2;}
  Console.OutputEncoding=new UTF8Encoding(false);
  Stage=Path.GetFullPath(args[2]);Directory.CreateDirectory(Stage);
  var dll=Path.GetFullPath(args[0]);AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(Path.GetDirectoryName(dll),new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
  try{Plugin=Assembly.LoadFrom(dll);}catch(Exception error){Console.Error.WriteLine("无法加载候选插件："+error.Message);return 2;}var result=0;
  Environment.SetEnvironmentVariable("PDM_ADDIN_VERBOSE_SCAN","1");
  if(args[1]=="journalscopehold")
  {
   using((IDisposable)T("ArchiveNativeJournalScope").GetMethod("Begin",Flags).Invoke(null,new object[]{args[3],@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe"})){File.WriteAllText(Path.Combine(Stage,"held"),"ready");Thread.Sleep(60000);}return 0;
  }
  if(args[1]=="journalscoperegression")
  {
   try{JournalScopeRegression();return 0;}catch(Exception error){File.WriteAllText(Path.Combine(Stage,"journal-regression-error.txt"),error.ToString());return 1;}
  }
  if(args[1]=="registeredreadersmoke"||args[1]=="automationreadersmoke"||args[1]=="comreadersmoke"||args[1]=="journalreadersmoke")
  {
   try
   {
    Application.OleRequired();
    Action<int,long> startedReader=(id,started)=>{OwnedPid=id;OwnedStart=started;File.WriteAllText(Path.Combine(Stage,"reader-owner.json"),Json.Serialize(new{id,started}));};
    if(args[1]=="journalreadersmoke")
    {
     const string journalValue="SolidWorks Journal Folders";var journal=Path.Combine(args[3],"journal");Directory.CreateDirectory(journal);
     using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks\SOLIDWORKS 2022\ExtReferences",true))
     {
      var before=key.GetValue(journalValue);var kind=key.GetValueKind(journalValue);File.WriteAllText(Path.Combine(Stage,"journal-backup.json"),Json.Serialize(new{before,kind=kind.ToString(),journal}));
      try{key.SetValue(journalValue,journal,Microsoft.Win32.RegistryValueKind.String);App=(ISldWorks)T("PdmAddin").GetMethod("StartNativeArchiveSession",Flags).Invoke(null,new object[]{args[3],startedReader});}
      finally{key.SetValue(journalValue,before,kind);File.WriteAllText(Path.Combine(Stage,"journal-restored.json"),Json.Serialize(new{passed=Equals(before,key.GetValue(journalValue))&&kind==key.GetValueKind(journalValue)}));}
     }
    }
    else if(args[1]=="comreadersmoke")
    {
     var existing=Process.GetProcessesByName("SLDWORKS").Select(p=>p.Id).ToArray();App=(ISldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application",true));var id=App.GetProcessID();
     if(existing.Contains(id)){App=null;throw new Exception("COM后台启动复用了原有实例，未操作原会话");}
     using(var process=Process.GetProcessById(id)){startedReader(id,process.StartTime.ToUniversalTime().Ticks);var request=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(args[3],"native-isolation-request.json")));T("ArchiveNativeProcessWorkspace").GetMethod("PrepareOwnedReader",Flags).Invoke(null,new object[]{App,process,Convert.ToInt32(request["CallerSolidWorksId"]),args[3]});}
    }
    else if(args[1]=="automationreadersmoke")
    {
     var request=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(args[3],"native-isolation-request.json")));var executable=Convert.ToString(request["SolidWorksExecutable"]);
     var launch=new ProcessStartInfo(executable){UseShellExecute=false,Arguments="-Embedding",CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)};launch.EnvironmentVariables["UPLM_NATIVE_ARCHIVE_JOB"]=args[3];
     using(var process=Process.Start(launch))
     {
      startedReader(process.Id,process.StartTime.ToUniversalTime().Ticks);var timer=Stopwatch.StartNew();while(!process.HasExited&&timer.Elapsed<TimeSpan.FromMinutes(2)){App=T("ArchiveNativeProcessWorkspace").GetMethod("FromRunningProcess",Flags).Invoke(null,new object[]{process.Id}) as ISldWorks;if(App!=null)break;Thread.Sleep(200);}
      if(App==null)throw new Exception("COM后台启动未完成");T("ArchiveNativeProcessWorkspace").GetMethod("PrepareOwnedReader",Flags).Invoke(null,new object[]{App,process,Convert.ToInt32(request["CallerSolidWorksId"]),args[3]});
     }
    }
    else App=(ISldWorks)T("PdmAddin").GetMethod("StartNativeArchiveSession",Flags).Invoke(null,new object[]{args[3],startedReader});
    var recognized=(bool)T("PdmAddin").GetMethod("IsNativeArchiveReaderAddin",Flags).Invoke(null,new object[]{args[3],OwnedPid});
    var marker=Path.Combine(args[3],"reader-addin.json");var isolation=Path.Combine(args[3],"native-reader-isolation.json");var isolated=false;
    if(File.Exists(isolation)){var audit=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(isolation));isolated=Convert.ToInt32(audit["ProcessId"])==OwnedPid&&Convert.ToInt64(audit["Started"])==OwnedStart&&Convert.ToInt32(audit["LegacyAddinUnloadResult"])==0&&Convert.ToBoolean(audit["StartupSettingsUnchanged"]);}
    if((!recognized&&!isolated)||App.GetDocumentCount()!=0)throw new Exception("已部署插件未按专用读取模式实际启动");
    if(recognized)File.Copy(marker,Path.Combine(Stage,"registered-reader-addin.json"));else File.Copy(isolation,Path.Combine(Stage,"native-reader-isolation.json"));
    File.WriteAllText(Path.Combine(Stage,"reader-results.json"),Json.Serialize(new{passed=true,registeredReaderGuardVerified=recognized,isolatedLegacyReaderVerified=isolated,noCadDocumentsOpened=true,assemblyId=Plugin.ManifestModule.ModuleVersionId.ToString("N")}));
   }
   catch(Exception error){File.WriteAllText(Path.Combine(Stage,"reader-error.txt"),error.ToString());result=1;}
   finally{if(OwnedPid>0)using(var owned=Process.GetProcessById(OwnedPid))if(!owned.HasExited&&owned.StartTime.ToUniversalTime().Ticks==OwnedStart)owned.Kill();}
   return result;
  }
  if(args[1]=="outputconsoleprobe"||args[1]=="outputconsoleoleprobe"||args[1]=="outputconsolerotprobe"||args[1]=="outputconsolerotlongprobe"||args[1]=="outputconsoleexactprobe"||args[1]=="outputconsolecontextprobe"||args[1]=="outputconsoleswitchprobe")
  {
   int originalPid=0;long originalStarted=0;string readerJob=null;
   try
   {
    if(args[1]=="outputconsoleoleprobe")Application.OleRequired();
    var existing=Process.GetProcessesByName("SLDWORKS").Select(process=>process.Id).ToArray();
    App=(ISldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application",true));
    var pid=App.GetProcessID();if(existing.Contains(pid)){App=null;throw new Exception("控制台验证返回已有实例，未操作");}
    OwnedPid=pid;OwnedStart=Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks;
    if(args[1]=="outputconsolecontextprobe")
    {
     App.UserControl=true;int rootError=0,rootWarning=0;var rootPath=Path.Combine(args[3],"CW0030002.101.SLDASM");
     var root=App.OpenDoc6(rootPath,2,3,"",ref rootError,ref rootWarning);if(root==null||rootError!=0||root.GetPathName()!=rootPath)throw new Exception("未能载入原复杂总装，未进行后台对照");
    }
    if(args[1]=="outputconsolerotprobe"||args[1]=="outputconsolerotlongprobe"||args[1]=="outputconsoleexactprobe"||args[1]=="outputconsolecontextprobe"||args[1]=="outputconsoleswitchprobe")
    {
     originalPid=OwnedPid;originalStarted=OwnedStart;
     var job=Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);readerJob=job;
     var executable=App.GetExecutablePath();if(Directory.Exists(executable))executable=Path.Combine(executable,"SLDWORKS.exe");
     File.WriteAllText(Path.Combine(job,"native-isolation-request.json"),Json.Serialize(new{JobId=Path.GetFileName(job),CallerSolidWorksId=OwnedPid,SolidWorksExecutable=executable,Inventory=new string[0],Requested=new Dictionary<string,string>()}));
     App=(ISldWorks)T("ArchiveNativeProcessWorkspace").GetMethod("StartReader",Flags).Invoke(null,new object[]{job,new Action<int,long>((id,start)=>{OwnedPid=id;OwnedStart=start;using(var worker=Process.GetCurrentProcess())File.WriteAllText(Path.Combine(job,"native-owner.json"),Json.Serialize(new{Id=id,Started=start,WorkerId=worker.Id,WorkerStarted=worker.StartTime.ToUniversalTime().Ticks}));})});
    }
    File.WriteAllText(Path.Combine(Stage,"owned-solidworks-"+args[1]+".json"),Json.Serialize(new{pid,start=OwnedStart,mode=args[1]}));
    if(args[1]=="outputconsoleswitchprobe")
    {
     var rootStage=Stage;Stage=Path.Combine(rootStage,"preparation");Directory.CreateDirectory(Stage);OutputBatchProbe(args[4],true,true,false);Stage=rootStage;
     App=(ISldWorks)T("ArchiveNativeProcessWorkspace").GetMethod("FreshValidationReader",Flags).Invoke(null,new object[]{readerJob});
    }
    OutputBatchProbe(args[4],true,args[1]=="outputconsolerotlongprobe"||args[1]=="outputconsoleexactprobe"||args[1]=="outputconsolecontextprobe"||args[1]=="outputconsoleswitchprobe",args[1]!="outputconsoleexactprobe"&&args[1]!="outputconsolecontextprobe"&&args[1]!="outputconsoleswitchprobe");
   }
   catch(Exception error){File.WriteAllText(Path.Combine(Stage,"console-error.txt"),error.ToString());Console.WriteLine("FAIL: "+error);result=1;}
   finally
   {
    if(App!=null&&OwnedPid>0){using(var process=Process.GetProcessById(OwnedPid))if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==OwnedStart)process.Kill();App=null;}
    if(originalPid>0){using(var original=Process.GetProcessById(originalPid))if(!original.HasExited&&original.StartTime.ToUniversalTime().Ticks==originalStarted)original.Kill();}
   }
   return result;
  }
  using(var monitor=new System.Threading.Timer(_=>{try{File.WriteAllText(Path.Combine(Stage,"live-diagnostics.txt"),(string)T("PluginDiagnosticLog").GetMethod("Snapshot",Flags).Invoke(null,null));}catch{}},null,1000,2000))
  using(var host=new Form{Opacity=0,ShowInTaskbar=false})
  {
   host.Shown+=async (_,__)=>{
    try
    {
     OleFilter.Register();var existing=Process.GetProcessesByName("SLDWORKS").Select(p=>p.Id).ToArray();
     if(args[1].StartsWith("registeredstartupsmoke",StringComparison.Ordinal))
     {
      var executable=@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
      using(var normal=Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable),WindowStyle=ProcessWindowStyle.Hidden}))
      {
       OwnedPid=normal.Id;OwnedStart=normal.StartTime.ToUniversalTime().Ticks;
       var timer=Stopwatch.StartNew();while(!normal.HasExited&&timer.Elapsed<TimeSpan.FromMinutes(2)){App=T("ArchiveNativeProcessWorkspace").GetMethod("FromRunningProcess",Flags).Invoke(null,new object[]{normal.Id}) as ISldWorks;if(App!=null)break;await Task.Delay(200);}
       if(App==null)throw new Exception("正常SolidWorks启动未连接，未执行其他操作");
      }
     }
     else App=(ISldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application",true));
     var pid=App.GetProcessID();if(existing.Contains(pid))
     {
      if(args[1]=="leafaudit"&&args.Length>4&&File.Exists(args[4]))
      {
       var expected=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[4]));
       if(Convert.ToInt32(expected["pid"])==pid&&Convert.ToInt64(expected["start"])==Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks)
       {LeafAudit(args[3]);App=null;return;}
      }
      App=null;throw new Exception("COM 返回了原有 SolidWorks 实例，未接管或关闭。");
     }
     OwnedPid=pid;OwnedStart=Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks;
     File.WriteAllText(Path.Combine(Stage,"owned-solidworks-"+args[1]+".json"),Json.Serialize(new{pid,start=OwnedStart,mode=args[1]}));
     if(args[1].StartsWith("registeredstartupsmoke",StringComparison.Ordinal)){await RegisteredStartupSmoke(args[1]=="registeredstartupsmokeautomation",args[1]=="registeredstartupsmokecom",args[1]=="registeredstartupsmokejournal");return;}
     if(args[1]=="managedreopen")
     {
      var readerJob=Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(readerJob);
      using(var owned=Process.GetProcessById(pid))T("ArchiveNativeProcessWorkspace").GetMethod("PrepareOwnedReader",Flags).Invoke(null,new object[]{App,owned,0,readerJob});
      var audit=Path.Combine(readerJob,"native-reader-isolation.json");if(File.Exists(audit))File.Copy(audit,Path.Combine(Stage,"native-reader-isolation.json"));
     }
     var interactiveSource=args[1]=="workspacevisible"||args[1].StartsWith("host",StringComparison.Ordinal)||args[1]=="httpfirstarchive"||args[1]=="httpflow";
     App.UserControl=interactiveSource;if(!interactiveSource)App.Visible=false;
     if(args[1]=="httpfirstarchive"||args[1]=="httpflow")App.Visible=true;
     if(args[1]=="managedreopen")App.UserControlBackground=true;
     if(args[1]=="addinprobe"||args[1]=="addinunloadprobe")
     {
      await Task.Delay(15000);int probeError=0,probeWarning=0;var probe=App.OpenDoc6(Path.Combine(args[3],"7000093.101.03-02.SLDPRT"),1,3,"",ref probeError,ref probeWarning);await Task.Delay(15000);
      var byProgId=App.GetAddInObject("Upton.Pdm.SolidWorks.Addin");var byGuid=App.GetAddInObject("{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
      if(args[1]=="addinunloadprobe")
      {
       var startup=@"SOFTWARE\SOLIDWORKS\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
       using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startup))
       using(var machine=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SOLIDWORKS\AddIns\{BCFD8A8A-472B-42E2-AC62-58BC17773650}"))
       {
        var before=key?.GetValue("");var machineBefore=machine?.GetValue("");var codeBase=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32")?.GetValue("CodeBase") as string;
        var installed=new Uri(codeBase).LocalPath;var unload=App.UnloadAddIn(installed);await Task.Delay(10000);
        var after=key?.GetValue("");var machineAfter=machine?.GetValue("");File.WriteAllText(Path.Combine(Stage,"addin-unload.json"),Json.Serialize(new{pid,installed,unload,before,after,machineBefore,machineAfter,unchanged=Equals(before,after)&&Equals(machineBefore,machineAfter)}));
        if(!Equals(before,after)||!Equals(machineBefore,machineAfter))
        {
         using(var restore=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startup,true)){if(before==null)restore?.DeleteValue("",false);else restore?.SetValue("",before,Microsoft.Win32.RegistryValueKind.DWord);}
         using(var restore=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SOLIDWORKS\AddIns\{BCFD8A8A-472B-42E2-AC62-58BC17773650}",true)){if(machineBefore==null)restore?.DeleteValue("",false);else restore?.SetValue("",machineBefore,Microsoft.Win32.RegistryValueKind.DWord);}
         throw new Exception("专用实例卸载改变了自动启动状态，已恢复原值，不采用此方案");
        }
       }
      }
      File.WriteAllText(Path.Combine(Stage,"addin-lookup.json"),Json.Serialize(new{pid,probeError,partOpened=probe!=null,progIdFound=byProgId!=null,guidFound=byGuid!=null,progType=byProgId?.GetType().FullName,guidType=byGuid?.GetType().FullName}));
      Console.WriteLine("PASS: 只读插件对象查找完成，未卸载或修改启动设置");return;
     }
     var interactive=App.GetAddInObject("{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
     if(interactive!=null)throw new Exception("独立实例加载了交互插件，已停止测试；未修改插件启动设置。");
     T("PdmDocumentIdentityStore").GetField("bindingDirectory",Flags).SetValue(null,Path.Combine(Stage,"bindings-"+args[1]));
     T("PdmDocumentIdentityStore").GetField("workspaceRootResolver",Flags).SetValue(null,new Func<string>(()=>Stage));
     Addin=New("PdmAddin");F(Addin,"application",App);F(Addin,"scanner",New("SolidWorksReferenceTreeScanner",App));
     using(var pane=(Control)New("PdmTaskPaneControl","图档完整性隔离验证"))
     {
      pane.CreateControl();F(Addin,"taskPaneControl",pane);
      if(args[1]=="h620workspace"){H620Workspace(args[3]);return;}
      if(args[1]=="firstarchive"){FirstArchive(args[3]);return;}
      if(args[1]=="lhhworkspace"){Workspace(args[3],false,"LHH30CA2R1000Z0C-E20E20(1).SLDASM");return;}
      if(args[1]=="importprobe"){ImportProbe(args[3]);return;}
      if(args[1]=="importpreflight"){ImportPreflight(args[3]);return;}
      if(args[1]=="workspacevisible"){Workspace(args[3]);return;}
      if(args[1]=="workspacecold"){App.UserControlBackground=true;Workspace(args[3],false,null,false);return;}
      if(args[1]=="resourceprobe"){Resources(args[3],"7000093.101.03-02.SLDDRW");return;}
      if(args[1].StartsWith("drawingcase-",StringComparison.Ordinal)){DrawingCase(args[3],args[1].Substring(12));return;}
      if(args[1]=="hostworkspace"||args[1]=="hosth620"||args[1]=="hostfesto"||args[1]=="hostsurface"||args[1]=="hostprofile"||args[1]=="hostassembly"||args[1]=="hostcq"||args[1]=="hostfailedpart"||args[1]=="hostfeaturepart"||args[1]=="hostall"||args[1]=="hostseries"||args[1]=="hostlhh"){await HostWorkspace(args[3],args[1]);return;}
      if(args[1]=="h620reopen"){ReopenH620(args[3]);return;}
      if(args[1]=="oleprobe"||args[1]=="olerail"){OleProbe(args[3],args[1]=="olerail");return;}
      if(args[1]=="olefile"){OleProbe(args[3],false,Path.GetFileName(args[4]));return;}
      if(args[1]=="olecatalog"){OleCatalog(args[3]);return;}
      if(args[1]=="olecatalogtail"){OleCatalog(args[3],true);return;}
      if(args[1]=="tableprobe"){TableProbe(args[3]);return;}
      if(args[1]=="tableeditprobe"){TableProbe(args[3],true);return;}
      if(args[1]=="tableproductionprobe"){TableProbe(args[3],false,true);return;}
      if(args[1]=="tablefileprobe")
      {
       using(var registration=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32")){if(App.UnloadAddIn(new Uri((string)registration.GetValue("CodeBase")).LocalPath)!=0)throw new Exception("未能停止专用实例交互插件");}
       App.UserControlBackground=true;App.CommandInProgress=true;TableProbe(args[3],false,true,Path.GetFileName(args[4]));App.CommandInProgress=false;return;
      }
      if(args[1]=="lifecycleprobe"||args[1]=="featurelifecycleprobe"){await LifecycleProbe(args[3],args[1]=="featurelifecycleprobe");return;}
      if(args[1]=="packedfileprobe"){await LifecycleProbe(args[3],true,args[4]);return;}
      if(args[1]=="outputbatchprobe"||args[1]=="outputbatchgcprobe"){OutputBatchProbe(args[4],args[1]=="outputbatchgcprobe");return;}
      if(args[1]=="resourcebaselineprobe"||args[1]=="resourcecommandprobe"){await LifecycleProbe(args[3],true,null,args[1]=="resourcecommandprobe",12);return;}
      if(args[1]=="mixedlifecycleprobe")
      {
       using(var registration=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32")){if(App.UnloadAddIn(new Uri((string)registration.GetValue("CodeBase")).LocalPath)!=0)throw new Exception("未能停止专用实例交互插件");}
       App.UserControlBackground=true;TableProbe(args[3],false,true);App.CloseAllDocuments(true);await LifecycleProbe(args[3],true);return;
      }
      if(args[1]=="geometryfile"){GeometryFile(args[3]);return;}
      if(args[1]=="detachfile"){GeometryFile(args[3],true);return;}
      if(args[1]=="breakfalsefile"){BreakFalseFile(args[3]);return;}
      if(args[1]=="packgoprobe"){PackGoProbe(args[3]);return;}
      if(args[1]=="preparemanagedfixture"){PrepareManagedFixture(args[3]);return;}
      if(args[1]=="managedreopen"){ManagedReopen(args[4]);return;}
      if(args[1]=="managedsourceprobe"){ManagedSourceProbe(args[4]);return;}
      if(args[1]=="lhhbreakprobe"){LhhBreakProbe(args[3]);return;}
      if(args[1]=="drawingcachedprobe"){var outline=CachedDrawingOutlines(Path.Combine(args[3],"7000093.101.03-02.SLDDRW"));File.WriteAllText(Path.Combine(Stage,"saved-drawing-outline.txt"),outline);Console.WriteLine("PASS: 未载入模型即读取工程图已保存的实际轮廓");return;}
      if(args[1]=="hostroot"){await HostWorkspace(args[3],args[1],Path.GetFileName(args[4]));return;}
      if(args[1]=="archivescopeprobe"){ArchiveScopeProbe(args[3]);return;}
      if(args[1]=="httpfirstarchive"){await HttpFirstArchive(args[3],args[4]);return;}
      if(args[1]=="assemblygeometry"){AssemblyGeometry(args[3]);return;}
      if(args[1]=="leafaudit"){LeafAudit(args[3]);return;}
      if(args[1]=="smartprobe"){SmartProbe(args[3]);return;}
      if(args[1]=="launchprobe"){await LaunchProbe();return;}
      if(args[1]=="referenceprobe")ReferenceProbe(args[3]);else if(args[1]=="prepare")Prepare(args[3]);else if(args[1]=="verify")Verify(args[3]);else if(args[1]=="dependencies")Dependencies(args[3]);else if(args[1]=="isolate")Isolate(args[3]);else if(args[1]=="workspace")Workspace(args[3]);else if(args[1]=="drawings")Workspace(args[3],true);else if(args[1]=="profile")Workspace(args[3],false,"标准机架后安全门型材框.SLDPRT");else if(args[1]=="resources")Resources(args[3]);else if(args[1]=="geometryprobe")GeometryProbe(args[3]);else if(args[1]=="safeguards")Safeguards(args[3]);else if(args[1]=="httpflow")await HttpFlow(args.Length>4&&File.Exists(args[4])?args[4]:args[3]);else throw new Exception("未知验证阶段");
     }
    }
    catch(Exception error){Console.WriteLine("FAIL: "+error);File.WriteAllText(Path.Combine(Stage,"native-"+args[1]+"-error.txt"),error.ToString());File.WriteAllText(Path.Combine(Stage,"diagnostics.txt"),(string)T("PluginDiagnosticLog").GetMethod("Snapshot",Flags).Invoke(null,null));result=1;}
    finally
    {
     if(App!=null&&OwnedPid>0)
     {
      try{var process=Process.GetProcessById(OwnedPid);if(process.StartTime.ToUniversalTime().Ticks==OwnedStart)process.Kill();}
      catch(Exception cleanup){Console.WriteLine("独立实例清理记录："+cleanup.Message);}
      App=null;
     }
     OleFilter.Revoke();host.Close();
    }
   };Application.Run(host);
  }
  return result;
 }
 static void Prepare(string fixture)
 {
  var raw=Path.Combine(Stage,"View","raw-source");Directory.CreateDirectory(raw);
  foreach(var folder in new[]{"working","historical"})foreach(var source in Directory.GetFiles(Path.Combine(fixture,folder)))
  {
   var target=Path.Combine(raw,Path.GetFileName(source));if(File.Exists(target))throw new Exception("隔离源文件重复："+target);
   File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);
  }
  var available=Directory.GetFiles(raw).ToList();var copied=new List<object>();
  for(var i=0;i<available.Count;i++)
  {
   var owner=available[i];if(!Native(owner))continue;
   var dependencies=App.GetDocumentDependencies2(owner,false,false,false) as Array;if(dependencies==null)continue;
   for(var j=1;j<dependencies.Length;j+=2)foreach(var source in (Convert.ToString(dependencies.GetValue(j))??"").Split('|'))
   {
    var target=Path.Combine(raw,Path.GetFileName(source));if(File.Exists(target)||!File.Exists(source))continue;
    if((bool)T("NativeArchiveManifest").GetMethod("IsInterconnectCachePath",Flags).Invoke(null,new object[]{source})
     ||(bool)T("PdmAddin").GetMethod("IsSolidWorksTemporaryVirtualComponentPath",Flags).Invoke(null,new object[]{source}))continue;
    File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);available.Add(target);copied.Add(new{source,target,sha256=Hash(target)});
   }
  }
  var project=Guid.NewGuid();var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"Id",Guid.NewGuid());
  var list=(IList)Activator.CreateInstance(T("ControlledOpenManifestDto").GetProperty("Files").PropertyType);
  var configurations=new Dictionary<string,string>();
  foreach(var path in available)
  {
   var name=Path.GetFileName(path);var cfg=Native(path)?App.GetActiveConfigurationName(path):"";configurations[name]=cfg??"";
   var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Sha256",Hash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Configuration",cfg??"");Set(file,"Revision","W1");list.Add(file);
   Bind(path,file,project);
  }
  Set(manifest,"Files",list);
  var root=Path.Combine(raw,"CW0030002.10.SLDASM");
  T("ControlledReferenceMaterializer").GetMethod("Prepare",Flags).Invoke(null,new object[]{App,root,manifest});
  var catalog=available.Select(path=>new{fileName=Path.GetFileName(path),path,configuration=configurations[Path.GetFileName(path)],sha256=Hash(path),length=new FileInfo(path).Length}).ToArray();
  File.WriteAllText(Path.Combine(Stage,"native-source-catalog.json"),Json.Serialize(new{raw,catalog,extraSources=copied}));
  Console.WriteLine("PASS: 原报错大装配实际文件已只读复制并重定位到隔离源目录；文件数="+catalog.Length);
 }
 static void Verify(string export)
 {
  var source=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(export));
  var offline=Convert.ToString(source["rawSource"]);if(Directory.Exists(offline))throw new Exception("隔离源目录仍可访问，不能作为离线来源验证");
  var results=new List<object>();
  foreach(var package in ((IEnumerable)source["packages"]).Cast<Dictionary<string,object>>())
  {
   var label=(string)package["name"];var folder=Path.Combine(Stage,"View","download-"+label);Directory.CreateDirectory(folder);
   var manifest=Json.Deserialize(Json.Serialize(package["manifest"]),T("ControlledOpenManifestDto"));var project=(Guid)Get(manifest,"ProjectId");
   foreach(var file in ((IEnumerable)Get(manifest,"Files")).Cast<object>())
   {
    var mapping=((IEnumerable)package["storedFiles"]).Cast<Dictionary<string,object>>().Single(item=>item["versionId"].ToString()==Get(file,"VersionId").ToString());
    var target=Path.Combine(folder,(string)Get(file,"RelativePath"));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy((string)mapping["serverPath"],target);File.SetAttributes(target,FileAttributes.ReadOnly);Bind(target,file,project);
   }
   var root=Path.Combine(folder,(string)Get(manifest,"RootRelativePath"));var rootFile=((IEnumerable)Get(manifest,"Files")).Cast<object>().Single(f=>(bool)Get(f,"IsRoot"));
   var timer=Stopwatch.StartNew();Console.WriteLine("BEGIN: 从服务器文件库独立打开 "+label);
   var opened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{root,2,Get(rootFile,"Configuration"),manifest});
   if(opened==null||!string.Equals(opened.GetPathName(),root,StringComparison.OrdinalIgnoreCase))throw new Exception("未打开指定受控根");
   T("PdmAddin").GetMethod("ValidateControlledLoadedPaths",Flags).Invoke(null,new object[]{opened,root,manifest,App});
   var components=(((IAssemblyDoc)opened).GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().ToArray()??new IComponent2[0];
   var ordinary=components.Where(c=>!c.IsVirtual).Select(c=>new{name=c.Name2,path=(c.GetModelDoc2() as IModelDoc2)?.GetPathName()??c.GetPathName(),configuration=c.ReferencedConfiguration,loaded=c.GetModelDoc2()!=null}).ToArray();
   results.Add(new{label,root,files=((IEnumerable)Get(manifest,"Files")).Cast<object>().Count(),components=components.Length,ordinary,seconds=timer.Elapsed.TotalSeconds});
   Console.WriteLine("PASS: "+label+"；组件="+components.Length+"；来源不可访问，实际受控来源核对通过；秒="+timer.Elapsed.TotalSeconds.ToString("F2"));
   App.CloseDoc(opened.GetTitle());
   foreach(var model in ((App.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).ToArray())App.CloseDoc(model.GetTitle());
  }
  File.WriteAllText(Path.Combine(Stage,"native-offline-results.json"),Json.Serialize(new{passed=true,ownedPid=OwnedPid,rawSourceUnavailable=true,results}));
 }
 static async Task<object> CallAsync(object target,string name,params object[] arguments)
 {
  var method=target.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length);
  var task=(Task)method.Invoke(target,arguments);await task;return task.GetType().GetProperty("Result")?.GetValue(task);
 }
 static IEnumerable<object> Walk(object root)
 {
  var pending=new Stack<object>();var visited=new HashSet<object>();pending.Push(root);
  while(pending.Count>0){var node=pending.Pop();if(!visited.Add(node))continue;yield return node;
   foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>().Concat(((IEnumerable)Get(node,"ReferencedModels")).Cast<object>()))pending.Push(child);}
 }
 static async Task HttpFirstArchive(string source,string contextPath)
 {
  var context=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(contextPath));var url=(string)context["url"];var project=Guid.Parse((string)context["projectId"]);
  if(!new Uri(url).IsLoopback)throw new Exception("首次存档验收仅允许本机隔离服务");
  using(var http=new System.Net.Http.HttpClient()){var health=Json.Deserialize<Dictionary<string,object>>(await http.GetStringAsync(url.TrimEnd('/')+"/health"));if(!health.ContainsKey("databaseName")||!System.Text.RegularExpressions.Regex.IsMatch((string)health["databaseName"],"^pdm_closure_20261025_[a-f0-9]{10}$"))throw new Exception("禁止在生产数据库运行首次存档验收");}
  var client=New("PdmApiClient",url);await CallAsync(client,"LoginAsync","admin","Closure-Own-20261025!",CancellationToken.None);F(Addin,"apiClient",client);F(Addin,"authenticatedUsername","admin");F(Addin,"currentProjectId",(Guid?)project);F(Addin,"lifetime",new CancellationTokenSource());F(Addin,"controlledWorkspace",New("ControlledWorkspaceManager",client,Path.Combine(Stage,"View")));
  if(((IEnumerable)await CallAsync(client,"GetDocumentsAsync",project,CancellationToken.None)).Cast<object>().Any())throw new Exception("首次存档项目并非空白，禁止用种子版本替代实际存档");
  App.UserControl=true;var path=Path.Combine(source,"CW0030002.10.SLDASM");Console.WriteLine("阶段：正在打开完整总装 "+path);int error=0,warning=0;var root=App.OpenDoc6(path,2,1,"",ref error,ref warning);if(root==null||error!=0)throw new Exception("首次存档总装未打开："+error);App.ActivateDoc3(root.GetTitle(),false,1,ref error);
  Console.WriteLine("阶段：总装已打开，正在扫描实际结构");var scanner=GetField(Addin,"scanner");var tree=scanner.GetType().GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{root,true});Console.WriteLine("阶段：实际结构已扫描，正在发现配套工程图");scanner.GetType().GetMethod("AddNativeAssociatedDrawings",Flags).Invoke(scanner,new object[]{root,tree});F(Addin,"currentTree",tree);
  var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("BatchOperationItem")));var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seen=new HashSet<object>();var selectable=T("BatchOperationDialog").GetMethod("CanSelectForCheckIn",Flags);
  void Select(object node,int depth)
  {
   if(!seen.Add(node))return;var full=(string)Get(node,"FullPath");
   if((bool)selectable.Invoke(null,new object[]{node,"admin"})&&!(bool)T("BatchOperationDialog").GetMethod("IsEmbedded",Flags).Invoke(null,new[]{node})&&paths.Add(full))
   {
    if(!Path.GetFullPath(full).StartsWith(Path.GetFullPath(source)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("待存档项超出隔离图纸目录，未修改外部原件："+full);
    list.Add(New("BatchOperationItem",node,depth));
   }
   foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>().Concat(((IEnumerable)Get(node,"ReferencedModels")).Cast<object>()))Select(child,depth+1);
  }
  Select(tree,0);if(list.Count<226)throw new Exception("完整图档选择范围不足226个原生文件，未以小样本代替："+list.Count);
  File.WriteAllText(Path.Combine(Stage,"selected-first-archive.json"),Json.Serialize(new{projectId=project,files=paths.OrderBy(value=>value).ToArray(),count=list.Count}));Console.WriteLine("BEGIN: 实际客户端全套首次存档，选择="+list.Count);
  var timer=Stopwatch.StartNew();Action<string> report=stage=>{Console.WriteLine("准备："+stage);File.WriteAllText(Path.Combine(Stage,"current-stage.txt"),stage);};
  report("按实际界面复核选择范围并补全压缩结构");var selected=await CallAsync(Addin,"PrepareArchiveScopeAsync",list,project,CancellationToken.None);
  await CallAsync(Addin,"ValidateArchiveInputsBeforePreparationAsync",selected,project,CancellationToken.None);await CallAsync(Addin,"ValidateBatchProjectAsync",selected,project);
  report("按实际界面预加载图档");await CallAsync(Addin,"PreloadBatchCheckInDocumentsAsync",selected,report,CancellationToken.None);
  if((bool)await CallAsync(Addin,"EnsureBatchCheckInSavedStateAsync",selected,project,CancellationToken.None))selected=await CallAsync(Addin,"PrepareArchiveScopeAsync",selected,project,CancellationToken.None);
  report("按实际界面核对登记重复项");var decisions=await CallAsync(Addin,"ReviewRegistrationDuplicatesAsync",selected,project);if(decisions==null)throw new Exception("登记重复项尚未确认，未提交");
  var plan=await CallAsync(Addin,"BuildBatchCheckInPlanAsync",selected,report,CancellationToken.None);plan=await CallAsync(Addin,"UpdateLinkedDrawingsAsync",plan,project,CancellationToken.None);
  var result=await CallAsync(Addin,"CheckInBatchAsync",Get(plan,"Items"),project,"真实完整图档首次存档验收",new Action<int,int,string,string>((index,total,name,state)=>{var text=(index+1)+"/"+total+" "+state+" "+name;Console.WriteLine(text);File.WriteAllText(Path.Combine(Stage,"current-stage.txt"),text);}),CancellationToken.None,null,null,decisions,Get(plan,"Preflights"),false,null);
  var failures=((IEnumerable)Get(result,"Failures")).Cast<object>().ToArray();if(failures.Length>0)throw new Exception(string.Join("；",failures));
  var documents=((IEnumerable)await CallAsync(client,"GetDocumentsAsync",project,CancellationToken.None)).Cast<object>().ToArray();var expected=Directory.GetFiles(source).Where(file=>Native(file)||new[]{".STEP",".STP"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).ToArray();
  if(documents.Length!=expected.Length||!new HashSet<string>(documents.Select(doc=>(string)Get(doc,"FileName")),StringComparer.OrdinalIgnoreCase).SetEquals(expected))throw new Exception("首次存档的实际服务器图档不完整或文件名改变");
  foreach(var doc in documents){var versions=((IEnumerable)await CallAsync(client,"GetVersionsAsync",(Guid)Get(doc,"Id"),CancellationToken.None,(int?)null)).Cast<object>().ToArray();if(versions.Length!=1)throw new Exception("首次存档产生多余或缺失版本："+Get(doc,"FileName"));}
  var rootDocument=documents.Single(doc=>((string)Get(doc,"FileName")).Equals("CW0030002.10.SLDASM",StringComparison.OrdinalIgnoreCase));if(App.GetOpenDocumentByName(path)==null)throw new Exception("存档关闭了原总装窗口");
  File.WriteAllText(Path.Combine(Stage,"http-first-archive-results.json"),Json.Serialize(new{passed=true,projectId=project,rootId=Get(rootDocument,"Id"),files=documents.Length,createdVersions=Get(result,"CreatedVersions"),seconds=timer.Elapsed.TotalSeconds,originalNamesKept=true,allServerVersionsVerified=true,rootWindowKept=true,realClientCheckInBatch=true}));Console.WriteLine("PASS: 全套真实客户端首次存档及服务器逐文件版本核对通过");
 }
 static async Task HttpFlow(string contextPath)
 {
  App.UserControl=true;
  var context=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(contextPath));var url=(string)context["url"];var project=Guid.Parse((string)context["projectId"]);var rootId=Guid.Parse((string)context["rootId"]);var geometry=context.ContainsKey("geometry")&&(bool)context["geometry"];
  if(!new Uri(url).IsLoopback)throw new Exception("HTTP原生验收仅允许本机隔离服务。");
  using(var check=new System.Net.Http.HttpClient()){var status=Json.Deserialize<Dictionary<string,object>>(await check.GetStringAsync(url.TrimEnd('/')+"/health"));if(!status.ContainsKey("databaseName")||!System.Text.RegularExpressions.Regex.IsMatch((string)status["databaseName"],"^pdm_closure_20261025_[a-f0-9]{10}$"))throw new Exception("HTTP原生验收禁止访问生产数据库。");}
  var client=New("PdmApiClient",url);await CallAsync(client,"LoginAsync","admin","Closure-Own-20261025!",CancellationToken.None);
  F(Addin,"apiClient",client);F(Addin,"authenticatedUsername","admin");F(Addin,"currentProjectId",(Guid?)project);
  var manifest=await CallAsync(client,"CreateControlledOpenManifestAsync",rootId,(Guid?)null,false,false,CancellationToken.None,false);
  var folder=Path.Combine(Stage,"View","http-large");Directory.CreateDirectory(folder);
  Console.WriteLine("BEGIN: 从隔离HTTP服务完整获取大装配");
  var manifestFiles=((IEnumerable)Get(manifest,"Files")).Cast<object>().ToArray();
  for(var offset=0;offset<manifestFiles.Length;offset+=4){await Task.WhenAll(manifestFiles.Skip(offset).Take(4).Select(file=>CallAsync(client,"DownloadControlledOpenFileAsync",file,folder,CancellationToken.None)));foreach(var file in manifestFiles.Skip(offset).Take(4))Bind(Path.Combine(folder,(string)Get(file,"RelativePath")),file,project);}
  var rootPath=Path.Combine(folder,(string)Get(manifest,"RootRelativePath"));
  var opened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{rootPath,2,Get(manifestFiles.Single(file=>(bool)Get(file,"IsRoot")),"Configuration"),manifest});
  if(opened==null)throw new Exception("大装配未能从真实HTTP获取并打开");
  Console.WriteLine("完成：原生大装配已打开，开始读取存档范围");var scanner=GetField(Addin,"scanner");object tree;
  var incremental=context.ContainsKey("incrementalManifestScope")&&(bool)context["incrementalManifestScope"];
  if(incremental)
  {
   var byDocument=manifestFiles.ToDictionary(file=>(Guid)Get(file,"DocumentId"),file=>{var node=New("CadTreeNode");var fileName=(string)Get(file,"FileName");Set(node,"DocumentId",Get(file,"DocumentId"));Set(node,"FullPath",Path.Combine(folder,(string)Get(file,"RelativePath")));Set(node,"FileName",fileName);Set(node,"InstancePath",fileName);Set(node,"Configuration",Get(file,"Configuration"));Set(node,"Kind",Enum.Parse(T("CadDocumentKind"),fileName.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?"Assembly":fileName.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?"Drawing":fileName.EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase)?"Part":"Other"));return node;});
   tree=byDocument[(Guid)Get(manifest,"RootDocumentId")];Set(tree,"StructureIndexPending",true);foreach(var node in byDocument.Values.Where(node=>!ReferenceEquals(node,tree)))((IList)Get(tree,"Children")).Add(node);
   foreach(var file in manifestFiles.Where(file=>((string)Get(file,"FileName")).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase))){var drawingNode=byDocument[(Guid)Get(file,"DocumentId")];var modelFile=manifestFiles.FirstOrDefault(candidate=>Path.GetFileNameWithoutExtension((string)Get(candidate,"FileName"))==Path.GetFileNameWithoutExtension((string)Get(file,"FileName"))&&!((string)Get(candidate,"FileName")).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase));if(modelFile!=null){var modelNode=byDocument[(Guid)Get(modelFile,"DocumentId")];((IList)Get(drawingNode,"ReferencedModels")).Add(modelNode);((IList)Get(modelNode,"Children")).Add(drawingNode);}}
   Console.WriteLine("增量范围来自已核对的受控清单；总装不提交；实际组件="+(((IAssemblyDoc)opened).GetComponents(false) as Array)?.Length);
  }
  else tree=scanner.GetType().GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{opened,true});F(Addin,"currentTree",tree);Console.WriteLine("完成：存档范围已读取");
  var filesByPath=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),T("ControlledOpenFileDto")));
  foreach(var file in manifestFiles)filesByPath.Add(Path.GetFullPath(Path.Combine(folder,(string)Get(file,"RelativePath"))),file);
  T("PdmAddin").GetMethods(Flags).Single(method=>method.Name=="ApplyControlledOpenMetadata"&&method.IsStatic).Invoke(null,new object[]{tree,filesByPath,project});
  var docs=(IEnumerable)await CallAsync(client,"GetDocumentsAsync",project,CancellationToken.None);var byId=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),T("DocumentDto")));
  foreach(var doc in docs)byId.Add(Get(doc,"Id"),doc);T("PdmAddin").GetMethod("ApplyMetadata",Flags).Invoke(Addin,new object[]{tree,byId});
  var nodes=Walk(tree).Where(node=>Get(node,"DocumentId")!=null).GroupBy(node=>(string)Get(node,"FullPath"),StringComparer.OrdinalIgnoreCase).Select(group=>group.First()).ToArray();
  Console.WriteLine("开始：核对项目全部图档版本");var versionCounts=new Dictionary<Guid,int>();var metadataCount=0;
  foreach(var doc in docs){if(++metadataCount%20==0)Console.WriteLine("版本进度="+metadataCount);var id=(Guid)Get(doc,"Id");var history=((IEnumerable)await CallAsync(client,"GetVersionsAsync",id,CancellationToken.None,(int?)null)).Cast<object>().ToArray();versionCounts[id]=history.Length;foreach(var node in nodes.Where(node=>(Guid)Get(node,"DocumentId")==id))T("PdmAddin").GetMethod("ApplyLatestVersion",Flags).Invoke(null,new[]{node,history.FirstOrDefault()});}
  var model=nodes.Single(node=>(string)Get(node,"FileName")=="7000093.101.03-02.SLDPRT");
  var drawing=nodes.FirstOrDefault(node=>(string)Get(node,"FileName")=="7000093.101.03-02.SLDDRW");
  if(drawing==null)throw new Exception("大装配范围未带出配套工程图");
  string outlineBefore=null;double[] finalMass=null;string editedDimension=null;double dimensionValue=0;
  if(geometry)outlineBefore=CachedDrawingOutlines((string)Get(drawing,"FullPath"));
  var session=(Guid)GetField(Addin,"checkoutSessionId");var results=new List<object>();var savedCycles=new List<Tuple<int,Guid,double[],string,double>>();
  for(var cycle=1;cycle<=2;cycle++)
  {
   Console.WriteLine("BEGIN: 大装配内零件与工程图存档循环 "+cycle);
   var checkout=await CallAsync(client,"CheckoutAsync",(Guid)Get(model,"DocumentId"),session,Environment.MachineName,CancellationToken.None,(Guid?)null);
   T("PdmAddin").GetMethod("ApplyCheckoutDocument",Flags).Invoke(Addin,new[]{model,checkout});
   var path=(string)Get(model,"FullPath");File.SetAttributes(path,FileAttributes.Normal);var part=(IModelDoc2)App.GetOpenDocumentByName(path);if(part==null){var er=0;var wr=0;part=App.OpenDoc6(path,1,1,"",ref er,ref wr);}part.SetReadOnlyState(false);
   var properties=part.Extension.CustomPropertyManager[""];
   if(geometry)
   {
    var activation=0;App.ActivateDoc3(part.GetTitle(),false,0,ref activation);if(activation!=0)throw new Exception("隔离零件激活失败");
    var dimension=FindDimension(part,editedDimension,false);
    if(dimension!=null){editedDimension=dimension.FullName;dimensionValue=dimension.SystemValue+0.001;if(dimension.SetSystemValue3(dimensionValue,1,null)!=0||!part.EditRebuild3()||Math.Abs(dimension.SystemValue-dimensionValue)>1e-10)throw new Exception("隔离副本的实际尺寸修改或重建失败");}
    else {AddTestHole(part,cycle);dimensionValue=0.003;}
    var massStatus=0;finalMass=(double[])part.Extension.GetMassProperties2(2,out massStatus,false);if(massStatus!=0)throw new Exception("修改后的实际几何未能读取");Console.WriteLine("实际几何修改="+editedDimension+"；新值="+dimensionValue);
   }
   properties.Add3("隔离验收循环",30,"HTTP-"+cycle,1);var saveError=0;var saveWarning=0;if(!part.Save3(1,ref saveError,ref saveWarning)||saveError!=0)throw new Exception("测试零件保存失败");
   Set(model,"IsModifiedInSolidWorks",false);Set(model,"WorkState",Enum.Parse(T("CadWorkState"),"PendingCheckIn"));Set(drawing,"RequiresModelUpdate",true);
   var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("BatchOperationItem")));list.Add(New("BatchOperationItem",model,2));list.Add(New("BatchOperationItem",drawing,3));
   var result=await CallAsync(Addin,"CheckInBatchAsync",list,project,"隔离HTTP双循环验证 "+cycle,new Action<int,int,string,string>((index,total,name,state)=>Console.WriteLine(state+" "+name)),CancellationToken.None,null,null,null,null,false,null);
   var failures=((IEnumerable)Get(result,"Failures")).Cast<object>().ToArray();if(failures.Length>0)throw new Exception(string.Join(";",failures));if((int)Get(result,"CreatedVersions")!=2)throw new Exception("存档升版范围超出零件及配套工程图");
   if(geometry){var versions=((IEnumerable)await CallAsync(client,"GetVersionsAsync",(Guid)Get(drawing,"DocumentId"),CancellationToken.None,(int?)null)).Cast<object>().ToArray();savedCycles.Add(Tuple.Create(cycle,(Guid)Get(versions[0],"Id"),finalMass.ToArray(),editedDimension,dimensionValue));}
   if(App.GetOpenDocumentByName(rootPath)==null)throw new Exception("存档过程中总装窗口消失");results.Add(new{cycle,created=Get(result,"CreatedVersions"),unchanged=Get(result,"UnchangedFiles"),successes=Get(result,"Successes")});
   Console.WriteLine("PASS: 真实HTTP大装配循环 "+cycle);
  }
  var documentsAfter=((IEnumerable)await CallAsync(client,"GetDocumentsAsync",project,CancellationToken.None)).Cast<object>().ToArray();
  if(!new HashSet<Guid>(documentsAfter.Select(doc=>(Guid)Get(doc,"Id"))).SetEquals(versionCounts.Keys))throw new Exception("存档产生了未预期的新增或删除图档");
  foreach(var entry in versionCounts){var after=((IEnumerable)await CallAsync(client,"GetVersionsAsync",entry.Key,CancellationToken.None,(int?)null)).Cast<object>().ToArray();var changed=entry.Key==(Guid)Get(model,"DocumentId")||entry.Key==(Guid)Get(drawing,"DocumentId");if(after.Length!=entry.Value+(changed?2:0))throw new Exception("实际版本数量异常，存在误升版或漏升版："+entry.Key);}
  if(geometry)
  {
   var previousOutline=outlineBefore;
   foreach(var saved in savedCycles)
   {
   GC.Collect();GC.WaitForPendingFinalizers();
   foreach(var document in ((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).ToArray())try{App.CloseDoc(document.GetTitle());}catch(COMException){}
   var pair=await CallAsync(client,"CreateControlledOpenManifestAsync",(Guid)Get(drawing,"DocumentId"),(Guid?)saved.Item2,false,false,CancellationToken.None,false);var pairFolder=Path.Combine(Stage,"View","reopened-pair-cycle-"+saved.Item1);Directory.CreateDirectory(pairFolder);
   foreach(var file in ((IEnumerable)Get(pair,"Files")).Cast<object>()){await CallAsync(client,"DownloadControlledOpenFileAsync",file,pairFolder,CancellationToken.None);Bind(Path.Combine(pairFolder,(string)Get(file,"RelativePath")),file,project);}
   var savedOutline=CachedDrawingOutlines(Path.Combine(pairFolder,(string)Get(pair,"RootRelativePath")));
   if(previousOutline==savedOutline)throw new Exception("本轮服务器工程图保存的视图仍未更新；未通过重开自动重建掩盖结果");previousOutline=savedOutline;
   var reopened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{Path.Combine(pairFolder,(string)Get(pair,"RootRelativePath")),3,"",pair});if(reopened==null)throw new Exception("存档后的配套工程图未能从服务器独立取回");
   var reopenedPart=App.GetOpenDocumentByName(Path.Combine(pairFolder,(string)Get(model,"FileName"))) as IModelDoc2;if(reopenedPart==null)throw new Exception("工程图没有载入本包的零件");if(saved.Item4!=null){var reopenedDimension=FindDimension(reopenedPart,saved.Item4);if(Math.Abs(reopenedDimension.SystemValue-saved.Item5)>1e-10)throw new Exception("服务器取回的零件没有实际修改");}else if(!HasFeature(reopenedPart,"UPLM隔离验证孔"+saved.Item1))throw new Exception("服务器取回的零件缺少实际测试孔");
   var status=0;var actualMass=(double[])reopenedPart.Extension.GetMassProperties2(2,out status,false);if(status!=0||actualMass.Length!=saved.Item3.Length||saved.Item3.Where((value,index)=>Math.Abs(value-actualMass[index])>Math.Max(1e-10,Math.Abs(value)*1e-7)).Any())throw new Exception("服务器取回的实际几何与提交结果不一致");
   File.WriteAllText(Path.Combine(Stage,"native-cycle-"+saved.Item1+"-readback.json"),Json.Serialize(new{passed=true,cycle=saved.Item1,drawingVersionId=saved.Item2,expectedMass=saved.Item3,actualMass,dimension=saved.Item4,value=saved.Item5,outlineSha256=Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(savedOutline))),drawingChangedFromPreviousCycle=true,savedViewsVerifiedWithoutLoadingModels=true}));
   Console.WriteLine("PASS: 第"+saved.Item1+"轮指定服务器版本独立取回，实际尺寸、几何和工程图轮廓均已更新");
   }
  }
  File.WriteAllText(Path.Combine(Stage,"http-flow-results.json"),Json.Serialize(new{passed=true,projectId=project,rootId,modelId=Get(model,"DocumentId"),drawingId=Get(drawing,"DocumentId"),geometry,editedDimension,dimensionValue,unrelatedVersionsUnchanged=true,rootPath,files=manifestFiles.Length,results}));
 }
 static IDimension FindDimension(IModelDoc2 model,string name,bool required=true)
 {
  if(name!=null)return model.Parameter(name) as IDimension??throw new Exception("原尺寸未找到："+name);
  var dimensions=new List<IDimension>();for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
   for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display!=null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension){var dimension=display.GetDimension2(0) as IDimension;if(dimension!=null&&!dimension.ReadOnly&&dimension.DrivenState==2&&dimension.GetType()==0&&dimension.SystemValue>0.01&&dimension.SystemValue<1)dimensions.Add(dimension);}
  var selected=dimensions.OrderByDescending(dimension=>dimension.SystemValue).FirstOrDefault();if(selected==null&&required)throw new Exception("隔离样本没有可安全修改的驱动线性尺寸");return selected;
 }
 static bool HasFeature(IModelDoc2 model,string name){for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)if(feature.Name==name)return true;return false;}
 static string CachedDrawingOutlines(string source)
 {
  var folder=Path.Combine(Stage,"saved-drawing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var path=Path.Combine(folder,Path.GetFileName(source));File.Copy(source,path);
  var before=((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(doc=>doc.GetPathName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
  int error=0,warning=0;var model=App.OpenDoc6(path,3,1027,"",ref error,ref warning);
  if(model==null||error!=0||model.GetPathName()!=path||model is not IDrawingDoc drawing||!drawing.IsDetailingMode())throw new Exception("无法在不载入模型的模式下核对服务器工程图已保存视图");
  try{var result=DrawingOutlines(drawing,true);if(((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Any(doc=>doc.GetPathName()!=path&&!before.Contains(doc.GetPathName())))throw new Exception("工程图保存视图核对意外载入模型，不能作为提交结果证据");return result;}
  finally{GC.Collect();GC.WaitForPendingFinalizers();App.CloseDoc(model.GetTitle());}
 }
 static string DrawingOutlines(IDrawingDoc drawing,bool requireGeometry=false)
 {
  var result=new List<string>();var hasGeometry=false;foreach(var sheet in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(sheet);for(var view=drawing.GetFirstView() as IView;view!=null;view=view.GetNextView() as IView){var data=view.GetPolylines7(0,out var polylines);hasGeometry|=data is Array values&&values.Length>0;result.Add(sheet+"|"+view.GetName2()+"|"+string.Join(",",(double[])view.GetOutline())+"|"+Json.Serialize(data)+"|"+Json.Serialize(polylines));}}if(requireGeometry&&!hasGeometry)throw new Exception("未读到工程图已保存的实际轮廓，不能确认2D同步完成");return string.Join(";",result);
 }
 static void AddTestHole(IModelDoc2 model,int cycle)
 {
  var status=0;var before=(double[])model.Extension.GetMassProperties2(2,out status,false);var box=(double[])((IPartDoc)model).GetPartBox(true);
  var plane=model.FirstFeature() as IFeature;while(plane!=null&&plane.GetTypeName2()!="RefPlane")plane=plane.GetNextFeature() as IFeature;if(plane==null||!plane.Select2(false,0))throw new Exception("测试副本没有可选基准面");
  model.SketchManager.InsertSketch(true);var sketch=model.GetActiveSketch2() as ISketch;var utility=App.GetMathUtility() as IMathUtility;
  var point=(utility.CreatePoint(new[]{box[0]+(box[3]-box[0])*(cycle==1?0.37:0.63),box[1]+(box[4]-box[1])*(cycle==1?0.23:0.73),(box[2]+box[5])/2}) as IMathPoint).MultiplyTransform(sketch.ModelToSketchTransform) as IMathPoint;var xy=(double[])point.ArrayData;
  if(model.SketchManager.CreateCircleByRadius(xy[0],xy[1],0,0.003)==null)throw new Exception("测试孔草图创建失败");model.SketchManager.InsertSketch(true);
  var cut=model.FeatureManager.FeatureCut3(false,false,false,1,1,0.01,0.01,false,false,false,false,0,0,false,false,false,false,false,true,true,false,false,false,0,0,false);
  if(cut==null||!model.EditRebuild3())throw new Exception("测试孔切除失败");cut.Name="UPLM隔离验证孔"+cycle;var after=(double[])model.Extension.GetMassProperties2(2,out status,false);
  if(status!=0||!(after[3]<before[3]-1e-12))throw new Exception("测试孔没有产生真实体积变化");Console.WriteLine("实际几何修改：测试孔"+cycle+"；体积="+before[3]+" → "+after[3]);
 }
 static void GeometryProbe(string source)
 {
  var path=Path.Combine(Stage,Path.GetFileName(source));File.Copy(source,path);File.SetAttributes(path,FileAttributes.Normal);int er=0,wr=0;var model=App.OpenDoc6(path,1,1,"",ref er,ref wr);if(model==null)throw new Exception("几何副本未打开");App.ActivateDoc3(model.GetTitle(),false,0,ref er);model.SetReadOnlyState(false);
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature){Console.WriteLine("特征="+feature.Name+"；"+feature.GetTypeName2());for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display!=null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension){var dimension=display.GetDimension2(0);Console.WriteLine("尺寸="+dimension.FullName+"；驱动="+dimension.DrivenState+"；类型="+dimension.GetType()+"；只读="+dimension.ReadOnly+"；值="+dimension.SystemValue);}}
  AddTestHole(model,1);AddTestHole(model,2);if(!model.Save3(1,ref er,ref wr)||er!=0)throw new Exception("几何修改副本未保存");Console.WriteLine("PASS: 两次原生几何切除及保存完成");
 }
 static object GetField(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
 static void Safeguards(string source)
 {
  var inventory=Directory.GetFiles(source);var originalHashes=inventory.ToDictionary(path=>path,Hash);var root=Path.Combine(source,"SS5Y-8位电磁阀座.SLDASM");int er=0,wr=0;var original=App.OpenDoc6(root,2,1,"",ref er,ref wr);if(original==null)throw new Exception("保护窗口未打开");App.ActivateDoc3(original.GetTitle(),false,0,ref er);var dirty=original.GetSaveFlag();var results=new List<object>();
  foreach(var name in new[]{"标准机架后安全门型材框.SLDPRT","S700130.M1.01-01.SLDDRW"})
  {
   var path=Path.Combine(source,name);Exception failure=null;
   try{using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,new Dictionary<string,string>{{path,path}},Stage,new Action<string>(text=>Console.WriteLine("处理："+text)))){}}
   catch(Exception error){failure=error is TargetInvocationException invocation?invocation.InnerException:error;}
   if(!(failure is IOException||failure is InvalidOperationException)||!failure.Message.Contains(name))throw new Exception("不安全文件未被明确阻止："+name,failure);
   if(App.GetOpenDocumentByName(root)==null||dirty!=original.GetSaveFlag()||originalHashes.Any(file=>Hash(file.Key)!=file.Value))throw new Exception("失败处理改变了原窗口、原文件或修改状态");
   var leftovers=Directory.GetDirectories(Stage).Where(folder=>Guid.TryParseExact(Path.GetFileName(folder),"N",out var value)).ToArray();if(leftovers.Length!=0)throw new Exception("失败的隔离副本未清理");
   results.Add(new{name,blockedBeforeUpload=true,originalWindowKept=true,sourceFilesUnchanged=true,temporaryCopiesRemoved=true,reason=failure.Message});Console.WriteLine("PASS: 安全拦截且保持原图档 "+name);
  }
  File.WriteAllText(Path.Combine(Stage,"safeguard-results.json"),Json.Serialize(new{passed=true,results}));
 }
 static void Resources(string source,string selectedDrawing=null)
 {
  var folder=Path.Combine(Stage,"drawings");Directory.CreateDirectory(folder);var protectedFiles=Directory.GetFiles(source).ToDictionary(path=>path,Hash);var templateHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var rows=new List<object>();
  var preferences=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Plugin.Location),"SolidWorks.Interop.swconst.dll")).GetType("SolidWorks.Interop.swconst.swUserPreferenceStringValue_e",true);
  foreach(var option in new[]{"swDefaultTemplatePart","swDefaultTemplateAssembly","swDefaultTemplateDrawing"}){var path=App.GetUserPreferenceStringValue((int)Enum.Parse(preferences,option));if(File.Exists(path))templateHashes[path]=Hash(path);}
  foreach(var path in protectedFiles.Keys){var target=Path.Combine(folder,Path.GetFileName(path));File.Copy(path,target);File.SetAttributes(target,FileAttributes.Normal);}
  foreach(var path in Directory.GetFiles(folder).Where(Native))
  {
   var dependencies=App.GetDocumentDependencies2(path,false,false,false) as Array;if(dependencies==null)continue;
   for(var i=1;i<dependencies.Length;i+=2)foreach(var old in (Convert.ToString(dependencies.GetValue(i))??"").Split('|')){var target=Path.Combine(folder,Path.GetFileName(old));if(File.Exists(target)&&old!=target)App.ReplaceReferencedDocument(path,old,target);}
  }
  var resourceType=T("ArchiveResourceIsolation");var layoutMethod=resourceType.GetMethod("DrawingLayout",Flags);var detach=resourceType.GetMethod("Detach",Flags);
  foreach(var path in Directory.GetFiles(folder,"*.SLDDRW").Where(path=>selectedDrawing==null||Path.GetFileName(path)==selectedDrawing))
  {
   Console.WriteLine("BEGIN: 工程图全部图页及模板 "+Path.GetFileName(path));int er=0,wr=0;var model=App.OpenDoc6(path,3,1,"",ref er,ref wr);if(model==null||er!=0)throw new Exception("工程图副本未能打开："+Path.GetFileName(path)+"；"+er);
   var drawing=(IDrawingDoc)model;foreach(var name in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(name);var template=(drawing.GetCurrentSheet() as ISheet).GetTemplateName();if(!string.IsNullOrWhiteSpace(template)&&File.Exists(template)&&!templateHashes.ContainsKey(template))templateHashes.Add(template,Hash(template));}
   var layout=(string)layoutMethod.Invoke(null,new object[]{model});var changed=(bool)detach.Invoke(null,new object[]{model});if(layout!=(string)layoutMethod.Invoke(null,new object[]{model}))throw new Exception("固化时工程图版式变化");
   if(!model.Save3(1,ref er,ref wr)||er!=0)throw new Exception("工程图副本保存失败");var title=model.GetTitle();App.CloseDoc(title);model=App.OpenDoc6(path,3,1,"",ref er,ref wr);if(model==null||layout!=(string)layoutMethod.Invoke(null,new object[]{model}))throw new Exception("重开后工程图版式变化："+Path.GetFileName(path));
   drawing=(IDrawingDoc)model;foreach(var name in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(name);if(!string.IsNullOrWhiteSpace((drawing.GetCurrentSheet() as ISheet).GetTemplateName()))throw new Exception("图框重开后仍关联原模板");}
   rows.Add(new{name=Path.GetFileName(path),changed,sheets=drawing.GetSheetCount(),reopenLayoutUnchanged=true});Console.WriteLine("PASS: "+Path.GetFileName(path));App.CloseDoc(model.GetTitle());
  }
  if(protectedFiles.Any(file=>Hash(file.Key)!=file.Value)||templateHashes.Any(file=>Hash(file.Key)!=file.Value))throw new Exception("源文件或图框模板原件发生变化");
  File.WriteAllText(Path.Combine(Stage,"resource-results.json"),Json.Serialize(new{passed=true,drawings=rows.Count,rows,templates=templateHashes,sourceFilesUnchanged=true}));
 }
 static void JournalScopeRegression()
 {
  const string executable=@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe",valueName="SolidWorks Journal Folders";
  var jobs=Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs");var owned=new List<string>();var checks=new List<string>();var concurrent=Path.Combine(Stage,"concurrent-setting");Directory.CreateDirectory(concurrent);
  using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks\SOLIDWORKS 2022\ExtReferences",true))
  {
   var original=key.GetValue(valueName,null,Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);var kind=key.GetValueKind(valueName);
   var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var symbolic=Path.Combine(Path.GetDirectoryName(profile),"CURRENT_USER");
   string Job(){var job=Path.Combine(jobs,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);owned.Add(Path.Combine(job,"journal"));File.WriteAllText(Path.Combine(job,"native-isolation-request.json"),Json.Serialize(new{JobId=Path.GetFileName(job),SolidWorksExecutable=executable}));return job;}
   IDisposable Begin(string job)=>(IDisposable)T("ArchiveNativeJournalScope").GetMethod("Begin",Flags).Invoke(null,new object[]{job,executable});
   object Current()=>key.GetValue(valueName,null,Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
   void Original(){if(!Equals(Current(),original)||key.GetValueKind(valueName)!=kind)throw new Exception("日志设置未恢复原值及原类型");}
   string AbortHolder()
   {
    var job=Job();var holder=Path.Combine(Stage,Path.GetFileName(job));Directory.CreateDirectory(holder);
    using(var process=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName){UseShellExecute=false,CreateNoWindow=true,Arguments="\""+Plugin.Location+"\" journalscopehold \""+holder+"\" \""+job+"\""}))
    {
     var started=process.StartTime.ToUniversalTime().Ticks;try{var timer=Stopwatch.StartNew();while(!File.Exists(Path.Combine(holder,"held"))&&!process.HasExited&&timer.Elapsed<TimeSpan.FromSeconds(15))Thread.Sleep(100);if(!File.Exists(Path.Combine(holder,"held"))||!Equals(Current(),Path.Combine(job,"journal")))throw new Exception("未确认测试工作进程独占日志设置");}
     finally{if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==started){process.Kill();if(!process.WaitForExit(5000))throw new Exception("测试工作进程未退出");}}
    }
    key.SetValue(valueName,Path.Combine(job,"journal").Replace(profile,symbolic),Microsoft.Win32.RegistryValueKind.String);return job;
   }
   try
   {
    var normal=Job();using(Begin(normal)){if(!Equals(Current(),Path.Combine(normal,"journal")))throw new Exception("专用日志目录未生效");}Original();checks.Add("正常退出恢复原值和类型");
    var normalized=Job();using(Begin(normalized))key.SetValue(valueName,Path.Combine(normalized,"journal").Replace(profile,symbolic),Microsoft.Win32.RegistryValueKind.String);Original();checks.Add("SolidWorks的CURRENT_USER路径改写仍恢复原值");
    var trailing=Job();using(Begin(trailing))key.SetValue(valueName,Path.Combine(trailing,"journal")+"\\",Microsoft.Win32.RegistryValueKind.String);Original();checks.Add("日志目录追加分隔符仍恢复原值");
    try{using(Begin(Job()))throw new InvalidOperationException("受控测试异常");}catch(InvalidOperationException error)when(error.Message=="受控测试异常"){}Original();checks.Add("启动异常恢复原值和类型");
    var abandoned=AbortHolder();T("ArchiveNativeJournalScope").GetMethod("Recover",Flags).Invoke(null,new object[]{abandoned});Original();checks.Add("工作进程被终止后调用方恢复设置");
    AbortHolder();using(Begin(Job())){}Original();checks.Add("后续作业恢复异常退出留下的设置");
    using(Begin(Job()))key.SetValue(valueName,concurrent,Microsoft.Win32.RegistryValueKind.String);if(!Equals(Current(),concurrent))throw new Exception("覆盖了其他操作新设置的日志目录");key.SetValue(valueName,original,kind);Original();checks.Add("保留并发设置而非覆盖");
    File.WriteAllText(Path.Combine(Stage,"journal-regression-results.json"),Json.Serialize(new{passed=true,checks,originalSettingRestored=true,noCadDocumentsOpened=true}));Console.WriteLine("PASS: 日志启动隔离与恢复"+checks.Count+"项实机检查");
   }
   finally{var current=Current() as string;if(owned.Contains(current)||owned.Any(path=>path.Replace(profile,symbolic)==current)||current==concurrent)key.SetValue(valueName,original,kind);}
  }
 }
 static async Task RegisteredStartupSmoke(bool automationProbe=false,bool comProbe=false,bool journalProbe=false)
 {
  App.UserControl=true;
  var until=Stopwatch.StartNew();while(!App.StartupProcessCompleted&&until.Elapsed<TimeSpan.FromSeconds(60))await Task.Delay(200);
  if(!App.StartupProcessCompleted)throw new Exception("正常注册插件启动未完成");
  string installed;using(var key=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32"))installed=new Uri((string)key.GetValue("CodeBase")).LocalPath;
  var expected=Hash(Plugin.Location);if(Hash(installed)!=expected)throw new Exception("注册插件与已验证候选文件不同");
  string loadedPath=null;using(var normal=Process.GetProcessById(OwnedPid))loadedPath=normal.Modules.Cast<ProcessModule>().Where(m=>Path.GetFileName(m.FileName).Equals("Upton.Pdm.SolidWorks.Addin.dll",StringComparison.OrdinalIgnoreCase)).Select(m=>m.FileName).FirstOrDefault();
  if(loadedPath!=null&&Hash(loadedPath)!=expected)throw new Exception("正常SolidWorks加载了其他版本的插件");
  var diagnosticRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UPLM","Diagnostics");
  var version=File.ReadAllText(Path.Combine(Path.GetDirectoryName(installed),".uplm-version")).Trim();
  string startupLog=null;var logTimer=Stopwatch.StartNew();
  while(logTimer.Elapsed<TimeSpan.FromSeconds(30))
  {
   foreach(var log in Directory.GetFiles(diagnosticRoot,"runtime-"+OwnedPid+"-"+OwnedStart+"-*",SearchOption.AllDirectories))
   {
    var lines=File.ReadAllLines(log).Select(line=>Json.Deserialize<Dictionary<string,object>>(line)).ToArray();
    if(lines.Any(line=>Convert.ToString(line["version"])==version&&Convert.ToString(line["message"]).Contains("ConnectToSW success")&&Convert.ToString(line["message"]).Contains("pid="+OwnedPid))){startupLog=log;break;}
   }
   if(startupLog!=null)break;await Task.Delay(200);
  }
  if(startupLog==null)throw new Exception("正常SolidWorks未记录已部署插件成功启动，未执行业务测试");
  if(App.GetDocumentCount()!=0)throw new Exception("启动核对会话意外载入了其他图档，未继续");
  File.Copy(startupLog,Path.Combine(Stage,"registered-normal-startup.jsonl"));
  var job=Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
  var exe=App.GetExecutablePath();if(Directory.Exists(exe))exe=Path.Combine(exe,"SLDWORKS.exe");
  File.WriteAllText(Path.Combine(job,"native-isolation-request.json"),Json.Serialize(new{JobId=Path.GetFileName(job),CallerSolidWorksId=OwnedPid,SolidWorksExecutable=exe,Inventory=new string[0],Requested=new Dictionary<string,string>()}));
  var childStage=Path.Combine(Stage,"reader");Directory.CreateDirectory(childStage);
  var childMode=journalProbe?"journalreadersmoke":comProbe?"comreadersmoke":automationProbe?"automationreadersmoke":"registeredreadersmoke";
  var launch=new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,Arguments="\""+Plugin.Location+"\" "+childMode+" \""+childStage+"\" \""+job+"\""};
  using(var worker=Process.Start(launch))
  {
   var workerStarted=worker.StartTime.ToUniversalTime().Ticks;var output=worker.StandardOutput.ReadToEndAsync();var error=worker.StandardError.ReadToEndAsync();var elapsed=Stopwatch.StartNew();
   try
   {
    while(!worker.HasExited&&elapsed.Elapsed<TimeSpan.FromMinutes(3))await Task.Delay(200);
    if(!worker.HasExited)throw new Exception("专用读取验证工作进程等待超时");
    File.WriteAllText(Path.Combine(Stage,"reader-stdout.txt"),await output);File.WriteAllText(Path.Combine(Stage,"reader-stderr.txt"),await error);
    if(worker.ExitCode!=0||!File.Exists(Path.Combine(childStage,"reader-results.json")))throw new Exception("专用读取会话未通过，详见读取工作进程记录");
    if(App.GetDocumentCount()!=0)throw new Exception("启动核对过程中意外载入了其他图档，未通过");
    var readerResult=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(childStage,"reader-results.json")));var registered=Convert.ToBoolean(readerResult["registeredReaderGuardVerified"]);
    var marker=registered?"registered-reader-addin.json":"native-reader-isolation.json";File.Copy(Path.Combine(childStage,marker),Path.Combine(Stage,marker));
    File.WriteAllText(Path.Combine(Stage,"registered-startup-results.json"),Json.Serialize(new{passed=true,installed,loadedPath,startupLog,version,sha256=expected,normalProcessId=OwnedPid,normalPluginActuallyLoaded=true,registeredReaderGuardVerified=registered,isolatedLegacyReaderVerified=Convert.ToBoolean(readerResult["isolatedLegacyReaderVerified"]),automationProbe,comProbe,journalProbe,assemblyId=Plugin.ManifestModule.ModuleVersionId.ToString("N"),noCadDocumentsOpened=true}));
    Console.WriteLine("PASS: 正常注册插件实际加载正确哈希，专用读取会话由同一已部署版本安全启动");
   }
   finally
   {
    if(!worker.HasExited&&worker.StartTime.ToUniversalTime().Ticks==workerStarted)worker.Kill();
    var ownerFile=Path.Combine(childStage,"reader-owner.json");if(File.Exists(ownerFile)){var owner=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ownerFile));try{using(var child=Process.GetProcessById(Convert.ToInt32(owner["id"])))if(!child.HasExited&&child.StartTime.ToUniversalTime().Ticks==Convert.ToInt64(owner["started"]))child.Kill();}catch(ArgumentException){}}
   }
  }
 }
 static async Task LaunchProbe()
 {
  App.UserControl=true;var executable=App.GetExecutablePath();if(Directory.Exists(executable))executable=Path.Combine(executable,"SLDWORKS.exe");
  var rows=new List<object>();
  using(var process=Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)}))
  {
   var started=process.StartTime.ToUniversalTime().Ticks;try
   {
    await Task.Delay(30000);process.Refresh();rows.Add(new{pid=process.Id,started,executable,exited=process.HasExited,exitCode=process.HasExited?(int?)process.ExitCode:null});
    File.WriteAllText(Path.Combine(Stage,"launch-probe.json"),Json.Serialize(rows));Console.WriteLine(Json.Serialize(rows));
   }
   finally{if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==started)process.Kill();}
  }
 }
 static async Task HostWorkspace(string source,string mode,string requestedRoot=null)
 {
  var protectedFiles=Directory.GetFiles(source).Where(path=>!Path.GetFileName(path).StartsWith("~$",StringComparison.Ordinal)).ToDictionary(path=>path,Hash);var names=mode=="hosth620"?new[]{"H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT"}:new[]{"SS5Y-8位电磁阀座.SLDASM","7000093.101.03-02.SLDPRT","7000093.101.03-02.SLDDRW"};
  if(mode=="hostfesto")names=new[]{"1376478_DSBC-32-500-PPSA-N3.SLDPRT"};
  if(mode=="hostsurface")names=new[]{"355.0700A.01.SLDPRT"};
  if(mode=="hostprofile")names=new[]{"标准机架后安全门型材框.SLDPRT"};
  if(mode=="hostassembly")names=new[]{"CW0030002.101.SLDASM"};
  if(mode=="hostlhh")names=new[]{"LHH30CA2R1000Z0C-E20E20(1).SLDASM"};
  if(mode=="hostroot")names=new[]{requestedRoot};
  if(mode=="hostcq")names=new[]{"CQ-2T-ISO63-100-20250105025527291.SLDPRT"};
  if(mode=="hostfailedpart")names=new[]{"700091.11-10.SLDPRT"};
  if(mode=="hostfeaturepart")names=new[]{"700091.11-03.SLDPRT"};
  if(mode=="hostseries")names=new[]{"700091.11-01.SLDPRT","GD-150F-20211228090001663.SLDPRT","700091.11-02.SLDPRT","M.BZ.Z.D01.A23.V00-01-20220408023629198.SLDPRT","BP1-直线滑轨HG30-20220104103207144.SLDPRT","MDBB32-500Z-20240130032612996.SLDPRT","CDS2F160-500(0_0).SLDPRT","BP1-六角薄螺母 细牙-20220316085838843.SLDPRT"};
  if(mode=="hostall")names=protectedFiles.Keys.Where(Native).Select(Path.GetFileName).ToArray();
  var root=Path.Combine(source,mode=="hostall"?"CW0030002.10.SLDASM":mode=="hostseries"?"CW0030002.101.SLDASM":names[0]);int error=0,warning=0;
  var original=App.OpenDoc6(root,root.EndsWith(".SLDASM")?2:1,1,"",ref error,ref warning);if(original==null||error!=0)throw new Exception("保护窗口未打开："+error);
  App.ActivateDoc3(original.GetTitle(),false,1,ref error);var dirty=original.GetSaveFlag();var title=original.GetTitle();
  double[] baselineMass=null;if(mode=="hosth620"){baselineMass=original.Extension.GetMassProperties2(2,out var status,false) as double[];if(status!=0||baselineMass==null)throw new Exception("基准几何未读取");}
  dirty=original.GetSaveFlag();title=original.GetTitle();
  var requested=names.Select(name=>Path.Combine(source,name)).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);var timer=Stopwatch.StartNew();
  var task=(Task)T("ArchiveNativeProcessWorkspace").GetMethod("CreateAsync",Flags).Invoke(null,new object[]{App,protectedFiles.Keys,requested,new Action<string>(name=>Console.WriteLine("独立处理："+name)),CancellationToken.None});await task;
  using(var workspace=(IDisposable)task.GetType().GetProperty("Result").GetValue(task))
  {
   var isolation=Path.Combine((string)GetField(workspace,"directory"),"native-reader-isolation.json");if(File.Exists(isolation))File.Copy(isolation,Path.Combine(Stage,"native-reader-isolation.json"));
   foreach(var audit in new[]{"native-journal-setting.json","native-journal-restored.json","reader-addin.json"}){var path=Path.Combine((string)GetField(workspace,"directory"),audit);if(File.Exists(path))File.Copy(path,Path.Combine(Stage,audit));}
   var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);var files=new List<object>();
   foreach(var copy in ((IEnumerable)Get(workspace,"Files")).Cast<object>())
   {var origin=(string)GetField(copy,"Source");var path=Path.Combine(output,Path.GetFileName(origin));File.Copy((string)GetField(copy,"Path"),path);files.Add(new{source=origin,path,sha256=Hash(path),configuration=Native(path)?App.GetActiveConfigurationName(path):"",dependencies=(string[])GetField(copy,"Dependencies"),frozen=(bool)GetField(copy,"ContentFrozen")});}
   var protection=new{windowKept=App.GetOpenDocumentByName(root)!=null,titleBefore=title,titleAfter=original.GetTitle(),dirtyBefore=dirty,dirtyAfter=original.GetSaveFlag(),activePath=(App.ActiveDoc as IModelDoc2)?.GetPathName(),expectedPath=root,fileMismatches=protectedFiles.Where(pair=>Hash(pair.Key)!=pair.Value).Select(pair=>new{path=pair.Key,before=pair.Value,after=Hash(pair.Key)}).ToArray()};
   File.WriteAllText(Path.Combine(Stage,"source-protection-details.json"),Json.Serialize(protection));
   if(!protection.windowKept||protection.titleAfter!=title||protection.dirtyAfter!=dirty||protection.activePath!=root||protection.fileMismatches.Length!=0)throw new Exception("原文件、原窗口、名称或活动上下文发生变化");
   File.WriteAllText(Path.Combine(Stage,"host-workspace-results.json"),Json.Serialize(new{passed=true,mode,rootName=Path.GetFileName(root),originalWindowKept=true,originalNameKept=true,activeContextKept=true,sourceFilesUnchanged=true,sourceCount=protectedFiles.Count,seconds=timer.Elapsed.TotalSeconds,baselineMass,files}));
   Console.WriteLine("PASS: 独立进程保留原名完成存档准备，原图档、窗口、活动上下文和源文件全部保留");
  }
 }
 static void ReopenH620(string record)
 {
  var result=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(record));
  var baseline=((IEnumerable)result["baselineMass"]).Cast<object>().Select(Convert.ToDouble).ToArray();
  var source=((IEnumerable)result["files"]).Cast<Dictionary<string,object>>().Single(file=>((string)file["path"]).EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase));
  var folder=Path.Combine(Stage,"clean");Directory.CreateDirectory(folder);var path=Path.Combine(folder,Path.GetFileName((string)source["path"]));File.Copy((string)source["path"],path);
  int error=0,warning=0;var model=App.OpenDoc6(path,1,1,"",ref error,ref warning);
  if(model==null||error!=0||model.GetPathName()!=path)throw new Exception("固化零件独立打开失败："+error);
  if(((IEnumerable)T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model})).Cast<object>().Any())throw new Exception("独立文件仍有活动外部链接");
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)if(feature.Is3DInterconnectFeature)throw new Exception("独立文件仍有3DInterconnect链接");
  if(((IPartDoc)model).GetBodies2(0,false) is not Array bodies||bodies.Length==0)throw new Exception("独立打开后没有实体");
  var mass=model.Extension.GetMassProperties2(2,out var status,false) as double[];
  if(status!=0||mass==null||mass.Length!=baseline.Length||baseline.Where((value,index)=>Math.Abs(value-mass[index])>Math.Max(1e-10,Math.Abs(value)*1e-7)).Any())throw new Exception("固化重开后的几何与源模型不一致");
  var onlyNative=Directory.GetFiles(folder).Where(file=>!Path.GetFileName(file).StartsWith("~$",StringComparison.Ordinal)).Count()==1;
  if(!onlyNative)throw new Exception("独立重开目录包含额外来源");
  File.WriteAllText(Path.Combine(Stage,"h620-reopen-results.json"),Json.Serialize(new{passed=true,originalFileName=Path.GetFileName(path),nativeFileOnly=onlyNative,noActiveLinks=true,noInterconnect=true,bodyCount=bodies.Length,geometryMatches=true,baselineMass=baseline,savedMass=mass}));
  Console.WriteLine("PASS: H620原名固化副本在无STEP目录独立重开，实体及几何一致，无活动外链");
 }
 static void OleProbe(string source,bool rail=false,string selected=null)
 {
  var name=selected??(rail?"BP1-直线滑轨HG30-20220104103207144.SLDPRT":"1376478_DSBC-32-500-PPSA-N3.SLDPRT");var path=Path.Combine(Stage,name);File.Copy(Path.Combine(source,name),path);var initial=Hash(Path.Combine(source,name));
  int error=0,warning=0;var model=App.OpenDoc6(path,1,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("OLE样本未打开："+error);
  var objects=new List<object>();int index=0;
  foreach(var item in ((model.Extension.GetOLEObjects(0) as Array)?.Cast<object>().OfType<ISwOLEObject>()??Enumerable.Empty<ISwOLEObject>()))
  {
   var buffer=(item.Buffer as Array)?.Cast<object>().Select(value=>unchecked((byte)Convert.ToInt32(value))).ToArray();var output=Path.Combine(Stage,"embedded-"+(index++)+".bin");if(buffer!=null)File.WriteAllBytes(output,buffer);
   var accepted=(bool)T("ArchiveEmbeddedPackageInspection").GetMethod("IsSelfContained",Flags).Invoke(null,new object[]{item});
   objects.Add(new{name=item.FileName,clsid=item.Clsid,linked=item.IsLinked,size=item.BufferSize,bufferLength=buffer?.Length,output,accepted});
  }
  File.WriteAllText(Path.Combine(Stage,"ole-objects.json"),Json.Serialize(objects));
  var table=model.GetDesignTable() as IDesignTable;var exported=Path.Combine(Stage,"embedded-design-table.xls");var saved=table?.SaveAsExcelFile(exported)??false;
  File.WriteAllText(Path.Combine(Stage,"ole-probe-results.json"),Json.Serialize(new{objects,designTable=table==null?null:new{table.FileName,table.LinkToFile,table.SourceType,rows=table.GetRowCount(),columns=table.GetColumnCount(),saved},originalUnchanged=initial==Hash(Path.Combine(source,name))}));
  Console.WriteLine("PASS: 嵌入对象目录与内容只读导出完成，原图未改写");
 }
 static void OleCatalog(string source,bool tail=false)
 {
  var files=Directory.GetFiles(source).Where(path=>Native(path)&&!Path.GetFileName(path).StartsWith("~$",StringComparison.Ordinal)&&(!tail||Path.GetFileName(path)[0]>=0x2e80)).ToArray();var hashes=files.ToDictionary(path=>path,Hash);var rows=new List<object>();
  foreach(var path in files.OrderBy(path=>path.EndsWith("CW0030002.10.SLDASM",StringComparison.OrdinalIgnoreCase)?0:1))
  {
   File.WriteAllText(Path.Combine(Stage,"current-file.txt"),Path.GetFileName(path));
   int error=0,warning=0;var model=App.GetOpenDocumentByName(path) as IModelDoc2??App.OpenDoc6(path,path.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?2:path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?3:1,3,"",ref error,ref warning);
   if(model==null||error!=0)throw new Exception("嵌入对象只读目录未能打开："+Path.GetFileName(path)+"；"+error);
   foreach(var item in (model.Extension.GetOLEObjects(0) as Array)?.OfType<ISwOLEObject>()??Enumerable.Empty<ISwOLEObject>())
   {
    var bytes=(item.Buffer as Array)?.Cast<object>().Select(value=>unchecked((byte)Convert.ToInt32(value))).ToArray();var output=Path.Combine(Stage,"object-"+rows.Count+".bin");if(bytes!=null)File.WriteAllBytes(output,bytes);
    var accepted=(bool)T("ArchiveEmbeddedPackageInspection").GetMethod("IsSelfContained",Flags).Invoke(null,new object[]{item});
    rows.Add(new{fileName=Path.GetFileName(path),clsid=item.Clsid,linked=item.IsLinked,objectName=item.FileName,size=item.BufferSize,accepted,output});
    File.WriteAllText(Path.Combine(Stage,"embedded-catalog.json"),Json.Serialize(rows));
   }
   App.CloseAllDocuments(true);Application.DoEvents();GC.Collect();GC.WaitForPendingFinalizers();Application.DoEvents();
  }
  if(hashes.Any(pair=>Hash(pair.Key)!=pair.Value))throw new Exception("只读嵌入对象核对不应改写隔离源");
  File.WriteAllText(Path.Combine(Stage,"embedded-catalog-results.json"),Json.Serialize(new{passed=true,files=files.Length,objects=rows.Count,sourceUnchanged=true,rows}));
  Console.WriteLine("PASS: 全套真实图档嵌入对象目录完成，未启动外部对象服务器或保存原文件");
 }
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
 static async Task LifecycleProbe(string source,bool features=false,string selected=null,bool command=false,int cycles=50)
 {
  var path=selected??Path.Combine(source,"700091.11-03.SLDPRT");var initial=Hash(path);var rows=new List<object>();
  using(var registration=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32"))
  {var installed=new Uri((string)registration.GetValue("CodeBase")).LocalPath;if(App.UnloadAddIn(installed)!=0)throw new Exception("未能停止本次专用实例的交互插件");}
  App.UserControlBackground=true;
  App.CommandInProgress=command;
  if(features)App.DocumentVisible(false,1);
  for(var index=0;index<cycles;index++)
  {
   int error=0,warning=0;var model=App.OpenDoc6(path,1,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("反复打开失败："+index+"；"+error);
   if(features)
   {
    App.ActivateDoc3(model.GetTitle(),false,1,ref error);
    T("NativeDesignDependencies").GetMethod("ValidateManaged",Flags).Invoke(null,new object[]{model,new Dictionary<string,string>{{Path.GetFileName(path),path}}});
   }
   var control=App.UserControl;var background=App.UserControlBackground;var count=App.GetDocumentCount();
   App.CloseAllDocuments(true);Application.DoEvents();if(index%8==7){GC.Collect();GC.WaitForPendingFinalizers();Application.DoEvents();}
   using(var native=Process.GetProcessById(OwnedPid))rows.Add(new{index,control,background,count,command,handles=native.HandleCount,memory=native.WorkingSet64,alive=App.GetProcessID()==OwnedPid});File.WriteAllText(Path.Combine(Stage,"lifecycle-rows.json"),Json.Serialize(rows));
   await Task.Delay(100);
  }
  App.CommandInProgress=false;
  if(Hash(path)!=initial)throw new Exception("生命周期验证不应改写隔离原件");Console.WriteLine("PASS: 同一真实零件连续"+cycles+"次只读打开关闭，会话及文件保持");
 }
 static void OutputBatchProbe(string source,bool forceGc,bool longPath=false,bool fullParameters=true)
 {
  using(var registration=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{BCFD8A8A-472B-42E2-AC62-58BC17773650}\InprocServer32"))
   if(App.UnloadAddIn(new Uri((string)registration.GetValue("CodeBase")).LocalPath)!=0)throw new Exception("专用实例交互插件未能停止");
  App.UserControlBackground=true;App.CommandInProgress=true;App.DocumentVisible(false,1);
  var names=new[]{"700091.11-01.SLDPRT","GD-150F-20211228090001663.SLDPRT","700091.11-02.SLDPRT","M.BZ.Z.D01.A23.V00-01-20220408023629198.SLDPRT","BP1-直线滑轨HG30-20220104103207144.SLDPRT","MDBB32-500Z-20240130032612996.SLDPRT","CDS2F160-500(0_0).SLDPRT","BP1-六角薄螺母 细牙-20220316085838843.SLDPRT"};
  var folder=longPath?Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs",Guid.NewGuid().ToString("N"),"native",Guid.NewGuid().ToString("N"),"package"):Path.Combine(Stage,"owned");Directory.CreateDirectory(folder);
  File.WriteAllText(Path.Combine(Stage,"batch-folder.json"),Json.Serialize(new{folder,longPath,folderCharacters=folder.Length}));
  foreach(var name in names){File.Copy(Path.Combine(source,name),Path.Combine(folder,name));File.SetAttributes(Path.Combine(folder,name),FileAttributes.Normal);}
  var controlled=names.ToDictionary(name=>name,name=>Path.Combine(folder,name),StringComparer.OrdinalIgnoreCase);var rows=new List<object>();
  foreach(var name in names)
  {
   void At(string step)=>File.WriteAllText(Path.Combine(Stage,"batch-current.txt"),name+"："+step);
   var path=controlled[name];var hash=Hash(path);int error=0,warning=0;At("打开");var model=App.GetOpenDocumentByName(path) as IModelDoc2??App.OpenDoc6(path,1,1,"",ref error,ref warning);
   if(model==null||error!=0||model.GetPathName()!=path)throw new Exception("实际输出文件未能独立打开："+name+"；"+error);
   App.ActivateDoc3(model.GetTitle(),false,1,ref error);
   At("设计表嵌入检查");if(T("ArchiveDesignTableLinks").GetMethod("PrepareAndCapture",Flags).Invoke(null,new object[]{App,model,Stage})!=null)throw new Exception("已准备输出仍需设计表处理："+name);
   T("ArchiveResourceIsolation").GetMethod("ValidateEmbeddedObjects",Flags).Invoke(null,new object[]{model});
   At("全部配置参数");var parameters=fullParameters?(string)T("ArchiveSmartComponentLinks").GetMethod("ConfigurationParameters",Flags).Invoke(null,new object[]{model}):"";
   At("实际设计来源");T("NativeDesignDependencies").GetMethod("ValidateManaged",Flags).Invoke(null,new object[]{model,controlled});
   At("资源检查");if((bool)T("ArchiveResourceIsolation").GetMethod("PrepareManaged",Flags).Invoke(null,new object[]{model,controlled}))throw new Exception("输出仍需资源处理："+name);
   At("生产文件指纹");if((string)T("ArchiveFileFingerprint").GetMethod("Read",Flags).Invoke(null,new object[]{path,CancellationToken.None})!=hash)throw new Exception("生产指纹读取后文件变化："+name);
   At("关闭");var closing=((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(item=>new{path=item.GetPathName(),title=item.GetTitle(),kind=item.GetType()}).OrderByDescending(item=>item.kind).ToArray();foreach(var item in closing)if(App.GetOpenDocumentByName(item.path)!=null)App.CloseDoc(item.title);Application.DoEvents();
   if(forceGc){At("强制回收");GC.Collect();GC.WaitForPendingFinalizers();Application.DoEvents();}
   if(App.GetDocumentCount()!=0||Hash(path)!=hash)throw new Exception("输出读取后文档或文件状态异常："+name);
   using(var process=Process.GetProcessById(OwnedPid))rows.Add(new{name,forceGc,parameters=parameters.Length,handles=process.HandleCount,memory=process.WorkingSet64});
   File.WriteAllText(Path.Combine(Stage,"batch-rows.json"),Json.Serialize(rows));
  }
  App.CommandInProgress=false;File.WriteAllText(Path.Combine(Stage,"batch-results.json"),Json.Serialize(new{passed=true,forceGc,files=rows.Count,originalNamesKept=true,bytesUnchanged=true}));Console.WriteLine("PASS: 大装配连续8个真实输出逐项读取、配置参数、资源与关闭验证完成");
 }
 static void TableProbe(string source,bool inplace=false,bool production=false,string fileName=null)
 {
  var name=fileName??"BP1-六角薄螺母-20220316085721612.SLDPRT";var original=Path.Combine(source,name);var sourceHash=Hash(original);var path=Path.Combine(Stage,name);File.Copy(original,path);File.SetAttributes(path,FileAttributes.Normal);
  int error=0,warning=0;var model=App.OpenDoc6(path,1,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("设计表隔离样本未打开");
  var table=model.GetDesignTable() as IDesignTable;if(table==null)throw new Exception("样本缺少原生设计表");
  IEnumerable<IFeature> AllFeatures()=>((IEnumerable)T("ArchiveSmartComponentLinks").GetMethod("Features",Flags).Invoke(null,new object[]{model})).Cast<IFeature>();
  var configurationNames=(model.GetConfigurationNames() as Array).Cast<object>().Select(Convert.ToString).ToArray();
  var noteStates=AllFeatures().Where(feature=>feature.GetTypeName2()=="NotesAreaFtrFolder").ToDictionary(feature=>feature.Name,feature=>(feature.IsSuppressed2(3,configurationNames) as Array).Cast<object>().Select(Convert.ToBoolean).ToArray());
  var parameters=T("ArchiveSmartComponentLinks").GetMethod("ConfigurationParameters",Flags);var before=(string)parameters.Invoke(null,new object[]{model});File.WriteAllText(Path.Combine(Stage,"parameters-before.txt"),before);
  var capture=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags);var geometry=capture.Invoke(null,new object[]{model});
  var objects=(model.Extension.GetOLEObjects(0) as Array)?.OfType<ISwOLEObject>().ToArray()??new ISwOLEObject[0];if(objects.Length!=1)throw new Exception("样本嵌入对象数量不符");
  var data=(byte[])T("ArchiveEmbeddedPackageInspection").GetMethod("ReadWorkbook",Flags).Invoke(null,new object[]{objects[0]});
  var clean=(byte[])T("ArchiveEmbeddedPackageInspection").GetMethod("RemoveWorkbookLinks",Flags).Invoke(null,new object[]{data});var workbook=Path.Combine(Stage,"clean-design-table.xlsx");File.WriteAllBytes(workbook,clean);
  File.WriteAllText(Path.Combine(Stage,"table-before.json"),Json.Serialize(new{configurationCount=(model.GetConfigurationNames() as Array)?.Length,sourceType=table.SourceType,linked=table.LinkToFile,table.FileName,cleanBytes=clean.Length}));
  var updated=false;
  if(production)updated=T("ArchiveDesignTableLinks").GetMethod("PrepareAndCapture",Flags).Invoke(null,new object[]{App,model,Stage})!=null;
  else if(inplace)
  {
   var existing=Process.GetProcessesByName("EXCEL").Select(process=>process.Id).ToArray();dynamic excel=Activator.CreateInstance(System.Type.GetTypeFromProgID("Excel.Application",true));
   GetWindowThreadProcessId(new IntPtr((int)excel.Hwnd),out var excelId);var excelProcess=Process.GetProcessById((int)excelId);var excelStarted=excelProcess.StartTime.ToUniversalTime().Ticks;
   if(existing.Contains((int)excelId))throw new Exception("Excel 返回已有会话，未接管");
   File.WriteAllText(Path.Combine(Stage,"owned-excel.json"),Json.Serialize(new{pid=excelId,start=excelStarted}));
   try
   {
    excel.Visible=false;excel.DisplayAlerts=false;excel.EnableEvents=false;excel.AutomationSecurity=3;excel.AskToUpdateLinks=false;
    dynamic blank=excel.Workbooks.Add();excel.Calculation=-4135;blank.Close(false);Marshal.ReleaseComObject((object)blank);
    model.Visible=true;int activation=0;App.ActivateDoc3(model.GetTitle(),false,1,ref activation);
    dynamic sheet=table.EditTable2(true);if(sheet==null)throw new Exception("原生设计表未返回工作表");
    dynamic actual=sheet.Application;GetWindowThreadProcessId(new IntPtr((int)actual.Hwnd),out var actualId);if(actualId!=excelId)throw new Exception("原生设计表未连接本次专用 Excel，未修改其内容");
    dynamic book=sheet.Parent;var links=book.LinkSources(1) as Array;
    foreach(var link in links?.Cast<object>()??Enumerable.Empty<object>())book.BreakLink(Convert.ToString(link),1);
    foreach(dynamic page in book.Worksheets)page.UsedRange.ClearHyperlinks();
    Marshal.ReleaseComObject((object)book);Marshal.ReleaseComObject((object)sheet);
    updated=table.UpdateTable(3,true);
   }
   finally
   {
    try{excel.Quit();}catch(COMException){}Marshal.ReleaseComObject((object)excel);
    try{excelProcess.Refresh();if(!excelProcess.HasExited&&excelProcess.StartTime.ToUniversalTime().Ticks==excelStarted)excelProcess.Kill();}catch(InvalidOperationException){}
   }
  }
  else {table.EditFeature();table.SourceType=2;table.FileName=workbook;table.LinkToFile=false;updated=table.UpdateFeature();}
  table=model.GetDesignTable() as IDesignTable;
  File.WriteAllText(Path.Combine(Stage,"table-updated.json"),Json.Serialize(new{updated,sourceType=table?.SourceType,linked=table?.LinkToFile,fileName=table?.FileName}));
  if(!updated)throw new Exception("原生设计表内容回写失败");
  if(inplace)
  {
   var restored=new List<object>();foreach(var pair in noteStates)
   {
    var feature=AllFeatures().Single(item=>item.Name==pair.Key);foreach(var value in new[]{false,true})
    {var selected=configurationNames.Where((name,index)=>pair.Value[index]==value).ToArray();if(selected.Length>0)restored.Add(new{name=pair.Key,suppressed=value,result=feature.SetSuppression2(value?0:1,3,selected)});}
   }
   File.WriteAllText(Path.Combine(Stage,"notes-restored.json"),Json.Serialize(restored));
  }
  var after=(string)parameters.Invoke(null,new object[]{model});File.WriteAllText(Path.Combine(Stage,"parameters-after.txt"),after);
  geometry.GetType().GetMethod("Verify",Flags).Invoke(geometry,new[]{capture.Invoke(null,new object[]{model}),(object)"design table links"});
  T("ArchiveResourceIsolation").GetMethod("ValidateEmbeddedObjects",Flags).Invoke(null,new object[]{model});
  error=warning=0;if(!model.Save3(1,ref error,ref warning)||error!=0)throw new Exception("设计表隔离副本保存失败");App.CloseAllDocuments(true);
  model=App.OpenDoc6(path,1,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("处理后零件重开失败");
  var reopened=(string)parameters.Invoke(null,new object[]{model});File.WriteAllText(Path.Combine(Stage,"parameters-reopened.txt"),reopened);
  if(before!=reopened)throw new Exception("重开后配置参数不一致");
  T("ArchiveResourceIsolation").GetMethod("ValidateEmbeddedObjects",Flags).Invoke(null,new object[]{model});
  if(Hash(original)!=sourceHash)throw new Exception("设计表处理改写了原始隔离源");
  File.WriteAllText(Path.Combine(Stage,"table-probe-results.json"),Json.Serialize(new{passed=true,sourceUnchanged=true,allConfigurationParametersKept=true,currentGeometryKept=true,originalFileNameKept=true,embeddedWorkbookHasNoExternalLinks=true}));
  Console.WriteLine("PASS: 真实标准件设计表通过原生接口清除工作簿外链，全部配置尺寸及参数特征保留，保存重开通过");
 }
 static void GeometryFile(string source,bool detach=false)
 {
  var hash=Hash(source);var path=Path.Combine(Stage,Path.GetFileName(source));File.Copy(source,path);int error=0,warning=0;
  var model=App.OpenDoc6(path,1,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("几何样本未打开："+error);
  var rows=new List<object>();
  object State(){var mass=model.Extension.GetMassProperties2(2,out var status,false);return new{status,mass,bodies=(((IPartDoc)model).GetBodies2(-1,false) as Array)?.Cast<object>().OfType<IBody2>().Select(body=>new{kind=body.GetType(),faces=body.GetFaceCount(),edges=body.GetEdgeCount(),box=body.GetBodyBox(),mass=body.GetMassProperties(1.0)}).ToArray()};}
  foreach(var configuration in (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString)??Enumerable.Empty<string>())
  {
   if(model.ConfigurationManager.ActiveConfiguration.Name!=configuration&&!model.ShowConfiguration2(configuration))throw new Exception("样本配置未打开："+configuration);
   var before=State();var rebuilt=model.EditRebuild3();var after=State();rows.Add(new{configuration,before,rebuilt,after});
  }
  string detachError=null;
  if(detach)
  {
   try{T("ArchiveReferenceIsolation").GetMethod("Detach",Flags,null,new[]{typeof(IModelDoc2)},null).Invoke(null,new object[]{model});}catch(TargetInvocationException failure){detachError=failure.InnerException?.Message??failure.Message;}
   File.WriteAllText(Path.Combine(Stage,"detachment-details.txt"),(string)T("PluginDiagnosticLog").GetMethod("Snapshot",Flags).Invoke(null,null));
  }
  File.WriteAllText(Path.Combine(Stage,"geometry-file-results.json"),Json.Serialize(new{file=source,rows,detachError,sourceUnchanged=hash==Hash(source)}));
  Console.WriteLine("PASS: 各配置几何原生只读核对完成，重建仅发生在隔离副本，源文件未改写");
 }
 static void BreakFalseFile(string source)
 {
  var initial=Hash(source);var path=Path.Combine(Stage,Path.GetFileName(source));File.Copy(source,path);int error=0,warning=0;
  var model=App.OpenDoc6(path,1,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("断链样本未打开");
  var capture=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags);var states=new Dictionary<string,object>();
  foreach(var name in (model.GetConfigurationNames() as Array).Cast<object>().Select(Convert.ToString))
  {if(model.ConfigurationManager.ActiveConfiguration.Name!=name&&!model.ShowConfiguration2(name))throw new Exception("断链前配置未打开："+name);states.Add(name,capture.Invoke(null,new object[]{model}));}
  File.WriteAllText(Path.Combine(Stage,"break-started.txt"),"同名隔离副本：仅断链接，不重新插入源特征");
  model.Extension.BreakAllExternalFileReferences2(false);
  foreach(var pair in states)
  {
   if(model.ConfigurationManager.ActiveConfiguration.Name!=pair.Key&&!model.ShowConfiguration2(pair.Key))throw new Exception("断链后配置未打开："+pair.Key);
   pair.Value.GetType().GetMethod("Verify",Flags).Invoke(pair.Value,new[]{capture.Invoke(null,new object[]{model}),(object)pair.Key});
   if((bool)T("ArchiveReferenceIsolation").GetMethod("HasDesignLinks",Flags).Invoke(null,new object[]{model}))throw new Exception("仍有活动设计链接");
  }
  error=warning=0;if(!model.Save3(1,ref error,ref warning)||error!=0)throw new Exception("断链副本保存失败："+error);App.CloseDoc(model.GetTitle());
  model=App.OpenDoc6(path,1,1,"",ref error,ref warning);if(model==null||error!=0||model.GetPathName()!=path)throw new Exception("断链副本独立重开失败");
  foreach(var pair in states){if(model.ConfigurationManager.ActiveConfiguration.Name!=pair.Key&&!model.ShowConfiguration2(pair.Key))throw new Exception("重开配置失败");pair.Value.GetType().GetMethod("Verify",Flags).Invoke(pair.Value,new[]{capture.Invoke(null,new object[]{model}),(object)pair.Key});}
  if(Hash(source)!=initial)throw new Exception("原文件被改写");
  File.WriteAllText(Path.Combine(Stage,"break-false-results.json"),Json.Serialize(new{passed=true,fileName=Path.GetFileName(path),sourceUnchanged=true,allConfigurationGeometryMatches=true,savedAndReopened=true,noActiveDesignLinks=true,configurations=states.Keys}));
  Console.WriteLine("PASS: 仅断链同名副本保存重开及全部配置几何核对通过，原文件未变");
 }
 static void PackGoProbe(string source)
 {
  App.UserControlBackground=true;
  var hashes=Directory.GetFiles(Path.GetDirectoryName(source)).Where(path=>!Path.GetFileName(path).StartsWith("~$",StringComparison.Ordinal)).ToDictionary(path=>path,Hash);
  try
  {
   var input=Path.Combine(Stage,"input");Directory.CreateDirectory(input);
   foreach(var original in hashes.Keys){var copy=Path.Combine(input,Path.GetFileName(original));File.Copy(original,copy);File.SetAttributes(copy,FileAttributes.Normal);}
   var inputSource=Path.Combine(input,Path.GetFileName(source));
   var dependencies=App.GetDocumentDependencies2(inputSource,false,false,false) as Array;
   if(dependencies!=null)for(var index=1;index<dependencies.Length;index+=2)foreach(var old in (Convert.ToString(dependencies.GetValue(index))??"").Split('|')){var target=Path.Combine(input,Path.GetFileName(old));if(File.Exists(target)&&!App.ReplaceReferencedDocument(inputSource,old,target))throw new Exception("隔离输入引用定位未完成："+Path.GetFileName(old));}
   int error=0,warning=0;var model=App.OpenDoc6(inputSource,1,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("原生打包隔离源图未打开："+error);
   var capture=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags);var states=new Dictionary<string,object>();
   string[] Features(IModelDoc2 document){var list=new List<string>();for(var feature=document.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)list.Add(feature.Name+"|"+feature.GetTypeName2());return list.ToArray();}
   var originalFeatures=Features(model);
   foreach(var name in (model.GetConfigurationNames() as Array).Cast<object>().Select(Convert.ToString)){if(model.ConfigurationManager.ActiveConfiguration.Name!=name&&!model.ShowConfiguration2(name))throw new Exception("源配置未打开："+name);states.Add(name,capture.Invoke(null,new object[]{model}));}
   var pack=(IPackAndGo)model.Extension.GetPackAndGo();pack.IncludeSuppressed=true;pack.IncludeDrawings=true;pack.FlattenToSingleFolder=false;
   if(!pack.GetDocumentNames(out var values)||values is not Array names)throw new Exception("原生打包清单未取得");
   var sources=names.Cast<object>().Select(Convert.ToString).ToArray();if(sources.Length==0||!string.Equals(sources[0],inputSource,StringComparison.OrdinalIgnoreCase))throw new Exception("原生打包根文件不符");
   if(sources.Any(path=>!path.StartsWith(input+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))throw new Exception("原生打包包含隔离输入之外的源文件，未执行保存");
   var output=Path.Combine(Stage,"package");Directory.CreateDirectory(output);
   var destinations=sources.Select((path,index)=>Path.Combine(output,index.ToString("D4"),Path.GetFileName(path))).ToArray();foreach(var path in destinations)Directory.CreateDirectory(Path.GetDirectoryName(path));
   if(!pack.SetDocumentSaveToNames(destinations))throw new Exception("原名打包目标设置失败");
   var statuses=model.Extension.SavePackAndGo((PackAndGo)pack) as Array;
   File.WriteAllText(Path.Combine(Stage,"pack-and-go-inventory.json"),Json.Serialize(new{sources,destinations,statuses=statesAsArray(statuses),exists=destinations.Select(File.Exists).ToArray()}));
   if(statuses==null||statuses.Length!=sources.Length||statuses.Cast<object>().Any(value=>Convert.ToInt32(value)!=0)||destinations.Any(path=>!File.Exists(path)))throw new Exception("原生打包未完整成功，请检查逐文件状态");
   App.CloseAllDocuments(true);error=warning=0;model=App.OpenDoc6(destinations[0],1,3,"",ref error,ref warning);if(model==null||error!=0||model.GetPathName()!=destinations[0])throw new Exception("原生打包副本重开失败");
   foreach(var pair in states){if(model.ConfigurationManager.ActiveConfiguration.Name!=pair.Key&&!model.ShowConfiguration2(pair.Key))throw new Exception("打包配置未打开");pair.Value.GetType().GetMethod("Verify",Flags).Invoke(pair.Value,new[]{capture.Invoke(null,new object[]{model}),(object)pair.Key});}
   var references=new List<object>();var outside=new List<string>();
   for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
    if(feature.GetDefinition() is IDerivedPartFeatureData derived){var resolved=derived.GetModelDoc()?.GetPathName();references.Add(new{feature=feature.Name,saved=derived.PathName,resolved});if(!string.IsNullOrWhiteSpace(resolved)&&!resolved.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))outside.Add(resolved);}
   var loaded=((App.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(document=>document.GetPathName()).Where(path=>!string.IsNullOrWhiteSpace(path)).ToArray();outside.AddRange(loaded.Where(path=>!path.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)));
   var featuresKept=originalFeatures.SequenceEqual(Features(model),StringComparer.Ordinal);
   File.WriteAllText(Path.Combine(Stage,"pack-and-go-results.json"),Json.Serialize(new{geometryMatches=true,featuresKept,originalNamesKept=sources.Select(Path.GetFileName).SequenceEqual(destinations.Select(Path.GetFileName)),references,loaded,outside=outside.Distinct().ToArray(),sourceUnchanged=hashes.All(pair=>Hash(pair.Key)==pair.Value)}));
   if(!featuresKept||outside.Count!=0||hashes.Any(pair=>Hash(pair.Key)!=pair.Value))throw new Exception("原生打包副本的特征、来源或原文件保护检查未通过");
   Console.WriteLine("PASS: 原生Pack and Go保留原名、全部配置几何及特征，实际载入来源均在包内，原件未改写");
  }
  finally{var unchanged=hashes.All(pair=>Hash(pair.Key)==pair.Value);File.WriteAllText(Path.Combine(Stage,"source-preservation.json"),Json.Serialize(new{files=hashes.Count,unchanged}));if(!unchanged)throw new Exception("原始输入保护检查未通过，不得认定打包成功");}
  object[] statesAsArray(Array values)=>values?.Cast<object>().ToArray();
 }
 static void PrepareManagedFixture(string source)
 {
  var ownedBase=Path.GetDirectoryName(Stage)+Path.DirectorySeparatorChar;
  source=Path.GetFullPath(source);
  if(!source.StartsWith(ownedBase+"fixture-",StringComparison.OrdinalIgnoreCase)
   ||!File.Exists(Path.Combine(source,"source-copy-manifest.json")))throw new Exception("只允许准备本任务完整复制的隔离图纸目录");
  if((App.GetDocuments() as Array)?.Length>0)throw new Exception("准备隔离文件前不应有任何已加载图档");
  var files=Directory.GetFiles(source).Where(Native).ToArray();var known=Directory.GetFiles(source).ToDictionary(Path.GetFileName,StringComparer.OrdinalIgnoreCase);var changed=new List<object>();
  foreach(var owner in files)
  {
   var references=App.GetDocumentDependencies2(owner,false,false,false) as Array;
   if(references==null)continue;
   if(references.Length%2!=0)throw new Exception("隔离文件原生引用清单不完整");
   for(var index=1;index<references.Length;index+=2)
    foreach(var reference in (Convert.ToString(references.GetValue(index))??"").Split('|'))
     if(Native(reference)&&known.TryGetValue(Path.GetFileName(reference),out var target)&&!string.Equals(reference,target,StringComparison.OrdinalIgnoreCase))
     {
      if(!App.ReplaceReferencedDocument(owner,reference,target))throw new Exception("隔离文件引用重定位失败："+Path.GetFileName(owner)+" → "+Path.GetFileName(reference));
      changed.Add(new{owner,reference,target});
     }
  }
  if((App.GetDocuments() as Array)?.Length>0)throw new Exception("隔离准备不应打开图档");
  File.WriteAllText(Path.Combine(source,"native-fixture-prepared.json"),Json.Serialize(new{passed=true,files=files.Length,source,directNativeReferencesRepointed=changed,preparedHashes=files.ToDictionary(path=>path,Hash)}));
  Console.WriteLine("PASS: 隔离图纸已按原名重定位原生引用，全程未打开或保存原始F盘图档");
 }
 static void ArchiveScopeProbe(string source)
 {
  var hashes=Directory.GetFiles(source).Where(p=>!Path.GetFileName(p).StartsWith("~$",StringComparison.Ordinal)).ToDictionary(p=>p,Hash);
  var rootPath=Path.Combine(source,"CW0030002.10.SLDASM");int error=0,warning=0;var model=App.OpenDoc6(rootPath,2,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("完整存档范围样本未打开");
  var scanner=GetField(Addin,"scanner");var tree=scanner.GetType().GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{model,true});
  var inventory=((IEnumerable)scanner.GetType().GetMethod("AddNativeAssociatedDrawings",Flags).Invoke(scanner,new[]{(object)model,tree})).Cast<string>().ToArray();
  var nodes=Walk(tree).ToArray();var files=nodes.Where(n=>!string.IsNullOrWhiteSpace((string)Get(n,"FullPath"))).GroupBy(n=>(string)Get(n,"FullPath"),StringComparer.OrdinalIgnoreCase).Select(g=>new{path=g.Key,name=(string)Get(g.First(),"FileName"),kind=Get(g.First(),"Kind").ToString(),role=Get(g.First(),"Role").ToString(),documentId=Get(g.First(),"DocumentId"),importedSource=(string)Get(g.First(),"ImportedSourcePath")}).ToArray();
  if(hashes.Any(p=>Hash(p.Key)!=p.Value))throw new Exception("原生范围读取改变了隔离源文件");
  var step=Path.Combine(source,"LHH30CA2R1000Z0C-E20E20(1).STEP");
  File.WriteAllText(Path.Combine(Stage,"actual-archive-scope.json"),Json.Serialize(new{passed=true,sourceUnchanged=true,inventory,files,lhhStepPresent=files.Any(f=>f.path.Equals(step,StringComparison.OrdinalIgnoreCase)),lhhStepInInventory=inventory.Contains(step,StringComparer.OrdinalIgnoreCase)}));
  Console.WriteLine("PASS: 实际设计树与原生存档范围已读取，节点="+nodes.Length+"；文件="+files.Length+"；滑轨STEP是否在范围="+files.Any(f=>f.path.Equals(step,StringComparison.OrdinalIgnoreCase)));
 }
 static void ManagedReopen(string recordPath)
 {
  var ownedBase=Path.GetDirectoryName(Stage)+Path.DirectorySeparatorChar;
  if(!Path.GetFullPath(recordPath).StartsWith(ownedBase,StringComparison.OrdinalIgnoreCase))throw new Exception("只能重开本任务生成的受控包");
  var record=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(recordPath));
  if(!(bool)record["passed"])throw new Exception("原生受控包尚未通过准备检查");
  var stored=((IEnumerable)record["files"]).Cast<Dictionary<string,object>>().ToArray();
  var protectedFiles=stored.ToDictionary(file=>(string)file["path"],file=>(string)file["sha256"]);
  var folder=Path.Combine(Stage,"View","retrieved");Directory.CreateDirectory(folder);
  var manifest=New("ControlledOpenManifestDto");var project=Guid.NewGuid();Set(manifest,"ProjectId",project);Set(manifest,"Id",Guid.NewGuid());
  var list=(IList)Activator.CreateInstance(T("ControlledOpenManifestDto").GetProperty("Files").PropertyType);
  var rootName=record.ContainsKey("rootName")?(string)record["rootName"]:(string)record["mode"]=="hostassembly"?"CW0030002.101.SLDASM":(string)record["mode"]=="hostall"?"CW0030002.10.SLDASM":"标准机架后安全门型材框.SLDPRT";string root=null;object rootFile=null;
  var rootType=rootName.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?2:1;
  var capture=T("ArchiveReferenceIsolation").GetMethod("CaptureConfigurations",Flags);
  object originalGeometry=null;
  if(rootType==2)
  {
   var source=(string)stored.Single(file=>Path.GetFileName((string)file["path"])==rootName)["source"];
   if(!Path.GetFullPath(source).StartsWith(Path.Combine(ownedBase,"fixture-"),StringComparison.OrdinalIgnoreCase))throw new Exception("原装配基准只能读取本任务隔离源");
   App.CommandInProgress=true;App.DocumentVisible(false,2);
   var sourceHash=Hash(source);int er=0,wr=0;var original=App.OpenDoc6(source,2,1,"",ref er,ref wr);if(original==null||er!=0)throw new Exception("原复杂装配基准读取失败");
   var activation=0;App.ActivateDoc3(original.GetTitle(),false,1,ref activation);
   if((App.ActiveDoc as IModelDoc2)?.GetPathName()!=source)throw new Exception("原装配未成为活动文档，无法完整解析基准");
   var resolved=((IAssemblyDoc)original).ResolveAllLightWeightComponents(false);
   File.WriteAllText(Path.Combine(Stage,"original-baseline-activation.json"),Json.Serialize(new{activation,resolved,activePath=(App.ActiveDoc as IModelDoc2)?.GetPathName()}));
   try{originalGeometry=capture.Invoke(null,new object[]{original});WriteGeometryEvidence("original-baseline-geometry.json",originalGeometry);}
   catch{var components=(((IAssemblyDoc)original).GetComponents(false) as Array)?.OfType<IComponent2>().Select(c=>new{name=c.Name2,path=c.GetPathName(),suppression=c.GetSuppression2(),model=(c.GetModelDoc2() as IModelDoc2)?.GetPathName(),transform=c.GetTotalTransform(false)?.ArrayData,localTransform=c.Transform2?.ArrayData}).ToArray();File.WriteAllText(Path.Combine(Stage,"original-baseline-components.json"),Json.Serialize(new{configuration=original.ConfigurationManager.ActiveConfiguration.Name,components}));throw;}
   GC.Collect();GC.WaitForPendingFinalizers();App.CloseAllDocuments(true);
   App.CommandInProgress=false;App.DocumentVisible(true,2);
   if(Hash(source)!=sourceHash)throw new Exception("基准读取不应改写原隔离源");
  }
  foreach(var entry in stored)
  {
   var origin=(string)entry["path"];var path=Path.Combine(folder,Path.GetFileName(origin));File.Copy(origin,path);var file=New("ControlledOpenFileDto");
   Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));Set(file,"Sha256",Hash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Revision","W1");Set(file,"IsNativeIsolated",true);Set(file,"Configuration",Native(path)?App.GetActiveConfigurationName(path):"");
   if(Path.GetFileName(path)==rootName){Set(file,"IsRoot",true);root=path;rootFile=file;}
   list.Add(file);Bind(path,file,project);
  }
  if(root==null)throw new Exception("受控包缺少原报错图档");Set(manifest,"Files",list);Set(manifest,"RootRelativePath",rootName);Set(manifest,"RootDocumentId",Get(rootFile,"DocumentId"));
  T("ControlledReferenceMaterializer").GetMethod("Prepare",Flags).Invoke(null,new object[]{App,root,manifest});
  var controlled=Directory.GetFiles(folder).Where(path=>!Path.GetFileName(path).StartsWith("~$",StringComparison.Ordinal)).ToDictionary(Path.GetFileName,path=>path,StringComparer.OrdinalIgnoreCase);
  foreach(var path in controlled.Values.Where(path=>rootType==1&&Native(path)&&path!=root))
  {int er=0,wr=0;var dependency=App.OpenDoc6(path,path.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?2:1,3,"",ref er,ref wr);if(dependency==null||er!=0||dependency.GetPathName()!=path)throw new Exception("受控来源未能独立取回："+Path.GetFileName(path));}
  var model=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{root,rootType,Get(rootFile,"Configuration"),manifest});
  if(model==null||model.GetPathName()!=root)throw new Exception("原报错图档未从新的受控目录打开");
  var baseline=originalGeometry??capture.Invoke(null,new object[]{model});
  try{T("ArchiveReferenceIsolation").GetMethod("VerifyManagedCopy",Flags).Invoke(null,new object[]{model,baseline,controlled});}
  catch{WriteGeometryEvidence("retrieved-geometry.json",capture.Invoke(null,new object[]{model}));throw;}
  var loaded=((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(document=>document.GetPathName()).ToArray();
  if(loaded.Any(path=>!controlled.Values.Contains(path,StringComparer.OrdinalIgnoreCase)
    &&!(bool)T("NativeArchiveManifest").GetMethod("IsInterconnectCachePath",Flags).Invoke(null,new object[]{path})
    &&!(bool)T("PdmAddin").GetMethod("IsSolidWorksTemporaryVirtualComponentPath",Flags).Invoke(null,new object[]{path})))throw new Exception("重新取回时加载了包外图档");
  foreach(var loadedModel in (App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>())
   T("NativeDesignDependencies").GetMethod("ValidateManaged",Flags).Invoke(null,new object[]{loadedModel,controlled});
  var dimension=rootType==1?FindDimension(model,null,false):null;string edited=null;
  if(dimension!=null)
  {
   model.SetReadOnlyState(false);File.SetAttributes(root,FileAttributes.Normal);var value=dimension.SystemValue;
   if(dimension.SetSystemValue3(value+0.0001,1,null)!=0||!model.EditRebuild3())throw new Exception("原生参数特征不能正常修改");
   if(dimension.SetSystemValue3(value,1,null)!=0||!model.EditRebuild3())throw new Exception("原生参数验证后未恢复几何");edited=dimension.FullName;
   T("ArchiveReferenceIsolation").GetMethod("VerifyManagedCopy",Flags).Invoke(null,new object[]{model,baseline,controlled});
  }
  if(protectedFiles.Any(file=>Hash(file.Key)!=file.Value))throw new Exception("读取验证修改了原存档包");
  var occurrences=model is IAssemblyDoc assembly?((assembly.GetComponents(false) as Array)?.OfType<IComponent2>()??Enumerable.Empty<IComponent2>()).Select(component=>new{instance=component.Name2,fileName=Path.GetFileName(component.GetPathName()),configuration=component.ReferencedConfiguration,suppressed=component.GetSuppression2()==0,embedded=component.IsVirtual}).ToArray():null;
  File.WriteAllText(Path.Combine(Stage,"managed-reopen-results.json"),Json.Serialize(new{passed=true,rootName,originalNamesKept=true,loadedOnlyControlledFiles=true,allConfigurationsVerified=true,originalAssemblyGeometryMatches=rootType==2,loaded,componentOccurrences=occurrences,editedDimension=edited,archiveBytesUnchanged=true}));
  Console.WriteLine("PASS: 原名受控包在新目录经真实下载重定位流程打开，全部配置重建、实际来源和原存档保护检查通过");
 }
 static void WriteGeometryEvidence(string name,object states)
 {
  var rows=new Dictionary<string,object>();foreach(DictionaryEntry pair in (IDictionary)states){var state=pair.Value;rows[(string)pair.Key]=new{bodies=GetField(state,"Bodies"),components=GetField(state,"Components"),mass=GetField(state,"Mass"),partShapes=GetField(state,"PartShapes"),componentGeometry=GetField(state,"ComponentGeometry"),features=GetField(state,"Features")};}
  File.WriteAllText(Path.Combine(Stage,name),Json.Serialize(rows));
 }
 static void AssemblyGeometry(string source)
 {
  var name="CW0030002.101.SLDASM";var path=Path.Combine(source,name);var initial=Hash(path);int error=0,warning=0;
  var model=App.OpenDoc6(path,2,3,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("复杂子装配未打开："+error);
  var rows=new List<object>();
  foreach(var cfg in (model.GetConfigurationNames() as Array).Cast<object>().Select(Convert.ToString))
  {
   if(model.ConfigurationManager.ActiveConfiguration.Name!=cfg&&!model.ShowConfiguration2(cfg))throw new Exception("复杂子装配配置未打开");
   int activation=0;App.ActivateDoc3(model.GetTitle(),false,1,ref activation);var resolve=((IAssemblyDoc)model).ResolveAllLightWeightComponents(false);
   object State(){var mass=model.Extension.GetMassProperties2(2,out var status,false);return new{status,mass,components=(((IAssemblyDoc)model).GetComponents(false) as Array)?.OfType<IComponent2>().Select(component=>new{name=component.Name2,path=component.GetPathName(),suppression=component.GetSuppression2(),loaded=component.GetModelDoc2()!=null}).ToArray()};}
   var before=State();var capture=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags);var verifiedBefore=capture.Invoke(null,new object[]{model});
   var rebuilt=model.ForceRebuild3(false);var verifiedAfter=capture.Invoke(null,new object[]{model});
   File.WriteAllText(Path.Combine(Stage,"component-geometry-"+rows.Count+".json"),Json.Serialize(new{cfg,before=GetField(verifiedBefore,"ComponentGeometry"),after=GetField(verifiedAfter,"ComponentGeometry")}));
   verifiedBefore.GetType().GetMethod("Verify",Flags).Invoke(verifiedBefore,new[]{verifiedAfter,(object)cfg});
   rows.Add(new{cfg,activation,resolve,before,rebuilt,after=State(),componentGeometryMatches=true});
  }
  File.WriteAllText(Path.Combine(Stage,"assembly-geometry.json"),Json.Serialize(new{name,rows,sourceUnchanged=Hash(path)==initial}));
  if(Hash(path)!=initial)throw new Exception("诊断改写了隔离输入");Console.WriteLine("PASS: 复杂子装配全配置只读几何诊断完成，未保存任何输入文件");
 }
 static void LeafAudit(string source)
 {
  var path=Path.Combine(source,"BP1-直线滑轨HG30-20220104103207144.SLDPRT");var before=Hash(path);
  var loadedBefore=((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(model=>model.GetPathName()).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
  var configurations=App.GetConfigurationNames(path) as Array;var dependencies=App.GetDocumentDependencies2(path,false,false,false) as Array;
  object loadedReferences=null;var loaded=App.GetOpenDocumentByName(path) as IModelDoc2;
  if(loaded!=null)
  {
   loaded.Extension.ListExternalFileReferences2(out var models,out var components,out var features,out var types,out var statuses,out var entities,out var owners,out var options,out var referencedConfigurations);
   var derived=new List<object>();for(var feature=loaded.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)if(feature.GetDefinition() is IDerivedPartFeatureData data)derived.Add(new{feature=feature.Name,path=data.PathName});
   loadedReferences=new{configuration=loaded.ConfigurationManager.ActiveConfiguration.Name,models,components,features,types,statuses,derived,active=T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{loaded})};
  }
  File.WriteAllText(Path.Combine(Stage,"leaf-metadata.json"),Json.Serialize(new{path,configurationCount=configurations?.Length,configurations=configurations?.Cast<object>().Take(8).ToArray(),dependencies=dependencies?.Cast<object>().ToArray(),loadedReferences,sourceUnchanged=Hash(path)==before}));
  var loadedAfter=((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).Select(model=>model.GetPathName()).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
  if(Hash(path)!=before||!loadedBefore.SequenceEqual(loadedAfter,StringComparer.Ordinal))throw new Exception("关闭状态元数据诊断不应打开或改写图档");
  Console.WriteLine("PASS: 只读取得导轨原生配置目录和依赖清单，未打开图档");
 }
 static void SmartProbe(string source)
 {
  var original=Path.Combine(source,"BP1-直线滑轨HG30-20220104103207144.SLDPRT");var originalHash=Hash(original);
  var path=Path.Combine(Stage,Path.GetFileName(original));File.Copy(original,path);File.SetAttributes(path,FileAttributes.Normal);int error=0,warning=0;
  var model=App.OpenDoc6(path,1,1,"",ref error,ref warning);if(model==null||error!=0||model.GetPathName()!=path)throw new Exception("智能关联隔离样本未打开");
  string[] Features(){var result=new List<string>();for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)result.Add(feature.Name+"|"+feature.GetTypeName2()+"|"+(feature.GetDefinition() is ISmartComponentFeatureData));return result.ToArray();}
  object References(){model.Extension.ListExternalFileReferences2(out var models,out var components,out var features,out var types,out var statuses,out var entities,out var owners,out var options,out var configs);return new{models,features,statuses};}
  var configurations=(model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();var featuresBefore=Features();var referencesBefore=References();
  var capture=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags);var geometryBefore=capture.Invoke(null,new object[]{model});
  var parameters=T("ArchiveSmartComponentLinks").GetMethod("ConfigurationParameters",Flags);var parametersBefore=(string)parameters.Invoke(null,new object[]{model});
  File.WriteAllText(Path.Combine(Stage,"smart-before.json"),Json.Serialize(new{configurationCount=configurations?.Length,features=featuresBefore,references=referencesBefore}));
  var smartArguments=new object[]{model,new Dictionary<string,string>{{Path.GetFileName(path),path}},false};
  T("ArchiveSmartComponentLinks").GetMethod("Prepare",Flags).Invoke(null,smartArguments);
  var referencesAfter=References();geometryBefore.GetType().GetMethod("Verify",Flags).Invoke(geometryBefore,new[]{capture.Invoke(null,new object[]{model}),(object)"smart link"});
  if(!featuresBefore.SequenceEqual(Features(),StringComparer.Ordinal)||!configurations.SequenceEqual((model.GetConfigurationNames() as Array).Cast<object>().Select(Convert.ToString),StringComparer.Ordinal))throw new Exception("智能关联处理改变了原有特征或配置目录");
  error=warning=0;if(!model.Save3(1,ref error,ref warning)||error!=0)throw new Exception("智能关联副本保存失败："+error);App.CloseAllDocuments(true);
  model=App.OpenDoc6(path,1,3,"",ref error,ref warning);if(model==null||error!=0||model.GetPathName()!=path)throw new Exception("智能关联副本未能原名重开");
  var active=((IEnumerable)T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model})).Cast<object>().ToArray();
  var allConfigurationParametersKept=parametersBefore==(string)parameters.Invoke(null,new object[]{model});
  var result=new{configurationCount=configurations.Length,allConfigurationParametersKept,featuresKept=featuresBefore.SequenceEqual(Features(),StringComparer.Ordinal),referencesBefore,referencesAfter,reopenedReferences=References(),active,sourceUnchanged=Hash(original)==originalHash};
  File.WriteAllText(Path.Combine(Stage,"smart-probe-results.json"),Json.Serialize(result));
  if(Hash(original)!=originalHash||active.Length>0||!allConfigurationParametersKept)throw new Exception("智能关联仍有效、全部配置参数不一致，或原文件保护检查未通过");
  Console.WriteLine("PASS: 智能关联仅在隔离副本断开，原名重开、特征和配置目录保持，原件未变；全配置几何还需继续验证");
 }
 static void DrawingCase(string source,string mode)
 {
  var name="7000093.101.03-02";var sourcePart=Path.Combine(source,name+".SLDPRT");var sourceDrawing=Path.Combine(source,name+".SLDDRW");
  var hashes=new[]{sourcePart,sourceDrawing}.ToDictionary(path=>path,Hash);var raw=Path.Combine(Stage,"raw");Directory.CreateDirectory(raw);
  var rawPart=Path.Combine(raw,name+".SLDPRT");var rawDrawing=Path.Combine(raw,name+".SLDDRW");File.Copy(sourcePart,rawPart);File.Copy(sourceDrawing,rawDrawing);
  foreach(var file in new[]{rawPart,rawDrawing})File.SetAttributes(file,FileAttributes.Normal);
  var prefix="uplm_"+Guid.NewGuid().ToString("N")+"_";if(mode=="short")prefix="u_"+Guid.NewGuid().ToString("N").Substring(0,8)+"_";
  var part=Path.Combine(Stage,prefix+name+".SLDPRT");var drawing=Path.Combine(Stage,prefix+name+".SLDDRW");
  var dependencies=App.GetDocumentDependencies2(rawDrawing,false,false,false) as Array;
  foreach(var reference in Enumerable.Range(0,dependencies.Length/2).Select(index=>Convert.ToString(dependencies.GetValue(index*2+1))))
   if(Path.GetFileName(reference)==Path.GetFileName(rawPart)&&reference!=rawPart&&!App.ReplaceReferencedDocument(rawDrawing,reference,rawPart))throw new Exception("源副本定位失败");
  if(mode=="copydoc")
  {var result=App.CopyDocument(rawDrawing,drawing,new[]{rawPart},new[]{part},3);Console.WriteLine("CopyDocument结果="+result);if(result!=0)throw new Exception("原生复制失败");}
  else
  {File.Copy(rawPart,part);File.Copy(rawDrawing,drawing);if(!App.ReplaceReferencedDocument(drawing,rawPart,part))throw new Exception("副本重定位失败");}
  int error=0,warning=0;
  if(mode!="prefix")
  {
   var model=App.OpenDoc6(part,1,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("模型未打开");App.ActivateDoc3(model.GetTitle(),false,0,ref error);
   T("ArchiveReferenceIsolation").GetMethod("Detach",Flags,null,new[]{typeof(IModelDoc2)},null).Invoke(null,new object[]{model});
   if(!model.Save3(1,ref error,ref warning)||error!=0)throw new Exception("模型未保存");
  }
  Console.WriteLine("开始打开工程图；模式="+mode);var document=App.OpenDoc6(drawing,3,1,"",ref error,ref warning);if(document==null||error!=0||document.GetPathName()!=drawing)throw new Exception("工程图身份不符");App.ActivateDoc3(document.GetTitle(),false,0,ref error);
  T("ArchiveResourceIsolation").GetMethod("Detach",Flags).Invoke(null,new object[]{document});Console.WriteLine("开始保存工程图；模式="+mode);
  if(!document.Save3(1,ref error,ref warning)||error!=0)throw new Exception("工程图未保存："+error);
  if(hashes.Any(pair=>Hash(pair.Key)!=pair.Value))throw new Exception("原件被修改");
  File.WriteAllText(Path.Combine(Stage,"drawing-case-results.json"),Json.Serialize(new{passed=true,mode,sourceUnchanged=true,drawing,part}));Console.WriteLine("PASS: 工程图保存完成；模式="+mode);
 }
 static void ImportPreflight(string source)
 {
  var name="LHH30CA2R1000Z0C-E20E20(1).SLDASM";var original=Path.Combine(source,name);var before=Hash(original);
  var path=Path.Combine(Stage,name);File.Copy(original,path);int error=0,warning=0;
  var model=App.OpenDoc6(path,2,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("预检样本未打开："+error);
  var timer=Stopwatch.StartNew();string message=null;
  try{T("ArchiveNativeCopyWorkspace").GetMethod("ValidateLoadedImports",Flags).Invoke(null,new object[]{App,new[]{path}});}
  catch(TargetInvocationException exception)when(exception.InnerException is InvalidOperationException){message=exception.InnerException.Message;}
  if(message==null||!message.Contains("未上传任何文件")||message.Contains("被占用"))throw new Exception("缺失导入模型未提前准确拦截");
  if(before!=Hash(original)||App.GetOpenDocumentByName(path)==null)throw new Exception("源文件或窗口未保留");
  File.WriteAllText(Path.Combine(Stage,"import-preflight-results.json"),Json.Serialize(new{passed=true,message,seconds=timer.Elapsed.TotalSeconds,sourceUnchanged=true,windowKept=true}));
  Console.WriteLine("PASS: 缺失导入模型在耗时副本准备前准确拦截，源文件与窗口保留；秒="+timer.Elapsed.TotalSeconds);
 }
 static void ManagedSourceProbe(string source)
 {
  var folder=Path.Combine(Stage,"copy");Directory.CreateDirectory(folder);
  foreach(var file in Directory.GetFiles(source).Where(path=>!Path.GetFileName(path).StartsWith("~$",StringComparison.Ordinal)))File.Copy(file,Path.Combine(folder,Path.GetFileName(file)));
  var controlled=Directory.GetFiles(folder).ToDictionary(Path.GetFileName,path=>path,StringComparer.OrdinalIgnoreCase);
  foreach(var path in controlled.Values.Where(Native))
  {
   var deps=App.GetDocumentDependencies2(path,false,false,false) as Array;
   if(deps!=null)for(var i=1;i<deps.Length;i+=2)foreach(var reference in Convert.ToString(deps.GetValue(i)).Split('|'))
    if(Native(reference)&&controlled.TryGetValue(Path.GetFileName(reference),out var target)&&reference!=target&&!App.ReplaceReferencedDocument(path,reference,target))throw new Exception("诊断副本引用重定位失败");
  }
  var modelPath=controlled["LHH30CA2R1000Z0C-E20E20(1).SLDASM"];int error=0,warning=0;
  var model=App.OpenDoc6(modelPath,2,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("来源诊断样本未打开："+error);
  object Snapshot(){
   model.Extension.ListExternalFileReferences2(out var models,out var components,out var names,out var types,out var statuses,out var entities,out var owners,out var options,out var configs);
   var features=((IEnumerable)T("ArchiveSmartComponentLinks").GetMethod("Features",Flags).Invoke(null,new object[]{model})).Cast<IFeature>().Select(feature=>{string imported=null;if(feature.Is3DInterconnectFeature&&feature.GetImportedFeatureParameters(out var parameters)==0&&parameters is IImport3DInterconnectData data)imported=data.GetReferencedFileName();return new{name=feature.Name,type=feature.GetTypeName2(),interconnect=feature.Is3DInterconnectFeature,imported};}).ToArray();
   var children=(((IAssemblyDoc)model).GetComponents(false) as Array)?.OfType<IComponent2>().Select(c=>new{name=c.Name2,path=c.GetPathName(),imported=c.GetImportedPath(),virtualPart=c.IsVirtual,loaded=(c.GetModelDoc2() as IModelDoc2)?.GetPathName()}).ToArray();
   return new{configuration=model.ConfigurationManager.ActiveConfiguration.Name,models,components,names,types,statuses,configs,features,children,managed=T("NativeDesignDependencies").GetMethod("ReadManaged",Flags).Invoke(null,new object[]{model})};
  }
  var before=Snapshot();File.WriteAllText(Path.Combine(Stage,"managed-source-before.json"),Json.Serialize(before));
  var changed=(bool)T("NativeDesignDependencies").GetMethod("Relocate",Flags).Invoke(null,new object[]{model,controlled,null});
  File.WriteAllText(Path.Combine(Stage,"managed-source-after.json"),Json.Serialize(new{changed,snapshot=Snapshot()}));
  Console.WriteLine("PASS: 实际导入来源和原生引用已导出，仅检查任务副本");
 }
 static void LhhBreakProbe(string source)
 {
  var name="LHH30CA2R1000Z0C-E20E20(1).SLDASM";var path=Path.Combine(Stage,name);var initial=Hash(Path.Combine(source,name));
  File.Copy(Path.Combine(source,name),path);File.Copy(Path.Combine(source,"LHH30CA2R1000Z0C-E20E20(1).STEP"),Path.Combine(Stage,"LHH30CA2R1000Z0C-E20E20(1).STEP"));
  int error=0,warning=0;var model=App.OpenDoc6(path,2,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("导入装配断链副本未打开");
  IFeature[] Features()=>((IEnumerable)T("ArchiveSmartComponentLinks").GetMethod("Features",Flags).Invoke(null,new object[]{model})).Cast<IFeature>().ToArray();
  var states=(IDictionary)T("ArchiveReferenceIsolation").GetMethod("CaptureConfigurations",Flags).Invoke(null,new object[]{model});
  var included=new HashSet<string>(Features().Where(f=>!f.Is3DInterconnectFeature).Select(f=>f.Name),StringComparer.Ordinal);
  string Parameters()=>(string)T("ArchiveSmartComponentLinks").GetMethod("ConfigurationParametersForFeatures",Flags).Invoke(null,new object[]{model,included});
  var parameters=Parameters();var result=Features().Single(f=>f.Is3DInterconnectFeature).BreakLink(true,true);
  File.WriteAllText(Path.Combine(Stage,"break-native-result.json"),Json.Serialize(new{result,features=Features().Select(f=>new{name=f.Name,type=f.GetTypeName2(),imported=f.Is3DInterconnectFeature}).ToArray()}));
  void Verify(){foreach(DictionaryEntry pair in states){if(model.ConfigurationManager.ActiveConfiguration.Name!=(string)pair.Key&&!model.ShowConfiguration2((string)pair.Key))throw new Exception("断链配置未恢复");var actual=T("ArchiveReferenceIsolation").GetMethod("Capture",Flags).Invoke(null,new object[]{model});pair.Value.GetType().GetMethod("Verify",Flags).Invoke(pair.Value,new[]{actual,pair.Key});}if(parameters!=Parameters())throw new Exception("断链改变已有参数或特征状态");}
  Verify();if(Features().Any(f=>f.Is3DInterconnectFeature))throw new Exception("导入链接未断开");
  var saveOptions=1|Convert.ToInt32(Enum.Parse(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Plugin.Location),"SolidWorks.Interop.swconst.dll")).GetType("SolidWorks.Interop.swconst.swSaveAsOptions_e"),"swSaveAsOptions_IncludeVirtualSubAsmComps"));
  if(!model.Save3(saveOptions,ref error,ref warning)||error!=0)throw new Exception("断链装配保存失败："+error);GC.Collect();GC.WaitForPendingFinalizers();App.CloseDoc(model.GetTitle());
  File.Move(Path.Combine(Stage,"LHH30CA2R1000Z0C-E20E20(1).STEP"),Path.Combine(Stage,"source-offline.step"));
  model=App.OpenDoc6(path,2,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("无STEP重开失败："+error);Verify();
  var children=(((IAssemblyDoc)model).GetComponents(false) as Array)?.OfType<IComponent2>().ToArray()??Array.Empty<IComponent2>();
  if(children.Any(c=>c.GetSuppression2()!=0&&c.GetModelDoc2()==null))throw new Exception("重开后嵌入组件未加载");
  if(Hash(Path.Combine(source,name))!=initial)throw new Exception("源装配被改写");
  File.WriteAllText(Path.Combine(Stage,"lhh-break-results.json"),Json.Serialize(new{passed=true,allConfigurationGeometryKept=true,allExistingParametersKept=true,originalFileNameKept=true,sourceUnchanged=true,savedAndReopenedWithoutStep=true,result,children=children.Select(c=>new{name=c.Name2,path=c.GetPathName(),isVirtual=c.IsVirtual}).ToArray()}));
  Console.WriteLine("PASS: 原生导入装配仅断同步链接，嵌入结构、已有参数及所有配置几何保持，保存后无STEP重开通过");
 }
 static void ImportProbe(string source)
 {
  var interconnect=(int)Enum.Parse(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Plugin.Location),"SolidWorks.Interop.swconst.dll")).GetType("SolidWorks.Interop.swconst.swUserPreferenceToggle_e"),"swMultiCAD_Enable3DInterconnect");
  var enabled=App.GetUserPreferenceToggle(interconnect);Console.WriteLine("3D Interconnect enabled before probe="+enabled);
  App.SetUserPreferenceToggle(interconnect,true);
  try
  {
  var name="LHH30CA2R1000Z0C-E20E20(1).SLDASM";var rows=new List<object>();
  File.Copy(Path.Combine(source,"LHH30CA2R1000Z0C-E20E20(1).STEP"),Path.Combine(Stage,"LHH30CA2R1000Z0C-E20E20(1).STEP"));
  foreach(var prefix in new[]{"uplm_probe_"})
  {
   var path=Path.Combine(Stage,prefix+name);File.Copy(Path.Combine(source,name),path);int error=0,warning=0;
   var model=App.OpenDoc6(path,2,577,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("打开探针失败："+error);
   ((IAssemblyDoc)model).ResolveAllLightWeightComponents(false);model.EditRebuild3();
   foreach(var component in (((IAssemblyDoc)model).GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>()??Enumerable.Empty<IComponent2>())
    if(component.GetSuppression2()==0)Console.WriteLine("隔离探针加载抑制组件="+component.SetSuppression2(2)+"；已加载="+(component.GetModelDoc2()!=null));
   var features=new List<object>();for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)features.Add(new{name=feature.Name,type=feature.GetTypeName2(),interconnect=feature.Is3DInterconnectFeature});
   var components=(((IAssemblyDoc)model).GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().Select(c=>new{name=c.Name2,path=c.GetPathName(),loaded=c.GetModelDoc2()!=null,suppression=c.GetSuppression2(),imported=c.GetImportedPath()}).ToArray();
   rows.Add(new{prefix,error,warning,features,components});
  }
  File.WriteAllText(Path.Combine(Stage,"import-probe.json"),Json.Serialize(rows));Console.WriteLine(Json.Serialize(rows));
  }
  finally{App.SetUserPreferenceToggle(interconnect,enabled);}
 }
 static void FirstArchive(string source)
 {
  var originals=Directory.GetFiles(source).ToDictionary(path=>path,Hash,StringComparer.OrdinalIgnoreCase);
  var fixture=Path.Combine(Stage,"fixture");Directory.CreateDirectory(fixture);
  foreach(var path in originals.Keys){var target=Path.Combine(fixture,Path.GetFileName(path));File.Copy(path,target);File.SetAttributes(target,FileAttributes.Normal);}
  File.WriteAllText(Path.Combine(Stage,"original-source-hashes.json"),Json.Serialize(originals));
  var inventory=Directory.GetFiles(fixture);var requested=inventory.Where(Native).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  var timer=Stopwatch.StartNew();
  try
  {
   using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,requested,Stage,new Action<string>(name=>Console.WriteLine("处理："+name))))
   {
    var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);int count=0;
    foreach(var copy in ((IEnumerable)Get(workspace,"Files")).Cast<object>()){File.Copy((string)copy.GetType().GetField("Path",Flags).GetValue(copy),Path.Combine(output,Path.GetFileName((string)copy.GetType().GetField("Source",Flags).GetValue(copy))));count++;}
    File.WriteAllText(Path.Combine(Stage,"first-archive-results.json"),Json.Serialize(new{passed=true,requested=requested.Count,files=count,seconds=timer.Elapsed.TotalSeconds}));
    Console.WriteLine("PASS: 全套首次存档副本准备完成，文件="+count);
   }
  }
  finally
  {
   var unchanged=originals.All(pair=>Hash(pair.Key)==pair.Value);File.WriteAllText(Path.Combine(Stage,"source-preservation.json"),Json.Serialize(new{unchanged,files=originals.Count}));
   if(!unchanged)throw new Exception("源文件改变");
  }
 }
 static void H620Workspace(string source)
 {
  var names=new[]{"H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT","H620_外观.STEP"};
  var originalHashes=names.ToDictionary(name=>name,name=>Hash(Path.Combine(source,name)));
  var fixture=Path.Combine(Stage,"fixture");Directory.CreateDirectory(fixture);
  foreach(var name in names)File.Copy(Path.Combine(source,name),Path.Combine(fixture,name));
  var input=Path.Combine(fixture,names[0]);var requested=new Dictionary<string,string>{{input,input}};
  var baselinePath=Path.Combine(Stage,"baseline_"+names[0]);File.Copy(input,baselinePath);
  int baselineError=0,baselineWarning=0;var baseline=App.OpenDoc6(baselinePath,1,1,"",ref baselineError,ref baselineWarning);
  if(baseline==null||baselineError!=0)throw new Exception("基准零件未能打开");
  var baselineMass=baseline.Extension.GetMassProperties2(2,out var baselineMassStatus,false) as double[];
  if(baselineMass==null||baselineMassStatus!=0)throw new Exception("基准几何无法读取");
  var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);
  var timer=Stopwatch.StartNew();
  using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,Directory.GetFiles(fixture),requested,Stage,new Action<string>(Console.WriteLine)))
   foreach(var copy in ((IEnumerable)Get(workspace,"Files")).Cast<object>())
    File.Copy((string)copy.GetType().GetField("Path",Flags).GetValue(copy),Path.Combine(output,Path.GetFileName((string)copy.GetType().GetField("Source",Flags).GetValue(copy))));
  Directory.Move(fixture,fixture+"-offline");
  // Only the native file is available in the open directory; no STEP source.
  File.Move(Path.Combine(output,names[1]),Path.Combine(Stage,"saved-step-not-in-package.STEP"));
  int error=0,warning=0;var model=App.OpenDoc6(Path.Combine(output,names[0]),1,1,"",ref error,ref warning);
  if(model==null||error!=0||!string.Equals(model.GetPathName(),Path.Combine(output,names[0]),StringComparison.OrdinalIgnoreCase))throw new Exception("固化文件独立打开失败或返回同名原件："+error);
  var active=(IEnumerable)T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model});
  if(active.Cast<object>().Any())throw new Exception("固化后仍存在活动外部来源");
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
   if(feature.Is3DInterconnectFeature)throw new Exception("仍有3DInterconnect链接");
  if(((IPartDoc)model).GetBodies2(0,false) is not Array bodies||bodies.Length==0)throw new Exception("独立打开后没有实体");
  var savedMass=model.Extension.GetMassProperties2(2,out var savedMassStatus,false) as double[];
  if(savedMass==null||savedMassStatus!=0||savedMass.Length!=baselineMass.Length||baselineMass.Where((value,index)=>Math.Abs(value-savedMass[index])>Math.Max(1e-10,Math.Abs(value)*1e-7)).Any())throw new Exception("独立保存重开后几何与源模型不一致");
  if(!originalHashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value))throw new Exception("原件发生变化");
  File.WriteAllText(Path.Combine(Stage,"h620-workspace-results.json"),Json.Serialize(new{passed=true,bodyCount=bodies.Length,noActiveLinks=true,noInterconnect=true,stepAbsentFromPackage=true,sourceUnchanged=true,reopenedGeometryMatches=true,baselineMass,savedMass,seconds=timer.Elapsed.TotalSeconds}));
  Console.WriteLine("PASS: H620保存几何固化后无STEP依赖，独立重开实体完整，原件未变更");
 }
 static void ReferenceProbe(string source)
 {
  var names=new[]{"H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT","H620_外观.STEP"};
  var hashes=names.ToDictionary(name=>name,name=>Hash(Path.Combine(source,name)));
  var folder=Path.Combine(Stage,"probe");Directory.CreateDirectory(folder);
  foreach(var name in names)File.Copy(Path.Combine(source,name),Path.Combine(folder,"uplm_probe_"+name));
  var part=Path.Combine(folder,"uplm_probe_"+names[0]);var step=Path.Combine(folder,"uplm_probe_"+names[1]);
  var deps=App.GetDocumentDependencies2(part,false,false,false) as Array;
  var replacements=new List<object>();
  if(deps!=null)for(var index=1;index<deps.Length;index+=2)
   foreach(var reference in (Convert.ToString(deps.GetValue(index))??"").Split('|'))
    replacements.Add(new{reference,target=step,replaced=App.ReplaceReferencedDocument(part,reference,step)});
  int error=0,warning=0;var model=App.OpenDoc6(part,1,1,"",ref error,ref warning);
  if(model==null||error!=0)throw new Exception("探针副本打开失败："+error);
  var features=new List<object>();
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
  {
   string imported=null;int? result=null;
   if(feature.Is3DInterconnectFeature){result=feature.GetImportedFeatureParameters(out var value);if(value is IImport3DInterconnectData data)imported=data.GetReferencedFileName();}
   features.Add(new{name=feature.Name,type=feature.GetTypeName2(),interconnect=feature.Is3DInterconnectFeature,result,imported});
  }
  var output=new{dependencies=deps,replacements,features,activeReferences=T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model}),warning,sourceUnchanged=hashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value)};
  File.WriteAllText(Path.Combine(Stage,"reference-probe.json"),Json.Serialize(output));Console.WriteLine(Json.Serialize(output));
  if(!hashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value))throw new Exception("原件发生变化");
 }
 static void Workspace(string source,bool drawings=false,string single=null,bool keepOriginal=true)
 {
  var inventory=Directory.GetFiles(source);var root=Path.Combine(source,"SS5Y-8位电磁阀座.SLDASM");int error=0,warning=0;
  var original=keepOriginal?App.OpenDoc6(root,2,1,"",ref error,ref warning):null;if(keepOriginal&&(original==null||error!=0))throw new Exception("保护样本未能打开");
  if(keepOriginal)App.ActivateDoc3(original.GetTitle(),false,0,ref error);var before=Hash(root);var dirty=original?.GetSaveFlag()??false;
  var requested=new[]{"SS5Y-8位电磁阀座.SLDASM","7000093.101.03-02.SLDPRT","7000093.101.03-02.SLDDRW"}.Select(name=>Path.Combine(source,name)).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  if(single!=null)requested=new Dictionary<string,string>{{Path.Combine(source,single),Path.Combine(source,single)}};
  if(drawings)requested=inventory.Where(path=>path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  var timer=Stopwatch.StartNew();Console.WriteLine("BEGIN: 原装配保持打开时准备独立副本，含工程图");
  using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,requested,Stage,new Action<string>(name=>Console.WriteLine("处理："+name))))
  {
   var result=new List<object>();var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);
   foreach(var file in ((IEnumerable)Get(workspace,"Files")).Cast<object>())
   {
    var originalPath=(string)file.GetType().GetField("Source",Flags).GetValue(file);var path=(string)file.GetType().GetField("Path",Flags).GetValue(file);var target=Path.Combine(output,Path.GetFileName(originalPath));File.Copy(path,target);
    result.Add(new{source=originalPath,path=target,sha256=Hash(target),frozen=(bool)file.GetType().GetField("ContentFrozen",Flags).GetValue(file)});
   }
   File.WriteAllText(Path.Combine(Stage,"workspace-results.json"),Json.Serialize(new{passed=true,originalKept=original!=null&&App.GetOpenDocumentByName(root)!=null,originalUnchanged=before==Hash(root),originalDirtyUnchanged=original==null||dirty==original.GetSaveFlag(),cold=!keepOriginal,seconds=timer.Elapsed.TotalSeconds,files=result}));
  }
  if(before!=Hash(root)||keepOriginal&&(App.GetOpenDocumentByName(root)==null||dirty!=original.GetSaveFlag()))throw new Exception("原装配或修改标志发生变化");
  Console.WriteLine(keepOriginal?"PASS: 原窗口、原文件和修改标志保持；独立副本准备完成":"PASS: 独立冷启动副本准备完成，源文件未改变");
 }
 static void Isolate(string catalogPath)
 {
  var prepared=Directory.Exists(catalogPath);var folder=Path.Combine(Stage,"View","isolated");
  if(!prepared)
  {
  var catalog=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(catalogPath));var raw=(string)catalog["raw"];
  Directory.CreateDirectory(folder);
  var files=((IEnumerable)catalog["catalog"]).Cast<Dictionary<string,object>>().ToArray();
  foreach(var file in files){var name=(string)file["fileName"];var source=(string)file["path"];if(!File.Exists(source))source=Path.Combine(raw+"-offline",name);var path=Path.Combine(folder,name);File.Copy(source,path);File.SetAttributes(path,FileAttributes.Normal);}
  foreach(var path in Directory.GetFiles(folder).Where(Native))
  {
   var deps=App.GetDocumentDependencies2(path,false,false,false) as Array;if(deps==null)continue;
   for(var i=1;i<deps.Length;i+=2)foreach(var source in (Convert.ToString(deps.GetValue(i))??"").Split('|'))
   {var target=Path.Combine(folder,Path.GetFileName(source));if(File.Exists(target)&&!string.Equals(source,target,StringComparison.OrdinalIgnoreCase))App.ReplaceReferencedDocument(path,source,target);}
  }
  }

  if(prepared){Directory.CreateDirectory(folder);foreach(var source in Directory.GetFiles(catalogPath)){var target=Path.Combine(folder,Path.GetFileName(source));File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);}}
  var rows=new List<object>();
  foreach(var name in new[]{"PSU-08OG-20250301014805833.SLDPRT","SS5Y-8位电磁阀座.SLDASM","LHH30CA2R1000Z0C-E20E20(1).SLDASM","7000093.101.03-02.SLDPRT"})
  {
   var path=Path.Combine(folder,name);if(!File.Exists(path)){Console.WriteLine("SKIP: "+name);continue;}
   var deps=App.GetDocumentDependencies2(path,false,false,false) as Array;if(deps!=null)for(var d=1;d<deps.Length;d+=2)foreach(var saved in (Convert.ToString(deps.GetValue(d))??"").Split('|')){var target=Path.Combine(folder,Path.GetFileName(saved));if(File.Exists(target)&&saved!=target&&!App.ReplaceReferencedDocument(path,saved,target))throw new Exception("隔离副本引用定位失败："+saved);}
   var error=0;var warning=0;Console.WriteLine("BEGIN: 固化外部设计来源 "+name);
   var model=App.OpenDoc6(path,name.EndsWith("SLDASM")?2:1,1,"",ref error,ref warning);
   if(model==null||model.GetPathName()!=path||error!=0)throw new Exception("隔离副本打开失败："+name+"；"+error);
   App.ActivateDoc3(model.GetTitle(),false,0,ref error);Console.WriteLine("激活结果="+error+"；活动文档="+(App.ActiveDoc as IModelDoc2)?.GetPathName());if(model is IAssemblyDoc)Console.WriteLine("隔离副本重建="+model.EditRebuild3());
   var changed=(bool)T("ArchiveReferenceIsolation").GetMethod("Detach",Flags,null,new[]{typeof(IModelDoc2)},null).Invoke(null,new object[]{model});
   changed|=(bool)T("ArchiveResourceIsolation").GetMethod("Detach",Flags).Invoke(null,new object[]{model});
   if(changed){error=warning=0;if(!model.Save3(513,ref error,ref warning)||error!=0)throw new Exception("固化副本保存失败："+name+"；"+error);}
   rows.Add(new{name,changed,dependencies=App.GetDocumentDependencies2(path,false,false,false)});
   App.CloseDoc(model.GetTitle());Console.WriteLine("PASS: "+name+"；是否固化="+changed);
  }
  File.WriteAllText(Path.Combine(Stage,"isolation-results.json"),Json.Serialize(rows));
 }
 static void Dependencies(string catalogPath)
 {
  var catalog=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(catalogPath));var raw=(string)catalog["raw"];
  var files=((IEnumerable)catalog["catalog"]).Cast<Dictionary<string,object>>().ToArray();var names=new HashSet<string>(files.Select(file=>(string)file["fileName"]),StringComparer.OrdinalIgnoreCase);
  var rows=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in files)
  {
   var name=(string)file["fileName"];var path=(string)file["path"];if(!File.Exists(path))path=Path.Combine(raw+"-offline",name);
   var dependencies=Native(path)?App.GetDocumentDependencies2(path,false,false,false) as Array:null;var related=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   if(dependencies!=null)for(var i=1;i<dependencies.Length;i+=2)foreach(var source in (Convert.ToString(dependencies.GetValue(i))??"").Split('|')){var leaf=Path.GetFileName(source);if(names.Contains(leaf)&&!leaf.Equals(name,StringComparison.OrdinalIgnoreCase))related.Add(leaf);}
   rows[name]=related.ToArray();
  }
  File.WriteAllText(Path.Combine(Stage,"native-dependencies.json"),Json.Serialize(rows));Console.WriteLine("PASS: 已读取实际原生逐文件依赖；SS5Y直接文件数="+rows["SS5Y-8位电磁阀座.SLDASM"].Length);
 }
 static void Bind(string path,object file,Guid project)
 {
  if(!(bool)T("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")}))throw new Exception("隔离版本绑定未写入");
  var isolation=Get(file,"NativeIsolation");
  if(isolation!=null||(bool)Get(file,"IsNativeIsolated"))
  {
   var args=new object[]{path,null};
   if(!(bool)T("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args)
    ||!(bool)T("PdmDocumentIdentityStore").GetMethod("TryRecordNativeIsolation",Flags).Invoke(null,new[]{(object)path,args[1],isolation==null?Get(file,"VersionId"):Get(isolation,"ReferenceSnapshotId"),(object)Hash(path)}))throw new Exception("隔离取回缺少原生来源校验记录");
  }
 }
 static bool Native(string p)=>new[]{".SLDASM",".SLDPRT",".SLDDRW"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase);
 static string Hash(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static System.Type T(string name)=>Plugin.GetType("Upton.Pdm.SolidWorks."+name,true);
 static object New(string name,params object[] args)=>Activator.CreateInstance(T(name),Flags&~BindingFlags.Static,null,args,null);
 static void Set(object o,string name,object value)=>o.GetType().GetProperty(name,Flags).SetValue(o,value);
 static object Get(object o,string name)=>o.GetType().GetProperty(name,Flags).GetValue(o);
 static void F(object o,string name,object value)=>o.GetType().GetField(name,Flags).SetValue(o,value);
 [ComImport,Guid("00000016-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMessageFilter
 { [PreserveSig]int HandleInComingCall(int type,IntPtr task,int tick,IntPtr info);[PreserveSig]int RetryRejectedCall(IntPtr task,int tick,int reject);[PreserveSig]int MessagePending(IntPtr task,int tick,int pending); }
 sealed class OleFilter:IMessageFilter
 {
  [DllImport("ole32.dll")]static extern int CoRegisterMessageFilter(IMessageFilter filter,out IMessageFilter previous);
  public static void Register(){CoRegisterMessageFilter(new OleFilter(),out var old);}public static void Revoke(){CoRegisterMessageFilter(null,out var old);}
  public int HandleInComingCall(int t,IntPtr a,int i,IntPtr b)=>0;public int RetryRejectedCall(IntPtr a,int tick,int reject)=>reject==2&&tick<60000?250:-1;public int MessagePending(IntPtr a,int b,int c)=>2;
 }
}
