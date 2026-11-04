using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

internal static partial class Program
{
    // --original-preview <desktop.exe> <raw.sldasm> <result.json>
    private static int RunOriginalPreviewChecks(string[] args)
    {
        var desktop = Path.GetFullPath(args[1]);
        var source = Path.GetFullPath(args[2]);
        var resultPath = Path.GetFullPath(args[3]);
        var live = args[0].StartsWith("--original-preview-live", StringComparison.Ordinal);
        var coldLive = args[0] == "--original-preview-live-cold";
        var performanceOnly = args[0] == "--original-preview-performance";
        var interrupt = args[0] == "--original-preview-interrupt";
        var previewFileName = coldLive ? "qa-preview-" + Guid.NewGuid().ToString("N") + ".SLDASM" : Path.GetFileName(source);
        string isolatedLiveFile = null;
        var liveApiBase = args.Length > 6 ? args[6].TrimEnd('/') : "https://192.168.2.8:5443";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = Path.Combine(Path.GetDirectoryName(desktop), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var assembly = Assembly.LoadFrom(desktop);
        var application = new Application();
        application.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(244, 246, 248));
        var window = (Window)Activator.CreateInstance(assembly.GetType("Upton.Pdm.Desktop.MainWindow", true));
        window.WindowState = WindowState.Normal; window.Width = 1500; window.Height = 950;
        window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; window.ShowActivated = false;
        var originalClient = window.GetType().GetField("apiClient", Flags).GetValue(window) as HttpClient;
        var nativeHandler = live ? (HttpMessageHandler)typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(field => typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType)).GetValue(originalClient) : null;
        var handler = new OriginalPreviewHandler(File.ReadAllBytes(source), live, nativeHandler);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var documentId = live ? Guid.Parse(args[5]) : Guid.NewGuid();
        var cacheRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON", "PDM", "Preview"));
        var testCache = Path.GetFullPath(Path.Combine(cacheRoot, documentId.ToString("N")));
        var results = new Dictionary<string, object>();
        var pulse = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var pulseClock = Stopwatch.StartNew();
        long previousPulse = 0, maximumUiGap = 0;
        pulse.Tick += (_, __) => { var now = pulseClock.ElapsedMilliseconds; maximumUiGap = Math.Max(maximumUiGap, now - previousPulse); previousPulse = now; };
        Exception failure = null;
        var started = false;
        window.ContentRendered += async (_, __) => {
            if (started) return;
            started = true;
            try
            {
                var wait = Stopwatch.StartNew();
                while (!(bool)window.GetType().GetField("workspaceNavigationReady", Flags).GetValue(window))
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("客户端测试界面未就绪。");
                    await Task.Delay(100);
                }
                await Task.Delay(1000);
                // This harness drives the native request chain directly. Keep the web app from
                // sending unrelated page-visibility messages while its native host is under test.
                var webView = window.FindName("WorkspaceView");
                var core = webView.GetType().GetProperty("CoreWebView2").GetValue(webView);
                core.GetType().GetMethod("NavigateToString").Invoke(core, new object[] { "<html><body>原图预览验收</body></html>" });
                await Task.Delay(500);
                window.GetType().GetField("apiClient", Flags).SetValue(window, client);
                window.GetType().GetField("accessToken", Flags).SetValue(window, "isolated-preview-test");
                string liveToken = null;
                if (live)
                {
                    var secrets = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[4]));
                    var login = await client.PostAsync(liveApiBase + "/api/auth/login", new StringContent(new JavaScriptSerializer().Serialize(new { username = secrets["bootstrapAdminUsername"], password = secrets["bootstrapAdminPassword"] }), System.Text.Encoding.UTF8, "application/json"));
                    login.EnsureSuccessStatusCode();
                    var session = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await login.Content.ReadAsStringAsync());
                    liveToken = (string)session["accessToken"];
                    window.GetType().GetField("accessToken", Flags).SetValue(window, liveToken);
                    if (session.TryGetValue("activeCompanyId", out var company) && company != null)
                        window.GetType().GetField("activeCompanyId", Flags).SetValue(window, company.ToString());
                    var bootstrap = window.GetType().GetField("bootstrapConfiguration", Flags).GetValue(window);
                    bootstrap.GetType().GetProperty("ApiBaseUrl").SetValue(bootstrap, liveApiBase);
                }
                var boundsType = window.GetType().GetNestedType("PreviewHostBounds", BindingFlags.NonPublic);
                var bounds = Activator.CreateInstance(boundsType, new object[] { 400d, 160d, 900d, 650d, 1600d, 900d, true });
                window.GetType().GetField("previewBounds", Flags).SetValue(window, bounds);
                window.GetType().GetField("previewHostSuspended", Flags).SetValue(window, false);
                Func<Guid, Guid?, Task> open = (id, frozen) => {
                    var payload = new Dictionary<string, object> { ["documentId"] = id.ToString(), ["fileName"] = previewFileName };
                    if (frozen.HasValue) payload["versionId"] = frozen.Value.ToString();
                    return (Task)window.GetType().GetMethod("PreviewDocumentAsync", Flags).Invoke(window, new object[] { payload });
                };
                Action assertReady = () => {
                    if (!(bool)window.GetType().GetField("previewDocumentReady", Flags).GetValue(window)) throw new Exception("原图未完成原生加载。");
                    var preview = window.GetType().GetField("embeddedPreview", Flags).GetValue(window);
                    var pathProperty = preview.GetType().GetProperty("CurrentDocumentPath", Flags);
                    var path = pathProperty != null ? (string)pathProperty.GetValue(preview)
                        : (string)preview.GetType().GetField("currentDocumentPath", Flags).GetValue(preview);
                    if (!path.EndsWith(Path.GetExtension(source), StringComparison.OrdinalIgnoreCase)) throw new Exception("预览未直接使用原始图档格式。");
                };
                previousPulse = pulseClock.ElapsedMilliseconds; pulse.Start();
                if (interrupt) {
                    var pending = open(documentId, null);
                    while (!(bool)window.GetType().GetField("previewNativeOpening", Flags).GetValue(window)) await Task.Delay(20);
                    await Task.Delay(1500);
                    var closing = Stopwatch.StartNew();
                    window.GetType().GetMethod("HideEmbeddedPreview", Flags).Invoke(window, new object[] { true });
                    results["hideDuringLoadMs"] = closing.ElapsedMilliseconds;
                    if (closing.ElapsedMilliseconds > 2000) throw new Exception("加载期间关闭预览阻塞主界面超过2秒。");
                    await pending;
                    if ((bool)window.GetType().GetField("previewDocumentReady", Flags).GetValue(window)) throw new Exception("关闭后的迟到事件恢复了预览。");
                }
                var timer = Stopwatch.StartNew(); await open(documentId, null); assertReady(); results["coldMs"] = timer.ElapsedMilliseconds;
                if (coldLive) {
                    var active = window.GetType().GetField("embeddedPreview", Flags).GetValue(window);
                    isolatedLiveFile = active.GetType().GetProperty("CurrentDocumentPath", Flags) != null
                        ? (string)active.GetType().GetProperty("CurrentDocumentPath", Flags).GetValue(active)
                        : (string)active.GetType().GetField("currentDocumentPath", Flags).GetValue(active);
                }
                await Task.Delay(150); pulse.Stop(); results["maximumUiGapMs"] = maximumUiGap;
                if (!live && window.GetType().GetField("embeddedPreview", Flags).GetValue(window).GetType().GetProperty("CurrentDocumentPath", Flags) != null && maximumUiGap > 2000)
                    throw new Exception("专用预览线程加载期间主界面响应超过2秒。");
                var firstReads = handler.FileReads;
                var expectedReads = 1;
                if (!live && firstReads != expectedReads) throw new Exception("首次预览下载次数异常。");
                timer.Restart(); await open(documentId, null); assertReady(); results["warmMs"] = timer.ElapsedMilliseconds;
                if (handler.FileReads != firstReads) throw new Exception("有效缓存被重复下载。");
                results["initialCacheHit"] = firstReads == 0;
                if (!performanceOnly && window.GetType().GetField("embeddedPreview", Flags).GetValue(window).GetType().GetProperty("CurrentDocumentPath", Flags) != null)
                    await ProbeThreadedPreviewAsync(window, results, Path.ChangeExtension(resultPath, ".bmp"));
                if (!live && !performanceOnly && !interrupt)
                {
                window.GetType().GetMethod("HideEmbeddedPreview", Flags).Invoke(window, new object[] { true });
                var cache = Path.Combine(testCache, handler.VersionId.ToString("N"), Path.GetFileName(source));
                if ((File.GetAttributes(cache) & FileAttributes.ReadOnly) == 0) throw new Exception("预览缓存不是只读。");
                File.SetAttributes(cache, FileAttributes.Normal);
                var bytes = File.ReadAllBytes(cache); bytes[0] ^= 1; File.WriteAllBytes(cache, bytes);
                await open(documentId, null); assertReady();
                if (handler.FileReads != 2 * expectedReads) throw new Exception("损坏缓存没有重新下载校验。");
                results["corruptCacheRepaired"] = true;
                await open(documentId, handler.VersionId); assertReady();
                if (!handler.LastMetadataQuery.Contains(handler.VersionId.ToString())) throw new Exception("历史版本选择未保留。");
                results["frozenVersionPreserved"] = true;
                var obsolete = open(handler.SlowDocumentId, null);
                if (await Task.WhenAny(handler.SlowStarted.Task, Task.Delay(5000)) != handler.SlowStarted.Task) throw new TimeoutException("取消测试未开始。");
                timer.Restart(); var latest = open(documentId, null);
                await Task.WhenAll(obsolete, latest); assertReady(); results["switchMs"] = timer.ElapsedMilliseconds;
                if (handler.Cancelled != 1 || (Guid?)window.GetType().GetField("previewDocumentId", Flags).GetValue(window) != documentId)
                    throw new Exception("旧请求未取消或覆盖了新选择。");
                results["obsoleteRequestCancelled"] = true;
                }
                results["fileReads"] = handler.FileReads;
                results["assemblyConversionRequests"] = handler.AssemblyRequests;
                if (!live) {
                    results["markupStartedBeforeFile"] = handler.MarkupStartedBeforeFile;
                    if (!performanceOnly && !handler.MarkupStartedBeforeFile) throw new Exception("首次预览的批注仍排在文件下载之后。");
                }
                if (handler.AssemblyRequests != 0) throw new Exception("原图预览仍调用装配转图服务。");
                if (liveToken != null)
                {
                    using (var logout = new HttpRequestMessage(HttpMethod.Post, liveApiBase + "/api/auth/logout"))
                    {
                        logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", liveToken);
                        await client.SendAsync(logout);
                    }
                    window.GetType().GetField("accessToken", Flags).SetValue(window, string.Empty);
                }
                results["status"] = "passed";
            }
            catch (Exception error) { failure = error.GetBaseException(); results["status"] = "failed"; results["message"] = failure.Message; }
            finally
            {
                File.WriteAllText(resultPath, new JavaScriptSerializer().Serialize(results));
                window.GetType().GetField("allowClose", Flags).SetValue(window, true);
                window.Close(); application.Shutdown(failure == null ? 0 : 1);
            }
        };
        window.Show(); application.Run();
        originalClient?.Dispose(); client.Dispose();
        if (!testCache.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("测试缓存目录校验失败。");
        if (!live && Directory.Exists(testCache)) { foreach (var file in Directory.GetFiles(testCache, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(testCache, true); }
        if (coldLive && isolatedLiveFile != null && Path.GetFullPath(isolatedLiveFile).StartsWith(testCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(isolatedLiveFile) == previewFileName && File.Exists(isolatedLiveFile)) {
            File.SetAttributes(isolatedLiveFile, FileAttributes.Normal); File.Delete(isolatedLiveFile);
        }
        Console.WriteLine(File.ReadAllText(resultPath));
        return failure == null ? 0 : 1;
    }

    private sealed class OriginalPreviewHandler : DelegatingHandler
    {
        private readonly byte[] bytes;
        private readonly string sha;
        private readonly bool live;
        public Guid VersionId = Guid.NewGuid(), SlowDocumentId = Guid.NewGuid();
        public int FileReads, AssemblyRequests, Cancelled;
        private bool markupStarted;
        public bool MarkupStartedBeforeFile;
        public string LastMetadataQuery = "";
        public TaskCompletionSource<bool> SlowStarted = new TaskCompletionSource<bool>();
        public OriginalPreviewHandler(byte[] content, bool useServer, HttpMessageHandler nativeHandler = null) : base(nativeHandler ?? new HttpClientHandler { UseProxy = false }) { live = useServer; bytes = content; using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/assembly-preview")) { AssemblyRequests++; return new HttpResponseMessage(HttpStatusCode.BadRequest); }
            if (live)
            {
                if (path.EndsWith("/file")) FileReads++;
                return await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            if (path.EndsWith("/preview-source-version"))
            {
                LastMetadataQuery = request.RequestUri.Query;
                if (path.Contains(SlowDocumentId.ToString()))
                {
                    SlowStarted.TrySetResult(true);
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { Cancelled++; throw; }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(new { id = VersionId, fileLength = bytes.LongLength, sha256 = sha })) };
            }
            if (path.EndsWith("/markup")) markupStarted = true;
            if (path.EndsWith("/file")) {
                if (FileReads == 0) MarkupStartedBeforeFile = markupStarted;
                FileReads++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
