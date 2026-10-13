using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly assembly;
    [STAThread]
    static int Main(string[] args)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => Assembly.LoadFrom(Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll"));
        assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        try
        {
            Scenario(false, false).GetAwaiter().GetResult();
            Scenario(true, false).GetAwaiter().GetResult();
            Scenario(true, true).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception) { Console.WriteLine(exception); return 1; }
    }

    static async Task Scenario(bool changed, bool dirty)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-session-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "part.SLDPRT");
        File.WriteAllText(path, "base");
        var plugin = New("PdmAddin");
        var session = (Guid)Field(plugin, "checkoutSessionId");
        var entry = New("LocalEditDraft");
        Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "ProjectId", Guid.NewGuid()); Set(entry, "SessionId", session);
        Set(entry, "CheckedOutAt", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560));
        Set(entry, "SourcePath", path); Set(entry, "BaseVersionId", Guid.NewGuid());
        Set(entry, "BaseSha256", Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { path }));
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            Set(entry, "ProcessId", process.Id); Set(entry, "ProcessStartedUtcTicks", process.StartTime.ToUniversalTime().Ticks);
        }
        if (changed) File.WriteAllText(path, "saved change");
        var journal = New("EditDraftJournal", root, "fixture");
        SetField(plugin, "editJournal", journal);
        ((IDictionary)Field(plugin, "localEdits")).Add(Get(entry, "DocumentId"), entry);
        SetField(plugin, "authenticatedUsername", "tester");
        var appType = plugin.GetType().GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
        var extensionType = modelType.GetProperty("Extension").PropertyType;
        var dirtyFlag = dirty;
        var saves = 0;
        var extension = new Proxy(extensionType, call =>
        {
            if (call.MethodName != "SaveAs") throw new Exception("Unexpected extension call: " + call.MethodName);
            File.WriteAllText((string)call.Args[0], "unsaved memory");
            call.Args[4] = 0; call.Args[5] = 0; dirtyFlag = false; saves++; return true;
        }).GetTransparentProxy();
        var model = new Proxy(modelType, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return path;
                case "GetType": return 1;
                case "GetSaveFlag": return dirtyFlag;
                case "get_Extension": return extension;
                case "SetSaveFlag": dirtyFlag = true; return null;
                default: throw new Exception("Unexpected model call: " + call.MethodName);
            }
        }).GetTransparentProxy();
        var loaded = true;
        var commandActive = true;
        var commandQueryFails = false;
        var documentReads = 0;
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            if (call.MethodName == "GetRunningCommandInfo")
            {
                if (commandQueryFails) throw new InvalidOperationException("模拟命令状态暂不可读");
                return new ReturnMessage(null, new object[] { commandActive ? 42 : 0, commandActive ? "插入零部件" : "", commandActive },
                    3, call.LogicalCallContext, call);
            }
            if (call.MethodName == "GetDocuments") { documentReads++; return loaded ? new[] { model } : Array.Empty<object>(); }
            throw new Exception("Unexpected application call: " + call.MethodName);
        }).GetTransparentProxy());
        SetField(plugin, "pendingTreeRefresh", 1);
        SetField(plugin, "pendingDrawingQrGraphicsRefresh", 1);
        Invoke(plugin, "OnSolidWorksIdle");
        Invoke(plugin, "RefreshTree", false, false);
        Require(((ICollection)Invoke(plugin, "PrepareClosedEdits")).Count == 0, "Command-time maintenance ran");
        Require(documentReads == 0 && saves == 0 && !(bool)Get(entry, "ObservedOpen"), "Insertion triggered native reads/save/close");
        Require((int)Field(plugin, "pendingTreeRefresh") == 1 && (int)Field(plugin, "pendingDrawingQrGraphicsRefresh") == 1,
            "Deferred work was lost");
        commandActive = false; commandQueryFails = true;
        Invoke(plugin, "OnSolidWorksIdle");
        Require(documentReads == 0, "Unknown command state allowed background work");
        commandQueryFails = false;
        var pending = (ICollection)Invoke(plugin, "PrepareClosedEdits");
        Require(pending.Count == 0 && (bool)Get(entry, "ObservedOpen"), "An open/referenced document was released");
        if (dirty)
        {
            Require(saves == 1 && dirtyFlag, "Draft snapshot cleared the original unsaved flag");
            Require(File.ReadAllText((string)Get(entry, "DraftPath")) == "unsaved memory", "Unsaved memory was not backed up");
            Require(File.ReadAllText(path) == "saved change", "Snapshot modified original file");
        }
        loaded = false;
        pending = (ICollection)Invoke(plugin, "PrepareClosedEdits");
        Require(pending.Count == 1 && (bool)Get(entry, "PendingClose"), "Closed document not queued durably");
        if (changed) Require((bool)Invoke(journal, "HasVerifiedDraft", entry), "Changed file lacks verified draft");
        using (var handler = new Handler(entry))
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client);
            handler.Fail = true;
            try { await (Task)Invoke(plugin, "ClosePreservedEditAsync", entry, CancellationToken.None); throw new Exception("Network failure ignored"); }
            catch (HttpRequestException) { Require((bool)Get(entry, "PendingClose"), "Failed close lost retry record"); }
            handler.Fail = false;
            await (Task)Invoke(plugin, "ClosePreservedEditAsync", entry, CancellationToken.None);
            Require((bool)Get(entry, "Closed") && !(bool)Get(entry, "PendingClose"), "Successful close not recorded");
            Require(handler.CloseCalls == 1 && handler.DraftPreserved == changed, "Wrong close semantics");
            Require(handler.CheckedOutAt == (DateTime)Get(entry, "CheckedOutAt"), "Checkout generation lost in HTTP serialization");
        }
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
        Console.WriteLine("PASS: open/reference protection, durable close/retry, generation serialization; changed=" + changed + ", unsaved=" + dirty);
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly object entry;
        public bool Fail, DraftPreserved;
        public int CloseCalls;
        public DateTime CheckedOutAt;
        public Handler(object entry) { this.entry = entry; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Fail) throw new HttpRequestException("模拟断网");
            var serializer = new JavaScriptSerializer();
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(serializer.Serialize(new[] { new
                { id = Get(entry, "DocumentId"), checkedOutBy = "tester", checkoutSessionId = Get(entry, "SessionId"), checkedOutAt = ((DateTime)Get(entry, "CheckedOutAt")).ToString("O") } })) };
            Require(request.RequestUri.AbsolutePath.EndsWith("/close-edit"), "Unexpected write endpoint");
            var body = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
            DraftPreserved = (bool)body["draftPreserved"];
            CheckedOutAt = DateTime.Parse((string)body["checkedOutAt"], null, System.Globalization.DateTimeStyles.RoundtripKind);
            CloseCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    sealed class Proxy : RealProxy
    {
        readonly Func<IMethodCallMessage, object> callback;
        public Proxy(System.Type type, Func<IMethodCallMessage, object> callback) : base(type) { this.callback = callback; }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try { var result = callback(call); return result as IMessage ?? new ReturnMessage(result, call.Args, call.ArgCount, call.LogicalCallContext, call); }
            catch (Exception exception) { return new ReturnMessage(exception, call); }
        }
    }
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static System.Type Type(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Flags & ~BindingFlags.Static, null, args, null);
    static object Get(object value, string name) => value.GetType().GetProperty(name, Flags).GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags).SetValue(value, data);
    static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static void SetField(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
    static object Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
}
