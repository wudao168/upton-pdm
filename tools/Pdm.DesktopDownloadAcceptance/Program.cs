using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Upton.Pdm.Desktop;

internal static class Program
{
    private const string Origin = "http://download-qa.local/";
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer Json = new();
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("UPLM 下载路径回归\r\n项目,工时\r\nP700001,0.5\r\n");
    private static readonly Dictionary<string, TaskCompletionSource<CoreWebView2DownloadOperation>> Finished = new();
    private static readonly Dictionary<CoreWebView2, TaskCompletionSource<bool>> Ready = new();
    private static readonly List<string> Passed = new();

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var root = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["WindowBackground"] = new SolidColorBrush(Colors.White);
        Exception? failure = null;
        DownloadWindow? window = null;
        ReviewOverlayWindow? review = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("下载回归超过两分钟。"); app.Shutdown(1); };
        app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; app.Shutdown(1); };
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new DownloadWindow();
                window.Show();
                var view = (WebView2CompositionControl)window.FindName("WorkspaceView");
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
                await view.EnsureCoreWebView2Async(environment);
                var core = view.CoreWebView2;
                Configure(window, core, root);
                core.Navigate(Origin + "index.html");
                await Wait(Ready[core].Task);
                Check(await core.ExecuteScriptAsync("window.isSecureContext") == "false", "回归应覆盖内网HTTP非安全上下文");
                var ui = Path.Combine(root, "ui");
                Directory.CreateDirectory(ui);
                File.WriteAllText(Path.Combine(ui, "review-overlay.html"), "<html><body>下载回归浮层</body></html>");
                review = new ReviewOverlayWindow(window, environment, ui, 1, Origin, "download-qa", x => Configure(window, x, root));
                review.Opacity = 0;
                review.ShowAt(-20000, -20000, 800, 600);
                await review.InitializeAsync();
                var reviewView = (WebView2CompositionControl)review.FindName("ReviewView");
                var reviewCore = reviewView.CoreWebView2;
                await Wait(Ready[reviewCore].Task);
                var token = Guid.NewGuid().ToString("N");
                var folder = Path.Combine(root, "用户选择目录");
                Directory.CreateDirectory(folder);

                var blobName = "中文导出-" + token + ".xlsx";
                var chosen = Path.Combine(folder, "用户改名.xlsx");
                window.Choices[blobName] = chosen;
                var saved = await Download(core, blobName);
                VerifySaved(saved, chosen);
                Check(!File.Exists(window.Suggested[blobName]), "未擅自保存到默认下载位置");
                Check(window.Suggested[blobName].EndsWith(blobName, StringComparison.Ordinal), "中文建议名称和扩展名保留");
                Passed.Add("Blob导出保存到中文目录、可改名、字节完整");

                var serverName = "server-" + token + ".pdf";
                var pdf = Path.Combine(folder, "指定文件.pdf");
                window.Choices[serverName] = pdf;
                var serverDone = ExpectDownload(serverName);
                core.Navigate(Origin + "attachment?name=" + Uri.EscapeDataString(serverName));
                VerifySaved(await Wait(serverDone), pdf);
                Passed.Add("带Content-Disposition的服务器附件使用指定路径");

                var canceledName = "cancel-" + token + ".zip";
                var canceled = await Download(core, canceledName);
                VerifyCanceled(canceled);
                Check(!File.Exists(window.Suggested[canceledName]), "取消后不生成默认下载文件");
                Passed.Add("取消选择后下载中断且不生成文件");

                var firstName = "main-" + token + ".csv";
                var secondName = "review-" + token + ".txt";
                var firstPath = Path.Combine(folder, "主界面.csv");
                var secondPath = Path.Combine(folder, "审核浮层.txt");
                window.Choices[firstName] = firstPath;
                window.Choices[secondName] = secondPath;
                window.NestedDialogName = firstName;
                window.WaitForName = secondName;
                var secondDone = ExpectDownload(secondName);
                window.DuringDialog = () => StartBlob(reviewCore, secondName);
                var firstDone = await Download(core, firstName);
                VerifySaved(firstDone, firstPath);
                VerifySaved(await Wait(secondDone), secondPath);
                Check(window.SecondObservedDuringDialog && window.MaxActiveChoices == 1, "主界面和浮层共用队列，不叠加保存窗口");
                Check(window.Failures.Count == 0, string.Join("；", window.Failures));
                Passed.Add("跨主界面和审核浮层并发下载串行选择");

                var afterCancelName = "again-" + token + ".csv";
                var afterCancelPath = Path.Combine(folder, "后续下载.csv");
                window.Choices[afterCancelName] = afterCancelPath;
                VerifySaved(await Download(core, afterCancelName), afterCancelPath);
                Passed.Add("取消及并发后仍能继续下载");

                var closeName = "shutdown-" + token + ".txt";
                ((CancellationTokenSource)typeof(MainWindow).GetField("bootstrapLifetime", Private)!.GetValue(window)!).Cancel();
                var beforeCloseChoices = window.Suggested.Count;
                VerifyCanceled(await Download(core, closeName));
                Check(window.Suggested.Count == beforeCloseChoices, "退出时不再弹保存窗口");
                Passed.Add("客户端退出期间取消下载，不再请求选址");
                Check(window.ChoicesOutsideDownloadEvent, "保存选择发生在WebView2事件返回后");
                Passed.Add("UI线程异步选址，避免WebView2下载事件重入");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                timeout.Stop();
                review?.Shutdown();
                if (window != null)
                {
                    window.Close();
                }
                app.Shutdown(failure == null ? 0 : 1);
            }
        };
        timeout.Start();
        app.Run();
        var report = new { succeeded = failure == null, passed = Passed, failure = failure?.ToString(), root,
            dialogInteraction = "路径选择使用测试桩；真实系统另存为窗口的按钮交互未自动化，原生UI工具初始化失败。" };
        File.WriteAllText(Path.Combine(root, "result.json"), Json.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine(Json.Serialize(report));
        return failure == null ? 0 : 1;
    }

    private static void Configure(DownloadWindow window, CoreWebView2 core, string root)
    {
        typeof(MainWindow).GetMethod("ConfigureLocalUiRequests", Private)!.Invoke(window, new object[] { core, root });
        Ready[core] = new TaskCompletionSource<bool>();
        core.NavigationCompleted += (_, e) => { if (e.IsSuccess) Ready[core].TrySetResult(true); };
        core.PermissionRequested += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin, StringComparison.Ordinal) && e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads)
                e.State = CoreWebView2PermissionState.Allow;
        };
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = new Uri(e.Request.Uri);
            var attachment = uri.AbsolutePath == "/attachment";
            var name = Uri.UnescapeDataString(uri.Query.Replace("?name=", ""));
            var bytes = attachment ? Payload : Encoding.UTF8.GetBytes("<html><body>下载路径隔离回归</body></html>");
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", attachment
                ? "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"" + name + "\""
                : "Content-Type: text/html; charset=utf-8");
        };
        core.DownloadStarting += (_, e) =>
        {
            var name = Path.GetFileName(e.ResultFilePath);
            window.Observed.Add(name);
            e.Handled = true;
            if (!Finished.TryGetValue(name, out var completion)) return;
            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) =>
            {
                if (operation.State != CoreWebView2DownloadState.InProgress) completion.TrySetResult(operation);
            };
        };
    }

    private static Task<CoreWebView2DownloadOperation> ExpectDownload(string name)
    {
        var completion = new TaskCompletionSource<CoreWebView2DownloadOperation>();
        Finished.Add(name, completion);
        return completion.Task;
    }
    private static async Task<CoreWebView2DownloadOperation> Download(CoreWebView2 core, string name)
    {
        var finished = ExpectDownload(name);
        await StartBlob(core, name);
        return await Wait(finished);
    }
    private static Task<string> StartBlob(CoreWebView2 core, string name) => core.ExecuteScriptAsync(
        "(()=>{const bytes=Uint8Array.from(atob(" + Json.Serialize(Convert.ToBase64String(Payload)) + "),c=>c.charCodeAt(0));" +
        "const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([bytes]));a.download=" + Json.Serialize(name) + ";a.click()})()");
    private static async Task<T> Wait<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(30000)) != task) throw new TimeoutException("原生下载未在30秒内完成。");
        return await task;
    }
    private static void VerifySaved(CoreWebView2DownloadOperation operation, string path)
    {
        Check(operation.State == CoreWebView2DownloadState.Completed, "下载应完成：" + operation.InterruptReason);
        Check(operation.ResultFilePath == path, "下载路径应为用户指定位置");
        Check(File.ReadAllBytes(path).SequenceEqual(Payload), "下载文件内容应完整");
    }
    private static void VerifyCanceled(CoreWebView2DownloadOperation operation) => Check(
        operation.State == CoreWebView2DownloadState.Interrupted && operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled,
        "取消应结束下载，原因应为用户取消");
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}

