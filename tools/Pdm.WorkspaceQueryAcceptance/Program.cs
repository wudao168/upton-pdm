using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly assembly;
    static void Field(object target,string name,object value)=>target.GetType().GetField(name,All).SetValue(target,value);
    static void Set(object target,string name,object value)=>target.GetType().GetProperty(name,All).SetValue(target,value);
    static Type Type(string name)=>assembly.GetType(name,true);
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    [STAThread] static int Main(string[] args)
    {
        try{return RunHost(args);}
        catch(Exception error){Console.WriteLine("FAIL startup "+error.GetType().FullName+" "+error.Message);return 1;}
    }
    static int RunHost(string[] args)
    {
        var host=AppDomain.CurrentDomain.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var p=Path.Combine(host,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        assembly=Assembly.LoadFrom(Path.Combine(host,"Upton.Pdm.Desktop.exe"));
        var app=new Application();app.Resources["WindowBackground"]=new SolidColorBrush(Color.FromRgb(244,246,248));
        var window=(Window)Activator.CreateInstance(Type("Upton.Pdm.Desktop.MainWindow"));
        var onLoaded=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),window,
            window.GetType().GetMethod("OnLoaded",All,null,new[]{typeof(object),typeof(RoutedEventArgs)},null));
        window.Loaded-=onLoaded;window.Opacity=0;window.ShowInTaskbar=false;window.Left=-20000;window.Top=-20000;
        Exception failure=null;
        window.Loaded+=async (_,__)=>
        {
            try{await Run(window,Path.GetFullPath(args[0]));}
            catch(Exception error){failure=error;Console.WriteLine("FAIL "+error.GetType().FullName+" "+error.Message);if(error.InnerException!=null)Console.WriteLine("INNER "+error.InnerException.GetType().FullName+" "+error.InnerException.Message);}
            finally{window.GetType().GetMethod("ExitApplication",All).Invoke(window,null);}
        };
        app.Run(window);return failure==null?0:1;
    }
    static async Task Run(Window window,string root)
    {
        if(Directory.Exists(root))throw new Exception("测试目录已存在");Directory.CreateDirectory(root);
        var view=window.FindName("WorkspaceView");
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"webview"),new CoreWebView2EnvironmentOptions("--disable-gpu"));
        var ensure=view.GetType().GetMethods().Where(m=>m.Name=="EnsureCoreWebView2Async").OrderBy(m=>m.GetParameters().Length).First();
        var ensureArgs=new object[ensure.GetParameters().Length];ensureArgs[0]=environment;
        await (Task)ensure.Invoke(view,ensureArgs);
        var settings=Type("Upton.Pdm.LocalSettings.WorkspaceSettingsStore");var settingsPath=Path.Combine(root,"settings.json");
        File.WriteAllText(settingsPath,new JavaScriptSerializer().Serialize(new{WorkspaceRoot=root}));settings.GetField("SettingsPath",All).SetValue(null,settingsPath);
        var bindings=Type("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore");bindings.GetField("bindingDirectory",All).SetValue(null,Path.Combine(root,"bindings"));
        var directory=Path.Combine(root,"View","TEST");Directory.CreateDirectory(directory);var path=Path.Combine(directory,"PART.SLDPRT");
        File.WriteAllText(path,"unarchived content A");var project=Guid.NewGuid();var document=Guid.NewGuid();var version=Guid.NewGuid();
        bindings.GetMethod("TryWriteControlledVersion",All).Invoke(null,new object[]{path,document,(Guid?)project,version,"W1",new string('0',64),1L});File.SetAttributes(path,FileAttributes.ReadOnly);
        var request=Activator.CreateInstance(Type("Upton.Pdm.SolidWorks.WorkspaceDocumentStateRequest"),true);
        Set(request,"DocumentId",document);Set(request,"FileName","PART.SLDPRT");Set(request,"LatestRevision","W1");
        var requests=Array.CreateInstance(request.GetType(),1);requests.SetValue(request,0);
        var context=Activator.CreateInstance(window.GetType().GetNestedType("WorkspaceStateRequestContext",BindingFlags.NonPublic),new object[]{project,"TEST","QA",requests});
        Field(window,"workspaceStateRequest",context);Field(window,"accessToken","test-token");Field(window,"activeCompanyId",Guid.NewGuid().ToString());
        var config=Activator.CreateInstance(Type("Upton.Pdm.ClientShared.ClientBootstrapConfiguration"),true);Set(config,"ApiBaseUrl","http://fixture.invalid");Field(window,"bootstrapConfiguration",config);
        var handler=new Versions(document,version);var original=(HttpClient)window.GetType().GetField("apiClient",All).GetValue(window);original.Dispose();Field(window,"apiClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
        Task Refresh()=>(Task)window.GetType().GetMethod("PublishWorkspaceLocalStateAsync",All).Invoke(window,new[]{context});
        await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Refresh()));
        Check(handler.Count==1,"12次并发刷新只核对一次版本");var originalBytes=File.ReadAllBytes(path);
        Check(Encoding.UTF8.GetString(originalBytes)=="unarchived content A"&&(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,
            "并发核对没有覆盖未存档内容或改变只读属性");
    }
    sealed class Versions:HttpMessageHandler
    {
        readonly Guid document,version;
        public Versions(Guid documentId,Guid versionId){document=documentId;version=versionId;}
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(!request.RequestUri.AbsolutePath.EndsWith("/versions"))throw new Exception("意外接口："+request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref Count);await Task.Delay(75,token);
            var value=new[]{new{id=version,documentId=document,sha256=new string('F',64),revision=new{display="W1"},propertySnapshot=new Dictionary<string,string>()}};
            return new(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value),Encoding.UTF8,"application/json")};
        }
    }
}
