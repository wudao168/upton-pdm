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

internal static class Program
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static Assembly assembly;
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string name,string text);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [STAThread] static void Main(string[] args)
 {
  AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])),new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
  assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
  Exception failure=null;
  using(var host=new Form{Opacity=0,ShowInTaskbar=false})
  using(var timer=new System.Windows.Forms.Timer{Interval=50})
  {
   timer.Tick+=(_,__)=>{var dialog=FindWindow("#32770","整体存档前保存文件");if(dialog!=IntPtr.Zero)SendMessage(dialog,0x111,new IntPtr(6),IntPtr.Zero);};
   host.Shown+=async (_,__)=>{try{ControlledReferenceScenario();ControlledScannerScenario();SearchScopeScenario();PropertyCompletenessScenario();await PreflightGateScenario(host);SuppressedReadScenario();ReadOnlyOpenScenario();DiagnosticTabScenario(host);CenteringScenario();CloseReferencedScenario();EntryScenario(host);await Scenario(false,false);await Scenario(true,false);await Scenario(true,true);}catch(Exception e){failure=e;}finally{host.Close();}};
   timer.Start();Application.Run(host);
  }
  if(failure!=null){Console.WriteLine("FAIL: "+failure.GetType().FullName+" "+failure.Message);if(failure.InnerException!=null)Console.WriteLine("INNER: "+failure.InnerException.GetType().FullName+" "+failure.InnerException.Message);Environment.ExitCode=1;}
 }
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
     case "get_IsVirtual":return false;case "GetSuppression":return 2;case "IsHidden":return false;case "GetModelDoc2":return null;
     case "GetChildren":return root?new[]{part}:new object[0];default:throw new Exception("Unexpected component mutation/read "+call.MethodName);}
   }).GetTransparentProxy();part=component(false);var rootComponent=component(true);
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>{
    if(call.MethodName=="get_Name")return mode=="configuration"?"Other":"TP900";
    if(call.MethodName=="GetRootComponent3")return mode=="unreadable"?null:rootComponent;
    throw new Exception("Unexpected configuration mutation "+call.MethodName);
   }).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var model=new Proxy(modelType,call=>{
    switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return "SUB.SLDASM";case "GetSaveFlag":return mode=="dirty";case "GetType":return 2;
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
 sealed class PreflightReadHandler:HttpMessageHandler
 {
  public int Writes;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {if(request.Method!=HttpMethod.Get){Writes++;throw new Exception("Unexpected write "+request.RequestUri);}return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")});}
 }
 static void ControlledReferenceScenario()
 {
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
 }
 sealed class Proxy:RealProxy
 {
  readonly Func<IMethodCallMessage,object> callback;
  public Proxy(System.Type type,Func<IMethodCallMessage,object> callback):base(type){this.callback=callback;}
  public override IMessage Invoke(IMessage message){var call=(IMethodCallMessage)message;try{var result=callback(call);return result as IMessage??new ReturnMessage(result,call.Args,call.ArgCount,call.LogicalCallContext,call);}catch(Exception e){return new ReturnMessage(e,call);}}
 }
 sealed class Model:RealProxy
 {
  public string Path;public bool Dirty;bool readOnly=true;readonly List<string> events;
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
     events.Add("save:"+System.IO.Path.GetFileName(Path));Dirty=false;call.Args[1]=0;call.Args[2]=0;value=true;break;
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