internal sealed class DownloadWindow : MainWindow
{
    public readonly Dictionary<string, string> Choices = new();
    public readonly Dictionary<string, string> Suggested = new();
    public readonly HashSet<string> Observed = new();
    public readonly List<string> Failures = new();
    public bool ChoicesOutsideDownloadEvent = true;
    public bool SecondObservedDuringDialog;
    public int MaxActiveChoices;
    private int activeChoices;
    public string? NestedDialogName;
    public string? WaitForName;
    public Func<Task>? DuringDialog;

    public DownloadWindow()
    {
        Title = "UPLM 下载路径隔离回归";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = Top = -20000;
        Opacity = 0;
        ShowActivated = false;
        ShowInTaskbar = false;
    }
    protected override string? SelectDownloadPath(string suggestedPath)
    {
        var name = Path.GetFileName(suggestedPath);
        Suggested.Add(name, suggestedPath);
        ChoicesOutsideDownloadEvent &= Observed.Contains(name);
        MaxActiveChoices = Math.Max(MaxActiveChoices, ++activeChoices);
        try
        {
            if (name == NestedDialogName && DuringDialog != null)
            {
                var frame = new DispatcherFrame();
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) =>
                {
                    if (Observed.Contains(WaitForName!) || DateTime.UtcNow - started > TimeSpan.FromSeconds(10))
                    {
                        SecondObservedDuringDialog = Observed.Contains(WaitForName!);
                        timer.Stop(); frame.Continue = false;
                    }
                };
                Dispatcher.BeginInvoke(new Action(async () => { try { await DuringDialog(); } catch (Exception e) { Failures.Add(e.Message); frame.Continue = false; } }));
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            return Choices.TryGetValue(name, out var chosen) ? chosen : null;
        }
        finally { activeChoices--; }
    }
}
