using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Upton.Pdm.Desktop;

public partial class ReviewAnnotationOverlayWindow : Window
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

    public ReviewAnnotationOverlayWindow(Window owner, CoreWebView2Environment environment, string uiFolder, long uiVersion,
        string? serverUiBaseUrl = null, string configurationVersion = "")
    {
        InitializeComponent();
        Owner = owner;
        this.environment = environment;
        this.uiFolder = uiFolder;
        this.uiVersion = uiVersion;
        this.serverUiBaseUrl = serverUiBaseUrl;
        this.configurationVersion = configurationVersion;
        AnnotationView.DefaultBackgroundColor = Color.Transparent;
        Activated += (_, _) => ActivityChanged?.Invoke();
        Deactivated += (_, _) => ActivityChanged?.Invoke();
        Closing += OnClosing;
    }

    public event Action<string>? MessageReceived;
    public event Action? ActivityChanged;

    public Task InitializeAsync() => initializationTask ??= InitializeCoreAsync();

    /// <summary>服务器发布新版本时同步刷新审核批注浮层，保证与网页端一致。</summary>
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
        AnnotationView.Source = ReviewOverlaySource.Build("review-annotation.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
    }

    public async Task PublishStateAsync(string stateJson)
    {
        pendingStateJson = stateJson;
        await InitializeAsync();
        if (!navigationReady || AnnotationView.CoreWebView2 == null) return;
        AnnotationView.CoreWebView2.PostWebMessageAsJson(stateJson);
    }

    public void ShowAt(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        AnnotationView.Visibility = Visibility.Visible;
        AnnotationView.IsHitTestVisible = true;
        if (!IsVisible) Show();
        _ = InitializeAsync();
    }

    public void HideOverlay()
    {
        AnnotationView.IsHitTestVisible = false;
        AnnotationView.Visibility = Visibility.Collapsed;
        if (IsVisible) Hide();
    }

    public void Shutdown()
    {
        allowClose = true;
        Close();
    }

    private async Task InitializeCoreAsync()
    {
        var annotationFile = Path.Combine(uiFolder, "review-annotation.html");
        if (!File.Exists(annotationFile))
        {
            throw new FileNotFoundException("未找到审核批注浮层页面，请重新构建客户端页面。", annotationFile);
        }

        await AnnotationView.EnsureCoreWebView2Async(environment);
        AnnotationView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            MainWindow.UiHostName,
            uiFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        AnnotationView.CoreWebView2.WebMessageReceived += (_, eventArgs) => MessageReceived?.Invoke(eventArgs.WebMessageAsJson);
        AnnotationView.NavigationCompleted += (_, eventArgs) =>
        {
            if (!eventArgs.IsSuccess && !useLocalFallback)
            {
                useLocalFallback = true;
                AnnotationView.Source = ReviewOverlaySource.Build("review-annotation.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
                return;
            }
            navigationReady = eventArgs.IsSuccess;
            if (navigationReady && pendingStateJson != null)
            {
                AnnotationView.CoreWebView2.PostWebMessageAsJson(pendingStateJson);
            }
        };
        AnnotationView.Source = ReviewOverlaySource.Build("review-annotation.html", serverUiBaseUrl, configurationVersion, uiVersion, useLocalFallback);
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose) return;
        eventArgs.Cancel = true;
        HideOverlay();
    }
}
