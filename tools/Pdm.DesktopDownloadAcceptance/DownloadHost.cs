using System;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Upton.Pdm.Desktop;

// 仅提供下载处理需要的窗口环境，避免回归测试触发登录、自启动注册和SolidWorks。
public partial class MainWindow : Window
{
    internal const string UiHostName = "appassets.pdm.local";
    private readonly CancellationTokenSource bootstrapLifetime = new();
    private readonly WebView2CompositionControl view = new();

    public MainWindow()
    {
        Width = 1100;
        Height = 700;
        Content = view;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("WorkspaceView", view);
        Closed += (_, _) => { bootstrapLifetime.Cancel(); view.Dispose(); bootstrapLifetime.Dispose(); };
    }

    private void ConfigureLocalUiRequests(CoreWebView2 core, string uiFolder) => core.DownloadStarting += OnDownloadStarting;
    private static void AppendClientDiagnostic(string message) => Console.WriteLine(message);
}
