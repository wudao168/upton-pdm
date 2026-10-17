using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static Assembly assembly;
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string name,string text);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
 [STAThread] static void Main(string[] args)
 {
  AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])),new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
  assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
  Exception failure=null;
  using(var host=new Form{Opacity=0,ShowInTaskbar=false})
  using(var timer=new System.Windows.Forms.Timer{Interval=50})
  {
   timer.Tick+=(_,__)=>{var dialog=FindWindow("#32770","整体存档前保存文件");if(dialog!=IntPtr.Zero)SendMessage(dialog,0x111,new IntPtr(6),IntPtr.Zero);if(args.Length>1){dialog=FindWindow("#32770","UPLM");if(dialog!=IntPtr.Zero){GetWindowThreadProcessId(dialog,out var process);if(process==(uint)System.Diagnostics.Process.GetCurrentProcess().Id)SendMessage(dialog,0x111,new IntPtr(1),IntPtr.Zero);}}};
   host.Shown+=async (_,__)=>{try{if(args.Length>1&&args[1]=="--rename-fill-regression"){ComponentRenameAndFillScenario(host);return;}if(args.Length>1&&args[1]=="--document-rename-regression"){RootRenameScenario();return;}if(args.Length>1&&args[1]=="--archive-network-regression"){await ArchiveNetworkScenario();return;}if(args.Length>1&&args[1]=="--cycle-scan-regression"){VisualReferenceCycleScenario();return;}if(args.Length>1&&args[1]=="--strict-scan-matrix"){VisualReferenceCycleScenario();StrictScanMatrix();SixDrawingDiscoveryScenario();NativeManifestScenario();RootRenameScenario();PlannedTaskTimerScenario(host);SuppressedReadScenario();return;}if(args.Length>1){await OpenDispatcherScenario(args[1]=="--expect-handle-failure");return;}await OpenDispatcherScenario(false);VisualReferenceCycleScenario();StrictScanMatrix();SixDrawingDiscoveryScenario();NativeManifestScenario();RootRenameScenario();ComponentRenameAndFillScenario(host);PlannedTaskTimerScenario(host);DrawingDiscoveryScenario();ControlledReferenceScenario();ControlledScannerScenario();SearchScopeScenario();PropertyCompletenessScenario();await PreflightGateScenario(host);await ArchiveNetworkScenario();await LatePreparationGate(host);DrawingSaveGate();SuppressedReadScenario();ReadOnlyOpenScenario();DiagnosticTabScenario(host);CenteringScenario();CloseReferencedScenario();EntryScenario(host);await Scenario(false,false);await Scenario(true,false);await Scenario(true,true);}catch(Exception e){failure=e;}finally{host.Close();}};
   timer.Start();Application.Run(host);
  }
  if(failure!=null){Console.WriteLine("FAIL: "+failure.GetType().FullName+" "+failure.Message);if(failure.InnerException!=null)Console.WriteLine("INNER: "+failure.InnerException.GetType().FullName+" "+failure.InnerException.Message);Environment.ExitCode=1;}
 }
 static async Task OpenDispatcherScenario(bool expectOldFailure)
 {
  var owner=Thread.CurrentThread.ManagedThreadId;
  using(var control=new LifecycleControl{Visible=false})
  {
   var context=(SynchronizationContext)New("TaskPaneSynchronizationContext",control);
   var before=new TaskCompletionSource<int>();
   await Task.Run(()=>context.Post(_=>before.SetResult(Thread.CurrentThread.ManagedThreadId),null));
   var actual=await before.Task;
   if(expectOldFailure)
   {
    if(actual==owner)throw new Exception("Legacy context did not reproduce thread-pool fallback");
    var invoke=new TaskCompletionSource<string>();context.Post(_=>{try{control.BeginInvoke((Action)(()=>{}));invoke.SetResult("unexpected success");}catch(InvalidOperationException error){invoke.SetResult(error.Message);}},null);
    if(!(await invoke.Task).Contains("Invoke"))throw new Exception("Legacy HWND error not reproduced");
    Console.WriteLine("PASS: reproduced legacy worker-thread fallback and the same Invoke/BeginInvoke error before HWND creation");
   }
   else
   {
    if(actual!=owner||control.IsHandleCreated)throw new Exception("Before-handle callback used the wrong thread or created the task-pane HWND");
    var handle=control.Handle;control.RemoveHandle();
    var after=new TaskCompletionSource<int>();
    await Task.Run(()=>context.CreateCopy().Post(_=>after.SetResult(Thread.CurrentThread.ManagedThreadId),null));
    if(await after.Task!=owner||control.IsHandleCreated)throw new Exception("Handle destruction or copied context changed callback ownership");
    var sent=0;await Task.Run(()=>context.Send(_=>sent=Thread.CurrentThread.ManagedThreadId,null));
    if(sent!=owner)throw new Exception("Send callback escaped the SW UI thread");
    control.Dispose();var cancelled=new TaskCompletionSource<int>();context.Post(_=>cancelled.SetResult(Thread.CurrentThread.ManagedThreadId),null);
    if(await cancelled.Task!=owner)throw new Exception("Cancellation cleanup escaped the UI thread after pane disposal");
    Console.WriteLine("PASS: Post, Send and copied contexts stay on owner UI thread before handle creation, after destruction and during disposal cleanup");
   }
  }
  if(expectOldFailure)return;
  foreach(var mode in new[]{"before-handle","destroyed-handle","worker","disconnect"})
  {
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var path=Path.Combine(Path.GetTempPath(),"open-dispatcher-"+Guid.NewGuid().ToString("N")+".SLDPRT");var opens=0;
   var model=new Proxy(modelType,call=>call.MethodName=="GetPathName"?path:throw new Exception("Unexpected model call "+call.MethodName)).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    if(Thread.CurrentThread.ManagedThreadId!=owner)throw new Exception("Native SW call reached worker thread");
    if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetDocuments")return new object[0];
    if(call.MethodName=="OpenDoc6"){opens++;return model;}if(call.MethodName=="ActivateDoc3")return model;
    throw new Exception("Unexpected SW call "+call.MethodName);
   }).GetTransparentProxy());
   using(var pane=(Control)New("PdmTaskPaneControl","句柄生命周期测试"))
   {
    pane.Visible=false;SetField(plugin,"taskPaneControl",pane);
    if(pane.IsHandleCreated)throw new Exception("Fixture unexpectedly created task pane HWND");
    var update=new TaskCompletionSource<int>();
    await Task.Run(()=>Type("PdmTaskPaneControl").GetMethod("RunOnUiThread",Flags).Invoke(pane,new object[]{(Action)(()=>update.SetResult(Thread.CurrentThread.ManagedThreadId))}));
    if(await update.Task!=owner||pane.IsHandleCreated)throw new Exception("Before-handle background status update escaped UI thread");
    if(!expectOldFailure)Type("PdmAddin").GetMethod("GetTaskPaneSynchronizationContext",Flags).Invoke(plugin,null);
    if(mode=="destroyed-handle"){var handle=pane.Handle;typeof(Control).GetMethod("DestroyHandle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(pane,null);}
    Action queue=()=>Type("PdmAddin").GetMethod("QueueOpenDocument",Flags).Invoke(plugin,new object[]{path,Enum.ToObject(Type("CadDocumentKind"),1),"Default",false,null,null});
    if(mode=="worker")await Task.Run(queue);else queue();
    if(mode=="disconnect"){SetField(plugin,"disconnecting",true);pane.Dispose();}
    for(var i=0;i<100&&(int)Field(plugin,"openOperationInProgress")!=0;i++)await Task.Delay(10);
    if((int)Field(plugin,"openOperationInProgress")!=0||(int)Field(plugin,"workspaceOperationInProgress")!=0)throw new Exception("Open operation remained busy");
    var expected=expectOldFailure||mode=="disconnect"?0:1;
    if(opens!=expected)throw new Exception("Queued open was lost, duplicated or executed after disconnect: "+mode);
    Console.WriteLine("PASS: "+(expectOldFailure?"reproduced legacy QueueOpenDocument handle failure":"queued native open exactly once")+" "+mode);
   }
  }
  if(!expectOldFailure)
  {
   var plugin=New("PdmAddin");using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
   using(var listener=(IDisposable)New("SolidWorksOpenRequestListener"))
   {
    ((HttpClient)Field(client,"httpClient")).DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
    SetField(plugin,"apiClient",client);SetField(plugin,"controlledOpenListener",listener);
    var request=New("SolidWorksOpenRequest");Set(request,"Mode","LatestReadOnly");SetField(plugin,"pendingControlledOpenRequest",request);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
    SetField(plugin,"application",new Proxy(appType,call=>new ReturnMessage(null,new object[]{0,"",false},3,call.LogicalCallContext,call)).GetTransparentProxy());
    Type("PdmAddin").GetMethod("TryStartControlledOpenRequest",Flags).Invoke(plugin,null);
    if(Field(plugin,"pendingControlledOpenRequest")!=request)throw new Exception("Startup request disappeared before task pane initialization");
    Console.WriteLine("PASS: client startup request retained until pane dispatcher initializes");
   }
  }
 }
 static void RootRenameScenario()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var recoveryField=Type("PdmAddin").GetField("documentRenameRecoveryDirectory",Flags);var previousRecovery=recoveryField.GetValue(null);
  foreach(var mode in new[]{"context-and-drawing","read-only-local-dependent","embedded-virtual","owned-dependent","open-dependent","replace-failure","reopen-failure","native-omits-drawing","concurrent-edit","concurrent-before-write"})
  {
   var folder=Path.Combine(Path.GetTempPath(),"pdm-root-rename-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   var oldPath=Path.Combine(folder,"旧总装.SLDASM");var newPath=Path.Combine(folder,"P700017-3.00.00.SLDASM");
   var part=Path.Combine(folder,"PART.SLDPRT");var drawing=Path.Combine(folder,"不同名工程图.SLDDRW");
   var virtualPath=Path.Combine(folder,"EMBEDDED.SLDPRT");if(mode=="embedded-virtual")File.WriteAllText(virtualPath,"embedded original bytes");
   var recovery=Path.Combine(folder,"recovery");recoveryField.SetValue(null,recovery);
   File.WriteAllText(oldPath,"original assembly bytes");File.WriteAllText(part,"original part bytes");File.WriteAllText(drawing,"original drawing bytes");
   if(mode=="read-only-local-dependent")File.SetAttributes(part,FileAttributes.ReadOnly);
   var loaded=true;var openPath=oldPath;var closes=0;var replaces=0;var cwd="original-working-directory";
   object model=null,extension=null;
   var pack=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true),call=>{
    if(call.MethodName=="set_IncludeDrawings"||call.MethodName=="set_IncludeSuppressed")return null;
    if(call.MethodName=="GetDocumentNames"){var output=(object[])call.Args.Clone();output[0]=mode=="native-omits-drawing"&&openPath==newPath?new[]{openPath,part}:new[]{openPath,part,drawing};return new ReturnMessage(true,output,output.Length,call.LogicalCallContext,call);}throw new Exception(call.MethodName);
   }).GetTransparentProxy();
   extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="GetPackAndGo"?pack:call.MethodName=="HasRenamedDocuments"?false:throw new Exception(call.MethodName)).GetTransparentProxy();
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"CONFIG-A":throw new Exception(call.MethodName)).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    switch(call.MethodName){case "GetPathName":return openPath;case "GetTitle":return Path.GetFileName(openPath);case "GetSaveFlag":return false;case "Save3":return true;case "get_ConfigurationManager":return manager;case "get_Extension":return extension;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var dependent=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?part:call.MethodName=="get_Visible"?true:call.MethodName=="IsOpenedReadOnly"?false:throw new Exception(call.MethodName)).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    switch(call.MethodName)
    {
     case "GetCurrentWorkingDirectory":return cwd;case "SetCurrentWorkingDirectory":cwd=(string)call.Args[0];return true;
     case "GetDocuments":return loaded?(mode=="open-dependent"?new[]{model,dependent}:new[]{model}):new object[0];
     case "GetDocumentDependencies2":var path=(string)call.Args[0];if(path==virtualPath)throw new Exception("Embedded component read independently");return path==part||path==drawing?new[]{"CONTEXT",File.ReadAllText(path).Contains("new-context")?newPath:oldPath}:new string[0];
     case "CloseDoc":loaded=false;closes++;return null;
     case "ReplaceReferencedDocument":replaces++;var target=(string)call.Args[0];if(mode=="replace-failure"&&target==drawing)return false;
      if(mode=="concurrent-edit"&&target==drawing)File.WriteAllText(part,"external user change");if(mode=="concurrent-before-write"&&target==part)File.WriteAllText(drawing,"external drawing change");File.AppendAllText(target,"|new-context");return true;
     case "OpenDoc6":var requested=(string)call.Args[0];if(mode=="reopen-failure"&&requested==newPath){call.Args[4]=1;call.Args[5]=0;return null;}openPath=requested;loaded=true;call.Args[4]=0;call.Args[5]=0;return model;
     case "ActivateDoc3":return model;
     default:throw new Exception("Unexpected rename application call "+call.MethodName);
    }
   }).GetTransparentProxy();
   var plugin=New("PdmAddin");SetField(plugin,"application",app);SetField(plugin,"authenticatedUsername","tester");
   var root=New("CadTreeNode");Set(root,"FullPath",oldPath);Set(root,"FileName",Path.GetFileName(oldPath));Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
   foreach(var path in new[]{part,drawing}){var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),path==part?"Part":"Drawing"));if(mode=="owned-dependent"&&path==part){Set(node,"DocumentId",Guid.NewGuid());Set(node,"CheckedOutBy","other-user");}((IList)Get(root,"Children")).Add(node);}
   if(mode=="embedded-virtual"){var node=New("CadTreeNode");Set(node,"FullPath",virtualPath);Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));((IList)Get(root,"Children")).Add(node);}
   SetField(plugin,"currentTree",root);var rejected=false;Exception renameError=null;
   try{Type("PdmAddin").GetMethod("RenameRootDocumentAndReferences",Flags).Invoke(plugin,new object[]{root,model,newPath});}
   catch(TargetInvocationException error)when(error.InnerException is IOException||error.InnerException is InvalidOperationException){rejected=true;renameError=error.InnerException;}
   var success=mode=="context-and-drawing"||mode=="read-only-local-dependent"||mode=="embedded-virtual";
   if(rejected==success||cwd!="original-working-directory"||(int)Field(plugin,"draftSnapshotInProgress")!=0)throw new Exception("Incorrect rename result "+mode+" "+renameError);
   if(success){if(File.Exists(oldPath)||!File.Exists(newPath)||File.ReadAllText(newPath)!="original assembly bytes"||!File.ReadAllText(part).Contains("new-context")||!File.ReadAllText(drawing).Contains("new-context")||openPath!=newPath)throw new Exception("Rename lost bytes, references or active model");}
   else if(mode=="concurrent-edit"){if(File.ReadAllText(part)!="external user change"||!Directory.EnumerateFiles(recovery,"rename.json",SearchOption.AllDirectories).Any())throw new Exception("Concurrent edit overwritten or recovery omitted");}
   else if(mode=="concurrent-before-write"){if(!File.Exists(oldPath)||File.Exists(newPath)||File.ReadAllText(part)!="original part bytes"||File.ReadAllText(drawing)!="external drawing change")throw new Exception("Unmodified external edit overwritten during rollback");}
   else if(!File.Exists(oldPath)||File.Exists(newPath)||File.ReadAllText(part)!="original part bytes"||File.ReadAllText(drawing)!="original drawing bytes")throw new Exception("Failed rename left a partial file/reference change "+mode);
   if(mode=="owned-dependent"||mode=="open-dependent")if(closes!=0||replaces!=0)throw new Exception("Rename permission/window preflight changed source files");
   if(mode=="read-only-local-dependent"&&(File.GetAttributes(part)&FileAttributes.ReadOnly)==0)throw new Exception("Dependent read-only attribute not restored");
   if(mode=="embedded-virtual"&&File.ReadAllText(virtualPath)!="embedded original bytes")throw new Exception("Embedded content changed during root rename");
   if(mode!="concurrent-edit"&&Directory.Exists(recovery)&&Directory.GetFiles(recovery,"*",SearchOption.AllDirectories).Length!=0)throw new Exception("Completed rename left recovery garbage");
   foreach(var file in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   Directory.Delete(folder,true);recoveryField.SetValue(null,previousRecovery);
   Console.WriteLine("PASS: root rename preserves bytes/configuration and updates exact context/drawing references "+mode);
  }
 }
 sealed class LifecycleControl:Control{public void RemoveHandle()=>DestroyHandle();}
 static void ReadOnlyOpenScenario()
 {
  foreach(var mode in new[]{"new","active","dirty","already-read-only","refused"})
  {
   var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var path=Path.Combine(Path.GetTempPath(),"ROOT.SLDASM");var readOnly=mode=="already-read-only";var changed=0;var opens=0;
   var model=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return path;case "IsOpenedReadOnly":return readOnly;case "GetSaveFlag":return mode=="dirty";case "SetReadOnlyState":changed++;if(mode=="refused")return false;readOnly=(bool)call.Args[0];return true;default:throw new Exception("Unexpected model method "+call.MethodName);}
   }).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){case "get_ActiveDoc":return mode=="new"?null:model;case "GetDocuments":return new object[0];case "OpenDoc6":opens++;if(((int)call.Args[2]&2)==0)throw new Exception("Missing read-only open flag");call.Args[4]=0;call.Args[5]=0;readOnly=true;return model;case "ActivateDoc3":call.Args[3]=0;return model;default:throw new Exception("Unexpected SW call "+call.MethodName);}
   }).GetTransparentProxy());
   try{plugin.GetType().GetMethod("OpenOrActivateDocumentOnSolidWorksThread",Flags).Invoke(plugin,new object[]{path,2,"Default",true});if(mode=="dirty"||mode=="refused")throw new Exception("Unsafe writable open allowed");if(!readOnly)throw new Exception("Root not read-only");}
   catch(TargetInvocationException e) when((mode=="dirty"&&e.InnerException is InvalidOperationException)||(mode=="refused"&&e.InnerException is IOException)){}
   if(mode=="dirty"&&changed!=0)throw new Exception("Dirty model was changed");if(mode=="new"&&opens!=1)throw new Exception("Open not exercised");
   Console.WriteLine("PASS: Read-only total assembly open "+mode);
  }
 } static void SuppressedReadScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-suppressed-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var path=Path.Combine(folder,"SUB.SLDASM");var partPath=Path.Combine(folder,"PART.SLDPRT");File.WriteAllText(path,"assembly");File.WriteAllText(partPath,"part");
  foreach(var mode in new[]{"new","loaded","dirty","configuration","unreadable","open-failed"})
  {
   var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;
   var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var componentType=interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
   object part=null;Func<bool,object> component=root=>new Proxy(componentType,call=>{
    switch(call.MethodName){case "GetPathName":return root?path:partPath;case "get_Name2":return root?"SUB":"PART-1";case "get_ReferencedConfiguration":return "TP900";
     case "get_IsVirtual":case "get_IsSpeedPak":return false;case "GetSuppression":case "GetSuppression2":return 2;case "IsHidden":return false;case "GetModelDoc2":return null;
     case "GetChildren":return root?new[]{part}:new object[0];default:throw new Exception("Unexpected component mutation/read "+call.MethodName);}
   }).GetTransparentProxy();part=component(false);var rootComponent=component(true);
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>{
    if(call.MethodName=="get_Name")return mode=="configuration"?"Other":"TP900";
    if(call.MethodName=="GetRootComponent3")return mode=="unreadable"?null:rootComponent;
    throw new Exception("Unexpected configuration mutation "+call.MethodName);
   }).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var model=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return "SUB.SLDASM";case "GetSaveFlag":return mode=="dirty";case "GetType":return 2;case "IsOpenedViewOnly":return false;
     case "get_ConfigurationManager":return manager;case "get_FeatureManager":return null;default:throw new Exception("Unexpected model mutation "+call.MethodName);}
   }).GetTransparentProxy();
   var opens=0;var closes=0;var visible=true;
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){case "GetRunningCommandInfo":return null;case "GetDocuments":return mode=="loaded"||mode=="dirty"?new[]{model}:new object[0];
     case "GetDocumentVisible":return visible;case "DocumentVisible":visible=(bool)call.Args[0];return null;
     case "OpenDoc6":opens++;if(((int)call.Args[2]&2)==0||(string)call.Args[3]!="TP900"||visible)throw new Exception("Unsafe suppressed open options");return mode=="open-failed"?null:model;
     case "CloseDoc":closes++;if((string)call.Args[0]!="SUB.SLDASM")throw new Exception("Closed wrong document");return null;
     default:throw new Exception("Unexpected active assembly access/mutation "+call.MethodName);}
   }).GetTransparentProxy());
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName","SUB.SLDASM");Set(node,"Configuration","TP900");
   try{var result=plugin.GetType().GetMethod("ReadSuppressedAssemblyStructure",Flags).Invoke(plugin,new[]{node});
    if(mode!="new"&&mode!="loaded")throw new Exception("Unsafe read succeeded "+mode);
    if(((IList)Get(result,"Children")).Count!=1)throw new Exception("Child omitted");
   }catch(TargetInvocationException e)when((mode=="dirty"||mode=="configuration"||mode=="unreadable")&&e.InnerException is InvalidOperationException||mode=="open-failed"&&e.InnerException is IOException){}
   var existing=mode=="loaded"||mode=="dirty";
   if(!visible||opens!=(existing?0:1)||closes!=(existing||mode=="open-failed"?0:1)||File.ReadAllText(path)!="assembly")throw new Exception("Suppressed document lifecycle changed user data "+mode);
   Console.WriteLine("PASS: Read suppressed local structure "+mode);
  }
 } static void PropertyCompletenessScenario()
 {
  ReferenceConfigurationPropertiesScenario();
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  foreach(var mode in new[]{"valid","empty","missing-manager","partial-names","disappeared"})
  {
   object manager=mode=="missing-manager"?null:new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>{
    if(call.MethodName=="get_Count")return mode=="empty"?0:mode=="partial-names"?2:1;
    if(call.MethodName=="GetNames")return mode=="empty"?null:new object[]{"名称"};
    if(call.MethodName=="Get6")return new ReturnMessage(mode=="disappeared"?1:2,new object[]{"名称",true,"零件","零件",true,false},6,call.LogicalCallContext,call);
    throw new Exception(call.MethodName);
   }).GetTransparentProxy();
   var values=new Dictionary<string,string>();var rejected=false;
   try{Type("PdmAddin").GetMethod("ReadPropertyManager",Flags).Invoke(null,new object[]{manager,"全局",values,true,true});}
   catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
   if(rejected!=(mode!="valid"&&mode!="empty")||mode=="valid"&&values["全局/名称"]!="零件"||mode=="empty"&&values.Count!=0)throw new Exception("Property completeness "+mode);
   Console.WriteLine("PASS: Property completeness "+mode);
  }
 } static async Task PreflightGateScenario(Form host)
 {
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  var folder=Path.Combine(Path.GetTempPath(),"pdm-preflight-gate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var items=Array.CreateInstance(Type("BatchOperationItem"),2);var models=new List<object>();
  for(var i=0;i<2;i++)
  {
   var path=Path.Combine(folder,"零件"+i+".SLDPRT");File.WriteAllText(path,"source");
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Configuration","missing");Set(node,"DocumentId",Guid.NewGuid());Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));items.SetValue(New("BatchOperationItem",node,0),i);
   models.Add(new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return path;if(call.MethodName=="GetSaveFlag")return false;if(call.MethodName=="GetConfigurationByName")return null;
    throw new Exception("Unexpected native mutation "+call.MethodName);
   }).GetTransparentProxy());
  }
  SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?models.ToArray():call.MethodName=="get_ActiveDoc"?null:throw new Exception(call.MethodName)).GetTransparentProxy());
  using(var pane=(Control)New("PdmTaskPaneControl","预检测试"))
  using(var handler=new PreflightReadHandler())
  using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
  {
   host.Controls.Add(pane);pane.CreateControl();SetField(plugin,"taskPaneControl",pane);((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});SetField(plugin,"apiClient",client);
   var rejected=false;
   try{await (Task)Type("PdmAddin").GetMethod("CheckInBatchAsync",Flags).Invoke(plugin,new object[]{items,Guid.NewGuid(),"test",null,CancellationToken.None,null,null,null,null});}
   catch(InvalidOperationException e){rejected=e.Message.Contains("零件0")&&e.Message.Contains("零件1")&&e.Message.Contains("尚未上传");}
   if(!rejected||handler.Writes!=0)throw new Exception("Preflight failed to collect all issues or wrote before validation");
  }
  Console.WriteLine("PASS: Multiple invalid files are reported together before registration, checkout or upload");
 }
 static async Task ArchiveNetworkScenario()
 {
  var message=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new object[]{new HttpRequestException("connection refused")});
  if(!message.Contains("服务器")||message.Contains("保存文件"))throw new Exception("Server connection failure was misreported as a CAD read/save failure");
  foreach(var mode in new[]{"connection","timeout","502","503","504","get-502","get-503","get-504","cancelled"})
  using(var cancellation=new CancellationTokenSource())
  using(var handler=new ArchiveNetworkHandler(mode,cancellation))
  using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
  {
   ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
   var plugin=New("PdmAddin");SetField(plugin,"apiClient",client);
   var items=Array.CreateInstance(Type("BatchOperationItem"),2);
   for(var i=0;i<2;i++){var node=New("CadTreeNode");Set(node,"DocumentId",Guid.NewGuid());Set(node,"FileName","PART"+i+".SLDPRT");Set(node,"FullPath",Path.Combine(Path.GetTempPath(),"NETWORK-PART"+i+".SLDPRT"));Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));items.SetValue(New("BatchOperationItem",node,0),i);}
   var rejected=false;
   try{await (Task)Type("PdmAddin").GetMethod("PreflightBatchCheckInItemsAsync",Flags).Invoke(plugin,new object[]{items,Guid.NewGuid(),null,null,null,new HashSet<string>(),null,cancellation.Token,Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem"))),new List<string>(),null,null});}
   catch(OperationCanceledException)when(mode=="cancelled"&&cancellation.IsCancellationRequested){rejected=true;}
   catch(InvalidOperationException error)when(mode!="cancelled"){rejected=error.Message.Contains("服务器")&&error.Message.Contains("尚未上传")&&!error.Message.Contains("保存文件");}
   if(!rejected||handler.Preflights!=(mode.StartsWith("get-")?0:1)||handler.Reads!=1||handler.Uploads!=0)throw new Exception("Server outage did not stop the batch before later requests/uploads: "+mode);
   Console.WriteLine("PASS: archive preflight stops on first "+mode+" and preserves cancellation semantics");
  }
  var local=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new object[]{new IOException("file locked")});
  if(!local.Contains("本地文件")||local.Contains("服务器"))throw new Exception("Local file failure was mislabeled as server failure");
 }
 sealed class ArchiveNetworkHandler:HttpMessageHandler
 {
  readonly string mode;readonly CancellationTokenSource cancellation;public int Preflights,Reads,Uploads;
  public ArchiveNetworkHandler(string mode,CancellationTokenSource cancellation){this.mode=mode;this.cancellation=cancellation;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(request.Method==HttpMethod.Get){Reads++;return Task.FromResult(new HttpResponseMessage(mode.StartsWith("get-")?(HttpStatusCode)int.Parse(mode.Substring(4)):HttpStatusCode.OK){Content=new StringContent(mode.StartsWith("get-")?"<html>proxy unavailable</html>":"[]")});}
   if(!request.RequestUri.AbsolutePath.EndsWith("/checkin/preflight")){Uploads++;throw new Exception("Unexpected archive write");}
   Preflights++;
   if(mode=="connection")throw new HttpRequestException("connection refused",new WebException("unavailable"));
   if(mode=="timeout")throw new TaskCanceledException("HTTP timeout");
   if(mode=="cancelled"){cancellation.Cancel();throw new OperationCanceledException(cancellation.Token);}
   return Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(mode)){Content=new StringContent("<html>proxy unavailable</html>")});
  }
 }
 sealed class PreflightReadHandler:HttpMessageHandler
 {
  public int Writes;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {if(request.Method!=HttpMethod.Get){Writes++;throw new Exception("Unexpected write "+request.RequestUri);}return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")});}
 }
 static async Task LatePreparationGate(Form host)
 {
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var folder=Path.Combine(Path.GetTempPath(),"pdm-late-copy-gate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var journal=Type("ArchiveBatchJournal").GetField("directory",Flags);var oldJournal=journal.GetValue(null);journal.SetValue(null,Path.Combine(folder,"batch-journal"));
  var session=(Guid)Field(plugin,"checkoutSessionId");SetField(plugin,"authenticatedUsername","tester");
  var items=Array.CreateInstance(Type("BatchOperationItem"),2);var models=new List<object>();var dirty=false;
  var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>call.MethodName=="get_Count"?0:call.MethodName=="GetNames"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
  var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="get_CustomPropertyManager"?manager:throw new Exception("Unexpected native mutation "+call.MethodName)).GetTransparentProxy();
  var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"默认":throw new Exception(call.MethodName)).GetTransparentProxy();
  for(var i=0;i<2;i++)
  {
   var path=Path.Combine(folder,i==0?"FIRST.SLDPRT":"SECOND.SLDPRT");File.WriteAllText(path,"saved geometry");
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Configuration","默认");Set(node,"DocumentId",Guid.NewGuid());Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",session);items.SetValue(New("BatchOperationItem",node,0),i);
   models.Add(new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?dirty:call.MethodName=="GetConfigurationByName"?configuration:call.MethodName=="get_Extension"?extension:throw new Exception("Unexpected native mutation "+call.MethodName)).GetTransparentProxy());
  }
  SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?models.ToArray():call.MethodName=="get_ActiveDoc"?null:throw new Exception(call.MethodName)).GetTransparentProxy());
  using(var pane=(Control)New("PdmTaskPaneControl","上传前整批检查"))
  using(var handler=new PreparationHandler())
  using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
  {
   host.Controls.Add(pane);pane.CreateControl();SetField(plugin,"taskPaneControl",pane);((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});SetField(plugin,"apiClient",client);
   Action<int,int,string,string> report=(index,total,file,stage)=>{if(file=="SECOND.SLDPRT"&&stage.Contains("准备上传副本"))dirty=true;};
   var rejected=false;
   try{await (Task)Type("PdmAddin").GetMethod("CheckInBatchAsync",Flags).Invoke(plugin,new object[]{items,Guid.NewGuid(),"test",report,CancellationToken.None,null,null,null,null});}
   catch(InvalidOperationException e){rejected=e.Message.Contains("SECOND.SLDPRT")&&e.Message.Contains("尚未上传");}
   if(!rejected||handler.Writes!=0||!dirty||File.ReadAllText(Path.Combine(folder,"FIRST.SLDPRT"))!="saved geometry")throw new Exception("Late native preparation failure uploaded or cleared changes");
  }
  journal.SetValue(null,oldJournal);
  Console.WriteLine("PASS: A later native preparation failure blocks every upload and preserves unsaved flags/source files");
 }
 sealed class PreparationHandler:HttpMessageHandler
 {
  public int Writes;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {if(request.Method!=HttpMethod.Get){Writes++;throw new Exception("Unexpected write "+request.RequestUri);}return Task.FromResult(new HttpResponseMessage(request.RequestUri.AbsolutePath.EndsWith("/versions")?HttpStatusCode.OK:HttpStatusCode.NotFound){Content=new StringContent(request.RequestUri.AbsolutePath.EndsWith("/versions")?"[]":"{}")});}
 }
 static void DrawingSaveGate()
 {
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var path=Path.Combine(Path.GetTempPath(),"LINKED.SLDPRT");var dirty=false;
  var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?dirty:throw new Exception("Unexpected model mutation "+call.MethodName)).GetTransparentProxy();
  SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{model}:throw new Exception(call.MethodName)).GetTransparentProxy());
  var drawing=New("CadTreeNode");Set(drawing,"FileName","LINKED.SLDDRW");var reference=New("CadTreeNode");Set(reference,"FullPath",path);Set(reference,"FileName","LINKED.SLDPRT");Set(reference,"Configuration","D14");((IList)Get(drawing,"ReferencedModels")).Add(reference);
  var method=Type("PdmAddin").GetMethod("ValidateDrawingModelsSaved",Flags);method.Invoke(plugin,new object[]{drawing,new Dictionary<string,bool>{{path,false}}});
  dirty=true;var rejected=false;
  try{method.Invoke(plugin,new object[]{drawing,new Dictionary<string,bool>{{path,false}}});}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=e.InnerException.Message.Contains("D14")&&e.InnerException.Message.Contains("检查期间")&&e.InnerException.Message.Contains("LINKED.SLDPRT");}
  if(!rejected||!dirty)throw new Exception("Drawing model state not diagnosed or unsaved flag cleared");
  Console.WriteLine("PASS: Linked drawing model save-state changes identify the model/configuration without clearing real edits");
 }
 static void ControlledReferenceScenario()
 {
  ImportedControlledReferences();
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  var folder=Path.Combine(Path.GetTempPath(),"pdm-controlled-ref-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var path=Path.Combine(folder,"PART.SLDPRT");var old=Path.Combine(folder,"legacy","PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(old));File.WriteAllText(old,"uncontrolled");
  foreach(var mode in new[]{"loaded-correct","suppressed","lightweight","loaded-wrong","suppressed-loaded-wrong","normal-unloaded","corrupt","missing"})
  {
   File.WriteAllText(path,"controlled");string hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
   var file=New("ControlledOpenFileDto");Set(file,"FileName","PART.SLDPRT");Set(file,"RelativePath","PART.SLDPRT");Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Sha256",hash);
   var manifest=New("ControlledOpenManifestDto");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
   var loaded=mode=="loaded-correct"||mode=="loaded-wrong"||mode=="suppressed-loaded-wrong";
   var model=loaded?new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?(mode=="loaded-correct"?path:old):throw new Exception(call.MethodName)).GetTransparentProxy():null;
   var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
    switch(call.MethodName){case "GetPathName":return old;case "GetModelDoc2":return model;case "GetSuppression":return mode=="lightweight"?1:mode=="normal-unloaded"?2:0;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?2:call.MethodName=="GetComponents"?new[]{component,component}:throw new Exception(call.MethodName)).GetTransparentProxy();
   if(mode=="corrupt")File.WriteAllText(path,"tampered");if(mode=="missing")File.Delete(path);
   var rejected=false;
   try{Type("PdmAddin").GetMethod("ValidateHistoricalControlledOpenReferences",Flags).Invoke(null,new object[]{root,Path.Combine(folder,"ROOT.SLDASM"),manifest});}
   catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
   if(rejected!=(mode!="loaded-correct"&&mode!="suppressed"&&mode!="lightweight"))throw new Exception("Wrong controlled reference outcome "+mode);
   if(mode=="loaded-correct"||mode=="loaded-wrong")
   {
    var app=new Proxy(Type("PdmAddin").GetField("application",Flags).FieldType,call=>call.MethodName=="GetType"?Type("PdmAddin").GetField("application",Flags).FieldType:throw new Exception("Unexpected application access "+call.MethodName)).GetTransparentProxy();
    var scanner=New("SolidWorksReferenceTreeScanner",app);var resolved=(string)Type("SolidWorksReferenceTreeScanner").GetMethod("ResolveComponentPath",Flags).Invoke(scanner,new[]{component});
    if(resolved!=(mode=="loaded-correct"?path:old))throw new Exception("Scanner ignored actual loaded model path");
   }
   Console.WriteLine("PASS: Controlled reference validation "+mode);
  }
 }
 static void ImportedControlledReferences()
 {
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  var folder=Path.Combine(Path.GetTempPath(),"pdm-import-open-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var cacheDirectory=Path.Combine(Path.GetTempPath(),"swx-open-"+Guid.NewGuid().ToString("N"),"IC~~");Directory.CreateDirectory(cacheDirectory);
  var native=Path.Combine(cacheDirectory,"source.stp.SLDPRT");var source=Path.Combine(folder,"source.stp");
  foreach(var mode in new[]{"correct","outside-workspace","corrupt","missing","unknown-source"})
  {
   File.WriteAllText(source,"original STEP bytes");var hash=(string)Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new object[]{source});var length=new FileInfo(source).Length;
   var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?native:throw new Exception(call.MethodName)).GetTransparentProxy();
   var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
    switch(call.MethodName){case "GetPathName":return native;case "GetModelDoc2":return model;case "GetImportedPath":return mode=="outside-workspace"?Path.Combine(folder,"outside","source.stp"):mode=="unknown-source"?Path.Combine(folder,"unknown.stp"):source;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?2:call.MethodName=="GetComponents"?new[]{component}:throw new Exception(call.MethodName)).GetTransparentProxy();
   var manifest=New("ControlledOpenManifestDto");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);var file=New("ControlledOpenFileDto");
   Set(file,"FileName","source.stp");Set(file,"RelativePath","source.stp");Set(file,"Sha256",hash);Set(file,"FileLength",length);files.Add(file);Set(manifest,"Files",files);
   if(mode=="corrupt")File.WriteAllText(source,"changed original");if(mode=="missing")File.Delete(source);
   var rejected=false;try{Type("PdmAddin").GetMethod("ValidateHistoricalControlledOpenReferences",Flags).Invoke(null,new object[]{root,Path.Combine(folder,"ROOT.SLDASM"),manifest});}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
   if(rejected!=(mode!="correct"))throw new Exception("Imported STEP controlled-open mismatch "+mode);
   Console.WriteLine("PASS: imported STEP controlled-open "+mode);
  }
  Directory.Delete(folder,true);Directory.Delete(cacheDirectory);Directory.Delete(Path.GetDirectoryName(cacheDirectory));
 }
 static void SearchScopeScenario()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  foreach(var mode in new[]{"success","open-error","set-error"})
  {
   var cwd="C:\\original";var folders="C:\\library;D:\\parts";var enabled=false;var refused=false;
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){case "GetType":return appType;case "GetCurrentWorkingDirectory":return cwd;case "GetSearchFolders":return folders;case "GetUserPreferenceToggle":return enabled;
     case "SetCurrentWorkingDirectory":cwd=(string)call.Args[0];return true;
     case "SetSearchFolders":if(mode=="set-error"&&!refused){refused=true;return false;}folders=(string)call.Args[1];return true;
     case "SetUserPreferenceToggle":enabled=(bool)call.Args[1];return null;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var manifest=New("ControlledOpenManifestDto");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);var file=New("ControlledOpenFileDto");Set(file,"FileName","PART.SLDPRT");files.Add(file);Set(manifest,"Files",files);
   try{using(var scope=(IDisposable)New("ControlledReferenceSearchScope",app,"C:\\controlled\\ROOT.SLDASM",manifest))
    {if(cwd!="C:\\controlled"||folders!="C:\\controlled"||!enabled)throw new Exception("Controlled search not applied");if(mode=="open-error")throw new IOException("simulated open failure");}}
   catch(IOException)when(mode=="open-error"){}
   catch(TargetInvocationException e)when(mode=="set-error"&&e.InnerException is InvalidOperationException){}
   if(cwd!="C:\\original"||folders!="C:\\library;D:\\parts"||enabled)throw new Exception("User search settings not restored "+mode);
   Console.WriteLine("PASS: Controlled search scope restores settings "+mode);
  }
 }
 static void ControlledScannerScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-scan-binding-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var rootPath=Path.Combine(folder,"ROOT.SLDASM");var partPath=Path.Combine(folder,"PART.SLDPRT");var old=Path.Combine(folder,"legacy","PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(old));File.WriteAllText(rootPath,"root");File.WriteAllText(partPath,"part");File.WriteAllText(old,"legacy");
  var identity=Type("PdmDocumentIdentityStore");var rootResolver=identity.GetField("workspaceRootResolver",Flags);var binding=identity.GetField("bindingDirectory",Flags);
  var originalRoot=rootResolver.GetValue(null);var originalBinding=binding.GetValue(null);
  try
  {
   rootResolver.SetValue(null,new Func<string>(()=>folder));binding.SetValue(null,Path.Combine(folder,"bindings"));var project=Guid.NewGuid();
   identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{rootPath,Guid.NewGuid(),project,Guid.NewGuid(),"W1","hash",4L});
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>call.MethodName=="GetType"?appType:throw new Exception(call.MethodName)).GetTransparentProxy();
   var model=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?rootPath:throw new Exception(call.MethodName)).GetTransparentProxy();
   var component=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetPathName"?old:call.MethodName=="GetModelDoc2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
   var scanner=New("SolidWorksReferenceTreeScanner",app);SetField(scanner,"activeDocumentDirectory",folder);SetField(scanner,"scannedModel",model);
   foreach(var sameProject in new[]{true,false})
   {
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{partPath,Guid.NewGuid(),sameProject?project:Guid.NewGuid(),Guid.NewGuid(),"W1","hash",4L});
    var actual=(string)Type("SolidWorksReferenceTreeScanner").GetMethod("ResolveComponentPath",Flags).Invoke(scanner,new[]{component});
    if(actual!=(sameProject?partPath:old))throw new Exception("Unloaded controlled reference binding mismatch");
    Console.WriteLine("PASS: Scanner trusts bound same-project copy only; sameProject="+sameProject);
   }
  }
  finally{rootResolver.SetValue(null,originalRoot);binding.SetValue(null,originalBinding);}
 }
 sealed class AssemblyProxy:RealProxy,IRemotingTypeInfo
 {
  readonly Func<IMethodCallMessage,object> callback;
  public AssemblyProxy(System.Type type,Func<IMethodCallMessage,object> callback):base(type){this.callback=callback;}
  public string TypeName{get;set;}
  public bool CanCastTo(System.Type type,object value)=>type.Namespace=="SolidWorks.Interop.sldworks";
  public override IMessage Invoke(IMessage message){var call=(IMethodCallMessage)message;try{return new ReturnMessage(callback(call),call.Args,call.ArgCount,call.LogicalCallContext,call);}catch(Exception e){return new ReturnMessage(e,call);}}
 }
 static void DiagnosticTabScenario(Form host)
 {
  host.ClientSize=new System.Drawing.Size(460,820);
  using(var pane=(Control)New("PdmTaskPaneControl","日志测试版本"))
  {
   host.Controls.Add(pane);pane.CreateControl();
   pane.GetType().GetMethod("SetWorkspaceOperationState",Flags).Invoke(pane,new object[]{true,"正在准备存档"});
   pane.GetType().GetMethod("SetWorkspaceOperationProgress",Flags).Invoke(pane,new object[]{"正在读取测试零件属性",1,3});
   var tabs=(TabControl)Field(pane,"tabs");var log=(TabPage)Field(pane,"diagnosticsTab");tabs.SelectedTab=log;
   if(!tabs.Enabled||!log.Enabled||((TabPage)Field(pane,"structureTab")).Enabled)throw new Exception("Operation tab gating incorrect");
   var viewer=log.Controls[0];viewer.GetType().GetMethod("RefreshLog",Flags).Invoke(viewer,new object[]{true});
   var content=(TextBox)Field(viewer,"content");if(!content.Text.Contains("正在读取测试零件属性"))throw new Exception("Live log not visible");
   pane.GetType().GetMethod("SetWorkspaceOperationState",Flags).Invoke(pane,new object[]{false,""});
   using(var bitmap=new System.Drawing.Bitmap(pane.Width,pane.Height)){pane.DrawToBitmap(bitmap,pane.ClientRectangle);bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostic-tab.png"));}
   Console.WriteLine("PASS: Log tab remains usable during operations and shows Chinese progress");
  }
 }
 static void DrawingArchiveVisibility()
 {
  foreach(var initial in new[]{false,true}) foreach(var fail in new[]{false,true})
  {
   var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;var visible=initial;var opens=0;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var model=new Proxy(modelType,call=>throw new Exception("Unexpected drawing call "+call.MethodName)).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    if(call.MethodName=="GetDocumentVisible")return visible;
    if(call.MethodName=="DocumentVisible"){visible=(bool)call.Args[0];return null;}
    if(call.MethodName=="OpenDoc6"){
     if(!visible || (int)call.Args[1]!=3 || ((int)call.Args[2]&2)==0)throw new Exception("Drawing lacks display data or readonly protection");
     opens++;if(fail)throw new IOException("模拟加载失败");call.Args[4]=0;call.Args[5]=0;return model;
    }
    throw new Exception("Unexpected application call "+call.MethodName);
   }).GetTransparentProxy());
   try{plugin.GetType().GetMethod("OpenDrawingForArchive",Flags).Invoke(plugin,new object[]{"isolated.SLDDRW"});if(fail)throw new Exception("Expected failure");}
   catch(TargetInvocationException error){if(!fail || !(error.InnerException is IOException))throw;}
   if(visible!=initial || opens!=1)throw new Exception("Drawing visibility not restored");
  }
  Console.WriteLine("PASS: Drawing archive generates display data, remains readonly and restores visibility on success/failure");
 }
 static void CenteringScenario()
 {
  DrawingArchiveVisibility();
  var method=Type("DialogWindowSizing").GetMethod("CenteredBounds",Flags);
  foreach(var area in new[]{new System.Drawing.Rectangle(0,0,1920,1040),new System.Drawing.Rectangle(-1920,0,1920,1040),new System.Drawing.Rectangle(1920,-1080,1280,1000)})
  foreach(var size in new[]{new System.Drawing.Size(720,460),new System.Drawing.Size(3000,2000)})
  {
   var bounds=(System.Drawing.Rectangle)method.Invoke(null,new object[]{area,size});
   if(!area.Contains(bounds)||Math.Abs((bounds.Left+bounds.Right)-(area.Left+area.Right))>1||Math.Abs((bounds.Top+bounds.Bottom)-(area.Top+area.Bottom))>1)throw new Exception("Dialog not centered or outside screen");
  }
  Console.WriteLine("PASS: Dialog centers on primary, negative-coordinate and secondary screens; oversize fits working area");
 } static void CloseReferencedScenario()
 {
  foreach(var mode in new[]{"referenced","suppressed","close-error","empty-title"})
  {
   var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);var closes=0;
   var model=new Proxy(modelType,call=>call.MethodName=="GetTitle"?(mode=="empty-title"?"":"PART.SLDPRT"):throw new Exception("Unexpected model call "+call.MethodName)).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    if(call.MethodName=="GetDocuments")return new[]{model};
    if(call.MethodName!="CloseDoc")throw new Exception("Unexpected SW call "+call.MethodName);
    closes++;if(mode=="close-error")throw new InvalidOperationException("模拟关闭失败");return null;
   }).GetTransparentProxy());
   var result=(bool)plugin.GetType().GetMethod("CloseBatchOpenedDocument",Flags).Invoke(plugin,new[]{model});
   if(result!=(mode=="referenced"||mode=="suppressed")||closes!=(mode=="empty-title"?0:1))throw new Exception("Wrong referenced close outcome: "+mode);
   Console.WriteLine("PASS: Temporary read cleanup "+mode);
  }
 } static void EntryScenario(Form host)
 {
  var plugin=New("PdmAddin");SetField(plugin,"lifetime",new CancellationTokenSource());var nativeCalls=0;var shown=false;
  var appType=plugin.GetType().GetField("application",Flags).FieldType;
  SetField(plugin,"application",new Proxy(appType,call=>{nativeCalls++;throw new Exception("Dialog entry touched native SW: "+call.MethodName);}).GetTransparentProxy());
  var directory=Path.Combine(Path.GetTempPath(),"UPLM-dialog-entry",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var root=New("CadTreeNode");var path=Path.Combine(directory,"import.SLDASM");File.WriteAllText(path,"fixture");
  Set(root,"FullPath",path);Set(root,"FileName","import.SLDASM");Set(root,"Kind",Enum.ToObject(Property(root,"Kind").PropertyType,0));SetField(plugin,"currentTree",root);
  var drawing=New("CadTreeNode");var drawingPath=Path.Combine(directory,"import.SLDDRW");File.WriteAllText(drawingPath,"drawing fixture");
  Set(drawing,"FullPath",drawingPath);Set(drawing,"FileName","import.SLDDRW");Set(drawing,"Kind",Enum.ToObject(Property(drawing,"Kind").PropertyType,2));
  ((IList)Get(root,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(root);
  var projects=Array.CreateInstance(Type("ProjectDto"),1);var project=New("ProjectDto");Set(project,"Id",Guid.NewGuid());Set(project,"Code","QA-ENTRY");Set(project,"Name","入口验证");projects.SetValue(project,0);SetField(plugin,"availableProjects",projects);
  var client=New("PdmApiClient","http://fixture.invalid/");((HttpClient)Field(client,"httpClient")).DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(plugin,"apiClient",client);
  using(var pane=(Control)New("PdmTaskPaneControl",""))
  using(var timer=new System.Windows.Forms.Timer{Interval=25})
  {
   host.Controls.Add(pane);pane.CreateControl();SetField(plugin,"taskPaneControl",pane);
   var method=plugin.GetType().GetMethod("OpenBatchOperationDialog",Flags);var args=new object[]{null,Enum.ToObject(Type("BatchOperationKind"),1),null};
   timer.Tick+=(_,__)=>{var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.GetType().Name=="BatchOperationDialog");if(dialog==null)return;shown=true;var area=Screen.FromControl(pane).WorkingArea;var bounds=dialog.Bounds;if(Math.Abs((bounds.Left+bounds.Right)-(area.Left+area.Right))>2||Math.Abs((bounds.Top+bounds.Bottom)-(area.Top+area.Bottom))>2)throw new Exception("Actual dialog not centered");dialog.Opacity=0;method.Invoke(plugin,args);dialog.DialogResult=DialogResult.Cancel;dialog.Close();};
   timer.Start();method.Invoke(plugin,args);timer.Stop();
   if(!shown||nativeCalls!=0||(int)Field(plugin,"batchDialogInProgress")!=0||(int)Field(plugin,"refreshSuppressionDepth")!=0)throw new Exception("Unsafe dialog entry or unreleased gate");
  }
  ((IDisposable)client).Dispose();((CancellationTokenSource)Field(plugin,"lifetime")).Dispose();Console.WriteLine("PASS: Batch dialog opens without native calls, duplicate entry blocked, cancel releases guards");
 } static async Task Scenario(bool dirty,bool denied)
 {
  var directory=Path.Combine(Path.GetTempPath(),"UPLM-save-regression",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
  SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentProjectId",project);
  var events=new List<string>();var models=new List<Model>();var nodes=new List<object>();
  var appType=plugin.GetType().GetField("application",Flags).FieldType;
  var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);
  for(int i=0;i<2;i++)
  {
   var path=Path.Combine(directory,"assembly"+i+".SLDASM");File.WriteAllText(path,"unchanged CAD fixture");File.SetAttributes(path,FileAttributes.ReadOnly);
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"DocumentId",Guid.NewGuid());Set(node,"Kind",Enum.ToObject(Property(node,"Kind").PropertyType,0));
   models.Add(new Model(modelType,path,dirty,events));nodes.Add(node);
  }
  ((IList)Get(nodes[0],"Children")).Add(nodes[1]);SetField(plugin,"currentTree",nodes[0]);
  SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?models.Select(m=>m.GetTransparentProxy()).ToArray():throw new Exception("Unexpected SW call "+call.MethodName)).GetTransparentProxy());
  var items=Array.CreateInstance(Type("BatchOperationItem"),2);for(int i=0;i<2;i++)items.SetValue(New("BatchOperationItem",nodes[i],i),i);
  using(var pane=(Control)New("PdmTaskPaneControl",""))
  using(var handler=new Handler(nodes,project,session,events,denied))
  {
   pane.CreateControl();SetField(plugin,"taskPaneControl",pane);
   var client=New("PdmApiClient","http://fixture.invalid/");((HttpClient)Field(client,"httpClient")).Dispose();
   SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});SetField(plugin,"apiClient",client);
   try
   {
    await (Task)plugin.GetType().GetMethod("EnsureBatchCheckInSavedStateAsync",Flags).Invoke(plugin,new object[]{items,project,CancellationToken.None});
    if(denied)throw new Exception("Permission failure ignored");
    if(dirty)
    {
     if(events.Count!=4||events[0]!="checkout:assembly1.SLDASM"||events[1]!="save:assembly1.SLDASM"||events[2]!="checkout:assembly0.SLDASM"||events[3]!="save:assembly0.SLDASM")throw new Exception("Wrong permission/save order: "+string.Join(",",events));
     if(models.Any(m=>m.Dirty)||nodes.Any(n=>(string)Get(n,"CheckedOutBy")!="tester"))throw new Exception("Save or checkout state not updated");
    }
    else if(events.Count!=0)throw new Exception("Clean no-change edit unexpectedly saved or checked out");
    foreach(var model in models)if(File.ReadAllText(model.Path)!="unchanged CAD fixture")throw new Exception("Changed file content");
    Console.WriteLine(dirty?"PASS: Read-only dirty parent assemblies acquire permission and save child-first without editing parts":"PASS: Clean no-change files do not save, acquire permission or create versions");
   }
   catch(InvalidOperationException) when(denied)
   {
    if(events.Any(e=>e.StartsWith("save:"))||models.Any(m=>!m.Dirty))throw new Exception("Denied checkout saved or discarded dirty content");
    Console.WriteLine("PASS: Denied permission stops before save and retains dirty content");
   }
  }
  foreach(var model in models){File.SetAttributes(model.Path,FileAttributes.Normal);File.Delete(model.Path);}Directory.Delete(directory);
  if(!dirty&&!denied)await SavePropagationScenario();
  if(!dirty&&!denied)await SharedAssemblySaveScenario();
  if(!dirty&&!denied)await PreloadCancellationScenario();
  if(!dirty&&!denied)await PreloadOwnershipScenario();
 }
 static async Task PreloadCancellationScenario()
 {
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  var path=Path.Combine(Path.GetTempPath(),"PRELOAD-CANCEL.SLDASM");var reads=0;
  var document=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?false:throw new Exception(call.MethodName)).GetTransparentProxy();
  SetField(plugin,"application",new Proxy(appType,call=>{if(call.MethodName=="get_ActiveDoc")return document;if(call.MethodName=="GetDocuments"){reads++;return new[]{document};}throw new Exception("Cancelled preload reached native mutation: "+call.MethodName);}).GetTransparentProxy());
  var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName","PRELOAD-CANCEL.SLDASM");var items=Array.CreateInstance(Type("BatchOperationItem"),1);items.SetValue(New("BatchOperationItem",node,0),0);
  using(var cancellation=new CancellationTokenSource())
  {
   var cancelled=false;try{await (Task)Type("PdmAddin").GetMethod("PreloadBatchCheckInDocumentsAsync",Flags).Invoke(plugin,new object[]{items,(Action<string>)(_=>cancellation.Cancel()),cancellation.Token});}
   catch(OperationCanceledException){cancelled=true;}
   if(!cancelled||reads!=1)throw new Exception("Cancelled preload continued reading/opening a document");
  }
  Console.WriteLine("PASS: cancellation during preload progress stops before another native read or any save");
 }
 static async Task PreloadOwnershipScenario()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  foreach(var mode in new[]{"wrong-path","clean-model","dirty-model","unknown-save-state","generated-drawing","editable-drawing"})
  {
   var plugin=New("PdmAddin");var path=Path.Combine(Path.GetTempPath(),"OWNED.SLDPRT");var other=Path.Combine(Path.GetTempPath(),"other","OWNED.SLDPRT");var closes=0;
   var document=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return mode=="wrong-path"?other:path;if(call.MethodName=="GetTitle")return "OWNED.SLDPRT";
    if(call.MethodName=="GetSaveFlag"){if(mode=="unknown-save-state")throw new COMException("fixture");return mode!="clean-model";}
    if(call.MethodName=="IsOpenedReadOnly")return mode!="editable-drawing";throw new Exception(call.MethodName);
   }).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){case "get_ActiveDoc":return null;case "GetDocuments":return new object[0];case "GetDocumentVisible":return true;case "DocumentVisible":return null;
     case "OpenDoc6":return document;case "CloseDoc":closes++;return null;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy());
   if(mode=="wrong-path")
   {
    var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName","OWNED.SLDPRT");Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));
    var items=Array.CreateInstance(Type("BatchOperationItem"),1);items.SetValue(New("BatchOperationItem",node,0),0);var rejected=false;
    try{await (Task)Type("PdmAddin").GetMethod("PreloadBatchCheckInDocumentsAsync",Flags).Invoke(plugin,new object[]{items,null,CancellationToken.None});}
    catch(InvalidOperationException e)when(e.Message.Contains("其他路径")){rejected=true;}
    if(!rejected)throw new Exception("Foreign document accepted by preload");
   }
   else
   {
    if(mode.Contains("drawing"))((ISet<string>)Field(plugin,"preloadedOwnedDrawings")).Add(path);
    var documents=Array.CreateInstance(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),1);documents.SetValue(document,0);
    Type("PdmAddin").GetMethod("ClosePreloadedArchiveDocuments",Flags).Invoke(plugin,new object[]{documents});
   }
   if(closes!=(mode=="clean-model"||mode=="generated-drawing"?1:0))throw new Exception("Preload cleanup closed a foreign or unsaved document: "+mode);
   Console.WriteLine("PASS: archive cleanup preserves user content and ownership "+mode);
  }
 }
 static void ReferenceConfigurationPropertiesScenario()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  foreach(var missing in new[]{false,true})
  {
   var lookups=new List<string>();var path=Path.Combine(Path.GetTempPath(),"MULTI.SLDPRT");
   Func<string,object> manager=scope=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>{
    if(call.MethodName=="get_Count")return 1;if(call.MethodName=="GetNames")return new object[]{"编号"};
    if(call.MethodName=="Get6")return new ReturnMessage(2,new object[]{"编号",true,scope+"-raw",scope+"-resolved",true,false},6,call.LogicalCallContext,call);
    throw new Exception("Unexpected property mutation "+call.MethodName);
   }).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="get_CustomPropertyManager"?manager((string)call.Args[0]):throw new Exception(call.MethodName)).GetTransparentProxy();
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>throw new Exception("Unexpected configuration mutation "+call.MethodName)).GetTransparentProxy();
   var document=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return path;if(call.MethodName=="GetSaveFlag")return false;if(call.MethodName=="get_Extension")return extension;
    if(call.MethodName=="GetConfigurationByName"){lookups.Add((string)call.Args[0]);return missing&&(string)call.Args[0]=="Alternate"?null:configuration;}
    throw new Exception("Read-only multi-configuration validation mutated or switched the model: "+call.MethodName);
   }).GetTransparentProxy();
   var plugin=New("PdmAddin");SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{document}:throw new Exception(call.MethodName)).GetTransparentProxy());
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName","MULTI.SLDPRT");Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(node,"Configuration","Default");
   var cards=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(Type("CadDocumentKind"),typeof(IReadOnlyList<string>)));
   var rejected=false;
   try{
    var values=(IReadOnlyDictionary<string,string>)Type("PdmAddin").GetMethod("ReadArchivePreflightProperties",Flags).Invoke(plugin,
      new object[]{node,cards,new Dictionary<string,IReadOnlyList<string>>{{path,new[]{"Default","Alternate"}}}});
    if(!missing){
     if(values["配置:Default/编号"]!="Default-resolved"||values["配置:Alternate/编号"]!="Alternate-resolved")throw new Exception("Referenced configuration properties were not retained");
     var prepared=new Dictionary<string,string>{{"配置:Default/编号","saved-primary"}};
     var merged=(IReadOnlyDictionary<string,string>)Type("PdmAddin").GetMethod("PreserveInspectedConfigurationProperties",Flags).Invoke(null,new object[]{node,prepared,values});
     if(merged["配置:Default/编号"]!="saved-primary"||merged["配置:Alternate/编号"]!="Alternate-resolved")throw new Exception("Prepared archive lost an alternate configuration or replaced freshly read primary properties");
    }
   }
   catch(TargetInvocationException e)when(missing&&e.InnerException is InvalidOperationException&&e.InnerException.Message.Contains("Alternate")){rejected=true;}
   if(rejected!=missing||!lookups.SequenceEqual(new[]{"Default","Alternate"}))throw new Exception("Not every referenced configuration was validated");
   Console.WriteLine("PASS: every referenced configuration checked without switching, rebuilding or saving; missing="+missing);
  }
 }
 static async Task SharedAssemblySaveScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-shared-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try
  {
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);var events=new List<string>();
   var names=new[]{"ROOT.SLDASM","SHARED.SLDASM","OUTER.SLDASM","INNER.SLDASM"};var models=new List<Model>();var nodes=new List<object>();
   foreach(var name in names){var path=Path.Combine(folder,name);File.WriteAllText(path,"saved source");var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),0));nodes.Add(node);models.Add(new Model(modelType,path,true,events));}
   ((IList)Get(nodes[0],"Children")).Add(nodes[1]);((IList)Get(nodes[0],"Children")).Add(nodes[2]);
   ((IList)Get(nodes[2],"Children")).Add(nodes[3]);
   var repeated=New("CadTreeNode");Set(repeated,"FullPath",Get(nodes[1],"FullPath"));Set(repeated,"FileName",names[1]);Set(repeated,"Kind",Enum.ToObject(Type("CadDocumentKind"),0));((IList)Get(nodes[3],"Children")).Add(repeated);
   models[1].OnSave=()=>{models[3].Dirty=true;models[2].Dirty=true;models[0].Dirty=true;};models[3].OnSave=()=>{models[2].Dirty=true;models[0].Dirty=true;};models[2].OnSave=()=>models[0].Dirty=true;
   SetField(plugin,"currentTree",nodes[0]);SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?models.Select(model=>model.GetTransparentProxy()).ToArray():throw new Exception(call.MethodName)).GetTransparentProxy());
   var items=Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{nodes[0]});
   using(var pane=(Control)New("PdmTaskPaneControl","重复子装配保存测试"))
   {
    pane.CreateControl();SetField(plugin,"taskPaneControl",pane);
    await (Task)Type("PdmAddin").GetMethod("EnsureBatchCheckInSavedStateAsync",Flags).Invoke(plugin,new object[]{items,Guid.NewGuid(),CancellationToken.None});
    if(models.Any(model=>model.Dirty)||!events.SequenceEqual(new[]{"save:SHARED.SLDASM","save:INNER.SLDASM","save:OUTER.SLDASM","save:ROOT.SLDASM"}))throw new Exception("Shared assemblies were saved by display depth instead of file dependencies");
   }
   Console.WriteLine("PASS: a shared assembly at shallow and deep occurrences saves once before every parent");
  }
  finally{Directory.Delete(folder,true);}
 }
 static async Task SavePropagationScenario()
 {
  foreach(var remainsDirty in new[]{false,true})
  {
   var folder=Path.Combine(Path.GetTempPath(),"pdm-save-propagation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   try
   {
    var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
    var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);var events=new List<string>();
    var names=new[]{"ROOT.SLDASM","PART.SLDPRT","PART.SLDDRW"};var models=new List<Model>();var nodes=new List<object>();
    for(var i=0;i<3;i++){var path=Path.Combine(folder,names[i]);File.WriteAllText(path,"saved source");var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",names[i]);Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),i));nodes.Add(node);models.Add(new Model(modelType,path,i==1,events));}
    models[1].OnSave=()=>{models[0].Dirty=true;models[2].Dirty=true;};models[0].OnSave=()=>{models[0].Dirty=remainsDirty;models[2].Dirty=true;};
    ((IList)Get(nodes[0],"Children")).Add(nodes[1]);((IList)Get(nodes[1],"Children")).Add(nodes[2]);SetField(plugin,"currentTree",nodes[0]);
    SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="GetDocuments"?models.Select(model=>model.GetTransparentProxy()).ToArray():throw new Exception(call.MethodName)).GetTransparentProxy());
    var items=Array.CreateInstance(Type("BatchOperationItem"),3);for(var i=0;i<3;i++)items.SetValue(New("BatchOperationItem",nodes[i],i),i);
    using(var pane=(Control)New("PdmTaskPaneControl","保存传播测试"))
    {
     pane.CreateControl();SetField(plugin,"taskPaneControl",pane);var rejected=false;
     try{await (Task)Type("PdmAddin").GetMethod("EnsureBatchCheckInSavedStateAsync",Flags).Invoke(plugin,new object[]{items,Guid.NewGuid(),CancellationToken.None});}
     catch(IOException)when(remainsDirty){rejected=true;}
     if(rejected!=remainsDirty||models[0].Dirty!=remainsDirty||models[1].Dirty||models[2].Dirty||!events.SequenceEqual(new[]{"save:PART.SLDPRT","save:ROOT.SLDASM","save:PART.SLDDRW"}))throw new Exception("Save ordering lost propagated changes or ignored a persistent dirty flag");
     if(names.Any(name=>File.ReadAllText(Path.Combine(folder,name))!="saved source"))throw new Exception("Save-order fixture modified source bytes");
    }
    Console.WriteLine("PASS: child-save dirties a clean parent; models save child-first, drawings last; persistent-dirty="+remainsDirty);
   }
   finally{Directory.Delete(folder,true);}
  }
 }
 static void VisualReferenceCycleScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-visual-reference-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try
  {
   var rootPath=Path.Combine(folder,"ROOT.SLDASM");var subPath=Path.Combine(folder,"SUB.SLDASM");
   File.WriteAllText(rootPath,"root");File.WriteAllText(subPath,"sub");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   var componentType=interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
   Func<string,string,Func<object[]>,object> component=(name,path,children)=>new Proxy(componentType,call=>{
    switch(call.MethodName){case "get_Name2":return name;case "GetPathName":return path;case "get_ReferencedConfiguration":return "Default";
     case "get_IsVirtual":case "get_IsSpeedPak":case "IsHidden":return false;case "GetSuppression2":return 2;case "GetModelDoc2":return null;case "GetChildren":return children();default:throw new Exception("Unexpected component method "+call.MethodName);}
   }).GetTransparentProxy();
   var phantom=component("external-parent-1",rootPath,()=>new object[0]);
   var sub=component("SUB-1",subPath,()=>new object[0]);
   var root=component("ROOT",rootPath,()=>new[]{sub});
   var constants=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location),"SolidWorks.Interop.swconst.dll"));
   var componentItem=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swTreeControlItemType_e",true),"swFeatureManagerItem_Component"));
   Func<object,object,object,object> item=(value,child,next)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.TreeControlItem",true),call=>{
    switch(call.MethodName){case "get_ObjectType":return value==null?0:componentItem;case "get_Object":return value;case "GetFirstChild":return child;case "GetNext":return next;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var phantomItem=item(phantom,null,null);var subItem=item(sub,phantomItem,null);var visualRoot=item(null,subItem,null);
   var featureManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatureTreeRootItem2"?visualRoot:throw new Exception(call.MethodName)).GetTransparentProxy();
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":call.MethodName=="GetRootComponent3"?root:throw new Exception(call.MethodName)).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    switch(call.MethodName){case "GetPathName":return rootPath;case "GetType":return 2;case "GetTitle":return "ROOT.SLDASM";case "IsOpenedViewOnly":return false;case "get_ConfigurationManager":return manager;case "get_FeatureManager":return featureManager;default:throw new Exception(call.MethodName);}
   }).GetTransparentProxy();
   var app=new Proxy(appType,call=>call.MethodName=="GetType"?appType:throw new Exception("Unexpected application mutation "+call.MethodName)).GetTransparentProxy();
   var scanner=New("SolidWorksReferenceTreeScanner",app);
   var scanned=Type("SolidWorksReferenceTreeScanner").GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{model,true});
   var direct=((IEnumerable)Get(scanned,"Children")).Cast<object>().ToArray();
   if(direct.Length!=1||((IList)Get(direct[0],"Children")).Count!=0)
    throw new Exception("FeatureManager reference node was inserted as a real child, creating ROOT -> SUB -> ROOT false cycle");
   if(File.ReadAllText(rootPath)!="root"||File.ReadAllText(subPath)!="sub")throw new Exception("Reference filtering changed source files");
   Console.WriteLine("PASS: visual external parent is not an assembly child and cannot create a false cycle");
   foreach(var mode in new[]{"visual-order","stale-visual-proxy","repeated-siblings","different-configuration","true-cycle"})
   {
    object nativeRoot=null;
    var nested=component("ROOT-CONFIG-B-1",rootPath,()=>new object[0]);
    // The native root and nested configuration have the same file, but different configurations.
    if(mode=="different-configuration")
    {
     nested=new Proxy(componentType,call=>{
      switch(call.MethodName){case "get_Name2":return "ROOT-CONFIG-B-1";case "GetPathName":return rootPath;case "get_ReferencedConfiguration":return "Config-B";
       case "get_IsVirtual":case "get_IsSpeedPak":case "IsHidden":return false;case "GetSuppression2":return 2;case "GetModelDoc2":return null;case "GetChildren":return new object[0];default:throw new Exception(call.MethodName);}
     }).GetTransparentProxy();
    }
    var first=component("SUB-1",subPath,()=>mode=="true-cycle"?new[]{nativeRoot}:mode=="different-configuration"?new[]{nested}:new object[0]);
    var second=component("SUB-2",subPath,()=>new object[0]);
    nativeRoot=component("ROOT",rootPath,()=>new[]{first,second});
    var stale=component("SUB-1",rootPath,()=>throw new Exception("Stale visual COM proxy was traversed"));
    var tree=mode=="visual-order"?item(null,item(second,null,null),null):item(null,item(mode=="stale-visual-proxy"?stale:first,null,item(second,null,null)),null);
    var fm=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatureTreeRootItem2"?tree:throw new Exception(call.MethodName)).GetTransparentProxy();
    var config=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":call.MethodName=="GetRootComponent3"?nativeRoot:throw new Exception(call.MethodName)).GetTransparentProxy();
    var cm=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?config:throw new Exception(call.MethodName)).GetTransparentProxy();
    var document=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
     switch(call.MethodName){case "GetPathName":return rootPath;case "GetType":return 2;case "GetTitle":return "ROOT.SLDASM";case "IsOpenedViewOnly":return false;case "get_ConfigurationManager":return cm;case "get_FeatureManager":return fm;default:throw new Exception(call.MethodName);}
    }).GetTransparentProxy();
    var scan=Type("SolidWorksReferenceTreeScanner").GetMethod("ScanDocument",Flags);scanner=New("SolidWorksReferenceTreeScanner",app);
    try
    {
     scanned=scan.Invoke(scanner,new object[]{document,true});
     if(mode=="true-cycle")throw new Exception("True native cycle was accepted");
     direct=((IEnumerable)Get(scanned,"Children")).Cast<object>().ToArray();
     var expected=mode=="visual-order"?new[]{"SUB-2","SUB-1"}:new[]{"SUB-1","SUB-2"};
     if(!direct.Select(node=>(string)Get(node,"ComponentSelectionName")).SequenceEqual(expected)||direct.Any(node=>(string)Get(node,"FullPath")!=subPath))throw new Exception("Native membership, instance or visual order lost: "+mode);
     if(mode=="different-configuration"&&((IList)Get(direct[0],"Children")).Count!=1)throw new Exception("Different configuration was rejected as a cycle");
    }
    catch(TargetInvocationException error)when(mode=="true-cycle"&&error.InnerException is InvalidOperationException&&error.InnerException.Message.Contains("真实组件引用形成循环"))
    {
     scanned=scan.Invoke(scanner,new object[]{document,false});var child=((IList)Get(scanned,"Children"))[0];var cycle=((IList)Get(child,"Children"))[0];
     if(string.IsNullOrWhiteSpace((string)Get(cycle,"ArchiveReadError"))||((IList)Get(cycle,"Children")).Count!=0)throw new Exception("Non-strict scan failed to stop and mark true cycle");
    }
    Console.WriteLine("PASS: authoritative component membership "+mode);
   }
  }
  finally{Directory.Delete(folder,true);}
 }
 static void StrictScanMatrix()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-strict-scan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try
  {
   var assemblyPath=Path.Combine(folder,"ROOT.SLDASM");var partPath=Path.Combine(folder,"PART.SLDPRT");var subPath=Path.Combine(folder,"SUB.SLDASM");
   File.WriteAllText(assemblyPath,"root fixture");File.WriteAllText(partPath,"part fixture");File.WriteAllText(subPath,"sub fixture");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   var componentType=interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
   var constants=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location),"SolidWorks.Interop.swconst.dll"));
   var suppressionType=constants.GetType("SolidWorks.Interop.swconst.swComponentSuppressionState_e",true);
   Func<string,int> state=name=>Convert.ToInt32(Enum.Parse(suppressionType,name));
   foreach(var mode in new[]{"resolved","fully-resolved","lightweight","fully-lightweight","suppressed","hidden","virtual","speedpak","state-error","invalid-state"})
   {
    var component=new Proxy(componentType,call=>{
     switch(call.MethodName)
     {
      case "get_IsVirtual":return mode=="virtual";
      case "get_IsSpeedPak":return mode=="speedpak";
      case "GetSuppression2":
       if(mode=="state-error")throw new COMException("simulated state read failure");
       return mode=="invalid-state"?99:state(mode=="resolved"?"swComponentResolved":mode=="lightweight"?"swComponentLightweight":mode=="fully-lightweight"?"swComponentFullyLightweight":mode=="suppressed"?"swComponentSuppressed":"swComponentFullyResolved");
      case "IsHidden":return mode=="hidden";
      default:throw new Exception("Unexpected state mutation "+call.MethodName);
     }
    }).GetTransparentProxy();
    object result=null;var rejected=false;
    try{result=Type("SolidWorksReferenceTreeScanner").GetMethod("GetStatus",Flags).Invoke(null,new object[]{component,partPath,true});}
    catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
    var blocked=mode=="speedpak"||mode=="state-error"||mode=="invalid-state";
    var expected=mode=="virtual"?"Virtual":mode=="hidden"?"Hidden":mode=="suppressed"?"Suppressed":mode.Contains("lightweight")?"Lightweight":"Normal";
    if(rejected!=blocked||!blocked&&result.ToString()!=expected)throw new Exception("Incorrect strict component state: "+mode);
    Console.WriteLine("PASS: strict component state "+mode+(rejected?" rejected":" retained as "+expected));
   }
   foreach(var mode in new[]{"multi-configuration","mixed-states","leaf-null","root-null","children-error","non-array","invalid-array-item","nested-assembly-null","model-state-error","view-only"})
   {
    object[] parts=null;
    Func<bool,int,object> component=(isRoot,index)=>new Proxy(componentType,call=>{
     switch(call.MethodName)
     {
      case "GetPathName":return isRoot?assemblyPath:mode=="nested-assembly-null"&&index==1?subPath:partPath;
      case "get_Name2":return isRoot?"ROOT":"PART-"+index;
      case "get_ReferencedConfiguration":return isRoot?"":index==0?"CONFIG-A":"CONFIG-B";
      case "get_IsVirtual":return !isRoot&&mode=="mixed-states"&&index==5;
      case "get_IsSpeedPak":return false;
      case "IsHidden":return !isRoot&&mode=="mixed-states"&&index==4;
      case "GetSuppression2":return state(!isRoot&&mode=="mixed-states"?(index==1?"swComponentLightweight":index==2?"swComponentFullyLightweight":index==3?"swComponentSuppressed":"swComponentFullyResolved"):"swComponentFullyResolved");
      case "GetModelDoc2":if(mode=="model-state-error")throw new COMException("simulated model state failure");return null;
      case "GetChildren":
       if(!isRoot)return mode=="leaf-null"||mode=="nested-assembly-null"&&index==1?null:new object[0];
       if(mode=="children-error")throw new COMException("simulated child enumeration failure");
       if(mode=="root-null")return null;
       if(mode=="non-array")return "invalid native result";
       if(mode=="invalid-array-item")return new object[]{parts[0],"invalid component"};
       return parts;
      default:throw new Exception("Unexpected component mutation "+call.MethodName);
     }
    }).GetTransparentProxy();
    parts=Enumerable.Range(0,mode=="mixed-states"?6:2).Select(index=>component(false,index)).ToArray();var rootComponent=component(true,0);
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"ROOT-CONFIG":call.MethodName=="GetRootComponent3"?rootComponent:throw new Exception("Configuration changed: "+call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
    var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
     switch(call.MethodName){case "GetPathName":return assemblyPath;case "GetType":return 2;case "GetTitle":return "ROOT.SLDASM";case "IsOpenedViewOnly":return mode=="view-only";case "get_ConfigurationManager":return manager;case "get_FeatureManager":return null;default:throw new Exception("Unexpected model mutation "+call.MethodName);}
    }).GetTransparentProxy();
    var app=new Proxy(appType,call=>call.MethodName=="GetType"?appType:throw new Exception("Unexpected application mutation "+call.MethodName)).GetTransparentProxy();
    var scanner=New("SolidWorksReferenceTreeScanner",app);var rejected=false;object result=null;
    try{result=Type("SolidWorksReferenceTreeScanner").GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{model,true});}
    catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
    var blocked=mode!="multi-configuration"&&mode!="mixed-states"&&mode!="leaf-null";
    if(rejected!=blocked)throw new Exception("Incorrect strict structure gate: "+mode+" rejected="+rejected);
    if(!blocked)
    {
     var children=((IEnumerable)Get(result,"Children")).Cast<object>().ToArray();
     if(children.Length!=parts.Length||(string)Get(result,"Configuration")!="ROOT-CONFIG"||(string)Get(children[0],"Configuration")!="CONFIG-A"||(string)Get(children[1],"Configuration")!="CONFIG-B")throw new Exception("Repeated configuration instances were omitted or switched");
     if(mode=="mixed-states"&&!children.Select(child=>Get(child,"Status").ToString()).SequenceEqual(new[]{"Normal","Lightweight","Lightweight","Suppressed","Hidden","Virtual"}))throw new Exception("Hidden/suppressed/lightweight/virtual components were dropped or relabeled");
    }
    if(File.ReadAllText(assemblyPath)!="root fixture"||File.ReadAllText(partPath)!="part fixture"||File.ReadAllText(subPath)!="sub fixture")throw new Exception("Scan changed model bytes");
    Console.WriteLine("PASS: strict structure "+mode+(rejected?" safely rejected":" complete without save/configuration switch"));
    if(mode=="children-error"||mode=="model-state-error")
    {
     var display=Type("SolidWorksReferenceTreeScanner").GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{model,false});
     if(string.IsNullOrWhiteSpace((string)Get(display,"ArchiveReadError")))throw new Exception("Display scan hid incomplete native data: "+mode);
     var archiveRejected=false;
     try{Type("PdmApiClient").GetMethod("ValidateArchiveReferenceCompleteness",Flags).Invoke(null,new[]{display});}
     catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){archiveRejected=true;}
     if(!archiveRejected)throw new Exception("Display-only fallback was accepted for archive: "+mode);
     Console.WriteLine("PASS: display fallback retains "+mode+" diagnostic and archive refuses it");
    }
   }
  }
  finally{Directory.Delete(folder,true);}
 }
 static void SixDrawingDiscoveryScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-six-drawings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try
  {
   var modelPath=Path.Combine(folder,"MODEL.SLDPRT");File.WriteAllText(modelPath,"model fixture");
   var drawings=Enumerable.Range(1,6).Select(index=>Path.Combine(folder,"independent-"+index+".SLDDRW")).ToArray();
   foreach(var drawing in drawings)File.WriteAllText(drawing,"drawing fixture");
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   foreach(var mode in new[]{"all-six","sixth-read-error","sixth-null","sixth-invalid"})
   {
    var root=New("CadTreeNode");Set(root,"FullPath",modelPath);Set(root,"FileName","MODEL.SLDPRT");Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
    var cwd="original-cwd";var readPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var app=new Proxy(appType,call=>{
     if(call.MethodName=="GetType")return appType;
     if(call.MethodName=="GetCurrentWorkingDirectory")return cwd;
     if(call.MethodName=="SetCurrentWorkingDirectory"){cwd=(string)call.Args[0];return true;}
     if(call.MethodName=="GetDocumentDependencies2")
     {
      var drawing=(string)call.Args[0];readPaths.Add(drawing);cwd="native-search-cwd";
      if(drawing==drawings[5]){if(mode=="sixth-read-error")throw new COMException("simulated last drawing read failure");if(mode=="sixth-null")return null;if(mode=="sixth-invalid")return new object[]{"unpaired reference"};}
      return new object[]{"MODEL",modelPath};
     }
     throw new Exception("Unexpected application mutation "+call.MethodName);
    }).GetTransparentProxy();
    var pack=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true),call=>{
     if(call.MethodName=="GetType")return interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true);
     if(call.MethodName=="set_IncludeDrawings"||call.MethodName=="set_IncludeSuppressed")return null;
     if(call.MethodName=="GetDocumentNames"){var output=(object[])call.Args.Clone();output[0]=new[]{modelPath}.Concat(drawings).ToArray();return new ReturnMessage(true,output,output.Length,call.LogicalCallContext,call);}
     throw new Exception("Unexpected PackAndGo mutation "+call.MethodName);
    }).GetTransparentProxy();
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="GetType"?interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true):call.MethodName=="GetPackAndGo"?pack:throw new Exception(call.MethodName)).GetTransparentProxy();
    var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true):call.MethodName=="get_Extension"?extension:throw new Exception(call.MethodName)).GetTransparentProxy();
    var scanner=New("SolidWorksReferenceTreeScanner",app);var rejected=false;
    try{Type("SolidWorksReferenceTreeScanner").GetMethod("AddNativeAssociatedDrawings",Flags).Invoke(scanner,new[]{model,root});}
    catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
    if(rejected!=(mode!="all-six")||readPaths.Count!=6||cwd!="original-cwd")throw new Exception("Incomplete six-drawing scan reported success or changed search directory: "+mode);
    var included=((IEnumerable)Get(root,"Children")).Cast<object>().Select(node=>(string)Get(node,"FullPath")).ToArray();
    if(!rejected&&(included.Length!=6||drawings.Except(included,StringComparer.OrdinalIgnoreCase).Any()))throw new Exception("One of six associated drawings was silently omitted");
    if(drawings.Any(path=>File.ReadAllText(path)!="drawing fixture"))throw new Exception("Drawing discovery changed source bytes");
    Console.WriteLine("PASS: six associated drawings "+mode+(rejected?" blocks whole operation":" all discovered"));
   }
  }
  finally{Directory.Delete(folder,true);}
 }
 static void NativeManifestScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-native-manifest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var virtualTop=Path.Combine(Path.GetTempPath(),"swx"+Guid.NewGuid().ToString("N"));
  try
  {
   var rootPath=Path.Combine(directory,"ROOT.SLDASM");var partPath=Path.Combine(directory,"PART.SLDPRT");var drawingPath=Path.Combine(directory,"DRAWING.SLDDRW");
   foreach(var path in new[]{rootPath,partPath,drawingPath})File.WriteAllText(path,System.IO.Path.GetFileName(path));
   var root=New("CadTreeNode");Set(root,"FullPath",rootPath);Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
   var part=New("CadTreeNode");Set(part,"FullPath",partPath);Set(part,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   var drawing=New("CadTreeNode");Set(drawing,"FullPath",drawingPath);Set(drawing,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
   ((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(drawing);
   var oldPath=Path.Combine(directory,"old","PART.SLDPRT");Set(part,"SourceReferencePath",oldPath);
   var sameNamePath=Path.Combine(directory,"other","PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(sameNamePath));File.WriteAllText(sameNamePath,"different content");
   var extraPath=Path.Combine(directory,"UNSEEN.SLDPRT");File.WriteAllText(extraPath,"extra model");
   var secondExtraPath=Path.Combine(directory,"六角螺母C级-M6.SLDPRT");File.WriteAllText(secondExtraPath,"second native dependency");
   var externalPath=Path.Combine(directory,"TABLE.xlsx");File.WriteAllText(externalPath,"external dependency");
   var validate=Type("NativeArchiveManifest").GetMethod("Validate",Flags);
   foreach(var mode in new[]{"all-in-tree","verified-old-path","unseen-model","same-name-other-path","changed-old-path","missing-file","external-file"})
   {
    if(mode=="changed-old-path"){Directory.CreateDirectory(Path.GetDirectoryName(oldPath));File.WriteAllText(oldPath,"different from verified part");}
    var input=mode=="all-in-tree"?new[]{rootPath,partPath,drawingPath}:mode=="verified-old-path"?new[]{rootPath,oldPath,drawingPath}:
     mode=="unseen-model"?new[]{rootPath,partPath,drawingPath,extraPath,secondExtraPath}:mode=="same-name-other-path"?new[]{rootPath,sameNamePath,drawingPath}:
     mode=="changed-old-path"?new[]{rootPath,oldPath,drawingPath}:mode=="missing-file"?new[]{rootPath,partPath,drawingPath,Path.Combine(directory,"MISSING.SLDPRT")}:
     new[]{rootPath,partPath,drawingPath,externalPath};
    var rejected=false;IReadOnlyList<string> external=null;try{external=(IReadOnlyList<string>)validate.Invoke(null,new object[]{root,input});}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
    if(rejected==(mode=="all-in-tree"||mode=="verified-old-path"||mode=="unseen-model"||mode=="external-file")
      ||mode=="external-file"&&(external?.Count!=1||external[0]!=externalPath))throw new Exception("Native manifest silently lost a dependency or rejected a verified path: "+mode);
    if(mode=="unseen-model"){
     foreach(var path in new[]{extraPath,secondExtraPath}){
      var attached=((IEnumerable)Get(root,"Children")).Cast<object>().SingleOrDefault(node=>(string)Get(node,"FullPath")==path);
      if(attached==null||Get(attached,"Role").ToString()!="ExternalFileDependency")throw new Exception("Additional CAD was not explicitly marked as an external file dependency: "+path);
     }
    }
    Console.WriteLine("PASS: native manifest "+mode);
   }
   var embeddedFolder=Path.Combine(virtualTop,"VC~~","STEP-assembly");Directory.CreateDirectory(embeddedFolder);
   var embeddedAssemblyPath=Path.Combine(embeddedFolder,"STEP-virtual^ROOT.SLDASM");
   var embeddedPartPath=Path.Combine(embeddedFolder,"STEP-part^ROOT.SLDPRT");
   var embeddedChildPath=Path.Combine(embeddedFolder,"STEP-rod^ROOT.SLDPRT");
   File.WriteAllText(embeddedAssemblyPath,"temporary embedded assembly");File.WriteAllText(embeddedPartPath,"temporary embedded part");
   var embeddedAssembly=New("CadTreeNode");Set(embeddedAssembly,"FullPath",embeddedAssemblyPath);Set(embeddedAssembly,"FileName",Path.GetFileName(embeddedAssemblyPath));
   Set(embeddedAssembly,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(embeddedAssembly,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
   var obsoleteIdentity=Guid.NewGuid();Set(embeddedAssembly,"DocumentId",obsoleteIdentity);Set(embeddedAssembly,"Revision","W1");
   var embeddedPart=New("CadTreeNode");Set(embeddedPart,"FullPath",embeddedPartPath);Set(embeddedPart,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   Set(embeddedPart,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
   var embeddedChild=New("CadTreeNode");Set(embeddedChild,"FullPath",embeddedChildPath);Set(embeddedChild,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   Set(embeddedChild,"Status",Enum.Parse(Type("CadReferenceStatus"),"Normal"));
   ((IList)Get(embeddedAssembly,"Children")).Add(embeddedPart);((IList)Get(embeddedAssembly,"Children")).Add(embeddedChild);
   ((IList)Get(root,"Children")).Add(embeddedAssembly);
   var modelItems=(IEnumerable)Type("PdmAddin").GetMethod("BuildBatchOperationItems",Flags).Invoke(null,new[]{root});
   if(modelItems.Cast<object>().Any(item=>new[]{embeddedAssemblyPath,embeddedPartPath,embeddedChildPath}.Contains((string)Get(Get(item,"Node"),"FullPath"))))
    throw new Exception("SolidWorks temporary virtual components became standalone archive files");
   var embeddedFiles=new[]{rootPath,partPath,drawingPath,embeddedAssemblyPath,embeddedPartPath,embeddedChildPath};
   var virtualExternal=(IReadOnlyList<string>)validate.Invoke(null,new object[]{root,embeddedFiles});
   if(virtualExternal.Count!=0)throw new Exception("Verified virtual components were treated as external data");
   Set(embeddedChild,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
   var snapshotRoot=New("CadTreeNode");Set(snapshotRoot,"FullPath",rootPath);Set(snapshotRoot,"FileName",Path.GetFileName(rootPath));
   Set(snapshotRoot,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(snapshotRoot,"DocumentId",Guid.NewGuid());
   ((IList)Get(snapshotRoot,"Children")).Add(embeddedAssembly);
   var request=Type("PdmApiClient").GetMethod("ToRequestNode",Flags).Invoke(null,new object[]{snapshotRoot,true,null});
   if(new JavaScriptSerializer().Serialize(request).Contains(obsoleteIdentity.ToString()))throw new Exception("Virtual component's stale PDM identity was persisted");
   Console.WriteLine("PASS: STEP-imported embedded virtual assembly and parts stay inside parent; no temporary file or stale identity archived");
   var priorSessionPart=Path.Combine(Path.GetTempPath(),"swx"+Guid.NewGuid().ToString("N"),"VC~~","STEP-assembly",Path.GetFileName(embeddedPartPath));
   var priorSessionFiles=new[]{rootPath,partPath,drawingPath,embeddedAssemblyPath,priorSessionPart,embeddedChildPath};
   if(((IReadOnlyList<string>)validate.Invoke(null,new object[]{root,priorSessionFiles})).Count!=0)
    throw new Exception("Prior SolidWorks session's virtual path was treated as an external file");
   Console.WriteLine("PASS: stale swx directory reconciles only by complete verified embedded hierarchy");
   var foreignSessionPart=Path.Combine(Path.GetPathRoot(Path.GetTempPath()),"Users","archived-other-user","AppData","Local","Temp","swx12345","VC~~","STEP-assembly",Path.GetFileName(embeddedPartPath));
   var foreignSessionFiles=new[]{rootPath,partPath,drawingPath,embeddedAssemblyPath,foreignSessionPart,embeddedChildPath};
   if(((IReadOnlyList<string>)validate.Invoke(null,new object[]{root,foreignSessionFiles})).Count!=0)
    throw new Exception("Another Windows user's saved virtual path was treated as an external file");
   Console.WriteLine("PASS: foreign-user Temp/swx virtual paths require the same complete verified embedded hierarchy");
   var unknownPath=Path.Combine(embeddedFolder,"UNVERIFIED^ROOT.SLDPRT");File.WriteAllText(unknownPath,"unknown temporary file");
   var rejectedUnknown=false;try{validate.Invoke(null,new object[]{root,embeddedFiles.Concat(new[]{unknownPath}).ToArray()});}
   catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejectedUnknown=true;}
   if(!rejectedUnknown)throw new Exception("Unverified temporary SolidWorks CAD was silently omitted");
   Console.WriteLine("PASS: unknown VC~~ temporary CAD remains blocked");
   var otherVirtualTop=Path.Combine(Path.GetTempPath(),"swx"+Guid.NewGuid().ToString("N"));
   var ambiguousPath=Path.Combine(otherVirtualTop,"VC~~","STEP-assembly",Path.GetFileName(embeddedPartPath));
   var ambiguous=New("CadTreeNode");Set(ambiguous,"FullPath",ambiguousPath);Set(ambiguous,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   Set(ambiguous,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));((IList)Get(root,"Children")).Add(ambiguous);
   var rejectedAmbiguous=false;try{validate.Invoke(null,new object[]{root,priorSessionFiles});}
   catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejectedAmbiguous=true;}
   if(!rejectedAmbiguous)throw new Exception("Ambiguous stale virtual hierarchy was automatically rebound");
   Console.WriteLine("PASS: ambiguous historical virtual path remains blocked");
   if(File.ReadAllText(partPath)!="PART.SLDPRT")throw new Exception("Native manifest modified a source CAD file");
  }
  finally{Directory.Delete(directory,true);if(Directory.Exists(virtualTop))Directory.Delete(virtualTop,true);}
 }
 static void PlannedTaskTimerScenario(Form host)
 {
  using(var pane=(Control)New("PdmTaskPaneControl",string.Empty))
  {
   pane.Dock=DockStyle.Fill;host.Controls.Add(pane);pane.CreateControl();
   var requests=0;Type("PdmTaskPaneControl").GetEvent("PlannedTasksRefreshRequested").AddEventHandler(pane,new EventHandler((_,__)=>requests++));
   Type("PdmTaskPaneControl").GetMethod("SetAuthenticatedUser").Invoke(pane,new object[]{"验收用户","fixture"});
   Type("PdmTaskPaneControl").GetMethod("SetPlannedTasks").Invoke(pane,new object[]{new[]{"任务一","任务二"}});
   var scroll=(System.Windows.Forms.Timer)Field(pane,"plannedTaskTimer");var refresh=(System.Windows.Forms.Timer)Field(pane,"plannedTaskRefreshTimer");
   var tick=typeof(System.Windows.Forms.Timer).GetMethod("OnTick",Flags);
   if(!scroll.Enabled||!refresh.Enabled||refresh.Interval<300000)throw new Exception("Planned task refresh interval is not independent/bounded");
   for(var index=0;index<100;index++)tick.Invoke(scroll,new object[]{EventArgs.Empty});
   if(requests!=0)throw new Exception("Visual task scrolling requested network refresh");
   tick.Invoke(refresh,new object[]{EventArgs.Empty});if(requests!=1)throw new Exception("Independent refresh timer did not request exactly one update");
   Type("PdmTaskPaneControl").GetMethod("SetAuthenticatedUser").Invoke(pane,new object[]{"",""});
   if(scroll.Enabled||refresh.Enabled)throw new Exception("Logged-out task timers still active");
   host.Controls.Remove(pane);
   Console.WriteLine("PASS: 100 planned-task scroll ticks cause zero network events; independent five-minute refresh and logout stop verified");
  }
 }
 static void DrawingDiscoveryScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-drawings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var identity=Type("PdmDocumentIdentityStore");var rootField=identity.GetField("workspaceRootResolver",Flags);var bindings=identity.GetField("bindingDirectory",Flags);
  var oldRoot=rootField.GetValue(null);var oldBindings=bindings.GetValue(null);
  rootField.SetValue(null,new Func<string>(()=>folder));bindings.SetValue(null,Path.Combine(folder,"bindings"));
  try
  {
   foreach(var mode in new[]{"variant-array","searched-path","bound-original","foreign-project","read-error","missing-drawing"})
   {
    var area=Path.Combine(folder,mode);Directory.CreateDirectory(area);var current=Path.Combine(area,"PART.SLDPRT");var original=Path.Combine(area,"old","PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(original));
    var drawing=Path.Combine(area,"independent-name.SLDDRW");File.WriteAllText(current,"model");File.WriteAllText(original,"model");if(mode!="missing-drawing")File.WriteAllText(drawing,"drawing");
    var project=Guid.NewGuid();var id=Guid.NewGuid();
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{current,id,project,Guid.NewGuid(),"W1","hash",5L});
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{original,id,mode=="foreign-project"?Guid.NewGuid():project,Guid.NewGuid(),"W1","hash",5L});
    var root=New("CadTreeNode");Set(root,"FullPath",Path.Combine(area,"ROOT.SLDASM"));Set(root,"FileName","ROOT.SLDASM");
    var part=New("CadTreeNode");Set(part,"FullPath",current);Set(part,"FileName","PART.SLDPRT");Set(part,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(part,"DocumentId",id);Set(part,"Configuration","默认");((IList)Get(root,"Children")).Add(part);
    var repeated=New("CadTreeNode");Set(repeated,"FullPath",current);Set(repeated,"FileName","PART.SLDPRT");Set(repeated,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(repeated,"Configuration","另一配置");((IList)Get(root,"Children")).Add(repeated);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var cwd="original-working-directory";var include=false;var suppressed=false;var dependenciesRead=0;
    var app=new Proxy(appType,call=>{
     if(call.MethodName=="GetType")return appType;
     if(call.MethodName=="GetCurrentWorkingDirectory")return cwd;
     if(call.MethodName=="SetCurrentWorkingDirectory"){cwd=(string)call.Args[0];return true;}
     if(call.MethodName=="GetDocumentDependencies2"){
      dependenciesRead++; if(mode=="read-error")throw new IOException("simulated native read failure");
      cwd="changed-by-native-search";
      var path=mode=="variant-array"||mode=="searched-path"&&(bool)call.Args[2]?current:original;
      return new object[]{"",path};
     }
     throw new Exception("Unexpected native write "+call.MethodName);
    }).GetTransparentProxy();
    var pack=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true),call=>{
     if(call.MethodName=="GetType")return interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true);
     if(call.MethodName=="set_IncludeDrawings"){include=(bool)call.Args[0];return null;}
     if(call.MethodName=="set_IncludeSuppressed"){suppressed=(bool)call.Args[0];return null;}
     if(call.MethodName=="GetDocumentNames"){var output=(object[])call.Args.Clone();output[0]=new object[]{current,drawing};return new ReturnMessage(true,output,output.Length,call.LogicalCallContext,call);}
     throw new Exception("Unexpected PackAndGo write "+call.MethodName);
    }).GetTransparentProxy();
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="GetType"?interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true):call.MethodName=="GetPackAndGo"?pack:throw new Exception(call.MethodName)).GetTransparentProxy();
    var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true):call.MethodName=="get_Extension"?extension:throw new Exception(call.MethodName)).GetTransparentProxy();
    var scanner=New("SolidWorksReferenceTreeScanner",app);var failed=false;
    try{Type("SolidWorksReferenceTreeScanner").GetMethod("AddNativeAssociatedDrawings",Flags).Invoke(scanner,new[]{model,root});}
    catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){failed=true;}
    if(failed!=(mode=="foreign-project"||mode=="read-error"||mode=="missing-drawing"))throw new Exception("Incorrect drawing discovery gate "+mode);
    if(!include||!suppressed||cwd!="original-working-directory")throw new Exception("Native drawing scan mutated user settings "+mode);
    if(!failed && (((IList)Get(part,"Children")).Count!=1||((IList)Get(repeated,"Children")).Count!=1))throw new Exception("Drawing missing from repeated/configuration instances "+mode+" counts="+((IList)Get(part,"Children")).Count+"/"+((IList)Get(repeated,"Children")).Count+" deps="+dependenciesRead);
    if(File.ReadAllText(current)!="model"||File.ReadAllText(original)!="model")throw new Exception("Discovery changed model bytes");
    Console.WriteLine("PASS: Native associated drawing discovery "+mode);
   }
  }
  finally{rootField.SetValue(null,oldRoot);bindings.SetValue(null,oldBindings);Directory.Delete(folder,true);}
 }
 sealed class Proxy:RealProxy
 {
  readonly Func<IMethodCallMessage,object> callback;
  public Proxy(System.Type type,Func<IMethodCallMessage,object> callback):base(type){this.callback=callback;}
  public override IMessage Invoke(IMessage message){var call=(IMethodCallMessage)message;try{var result=callback(call);return result as IMessage??new ReturnMessage(result,call.Args,call.ArgCount,call.LogicalCallContext,call);}catch(Exception e){return new ReturnMessage(e,call);}}
 }
 sealed class Model:RealProxy
 {
  public string Path;public bool Dirty;public Action OnSave;bool readOnly=true;readonly List<string> events;
  public Model(System.Type type,string path,bool dirty,List<string> events):base(type){Path=path;Dirty=dirty;this.events=events;}
  public override IMessage Invoke(IMessage message)
  {
   var call=(IMethodCallMessage)message;object value;
   switch(call.MethodName)
   {
    case "GetPathName":value=Path;break;
    case "GetSaveFlag":value=Dirty;break;
    case "IsOpenedReadOnly":value=readOnly;break;
    case "SetReadOnlyState":readOnly=(bool)call.Args[0];value=true;break;
    case "Save3":
     if(readOnly||(File.GetAttributes(Path)&FileAttributes.ReadOnly)!=0)return new ReturnMessage(new IOException("Regression: Save3 called while read-only"),call);
     events.Add("save:"+System.IO.Path.GetFileName(Path));Dirty=false;OnSave?.Invoke();call.Args[1]=0;call.Args[2]=0;value=true;break;
    default:return new ReturnMessage(new Exception("Unexpected model call "+call.MethodName),call);
   }
   return new ReturnMessage(value,call.Args,call.ArgCount,call.LogicalCallContext,call);
  }
 }
 sealed class Handler:HttpMessageHandler
 {
  readonly List<object> nodes;readonly Guid project,session;readonly List<string> events;readonly bool denied;
  public Handler(List<object> nodes,Guid project,Guid session,List<string> events,bool denied){this.nodes=nodes;this.project=project;this.session=session;this.events=events;this.denied=denied;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(!request.RequestUri.AbsolutePath.EndsWith("/checkout"))throw new Exception("Unexpected business write "+request.RequestUri.AbsolutePath);
   var node=nodes.Single(n=>request.RequestUri.AbsolutePath.Contains(((Guid)Get(n,"DocumentId")).ToString()));
   events.Add("checkout:"+(string)Get(node,"FileName"));
   var json=denied?"{\"message\":\"图档正在由其他人编辑\"}":new JavaScriptSerializer().Serialize(new{id=Get(node,"DocumentId"),projectId=project,fileName=Get(node,"FileName"),checkedOutBy="tester",checkoutSessionId=session,revision=new{display="A"}});
   return Task.FromResult(new HttpResponseMessage(denied?HttpStatusCode.Conflict:HttpStatusCode.OK){Content=new StringContent(json)});
  }
 }
 static System.Type Type(string name)=>assembly.GetType("Upton.Pdm.SolidWorks."+name,true);
 static object New(string name,params object[] args)=>Activator.CreateInstance(Type(name),Flags&~BindingFlags.Static,null,args,null);
 static PropertyInfo Property(object o,string name)=>o.GetType().GetProperty(name,Flags);
 static object Get(object o,string name)=>Property(o,name).GetValue(o);
 static void Set(object o,string name,object value)=>Property(o,name).SetValue(o,value);
 static object Field(object o,string name)=>o.GetType().GetField(name,Flags).GetValue(o);
 static void SetField(object o,string name,object value)=>o.GetType().GetField(name,Flags).SetValue(o,value);
}
