using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Upton.Pdm.Desktop;

public partial class ReviewOverlayWindow : Window
{
    private readonly CoreWebView2Environment environment;
    private readonly string uiFolder;
    private readonly long uiVersion;
    private string? serverUiBaseUrl;
    private string configurationVersion = string.Empty;
    private bool useLocalFallback;
    private Task? initializationTask;
    private string? pendingStateJson;
    private bool navigationReady;
    private bool allowClose;

    public ReviewOverlayWindow(Window owner, CoreWebView2Environment environment, string uiFolder, long uiVersion,
        string? serverUiBaseUrl = null, string configurationVersion = "")
    {
        InitializeComponent();
        Owner = owner;
        this.environment = environment;
        this.uiFolder = uiFolder;
        this.uiVersion = uiVersion;
        this.serverUiBaseUrl = serverUiBaseUrl;
        this.configurationVersion = configurationVersion;
        ReviewView.DefaultBackgroundColor = Color.Transparent;
        Activated += (_, _) => ActivityChanged?.Invoke();
        Deactivated += (_, _) => ActivityChanged?.Invoke();
        Closing += OnClosing;
    }

    public event Action<string>? MessageReceived;
    public event Action? ActivityChanged;

    public Task InitializeAsync() => initializationTask ??= InitializeCoreAsync();

    /// <summary>服务器发布新版本时同步刷新审核浮层，避免客户端沿用启动时的旧界面。</summary>
    public void UseConfiguration(string? uiBaseUrl, string version)
    {
        if (string.Equals(serverUiBaseUrl, uiBaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(configurationVersion, version, StringComparison.Ordinal))
        {
            return;
        }
        serverUiBaseUrl = uiBaseUrl;
        configurationVersion = version ?? string.Empty;
        useLocalFallback = false;
        if (initializationTask is null) return;
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await InitializeAsync();
        navigationReady = false;
        ReviewView.Source = ReviewOverlaySource.Build("review-overlay.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
    }

    public async Task PublishStateAsync(string stateJson)
    {
        pendingStateJson = stateJson;
        await InitializeAsync();
        if (!navigationReady || ReviewView.CoreWebView2 == null) return;
        ReviewView.CoreWebView2.PostWebMessageAsJson(stateJson);
    }

    public void ShowAt(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        ReviewView.Visibility = Visibility.Visible;
        ReviewView.IsHitTestVisible = true;
        if (!IsVisible) Show();
        _ = InitializeAsync();
    }

    public void HideOverlay()
    {
        ReviewView.IsHitTestVisible = false;
        ReviewView.Visibility = Visibility.Collapsed;
        if (IsVisible) Hide();
    }

    public void Shutdown()
    {
        allowClose = true;
        Close();
    }

    private async Task InitializeCoreAsync()
    {
        var overlayFile = Path.Combine(uiFolder, "review-overlay.html");
        if (!File.Exists(overlayFile))
        {
            throw new FileNotFoundException("未找到图纸审核浮层页面，请重新构建客户端页面。", overlayFile);
        }

        await ReviewView.EnsureCoreWebView2Async(environment);
        ReviewView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            MainWindow.UiHostName,
            uiFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        ReviewView.CoreWebView2.WebMessageReceived += (_, eventArgs) => MessageReceived?.Invoke(eventArgs.WebMessageAsJson);
        ReviewView.NavigationCompleted += (_, eventArgs) =>
        {
            if (!eventArgs.IsSuccess && !useLocalFallback)
            {
                useLocalFallback = true;
                ReviewView.Source = ReviewOverlaySource.Build("review-overlay.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
                return;
            }
            navigationReady = eventArgs.IsSuccess;
            if (navigationReady && pendingStateJson != null)
            {
                ReviewView.CoreWebView2.PostWebMessageAsJson(pendingStateJson);
            }
        };
        ReviewView.Source = ReviewOverlaySource.Build("review-overlay.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose) return;
        eventArgs.Cancel = true;
        HideOverlay();
    }
}
