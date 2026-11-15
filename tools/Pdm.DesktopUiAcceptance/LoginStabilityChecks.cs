using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

internal static partial class Program
{
    // Exercise the real desktop navigation method with an isolated WebView profile;
    // no login request, credential save, server setting change or update download.
    static int RunLoginStabilityChecks(string[] args)
    {
        var directory = AppDomain.CurrentDomain.BaseDirectory;
        var profile = Path.Combine(directory, "login-stability-profile-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", profile);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "Upton.Pdm.Desktop.exe"));
        var app = new Application();
        app.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(244, 246, 248));
        var window = (Window)Activator.CreateInstance(assembly.GetType("Upton.Pdm.Desktop.MainWindow", true));
        window.Opacity = 0; window.ShowInTaskbar = false; window.Left = -20000; window.Top = -20000;
        Exception failure = null;
        var done = false; var busy = false; var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; done = true; Exit(window); };
        timer.Tick += async (_, __) => {
            if (busy || done) return;
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(90)) {
                failure = new TimeoutException("登录页未在90秒内就绪。"); done = true; Exit(window); return;
            }
            var view = window.FindName("WorkspaceView");
            var core = view?.GetType().GetProperty("CoreWebView2")?.GetValue(view);
            if (core == null || !(bool)window.GetType().GetField("workspaceNavigationReady", Flags).GetValue(window)) return;
            busy = true;
            try {
                // Stop background package processing in the test instance only.
                ((CancellationTokenSource)window.GetType().GetField("bootstrapLifetime", Flags).GetValue(window)).Cancel();
                if (await Execute(core, "!!document.querySelector('.pdm-login-artwork__image')") != "true") return;
                await Execute(core, "window.__loginStableDocument=true;window.__loginStableImage=document.querySelector('.pdm-login-artwork__image');const field=document.querySelector('input[name=username]');field.value='稳定性验收';field.dispatchEvent(new Event('input',{bubbles:true}));field.focus();field.setSelectionRange(2,4)");
                var configType = assembly.GetType("Upton.Pdm.ClientShared.ClientBootstrapConfiguration", true);
                var configuration = Activator.CreateInstance(configType);
                configType.GetProperty("UiBaseUrl").SetValue(configuration, "https://appassets.pdm.local/");
                var apply = window.GetType().GetMethod("ApplyServerUiConfiguration", Flags);
                var token = window.GetType().GetField("accessToken", Flags);
                token.SetValue(window, "");
                var originalSource = view.GetType().GetProperty("Source").GetValue(view).ToString();
                for (var poll = 0; poll < 8; poll++) {
                    configType.GetProperty("ConfigurationVersion").SetValue(configuration, "login-stability-" + poll);
                    // Cover local fallback and server-backed login, including changed versions.
                    window.GetType().GetField("usingServerUi", Flags).SetValue(window, poll % 2 == 0);
                    if ((bool)apply.Invoke(window, new[] { configuration, (object)true }))
                        throw new Exception("未登录时自动配置检查触发了导航。");
                    await Task.Delay(1000);
                    var stable = await Execute(core, "window.__loginStableDocument===true&&window.__loginStableImage===document.querySelector('.pdm-login-artwork__image')&&document.querySelector('input[name=username]').value==='稳定性验收'&&document.activeElement===document.querySelector('input[name=username]')&&document.activeElement.selectionStart===2&&document.activeElement.selectionEnd===4");
                    if (stable != "true" || view.GetType().GetProperty("Source").GetValue(view).ToString() != originalSource)
                        throw new Exception("登录页文档、画面、输入或光标在配置轮询后发生变化。");
                }
                Console.WriteLine("PASS 8 native automatic checks preserve login document, image, input, focus and selection");
                token.SetValue(window, "fixture-session");
                if (!(bool)apply.Invoke(window, new[] { configuration, (object)true }))
                    throw new Exception("已登录时正常界面版本更新被意外阻止。");
                Console.WriteLine("PASS authenticated automatic update remains available");
                token.SetValue(window, "");
                configType.GetProperty("ConfigurationVersion").SetValue(configuration, "manual-server-selection");
                if (!(bool)apply.Invoke(window, new[] { configuration, (object)false }))
                    throw new Exception("未登录时手动切换服务器被意外阻止。");
                Console.WriteLine("PASS explicit server change remains available while signed out");
                done = true; Exit(window);
            } catch (Exception error) { failure = error; done = true; Exit(window); }
            finally { busy = false; }
        };
        timer.Start(); app.Run(window); timer.Stop();
        if (failure == null && done) return 0;
        Console.WriteLine("FAIL login stability " + failure); return 1;
    }
}