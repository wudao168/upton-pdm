using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Upton.Pdm.Desktop;

public partial class MainWindow : Window
{
    private const string UiHost = "appassets.pdm.local";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWorkspaceAsync();
        }
        catch (Exception exception)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show(
                this,
                $"PDM 客户端启动失败。\n\n{exception.Message}",
                "UPTON PDM",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task InitializeWorkspaceAsync()
    {
        var uiFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
        var indexFile = Path.Combine(uiFolder, "index.html");
        if (!File.Exists(indexFile))
        {
            throw new FileNotFoundException("未找到客户端页面，请先执行 pnpm --dir src/pdm-ui build。", indexFile);
        }

        await WorkspaceView.EnsureCoreWebView2Async();
        WorkspaceView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            UiHost,
            uiFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        WorkspaceView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WorkspaceView.NavigationCompleted += (_, args) =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (!args.IsSuccess)
            {
                MessageBox.Show(this, $"页面加载失败：{args.WebErrorStatus}", "UPTON PDM");
            }
        };
        WorkspaceView.Source = new Uri($"https://{UiHost}/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var serializer = new JavaScriptSerializer();
        var message = serializer.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
        if (message == null || !message.TryGetValue("type", out var typeValue))
        {
            return;
        }

        var type = typeValue as string;
        if (type == "open-document" &&
            message.TryGetValue("payload", out var payloadValue) &&
            payloadValue is Dictionary<string, object> payload)
        {
            TryOpenLocalDocument(payload);
        }
    }

    private void TryOpenLocalDocument(IReadOnlyDictionary<string, object> payload)
    {
        if (!payload.TryGetValue("localPath", out var pathValue))
        {
            MessageBox.Show(this, "文档尚未下载到本地工作区。", "UPTON PDM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = pathValue as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "本地文档不存在，请先获取权限或下载。", "UPTON PDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
