using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    [STAThread] static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { while (error.InnerException != null) error = error.InnerException; Console.WriteLine("FAIL startup " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    static int Run(string[] args)
    {
        var directory = AppDomain.CurrentDomain.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "Upton.Pdm.Desktop.exe"));
        var application = new Application();
        application.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(244, 246, 248));
        var window = (Window)Activator.CreateInstance(assembly.GetType("Upton.Pdm.Desktop.MainWindow", true));
        window.Opacity = 0; window.ShowInTaskbar = false;
        window.WindowState = WindowState.Normal; window.Left = -20000; window.Top = -20000;
        Exception failure = null; bool done = false, busy = false;
        string lastLocation = null;
        application.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; done = true; application.Shutdown(1); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        var started = DateTime.UtcNow;
        timer.Tick += async (_, __) =>
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
            {
                failure = new TimeoutException("原生客户端界面未在60秒内准备完成。"); done = true; application.Shutdown(1); return;
            }
            if (busy || done) return;
            var view = window.FindName("WorkspaceView");
            var core = view?.GetType().GetProperty("CoreWebView2")?.GetValue(view);
            if (core == null) return;
            // Reloading before the first successful navigation aborts that navigation and
            // legitimately triggers the client's offline fallback. Wait for its ready signal.
            if (!(bool)window.GetType().GetField("workspaceNavigationReady", Flags).GetValue(window)) return;
            busy = true;
            try
            {
                var location = await Execute(core, "location.href");
                if (location != lastLocation) { Console.WriteLine("Native UI location: " + location); lastLocation = location; }
                if (!location.Contains(args[0])) { busy = false; return; }
                using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) })
                {
                    var secrets = Json.DeserializeObject(File.ReadAllText(args[1])) as System.Collections.Generic.Dictionary<string, object>;
                    var response = await client.PostAsync("http://192.168.2.8:5173/api/auth/login", new StringContent(Json.Serialize(new { username = secrets["bootstrapAdminUsername"], password = secrets["bootstrapAdminPassword"] }), System.Text.Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode();
                    Console.WriteLine("Native test login completed");
                    var session = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("Native session response read; setting isolated browser session");
                    await Execute(core, "localStorage.setItem('upton-pdm-session'," + Json.Serialize(session) + ")");
                    Console.WriteLine("Native isolated browser session set; requesting reload");
                    core.GetType().GetMethod("Reload").Invoke(core, null);
                    Console.WriteLine("Native reload requested");
                }
                System.Collections.Generic.Dictionary<string, object> result = null;
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(1000);
                    var currentView = window.FindName("WorkspaceView");
                    core = currentView?.GetType().GetProperty("CoreWebView2")?.GetValue(currentView);
                    if (core == null) continue;
                    Console.WriteLine("Native page probe " + attempt);
                    string page;
                    try
                    {
                        page = await Execute(core, "JSON.stringify({url:location.href,visibleVersion:document.body.innerText.includes('V" + args[0] + "'),observedVersion:(document.body.innerText.match(/V\\d{4}\\.\\d{2}\\.\\d{2}\\.\\d{4,6}/)||[])[0],scripts:[...document.querySelectorAll('script[src]')].map(node=>node.src)})");
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("Native read-only page probe timed out during navigation; reacquiring core on the next bounded attempt");
                        continue;
                    }
                    if (page == "null") continue;
                    result = Json.DeserializeObject(Json.Deserialize<string>(page)) as System.Collections.Generic.Dictionary<string, object>;
                    if ((bool)result["visibleVersion"]) break;
                }
                Console.WriteLine("Native main page inspected");
                result.TryGetValue("observedVersion", out var observedVersion);
                Console.WriteLine("Native visible version: " + observedVersion);
                if (!(bool)result["visibleVersion"])
                {
                    Console.WriteLine("Native page state: " + await Execute(core, "JSON.stringify({title:document.title,text:document.body.innerText.slice(0,1200)})"));
                    throw new Exception("原生客户端可见版本号不一致。");
                }
                await Execute(core, "window.chrome.webview.postMessage({type:'review-overlay-state',payload:{visible:false}})");
                await Task.Delay(1000);
                var overlay = window.GetType().GetField("reviewOverlay", Flags).GetValue(window);
                Console.WriteLine("Native review overlay requested");
                if (overlay == null) throw new Exception("原生审核浮层未创建。");
                ((Window)overlay).Opacity = 0;
                ((Window)overlay).ShowInTaskbar = false;
                overlay.GetType().GetMethod("ShowAt", Flags).Invoke(overlay, new object[] { -20000d, -20000d, 800d, 600d });
                await (Task)overlay.GetType().GetMethod("InitializeAsync", Flags).Invoke(overlay, null);
                var reviewView = ((Window)overlay).FindName("ReviewView");
                await Task.Delay(1000);
                var reviewCore = reviewView.GetType().GetProperty("CoreWebView2").GetValue(reviewView);
                var reviewPage = await Execute(reviewCore, "JSON.stringify({url:location.href,scripts:[...document.querySelectorAll('script[src]')].map(node=>node.src)})");
                var reviewResult = Json.DeserializeObject(Json.Deserialize<string>(reviewPage)) as System.Collections.Generic.Dictionary<string, object>;
                if (!((string)reviewResult["url"]).Contains(args[0]) || !((string)reviewResult["url"]).StartsWith("http://192.168.2.8:5173/")) throw new Exception("审核浮层未使用服务器同版界面。");
                if (args.Length > 3)
                {
                    var extra = Json.DeserializeObject(File.ReadAllText(args[3])) as System.Collections.Generic.Dictionary<string, object>;
                    if (extra.ContainsKey("workspaceRoot"))
                    {
                        await VerifyWorkspaceRecovery(assembly, window, extra);
                        result["workspaceArchiveRecoveryVerified"] = true;
                    }
                    else
                    {
                    var fixture = Json.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(args[3]));
                    var previewMethod = window.GetType().GetMethod("PreviewDocumentAsync", Flags);
                    Func<string, Task> preview = version => (Task)previewMethod.Invoke(window, new object[] {
                        new System.Collections.Generic.Dictionary<string, object> { ["documentId"] = fixture["documentId"], ["fileName"] = fixture["fileName"], ["versionId"] = version }
                    });
                    await preview(fixture["badVersion"]);
                    if ((bool)window.GetType().GetField("previewDocumentReady", Flags).GetValue(window)) throw new Exception("失败工程图被标记为预览就绪。");
                    var stale = preview(fixture["badVersion"]);
                    var latest = preview(fixture["goodVersion"]);
                    await Task.WhenAll(stale, latest);
                    if (!(bool)window.GetType().GetField("previewDocumentReady", Flags).GetValue(window)
                        || (Guid?)window.GetType().GetField("previewVersionId", Flags).GetValue(window) != Guid.Parse(fixture["goodVersion"]))
                        throw new Exception("连续切图未保留最后选择的版本。");
                    result["nativePreviewFailureAndSwitchVerified"] = true;
                    Console.WriteLine("PASS native MainWindow reports failed 2D and serializes switching to a valid same-name version");
                    }
                }
                result["review"] = reviewResult; result["succeeded"] = true; result["version"] = args[0];
                File.WriteAllText(args[2], Json.Serialize(result));
                Console.WriteLine("PASS actual native MainWindow + review WebView2 use server UI " + args[0]);
                done = true; Exit(window);
            }
            catch (Exception error) { failure = error; done = true; Exit(window); }
            finally { busy = false; }
        };
        timer.Start(); application.Run(window); timer.Stop();
        if (failure == null && done) return 0;
        Console.WriteLine("FAIL " + failure); return 1;
    }
    static async Task<string> Execute(object core, string script)
    {
        var task = (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync").Invoke(core, new object[] { script });
        if (await Task.WhenAny(task, Task.Delay(5000)) != task)
            throw new TimeoutException("WebView2脚本调用未在5秒内返回，请查看上一条验收步骤日志。");
        return await task;
    }
    static async Task VerifyWorkspaceRecovery(Assembly assembly, Window window, System.Collections.Generic.Dictionary<string, object> fixture)
    {
        const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var workspace = (string)fixture["workspaceRoot"];
        var settings = assembly.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true);
        var settingsPath = Path.Combine(workspace, "test-workspace-settings.json");
        File.WriteAllText(settingsPath, Json.Serialize(new { WorkspaceRoot = workspace }));
        settings.GetField("SettingsPath", staticFlags).SetValue(null, settingsPath);
        var bindings = assembly.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true);
        bindings.GetField("bindingDirectory", staticFlags).SetValue(null, Path.Combine(workspace,"test-bindings"));
        var project = Guid.Parse((string)fixture["projectId"]);
        var code = (string)fixture["projectCode"];
        var requestType = assembly.GetType("Upton.Pdm.SolidWorks.WorkspaceDocumentStateRequest", true);
        var files = (System.Collections.IList)fixture["files"];
        var requests = Array.CreateInstance(requestType, files.Count);
        var original = new System.Collections.Generic.Dictionary<string, byte[]>();
        for (var i=0;i<files.Count;i++)
        {
            var file = (System.Collections.Generic.Dictionary<string, object>)files[i];
            var path = (string)file["path"]; original[path] = File.ReadAllBytes(path);
            var id = Guid.Parse((string)file["documentId"]);
            var written = (bool)bindings.GetMethod("TryWriteControlledVersion",staticFlags).Invoke(null,
                new object[]{path,id,(Guid?)project,Guid.NewGuid(),"W0",new string('0',64),1L});
            if (!written) throw new Exception("测试校验记录未写入。");
            File.SetAttributes(path,FileAttributes.ReadOnly);
            var request = Activator.CreateInstance(requestType);
            requestType.GetProperty("DocumentId").SetValue(request,id);
            requestType.GetProperty("FileName").SetValue(request,(string)file["fileName"]);
            requestType.GetProperty("LatestRevision").SetValue(request,(string)file["revision"]);
            requests.SetValue(request,i);
        }
        var watcher = window.GetType().GetField("workspaceWatcher",Flags).GetValue(window) as IDisposable;
        watcher?.Dispose();
        var contextType = window.GetType().GetNestedType("WorkspaceStateRequestContext",BindingFlags.NonPublic);
        var context = Activator.CreateInstance(contextType,new object[]{project,code,"admin",requests});
        await (Task)window.GetType().GetMethod("PublishWorkspaceLocalStateAsync",Flags).Invoke(window,new[]{context});
        var snapshot = window.GetType().GetField("workspaceStateSnapshot",Flags).GetValue(window);
        if(snapshot==null) throw new Exception("客户端未返回工作区校验结果。");
        foreach(var item in (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Items").GetValue(snapshot))
        {
            var type=item.GetType(); var path=(string)type.GetProperty("FullPath").GetValue(item);
            var state=(string)type.GetProperty("LocalState").GetValue(item);
            var expected=path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?"IntegrityMismatch":"ReadOnlyCache";
            if(state!=expected) throw new Exception("工作区校验结果不符："+Path.GetFileName(path)+" "+state);
            if(!System.Linq.Enumerable.SequenceEqual(original[path],File.ReadAllBytes(path))) throw new Exception("校验恢复修改了图档内容。");
        }
        Console.WriteLine("PASS actual MainWindow verifies real server versions, repairs stale root/part metadata and retains corrupted drawing warning");
    }
    static void Exit(Window window) => window.GetType().GetMethod("ExitApplication", Flags).Invoke(window, null);
}
