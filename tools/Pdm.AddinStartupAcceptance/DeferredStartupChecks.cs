using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private static int VerifyDeferredStartup(string path, string output)
    {
        var assembly = Assembly.LoadFrom(path);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var file = Path.Combine(Path.GetDirectoryName(path), new AssemblyName(e.Name).Name + ".dll"); return File.Exists(file) ? Assembly.LoadFrom(file) : null; };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var ready = false;
        var application = new StartupProxy(typeof(ISldWorks), call =>
        {
            if (call.MethodName == "get_StartupProcessCompleted") return ready;
            if (call.MethodName == "CreateTaskpaneView2") return new StartupProxy(((MethodInfo)call.MethodBase).ReturnType, _ => null).GetTransparentProxy();
            if (((MethodInfo)call.MethodBase).ReturnType == typeof(bool)) return true;
            return null;
        }).GetTransparentProxy();
        var type = assembly.GetType("Upton.Pdm.SolidWorks.PdmAddin", true); var addin = Activator.CreateInstance(type);
        object Call(string method, params object[] values) => type.GetMethod(method, flags).Invoke(addin, values);
        var scheduled = type.GetField("serverInitializationScheduled", flags);
        Exception failure = null; var connected = false; var watch = Stopwatch.StartNew();
        try
        {
            Application.EnableVisualStyles();
            connected = (bool)Call("ConnectToSW", application, 1);
            if (!connected || (int)scheduled.GetValue(addin) != 0) throw new Exception("加载回调未正确延后联网初始化");
            var connectMs = watch.ElapsedMilliseconds;
            Call("TryInitializeServerAfterStartup");
            if ((int)scheduled.GetValue(addin) != 0) throw new Exception("SolidWorks尚未启动完成时提前联网");
            ready = true; Call("TryInitializeServerAfterStartup"); Call("TryInitializeServerAfterStartup");
            if ((int)scheduled.GetValue(addin) != 1) throw new Exception("启动完成后没有一次性安排后台初始化");
            Call("DisconnectFromSW"); connected = false;
            Application.DoEvents();
            if ((int)scheduled.GetValue(addin) != 0 || type.GetField("apiClient", flags).GetValue(addin) != null) throw new Exception("卸载后遗留的初始化任务重新连接服务器");
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { succeeded = true, connectMilliseconds = connectMs, checks = new[] { "加载回调只初始化本机界面", "启动未完成时不安排网络初始化", "启动完成后只安排一次后台初始化", "卸载取消尚未执行的后台任务" } }));
            Console.WriteLine("PASS deferred startup proxy; ConnectToSW=" + connectMs + "ms");
        }
        catch (Exception error)
        {
            failure = error; Console.WriteLine(error);
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { succeeded = false, error = error.ToString() }));
        }
        finally { if (connected) Call("DisconnectFromSW"); }
        return failure == null ? 0 : 1;
    }

    private sealed class StartupProxy : RealProxy
    {
        private readonly Func<IMethodCallMessage, object> handler;
        public StartupProxy(Type type, Func<IMethodCallMessage, object> handler) : base(type) { this.handler = handler; }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try
            {
                var result = handler(call); var method = (MethodInfo)call.MethodBase;
                if (result == null && method.ReturnType.IsValueType && method.ReturnType != typeof(void)) result = Activator.CreateInstance(method.ReturnType);
                var args = call.Args; var parameters = method.GetParameters();
                for (var i = 0; i < args.Length; i++) if (parameters[i].IsOut) { var value = parameters[i].ParameterType.GetElementType(); args[i] = value.IsValueType ? Activator.CreateInstance(value) : null; }
                return new ReturnMessage(result, args, args.Length, call.LogicalCallContext, call);
            }
            catch (Exception error) { return new ReturnMessage(error, call); }
        }
    }
}
