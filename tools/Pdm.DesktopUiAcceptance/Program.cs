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
                    await Execute(core, "localStorage.setItem('upton-pdm-session'," + Json.Serialize(session) + ")");
                    core.GetType().GetMethod("Reload").Invoke(core, null);
                }
                await Task.Delay(3000);
                var page = await Execute(core, "JSON.stringify({url:location.href,visibleVersion:document.body.innerText.includes('V" + args[0] + "'),scripts:[...document.querySelectorAll('script[src]')].map(node=>node.src)})");
                var result = Json.DeserializeObject(Json.Deserialize<string>(page)) as System.Collections.Generic.Dictionary<string, object>;
                Console.WriteLine("Native main page inspected");
                if (!(bool)result["visibleVersion"]) throw new Exception("原生客户端可见版本号不一致。");
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
        return await task;
    }
    static void Exit(Window window) => window.GetType().GetMethod("ExitApplication", Flags).Invoke(window, null);
}
