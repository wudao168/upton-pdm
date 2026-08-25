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
    private Task? initializationTask;
    private string? pendingStateJson;
    private bool navigationReady;
    private bool allowClose;

    public ReviewOverlayWindow(Window owner, CoreWebView2Environment environment, string uiFolder, long uiVersion)
    {
        InitializeComponent();
        Owner = owner;
        this.environment = environment;
        this.uiFolder = uiFolder;
        this.uiVersion = uiVersion;
        ReviewView.DefaultBackgroundColor = Color.Transparent;
        Activated += (_, _) => ActivityChanged?.Invoke();
        Deactivated += (_, _) => ActivityChanged?.Invoke();
        Closing += OnClosing;
    }

    public event Action<string>? MessageReceived;
    public event Action? ActivityChanged;

    public Task InitializeAsync() => initializationTask ??= InitializeCoreAsync();

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
            navigationReady = eventArgs.IsSuccess;
            if (navigationReady && pendingStateJson != null)
            {
                ReviewView.CoreWebView2.PostWebMessageAsJson(pendingStateJson);
            }
        };
        ReviewView.Source = new Uri($"https://{MainWindow.UiHostName}/review-overlay.html?v={uiVersion}");
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose) return;
        eventArgs.Cancel = true;
        HideOverlay();
    }
}
