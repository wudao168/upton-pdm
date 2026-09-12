using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfPoint = System.Windows.Point;
using Microsoft.Web.WebView2.Core;
using Upton.Pdm.ClientShared;
using Upton.Pdm.LocalSettings;
using Upton.Pdm.SolidWorks;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace Upton.Pdm.Desktop;

public partial class MainWindow : Window
{
    internal const string UiHostName = "appassets.pdm.local";
    private const int WindowMessageSystemCommand = 0x0112;
    private const int MenuExit = 0x1FE0;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private readonly string[] startupArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    private readonly bool startedWithWindows = Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
    private readonly HttpClient apiClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly CancellationTokenSource bootstrapLifetime = new();
    private readonly SolidWorksOpenBridge solidWorksBridge = new();
    private string accessToken = string.Empty;
    private string activeCompanyId = string.Empty;
    private string currentTheme = "a";
    private EDrawingsPreviewControl? embeddedPreview;
    private IReadOnlyList<KeyValuePair<string, string>> previewProperties = Array.Empty<KeyValuePair<string, string>>();
    private PreviewHostBounds? previewBounds;
    private PreviewHostBounds? reviewOverlayBounds;
    private ReviewOverlayWindow? reviewOverlay;
    private bool reviewOverlayVisible;
    private bool reviewOverlaySuspended;
    private bool previewDocumentReady;
    private int previewRequestGeneration;
    private Guid? previewDocumentId;
    private Guid? previewVersionId;
    private string previewMarkupDirectory = string.Empty;
    private int previewMarkupSaveActive;
    private HwndSource? windowSource;
    private IntPtr systemMenu;
    private bool startWithWindows;
    private bool allowClose;
    private bool interactiveSurfacesSuspended;
    private WinForms.NotifyIcon? trayIcon;
    private string[]? pendingExternalRequestArgs;
    private bool workspaceNavigationReady;
    private bool hideAfterStartupNavigation;
    private ClientBootstrapConfiguration bootstrapConfiguration = new();
    private bool usingServerUi;
    private bool attemptedLocalUiFallback;
    private WorkspaceStateRequestContext? workspaceStateRequest;
    private WorkspaceLocalStateSnapshot? workspaceStateSnapshot;
    private FileSystemWatcher? workspaceWatcher;
    private System.Threading.Timer? workspaceRefreshTimer;
    private int workspaceStateGeneration;

    public MainWindow()
    {
        InitializeComponent();
        LoadingPanel.Loaded += (_, _) => InitializeLoadingAnimation();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ApplyPreviewSurfaces();
        LocationChanged += (_, _) => ApplyPreviewSurfaces();
        StateChanged += OnWindowStateChanged;
        Activated += OnWindowActivated;
        // eDrawings opens native markup editors that temporarily deactivate this WPF window.
        // Keep the embedded ActiveX preview mounted while those editors are active.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyReviewOverlayBounds));
        Closing += OnClosing;
        Closed += OnClosed;
        System.Windows.Application.Current.SessionEnding += OnSessionEnding;

        // A collapsed WebView2 cannot reliably initialize its composition surface.
        // Keep it active while minimized, then move the ready client to the tray.
        hideAfterStartupNavigation = startedWithWindows;
        if (hideAfterStartupNavigation)
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
    }

    private void InitializeLoadingAnimation()
    {
        var red = MediaColor.FromRgb(0xFF, 0x3D, 0x00);
        var orange = MediaColor.FromRgb(0xFF, 0x98, 0x00);
        var yellow = MediaColor.FromRgb(0xFF, 0xD6, 0x00);
        var green = MediaColor.FromRgb(0x49, 0xB6, 0x53);
        var blue = MediaColor.FromRgb(0x24, 0x95, 0xE8);
        var white = MediaColor.FromRgb(0xF8, 0xFA, 0xFC);
        var cube = new Model3DGroup();
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(-.5, .5, .5), new Point3D(.5, .5, .5), new Point3D(.5, -.5, .5), new Point3D(-.5, -.5, .5) },
            "L", true, new[] { orange, green, blue, blue, white, red, yellow, orange, green }));
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(.5, .5, -.5), new Point3D(-.5, .5, -.5), new Point3D(-.5, -.5, -.5), new Point3D(.5, -.5, -.5) },
            "阿", false, new[] { blue, yellow, red, white, green, orange, red, blue, yellow }));
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(.5, .5, .5), new Point3D(.5, .5, -.5), new Point3D(.5, -.5, -.5), new Point3D(.5, -.5, .5) },
            "M", true, new[] { yellow, red, blue, red, white, green, blue, green, yellow }));
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(-.5, .5, -.5), new Point3D(-.5, .5, .5), new Point3D(-.5, -.5, .5), new Point3D(-.5, -.5, -.5) },
            "顿", false, new[] { orange, blue, white, green, red, yellow, blue, white, green }));
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(-.5, .5, -.5), new Point3D(.5, .5, -.5), new Point3D(.5, .5, .5), new Point3D(-.5, .5, .5) },
            "P", true, new[] { red, green, blue, green, white, yellow, blue, yellow, red }));
        cube.Children.Add(CreateLoadingCubeFace(
            new[] { new Point3D(-.5, -.5, .5), new Point3D(.5, -.5, .5), new Point3D(.5, -.5, -.5), new Point3D(-.5, -.5, -.5) },
            "普", false, new[] { white, green, orange, red, blue, yellow, green, orange, red }));
        LoadingCubeVisual.Content = cube;

        var alignCornerY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -45);
        var alignCornerX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -54.736);
        var axialRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var transforms = new Transform3DGroup();
        transforms.Children.Add(new RotateTransform3D(alignCornerY));
        transforms.Children.Add(new RotateTransform3D(alignCornerX));
        transforms.Children.Add(new RotateTransform3D(axialRotation));
        LoadingCubeVisual.Transform = transforms;
        BeginContinuousRotation(axialRotation, 0, 360, 1.5);
    }

    private static GeometryModel3D CreateLoadingCubeFace(Point3D[] positions, string label, bool latin, MediaColor[] colors)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions),
            TextureCoordinates = new PointCollection
            {
                new WpfPoint(0, 0), new WpfPoint(1, 0), new WpfPoint(1, 1), new WpfPoint(0, 1)
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        var material = new EmissiveMaterial(CreateLoadingFaceBrush(label, latin, colors));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static MediaBrush CreateLoadingFaceBrush(string label, bool latin, IReadOnlyList<MediaColor> colors)
    {
        const double faceSize = 90;
        const double tileSize = faceSize / 3;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(MediaColor.FromRgb(0x07, 0x19, 0x36)), null, new Rect(0, 0, faceSize, faceSize));
            for (var index = 0; index < colors.Count; index++)
            {
                var left = index % 3 * tileSize + .6;
                var top = index / 3 * tileSize + .6;
                context.DrawRectangle(new SolidColorBrush(colors[index]), null, new Rect(left, top, tileSize - 1.2, tileSize - 1.2));
            }

            var typeface = new Typeface(new MediaFontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);
            var fontSize = latin ? 58 : 48;
            var shadow = new FormattedText(label, CultureInfo.GetCultureInfo("zh-CN"), WpfFlowDirection.LeftToRight,
                typeface, fontSize, new SolidColorBrush(MediaColor.FromArgb(230, 0x05, 0x17, 0x36)), 1);
            var foreground = new FormattedText(label, CultureInfo.GetCultureInfo("zh-CN"), WpfFlowDirection.LeftToRight,
                typeface, fontSize, WpfBrushes.White, 1);
            var origin = new WpfPoint((faceSize - foreground.Width) / 2, (faceSize - foreground.Height) / 2);
            foreach (var offset in new[] { new Vector(-1.4, 0), new Vector(1.4, 0), new Vector(0, -1.4), new Vector(0, 1.4) })
            {
                context.DrawText(shadow, origin + offset);
            }
            context.DrawText(foreground, origin);
        }

        return new VisualBrush(visual) { Stretch = Stretch.Fill };
    }

    private static void BeginContinuousRotation(AxisAngleRotation3D rotation, double from, double to, double seconds)
    {
        rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            bootstrapConfiguration = await ClientBootstrapLoader.LoadAsync(bootstrapLifetime.Token);
            apiClient.BaseAddress = new Uri(bootstrapConfiguration.ApiBaseUrl, UriKind.Absolute);
            await InitializeWorkspaceAsync();
            _ = MonitorBootstrapAsync();
        }
        catch (Exception exception)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (hideAfterStartupNavigation)
            {
                RestoreFromNotificationArea();
            }
            WpfMessageBox.Show(
                this,
                $"PLM 客户端启动失败。\n\n{exception.Message}",
                "UPLM",
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
            UiHostName,
            uiFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        WorkspaceView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WorkspaceView.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess)
            {
                if (usingServerUi && !attemptedLocalUiFallback)
                {
                    attemptedLocalUiFallback = true;
                    usingServerUi = false;
                    var fallbackVersion = File.GetLastWriteTimeUtc(indexFile).Ticks;
                    WorkspaceView.Source = new Uri($"https://{UiHostName}/index.html?v={fallbackVersion}");
                    return;
                }

                LoadingPanel.Visibility = Visibility.Collapsed;
                hideAfterStartupNavigation = false;
                RestoreFromNotificationArea();
                WpfMessageBox.Show(this, $"页面加载失败：{args.WebErrorStatus}", "UPLM");
            }
            else
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                workspaceNavigationReady = true;
                if (hideAfterStartupNavigation)
                {
                    hideAfterStartupNavigation = false;
                    HideToNotificationArea();
                }
                var requestArgs = pendingExternalRequestArgs ?? startupArgs;
                pendingExternalRequestArgs = null;
                await DispatchLaunchRequestAsync(requestArgs);
            }
            _ = PublishSolidWorksCapabilityAsync();
            _ = PublishClientVersionAsync();
        };
        var uiVersion = File.GetLastWriteTimeUtc(indexFile).Ticks;
        reviewOverlay = new ReviewOverlayWindow(this, WorkspaceView.CoreWebView2.Environment, uiFolder, uiVersion);
        reviewOverlay.MessageReceived += OnReviewOverlayMessageReceived;
        reviewOverlay.ActivityChanged += ApplyPreviewSurfaces;
        usingServerUi = Uri.TryCreate(bootstrapConfiguration.UiBaseUrl, UriKind.Absolute, out var serverUiUrl)
            && (serverUiUrl.Scheme == Uri.UriSchemeHttp || serverUiUrl.Scheme == Uri.UriSchemeHttps);
        WorkspaceView.Source = usingServerUi
            ? new Uri(serverUiUrl!, $"?configuration={Uri.EscapeDataString(bootstrapConfiguration.ConfigurationVersion)}")
            : new Uri($"https://{UiHostName}/index.html?v={uiVersion}");
    }

    private async Task MonitorBootstrapAsync()
    {
        var observedConfigurationVersion = bootstrapConfiguration.ConfigurationVersion;
        var observedUiBaseUrl = bootstrapConfiguration.UiBaseUrl;
        while (!bootstrapLifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(bootstrapConfiguration.PollSeconds), bootstrapLifetime.Token);
                var latest = await ClientBootstrapLoader.LoadAsync(bootstrapLifetime.Token);
                await ClientPackageUpdater.StageAsync(
                    "desktop",
                    latest.Desktop,
                    AppDomain.CurrentDomain.BaseDirectory,
                    bootstrapLifetime.Token);

                if (!string.Equals(observedConfigurationVersion, latest.ConfigurationVersion, StringComparison.Ordinal)
                    || !string.Equals(observedUiBaseUrl, latest.UiBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    observedConfigurationVersion = latest.ConfigurationVersion;
                    observedUiBaseUrl = latest.UiBaseUrl;
                    bootstrapConfiguration = latest;
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (WorkspaceView.CoreWebView2 == null || !Uri.TryCreate(latest.UiBaseUrl, UriKind.Absolute, out var uiUrl)) return;
                        usingServerUi = true;
                        attemptedLocalUiFallback = false;
                        WorkspaceView.Source = new Uri(uiUrl, $"?configuration={Uri.EscapeDataString(latest.ConfigurationVersion)}");
                    }));
                }
                else
                {
                    bootstrapConfiguration = latest;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Keep the current cached configuration and retry on the next interval.
            }
        }
    }

    private static string Serialize(object value) => new JavaScriptSerializer().Serialize(value);

    private async Task DispatchLaunchRequestAsync(IReadOnlyList<string> arguments)
    {
        if (WorkspaceView.CoreWebView2 == null || arguments == null || arguments.Count == 0) return;

        if (arguments.Count >= 4 && string.Equals(arguments[0], "--compare", StringComparison.OrdinalIgnoreCase))
        {
            var compareScript = $"window.dispatchEvent(new CustomEvent('pdm-open-version-compare', {{ detail: {{ documentId: {Serialize(arguments[1])}, leftVersionId: {Serialize(arguments[2])}, rightVersionId: {Serialize(arguments[3])} }} }}));";
            await WorkspaceView.CoreWebView2.ExecuteScriptAsync(compareScript);
            return;
        }

        var projectArgumentIndex = FindArgument(arguments, "--project");
        if (projectArgumentIndex < 0
            || projectArgumentIndex + 1 >= arguments.Count
            || !Guid.TryParse(arguments[projectArgumentIndex + 1], out var projectId))
        {
            return;
        }

        var tab = "documents";
        var tabArgumentIndex = FindArgument(arguments, "--tab");
        if (tabArgumentIndex >= 0 && tabArgumentIndex + 1 < arguments.Count)
        {
            tab = arguments[tabArgumentIndex + 1];
        }

        var projectScript = $"window.dispatchEvent(new CustomEvent('pdm-open-project', {{ detail: {{ projectId: {Serialize(projectId.ToString("D"))}, tab: {Serialize(tab)} }} }}));";
        await WorkspaceView.CoreWebView2.ExecuteScriptAsync(projectScript);
    }

    private static int FindArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
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
        if (type == "credentials-request")
        {
            _ = PublishRememberedCredentialsAsync();
            return;
        }

        if (type == "desktop-settings-request")
        {
            _ = PublishDesktopSettingsAsync();
            return;
        }

        if (type == "client-version-request")
        {
            _ = PublishClientVersionAsync();
            return;
        }

        if (type == "workspace-maintenance-request")
        {
            _ = PublishWorkspaceMaintenanceAsync();
            return;
        }

        if (type == "workspace-cache-clean")
        {
            ClearReusableWorkspaceCache();
            return;
        }

        if (type == "workspace-local-state-request"
            && message.TryGetValue("payload", out var workspacePayloadValue)
            && workspacePayloadValue is Dictionary<string, object> workspacePayload)
        {
            if (TryReadWorkspaceStateRequest(workspacePayload, out var request))
            {
                workspaceStateRequest = request;
                ConfigureWorkspaceWatcher(request);
                _ = PublishWorkspaceLocalStateAsync(request);
            }
            return;
        }

        if (type == "workspace-open-folder"
            && message.TryGetValue("payload", out var folderPayloadValue)
            && folderPayloadValue is Dictionary<string, object> folderPayload)
        {
            OpenWorkspaceFolder(folderPayload);
            return;
        }

        if (type == "theme-change" && TryReadPayloadString(message, "theme", out var requestedTheme))
        {
            currentTheme = requestedTheme == "c" || requestedTheme == "o" ? requestedTheme : "a";
            embeddedPreview?.ApplyTheme(currentTheme);
            return;
        }

        if (type == "workspace-folder-browse")
        {
            BrowseWorkspaceRoot();
            return;
        }

        if (type == "desktop-settings-save"
            && message.TryGetValue("payload", out var desktopSettingsPayloadValue)
            && desktopSettingsPayloadValue is Dictionary<string, object> desktopSettingsPayload)
        {
            if (desktopSettingsPayload.TryGetValue("startWithWindows", out var startWithWindowsValue)
                && startWithWindowsValue is bool requestedStartWithWindows)
            {
                UpdateStartWithWindows(requestedStartWithWindows);
            }
            if (desktopSettingsPayload.TryGetValue("workspaceRoot", out var workspaceRootValue)
                && workspaceRootValue is string requestedWorkspaceRoot)
            {
                UpdateWorkspaceRoot(requestedWorkspaceRoot);
            }
            return;
        }

        if (type == "credentials-save" && TryReadCredentials(message, out var username, out var password))
        {
            TryUpdateRememberedCredentials(() => RememberedCredentialsStore.SaveCredentials(username, password));
            return;
        }

        if (type == "credentials-clear")
        {
            TryUpdateRememberedCredentials(RememberedCredentialsStore.Clear);
            return;
        }

        if (type == "session-ready" && TryReadPayloadString(message, "accessToken", out var token))
        {
            accessToken = token;
            activeCompanyId = TryReadPayloadString(message, "activeCompanyId", out var companyId) ? companyId : string.Empty;
            return;
        }

        if (type == "session-clear")
        {
            accessToken = string.Empty;
            activeCompanyId = string.Empty;
            HideEmbeddedPreview(true);
            HideReviewOverlay();
            return;
        }

        if (type == "document-selected")
        {
            HideEmbeddedPreview(true);
            return;
        }

        if (type == "preview-host-hide")
        {
            HideEmbeddedPreview(true);
            HideReviewOverlay();
            return;
        }

        if (type == "preview-host-suspend")
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            reviewOverlaySuspended = true;
            HideReviewOverlay();
            return;
        }

        if (type == "review-overlay-hide")
        {
            HideReviewOverlay();
            return;
        }

        if (type == "review-overlay-suspend")
        {
            reviewOverlaySuspended = true;
            HideReviewOverlay();
            return;
        }

        if (type == "review-overlay-state"
            && message.TryGetValue("payload", out var reviewStatePayloadValue)
            && reviewStatePayloadValue is Dictionary<string, object> reviewStatePayload)
        {
            UpdateReviewOverlayState(e.WebMessageAsJson, reviewStatePayload);
            return;
        }

        if (type == "review-overlay-bounds"
            && message.TryGetValue("payload", out var reviewBoundsPayloadValue)
            && reviewBoundsPayloadValue is Dictionary<string, object> reviewBoundsPayload)
        {
            UpdateReviewOverlayBounds(reviewBoundsPayload);
            return;
        }

        if (type == "preview-host-bounds" &&
            message.TryGetValue("payload", out var boundsPayloadValue) &&
            boundsPayloadValue is Dictionary<string, object> boundsPayload)
        {
            UpdatePreviewBounds(boundsPayload);
            return;
        }

        if (type == "preview-host-fit")
        {
            embeddedPreview?.FitDocument();
            return;
        }

        if (type == "preview-host-command" && TryReadPayloadString(message, "command", out var previewCommand))
        {
            embeddedPreview?.ExecuteCommand(previewCommand);
            return;
        }

        if (type == "preview-host-save-markup")
        {
            _ = SaveCurrentMarkupAsync();
            return;
        }

        if (type == "solidworks-capability-request")
        {
            _ = PublishSolidWorksCapabilityAsync();
            return;
        }

        if (type == "open-document"
            && message.TryGetValue("payload", out var openPayloadValue)
            && openPayloadValue is Dictionary<string, object> openPayload)
        {
            _ = OpenInSolidWorksAsync(openPayload);
            return;
        }

        if (type == "preview-document" &&
            message.TryGetValue("payload", out var payloadValue) &&
            payloadValue is Dictionary<string, object> payload)
        {
            _ = PreviewDocumentAsync(payload);
        }
    }

    private async Task OpenInSolidWorksAsync(IReadOnlyDictionary<string, object> payload)
    {
        try
        {
            if (!payload.TryGetValue("projectId", out var projectIdValue)
                || !Guid.TryParse(projectIdValue as string, out var projectId)
                || !payload.TryGetValue("documentId", out var documentIdValue)
                || !Guid.TryParse(documentIdValue as string, out var documentId))
            {
                throw new InvalidOperationException("项目或图档标识无效，不能发送到SolidWorks。");
            }

            var mode = payload.TryGetValue("mode", out var modeValue) ? modeValue as string : "LatestReadOnly";
            Guid? versionId = null;
            if (payload.TryGetValue("versionId", out var versionIdValue)
                && Guid.TryParse(versionIdValue as string, out var parsedVersionId))
            {
                versionId = parsedVersionId;
            }

            await PublishSolidWorksStatusAsync("loading", "正在准备SolidWorks受控打开请求…");
            var message = await solidWorksBridge.SendAsync(projectId, documentId, versionId, mode ?? "LatestReadOnly", CancellationToken.None);
            await PublishSolidWorksStatusAsync("ready", message);
        }
        catch (Exception exception)
        {
            await PublishSolidWorksStatusAsync("error", exception.Message);
        }
    }

    private async Task PublishSolidWorksCapabilityAsync()
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var detail = new { available = solidWorksBridge.IsAvailable };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', {{ detail: {Serialize(detail)} }}));";
        try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
    }

    private async Task PublishSolidWorksStatusAsync(string state, string message)
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var detail = new { state, message };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-solidworks-status', {{ detail: {Serialize(detail)} }}));";
        try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
    }

    private async Task PublishWorkspaceLocalStateAsync(WorkspaceStateRequestContext request)
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var generation = Interlocked.Increment(ref workspaceStateGeneration);
        try
        {
            var snapshot = await Task.Run(() => WorkspaceLocalStateReader.Read(
                WorkspaceSettingsStore.GetWorkspaceRoot(),
                request.ProjectId,
                request.ProjectCode,
                request.CurrentUsername,
                request.Documents));
            if (generation != workspaceStateGeneration) return;
            workspaceStateSnapshot = snapshot;
            var detail = new
            {
                projectId = snapshot.ProjectId,
                projectCode = snapshot.ProjectCode,
                projectDirectory = snapshot.ProjectDirectory,
                projectDirectoryExists = snapshot.ProjectDirectoryExists,
                items = snapshot.Items.Select(item => new
                {
                    documentId = item.DocumentId,
                    fileName = item.FileName,
                    fullPath = item.FullPath,
                    localState = item.LocalState,
                    localStateLabel = item.LocalStateLabel,
                    localRevision = item.LocalRevision,
                    latestRevision = item.LatestRevision,
                    message = item.Message,
                    isReadOnly = item.IsReadOnly,
                    lastWriteTimeUtc = item.LastWriteTimeUtc
                })
            };
            var script = $"window.dispatchEvent(new CustomEvent('pdm-workspace-local-state', {{ detail: {Serialize(detail)} }}));";
            await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException)
        {
            if (generation != workspaceStateGeneration) return;
            var detail = new { projectId = request.ProjectId, projectCode = request.ProjectCode, error = exception.Message, items = Array.Empty<object>() };
            var script = $"window.dispatchEvent(new CustomEvent('pdm-workspace-local-state', {{ detail: {Serialize(detail)} }}));";
            try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
            catch (InvalidOperationException) { }
        }
    }

    private void ConfigureWorkspaceWatcher(WorkspaceStateRequestContext request)
    {
        workspaceWatcher?.Dispose();
        workspaceWatcher = null;
        var directory = WorkspaceLocalStateReader.ProjectDirectory(WorkspaceSettingsStore.GetWorkspaceRoot(), request.ProjectCode);
        if (!Directory.Exists(directory)) return;
        workspaceWatcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        workspaceWatcher.Changed += OnWorkspaceFileChanged;
        workspaceWatcher.Created += OnWorkspaceFileChanged;
        workspaceWatcher.Deleted += OnWorkspaceFileChanged;
        workspaceWatcher.Renamed += OnWorkspaceFileChanged;
        workspaceWatcher.EnableRaisingEvents = true;
    }

    private void OnWorkspaceFileChanged(object sender, FileSystemEventArgs eventArgs)
    {
        workspaceRefreshTimer ??= new System.Threading.Timer(_ => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (workspaceStateRequest != null) _ = PublishWorkspaceLocalStateAsync(workspaceStateRequest);
        })), null, Timeout.Infinite, Timeout.Infinite);
        workspaceRefreshTimer.Change(450, Timeout.Infinite);
    }

    private void OpenWorkspaceFolder(IReadOnlyDictionary<string, object> payload)
    {
        try
        {
            if (!payload.TryGetValue("projectId", out var projectIdValue)
                || !Guid.TryParse(projectIdValue as string, out var projectId)
                || workspaceStateSnapshot == null
                || workspaceStateSnapshot.ProjectId != projectId)
            {
                throw new InvalidOperationException("请先刷新当前项目工作区。" );
            }
            var documentId = payload.TryGetValue("documentId", out var documentIdValue)
                && Guid.TryParse(documentIdValue as string, out var parsedDocumentId)
                    ? parsedDocumentId
                    : Guid.Empty;
            var file = workspaceStateSnapshot.Items.FirstOrDefault(item => item.DocumentId == documentId && File.Exists(item.FullPath));
            var directory = workspaceStateSnapshot.ProjectDirectory;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var arguments = file == null ? string.Concat("\"", directory, "\"") : string.Concat("/select,\"", file.FullPath, "\"");
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is InvalidOperationException
            || exception is NotSupportedException)
        {
            WpfMessageBox.Show(this, exception.Message, "UPLM工作区", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool TryReadWorkspaceStateRequest(
        IReadOnlyDictionary<string, object> payload,
        out WorkspaceStateRequestContext request)
    {
        request = null!;
        if (!payload.TryGetValue("projectId", out var projectIdValue)
            || !Guid.TryParse(projectIdValue as string, out var projectId)
            || !payload.TryGetValue("projectCode", out var projectCodeValue)
            || string.IsNullOrWhiteSpace(projectCodeValue as string))
        {
            return false;
        }
        var documents = new List<WorkspaceDocumentStateRequest>();
        if (payload.TryGetValue("documents", out var documentsValue) && documentsValue is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> values)
                    || !values.TryGetValue("documentId", out var documentIdValue)
                    || !Guid.TryParse(documentIdValue as string, out var documentId)) continue;
                documents.Add(new WorkspaceDocumentStateRequest
                {
                    DocumentId = documentId,
                    FileName = values.TryGetValue("fileName", out var fileName) ? fileName as string ?? string.Empty : string.Empty,
                    LatestRevision = values.TryGetValue("latestRevision", out var latestRevision) ? latestRevision as string ?? string.Empty : string.Empty,
                    CheckedOutBy = values.TryGetValue("checkedOutBy", out var checkedOutBy) ? checkedOutBy as string ?? string.Empty : string.Empty
                });
            }
        }
        request = new WorkspaceStateRequestContext(
            projectId,
            projectCodeValue as string ?? string.Empty,
            payload.TryGetValue("currentUsername", out var usernameValue) ? usernameValue as string ?? string.Empty : string.Empty,
            documents);
        return true;
    }

    private async Task PublishRememberedCredentialsAsync()
    {
        if (WorkspaceView.CoreWebView2 is null)
        {
            return;
        }

        var remembered = RememberedCredentialsStore.TryLoadCredentials(out var username, out var password);
        var detail = new { username, password, remember = remembered };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-remembered-credentials', {{ detail: {Serialize(detail)} }}));";
        await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task PublishDesktopSettingsAsync(string error = "", string message = "")
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var detail = new
        {
            available = true,
            startWithWindows,
            closeBehavior = "notificationArea",
            workspaceRoot = WorkspaceSettingsStore.GetWorkspaceRoot(),
            defaultWorkspaceRoot = WorkspaceSettingsStore.DefaultWorkspaceRoot,
            error,
            message
        };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-desktop-settings', {{ detail: {Serialize(detail)} }}));";
        try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
    }

    private async Task PublishClientVersionAsync()
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var version = ClientPackageUpdater.GetInstalledVersion(AppDomain.CurrentDomain.BaseDirectory);
        var detail = new { version };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-client-version', {{ detail: {Serialize(detail)} }}));";
        try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
    }

    private async Task PublishWorkspaceMaintenanceAsync(string error = "", string message = "")
    {
        if (WorkspaceView.CoreWebView2 == null) return;
        var usage = WorkspaceMaintenance.ReadUsage(WorkspaceSettingsStore.GetWorkspaceRoot());
        var detail = new
        {
            available = true,
            usage.WorkspaceRoot,
            usage.WorkingFiles,
            usage.WorkingBytes,
            usage.SnapshotFiles,
            usage.SnapshotBytes,
            usage.RecoveryFiles,
            usage.RecoveryBytes,
            error,
            message
        };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-workspace-maintenance', {{ detail: {Serialize(detail)} }}));";
        try { await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
    }

    private void ClearReusableWorkspaceCache()
    {
        try
        {
            WorkspaceMaintenance.ClearReusableCache(WorkspaceSettingsStore.GetWorkspaceRoot());
            _ = PublishWorkspaceMaintenanceAsync(message: "未占用的只读缓存已清理；项目工作文件和恢复副本未改动。");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is ArgumentException || exception is NotSupportedException)
        {
            _ = PublishWorkspaceMaintenanceAsync(error: exception.Message);
        }
    }

    private void UpdateStartWithWindows(bool enabled)
    {
        try
        {
            DesktopStartupSettings.SetEnabled(enabled);
            startWithWindows = enabled;
            _ = PublishDesktopSettingsAsync(message: "客户端启动设置已保存。");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException)
        {
            _ = PublishDesktopSettingsAsync(exception.Message);
        }
    }

    private void BrowseWorkspaceRoot()
    {
        using (var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择UPLM本地缓存工作区",
            SelectedPath = WorkspaceSettingsStore.GetWorkspaceRoot(),
            ShowNewFolderButton = true
        })
        {
            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            var detail = new { workspaceRoot = dialog.SelectedPath };
            var script = $"window.dispatchEvent(new CustomEvent('pdm-workspace-folder-selected', {{ detail: {Serialize(detail)} }}));";
            _ = WorkspaceView.CoreWebView2?.ExecuteScriptAsync(script);
        }
    }

    private void UpdateWorkspaceRoot(string workspaceRoot)
    {
        try
        {
            var saved = WorkspaceSettingsStore.SaveWorkspaceRoot(workspaceRoot);
            _ = PublishDesktopSettingsAsync(message: string.Concat("本地工作区已设置为：", saved));
            _ = PublishWorkspaceMaintenanceAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is ArgumentException || exception is NotSupportedException)
        {
            _ = PublishDesktopSettingsAsync(exception.Message);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        windowSource = HwndSource.FromHwnd(handle);
        windowSource?.AddHook(WindowMessageHook);
        systemMenu = GetSystemMenu(handle, false);
        if (systemMenu != IntPtr.Zero)
        {
            AppendMenu(systemMenu, MenuSeparator, UIntPtr.Zero, string.Empty);
            AppendMenu(systemMenu, MenuString, new UIntPtr(MenuExit), "退出 UPLM");
        }

        try
        {
            startWithWindows = DesktopStartupSettings.EnsureConfigured();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException)
        {
            startWithWindows = false;
        }
        InitializeTrayIcon();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowMessageSystemCommand) return IntPtr.Zero;
        var command = wParam.ToInt32();
        if (command == MenuExit)
        {
            allowClose = true;
            Close();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose) return;
        eventArgs.Cancel = true;
        HideToNotificationArea();
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (WindowState == WindowState.Minimized && !allowClose && !hideAfterStartupNavigation)
        {
            SuspendInteractiveSurfaces();
            ShowInTaskbar = true;
            return;
        }

        ResumeInteractiveSurfaces();
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeButtonClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs eventArgs)
    {
        ExitApplication();
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs) => ApplyPreviewSurfaces();

    private void InitializeTrayIcon()
    {
        var icon = LoadClientIcon();

        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("打开 UPLM");
        var exitItem = new WinForms.ToolStripMenuItem("退出 UPLM");
        openItem.Click += (_, _) => RestoreFromNotificationArea();
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "UPLM",
            Visible = true,
            ContextMenuStrip = menu
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromNotificationArea();
    }

    private static System.Drawing.Icon LoadClientIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("Assets/PdmClient.ico", UriKind.Relative));
        if (resource?.Stream != null)
        {
            using (resource.Stream)
            using (var embeddedIcon = new System.Drawing.Icon(resource.Stream))
            {
                return (System.Drawing.Icon)embeddedIcon.Clone();
            }
        }

        var executable = Process.GetCurrentProcess().MainModule?.FileName;
        return (!string.IsNullOrWhiteSpace(executable)
                ? System.Drawing.Icon.ExtractAssociatedIcon(executable)
                : null)
            ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void HideToNotificationArea()
    {
        SuspendInteractiveSurfaces();
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromNotificationArea()
    {
        hideAfterStartupNavigation = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        ResumeInteractiveSurfaces();
        Activate();
    }

    private void SuspendInteractiveSurfaces()
    {
        if (interactiveSurfacesSuspended) return;
        interactiveSurfacesSuspended = true;
        HideReviewOverlay();
        PreviewFrame.Visibility = Visibility.Collapsed;
        PreviewOverlay.IsHitTestVisible = false;
        WorkspaceView.IsHitTestVisible = false;
        WorkspaceView.Visibility = Visibility.Collapsed;
        RootGrid.IsHitTestVisible = false;
    }

    private void ResumeInteractiveSurfaces()
    {
        if (interactiveSurfacesSuspended)
        {
            RootGrid.IsHitTestVisible = true;
            WorkspaceView.Visibility = Visibility.Visible;
            WorkspaceView.IsHitTestVisible = true;
            PreviewOverlay.IsHitTestVisible = true;
            interactiveSurfacesSuspended = false;
        }

        Dispatcher.BeginInvoke(new Action(ApplyPreviewSurfaces));
    }

    internal void RestoreFromExternalRequest(string[] arguments)
    {
        RestoreFromNotificationArea();
        if (arguments == null || arguments.Length == 0) return;

        if (!workspaceNavigationReady || WorkspaceView.CoreWebView2 == null)
        {
            pendingExternalRequestArgs = arguments;
            return;
        }

        _ = DispatchLaunchRequestAsync(arguments);
    }

    private void ExitApplication()
    {
        allowClose = true;
        Close();
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs eventArgs) => allowClose = true;

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        System.Windows.Application.Current.SessionEnding -= OnSessionEnding;
        workspaceWatcher?.Dispose();
        workspaceWatcher = null;
        workspaceRefreshTimer?.Dispose();
        workspaceRefreshTimer = null;
        windowSource?.RemoveHook(WindowMessageHook);
        if (trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.Icon?.Dispose();
            trayIcon.Dispose();
            trayIcon = null;
        }
        DisposeClientResources();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string text);

    private static bool TryReadCredentials(
        IReadOnlyDictionary<string, object> message,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (!message.TryGetValue("payload", out var payloadValue)
            || payloadValue is not Dictionary<string, object> payload
            || !payload.TryGetValue("username", out var usernameValue)
            || !payload.TryGetValue("password", out var passwordValue))
        {
            return false;
        }

        username = usernameValue as string ?? string.Empty;
        password = passwordValue as string ?? string.Empty;
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
    }

    private void TryUpdateRememberedCredentials(Action update)
    {
        try
        {
            update();
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is UnauthorizedAccessException
            || exception is CryptographicException)
        {
            WpfMessageBox.Show(
                this,
                $"账号保存失败。\n\n{exception.Message}",
                "UPLM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TryOpenLocalDocument(IReadOnlyDictionary<string, object> payload)
    {
        if (!payload.TryGetValue("localPath", out var pathValue))
        {
            WpfMessageBox.Show(this, "文档尚未下载到本地工作区。", "UPLM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = pathValue as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            WpfMessageBox.Show(this, "本地文档不存在，请先获取权限或下载。", "UPLM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static bool TryReadPayloadString(IReadOnlyDictionary<string, object> message, string name, out string value)
    {
        value = string.Empty;
        return message.TryGetValue("payload", out var payloadValue)
            && payloadValue is Dictionary<string, object> payload
            && payload.TryGetValue(name, out var raw)
            && !string.IsNullOrWhiteSpace(value = raw as string ?? string.Empty);
    }

    private async Task PreviewDocumentAsync(IReadOnlyDictionary<string, object> payload)
    {
        var requestGeneration = Interlocked.Increment(ref previewRequestGeneration);
        previewDocumentReady = false;
        previewDocumentId = null;
        previewVersionId = null;
        previewMarkupDirectory = string.Empty;
        PreviewFrame.Visibility = Visibility.Collapsed;
        UpdatePreviewProperties(payload);
        try
        {
            await PublishPreviewStatusAsync("loading", string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("登录会话尚未传递到客户端，请重新登录后再预览。");
            }

            if (!payload.TryGetValue("documentId", out var documentIdValue)
                || !Guid.TryParse(documentIdValue as string, out var documentId))
            {
                throw new InvalidOperationException("图档标识无效，不能预览。");
            }

            var fileName = payload.TryGetValue("fileName", out var fileNameValue) ? Path.GetFileName(fileNameValue as string) : string.Empty;
            using var versionsRequest = CreateApiRequest(HttpMethod.Get, $"/api/documents/{documentId}/versions");
            using var versionsResponse = await apiClient.SendAsync(versionsRequest);
            var versionsJson = await versionsResponse.Content.ReadAsStringAsync();
            if (!versionsResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ReadApiError(versionsJson, "版本记录读取失败。"));
            }

            var versions = new JavaScriptSerializer().Deserialize<VersionResponse[]>(versionsJson) ?? Array.Empty<VersionResponse>();
            Guid? requestedVersionId = null;
            if (payload.TryGetValue("versionId", out var versionIdValue)
                && Guid.TryParse(versionIdValue as string, out var parsedVersionId))
            {
                requestedVersionId = parsedVersionId;
            }
            var version = requestedVersionId.HasValue
                ? versions.SingleOrDefault(item => item.Id == requestedVersionId.Value)
                    ?? throw new InvalidOperationException("图纸审核绑定的版本不存在，不能继续审核。")
                : versions.OrderByDescending(item => item.CreatedAt).FirstOrDefault()
                    ?? throw new InvalidOperationException("该图档已登记，但尚未提交首个存档版本。装配体中可见的可能只是SolidWorks缓存几何，不是可下载的源文件；请从原始工作目录找回文件后完成首次存档。");
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "document.bin";
            var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON", "PDM", "Preview", documentId.ToString("N"), version.Id.ToString("N"));
            Directory.CreateDirectory(cacheDirectory);
            var cachedFile = Path.Combine(cacheDirectory, fileName);
            if (!await IsValidCacheAsync(cachedFile, version.FileLength, version.Sha256))
            {
                var temporaryFile = cachedFile + ".download";
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
                using var fileRequest = CreateApiRequest(HttpMethod.Get, $"/api/documents/{documentId}/versions/{version.Id}/file?download=false");
                using var fileResponse = await apiClient.SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead);
                if (!fileResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(ReadApiError(await fileResponse.Content.ReadAsStringAsync(), "版本文件下载失败。"));
                }
                using (var input = await fileResponse.Content.ReadAsStreamAsync())
                using (var output = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                {
                    await input.CopyToAsync(output);
                }
                if (!await IsValidCacheAsync(temporaryFile, version.FileLength, version.Sha256))
                {
                    File.Delete(temporaryFile);
                    throw new InvalidDataException("预览文件SHA-256校验失败。");
                }
                if (File.Exists(cachedFile)) File.Delete(cachedFile);
                File.Move(temporaryFile, cachedFile);
                File.SetAttributes(cachedFile, File.GetAttributes(cachedFile) | FileAttributes.ReadOnly);
            }

            if (requestGeneration != previewRequestGeneration)
            {
                return;
            }

            var markupPath = Path.Combine(cacheDirectory, "review.markup");
            try
            {
                if (!await DownloadMarkupAsync(documentId, version.Id, markupPath))
                {
                    markupPath = string.Empty;
                }
            }
            catch (Exception exception)
            {
                markupPath = string.Empty;
                await PublishMarkupStatusAsync("warning", $"历史批注暂未加载：{exception.Message}");
            }

            if (requestGeneration != previewRequestGeneration)
            {
                return;
            }

            EnsureEmbeddedPreview();
            embeddedPreview!.OpenDocument(cachedFile, markupPath);
            previewDocumentReady = true;
            previewDocumentId = documentId;
            previewVersionId = version.Id;
            previewMarkupDirectory = cacheDirectory;
            ApplyPreviewBounds();
            await PublishPreviewStatusAsync("ready", Path.GetFileName(cachedFile), string.Empty);
        }
        catch (Exception exception)
        {
            if (requestGeneration != previewRequestGeneration)
            {
                return;
            }

            previewDocumentReady = false;
            previewDocumentId = null;
            previewVersionId = null;
            previewMarkupDirectory = string.Empty;
            PreviewFrame.Visibility = Visibility.Collapsed;
            embeddedPreview?.CloseDocument();
            await PublishPreviewStatusAsync("error", string.Empty, exception.Message);
        }
    }

    private async Task SaveCurrentMarkupAsync()
    {
        if (Interlocked.Exchange(ref previewMarkupSaveActive, 1) != 0)
        {
            return;
        }

        try
        {
            if (!previewDocumentReady || embeddedPreview == null
                || previewDocumentId is not Guid documentId
                || previewVersionId is not Guid versionId
                || string.IsNullOrWhiteSpace(previewMarkupDirectory))
            {
                throw new InvalidOperationException("当前预览尚未加载完成，不能保存批注。");
            }

            await PublishMarkupStatusAsync("saving", "正在保存批注…");
            Directory.CreateDirectory(previewMarkupDirectory);
            var requestedPath = Path.Combine(previewMarkupDirectory, $"review-{Guid.NewGuid():N}.markup");
            embeddedPreview.SaveMarkup(requestedPath);
            var generatedMarkupPath = await WaitForMarkupFileAsync(requestedPath, TimeSpan.FromSeconds(15));

            using var request = CreateApiRequest(HttpMethod.Put, $"/api/documents/{documentId}/versions/{versionId}/markup");
            using var input = new FileStream(generatedMarkupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            request.Content = new StreamContent(input, 128 * 1024);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await apiClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ReadApiError(responseText, "批注上传失败。"));
            }

            await PublishMarkupStatusAsync("saved", "批注已保存。切换图档时不会保存或修改源图纸。");
        }
        catch (Exception exception)
        {
            await PublishMarkupStatusAsync("error", exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref previewMarkupSaveActive, 0);
        }
    }

    private async Task<bool> DownloadMarkupAsync(Guid documentId, Guid versionId, string targetPath)
    {
        using var request = CreateApiRequest(HttpMethod.Get, $"/api/documents/{documentId}/versions/{versionId}/markup");
        using var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadApiError(await response.Content.ReadAsStringAsync(), "历史批注下载失败。"));
        }

        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.download";
        try
        {
            using (var input = await response.Content.ReadAsStreamAsync())
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await input.CopyToAsync(output);
            }
            if (new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidDataException("服务器返回的批注文件为空。");
            }
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(temporaryPath, targetPath);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> WaitForMarkupFileAsync(string requestedPath, TimeSpan timeout)
    {
        var directory = Path.GetDirectoryName(requestedPath) ?? throw new InvalidOperationException("批注缓存目录无效。");
        var prefix = Path.GetFileNameWithoutExtension(requestedPath);
        var deadline = DateTime.UtcNow + timeout;
        string? previousPath = null;
        long previousLength = -1;
        while (DateTime.UtcNow < deadline)
        {
            var candidate = Directory.EnumerateFiles(directory, $"{prefix}*.markup")
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault(path => new FileInfo(path).Length > 0);
            if (candidate != null)
            {
                var length = new FileInfo(candidate).Length;
                if (string.Equals(candidate, previousPath, StringComparison.OrdinalIgnoreCase) && length == previousLength)
                {
                    return candidate;
                }
                previousPath = candidate;
                previousLength = length;
            }
            await Task.Delay(150);
        }

        throw new InvalidOperationException("eDrawings未生成有效的批注文件。");
    }

    private void EnsureEmbeddedPreview()
    {
        if (embeddedPreview != null)
        {
            return;
        }

        embeddedPreview = new EDrawingsPreviewControl();
        embeddedPreview.ApplyTheme(currentTheme);
        embeddedPreview.UserMessageRequested += message => Dispatcher.BeginInvoke(new Action(() =>
            WpfMessageBox.Show(this, message, "UPLM", MessageBoxButton.OK, MessageBoxImage.Information)));
        embeddedPreview.MarkupModifiedChanged += modified => Dispatcher.BeginInvoke(new Action(() =>
            _ = PublishMarkupStatusAsync(modified ? "dirty" : "clean", modified ? "批注尚未保存。" : string.Empty)));
        embeddedPreview.UpdateProperties(previewProperties);
        EmbeddedPreviewHost.Child = embeddedPreview;
    }

    private void UpdatePreviewBounds(IReadOnlyDictionary<string, object> payload)
    {
        if (!TryReadNumber(payload, "left", out var left)
            || !TryReadNumber(payload, "top", out var top)
            || !TryReadNumber(payload, "width", out var width)
            || !TryReadNumber(payload, "height", out var height))
        {
            return;
        }

        TryReadNumber(payload, "viewportWidth", out var viewportWidth);
        TryReadNumber(payload, "viewportHeight", out var viewportHeight);
        var visible = !payload.TryGetValue("visible", out var visibleValue) || Convert.ToBoolean(visibleValue);
        previewBounds = new PreviewHostBounds(left, top, width, height, viewportWidth, viewportHeight, visible);
        reviewOverlaySuspended = false;
        ApplyPreviewSurfaces();
    }

    private void UpdateReviewOverlayBounds(IReadOnlyDictionary<string, object> payload)
    {
        if (!TryReadNumber(payload, "left", out var left)
            || !TryReadNumber(payload, "top", out var top)
            || !TryReadNumber(payload, "width", out var width)
            || !TryReadNumber(payload, "height", out var height))
        {
            return;
        }

        TryReadNumber(payload, "viewportWidth", out var viewportWidth);
        TryReadNumber(payload, "viewportHeight", out var viewportHeight);
        var visible = !payload.TryGetValue("visible", out var visibleValue) || Convert.ToBoolean(visibleValue);
        reviewOverlayBounds = new PreviewHostBounds(left, top, width, height, viewportWidth, viewportHeight, visible);
        reviewOverlaySuspended = false;
        ApplyReviewOverlayBounds();
    }

    private void UpdateReviewOverlayState(string stateJson, IReadOnlyDictionary<string, object> payload)
    {
        reviewOverlayVisible = payload.TryGetValue("visible", out var visibleValue) && Convert.ToBoolean(visibleValue);
        _ = reviewOverlay?.PublishStateAsync(stateJson);
        ApplyReviewOverlayBounds();
    }

    private void OnReviewOverlayMessageReceived(string messageJson)
    {
        var serializer = new JavaScriptSerializer();
        var message = serializer.Deserialize<Dictionary<string, object>>(messageJson);
        if (message == null || !message.TryGetValue("type", out var typeValue)) return;
        var type = typeValue as string;
        if (type == "preview-host-command" && TryReadPayloadString(message, "command", out var previewCommand))
        {
            embeddedPreview?.ExecuteCommand(previewCommand);
            return;
        }

        if (type == "review-overlay-action")
        {
            WorkspaceView.CoreWebView2?.PostWebMessageAsJson(messageJson);
        }
    }

    private bool IsPreviewSurfaceActive => IsActive || reviewOverlay?.IsActive == true;

    private void ApplyPreviewSurfaces()
    {
        ApplyPreviewBounds();
        ApplyReviewOverlayBounds();
    }

    private void ApplyPreviewBounds()
    {
        if (!IsVisible || WindowState == WindowState.Minimized
            || !previewDocumentReady || previewBounds is not { Visible: true } bounds
            || bounds.Width < 80 || bounds.Height < 80
            || WorkspaceView.ActualWidth <= 0 || WorkspaceView.ActualHeight <= 0)
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = bounds.ViewportWidth > 0 ? WorkspaceView.ActualWidth / bounds.ViewportWidth : 1d;
        var scaleY = bounds.ViewportHeight > 0 ? WorkspaceView.ActualHeight / bounds.ViewportHeight : 1d;
        var origin = WorkspaceView.TranslatePoint(new System.Windows.Point(0, 0), PreviewOverlay);
        var viewportLeft = Math.Max(0, origin.X);
        var viewportTop = Math.Max(0, origin.Y);
        var viewportRight = Math.Min(PreviewOverlay.ActualWidth, origin.X + WorkspaceView.ActualWidth);
        var viewportBottom = Math.Min(PreviewOverlay.ActualHeight, origin.Y + WorkspaceView.ActualHeight);
        var requestedLeft = origin.X + bounds.Left * scaleX;
        var requestedTop = origin.Y + bounds.Top * scaleY;
        var left = Math.Max(viewportLeft, requestedLeft);
        var top = Math.Max(viewportTop, requestedTop);
        var right = Math.Min(viewportRight, requestedLeft + bounds.Width * scaleX);
        var bottom = Math.Min(viewportBottom, requestedTop + bounds.Height * scaleY);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        if (width < 80 || height < 80)
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            return;
        }

        System.Windows.Controls.Canvas.SetLeft(PreviewFrame, left);
        System.Windows.Controls.Canvas.SetTop(PreviewFrame, top);
        PreviewFrame.Width = width;
        PreviewFrame.Height = height;
        PreviewFrame.Visibility = Visibility.Visible;
        embeddedPreview?.RefreshPreview();
    }

    private void ApplyReviewOverlayBounds()
    {
        if (reviewOverlay == null
            || !IsPreviewSurfaceActive || !IsVisible || WindowState == WindowState.Minimized
            || reviewOverlaySuspended || !reviewOverlayVisible
            || reviewOverlayBounds is not { Visible: true } bounds
            || bounds.Width < 80 || bounds.Height < 80
            || WorkspaceView.ActualWidth <= 0 || WorkspaceView.ActualHeight <= 0)
        {
            HideReviewOverlay();
            return;
        }

        var scaleX = bounds.ViewportWidth > 0 ? WorkspaceView.ActualWidth / bounds.ViewportWidth : 1d;
        var scaleY = bounds.ViewportHeight > 0 ? WorkspaceView.ActualHeight / bounds.ViewportHeight : 1d;
        var origin = WorkspaceView.TranslatePoint(new System.Windows.Point(0, 0), RootGrid);
        var viewportLeft = Math.Max(0, origin.X);
        var viewportTop = Math.Max(0, origin.Y);
        var viewportRight = Math.Min(RootGrid.ActualWidth, origin.X + WorkspaceView.ActualWidth);
        var viewportBottom = Math.Min(RootGrid.ActualHeight, origin.Y + WorkspaceView.ActualHeight);
        var requestedLeft = origin.X + bounds.Left * scaleX;
        var requestedTop = origin.Y + bounds.Top * scaleY;
        var left = Math.Max(viewportLeft, requestedLeft);
        var top = Math.Max(viewportTop, requestedTop);
        var right = Math.Min(viewportRight, requestedLeft + bounds.Width * scaleX);
        var bottom = Math.Min(viewportBottom, requestedTop + bounds.Height * scaleY);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        if (width < 80 || height < 80)
        {
            HideReviewOverlay();
            return;
        }

        var screenPoint = RootGrid.PointToScreen(new System.Windows.Point(left, top));
        var presentationSource = PresentationSource.FromVisual(this);
        if (presentationSource?.CompositionTarget != null)
        {
            screenPoint = presentationSource.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }
        reviewOverlay.ShowAt(screenPoint.X, screenPoint.Y, width, height);
    }

    private void HideReviewOverlay() => reviewOverlay?.HideOverlay();

    private void HideEmbeddedPreview(bool closeDocument)
    {
        Interlocked.Increment(ref previewRequestGeneration);
        previewDocumentReady = false;
        PreviewFrame.Visibility = Visibility.Collapsed;
        if (closeDocument)
        {
            embeddedPreview?.CloseDocument();
            previewDocumentId = null;
            previewVersionId = null;
            previewMarkupDirectory = string.Empty;
        }
    }

    private void UpdatePreviewProperties(IReadOnlyDictionary<string, object> payload)
    {
        var fields = new[]
        {
            (Label: "物料编码", Key: "drawingNumber"),
            (Label: "名称", Key: "name"),
            (Label: "规格/型号", Key: "specification"),
            (Label: "材质", Key: "material"),
            (Label: "品牌", Key: "brand"),
            (Label: "表面处理", Key: "surfaceTreatment"),
            (Label: "版本", Key: "revision"),
            (Label: "状态", Key: "status"),
        };

        var values = new List<KeyValuePair<string, string>>();
        foreach (var field in fields)
        {
            var value = payload.TryGetValue(field.Key, out var raw) && raw is string text
                ? text.Trim()
                : string.Empty;
            values.Add(new KeyValuePair<string, string>(
                field.Label,
                string.IsNullOrWhiteSpace(value) ? "—" : value));
        }

        previewProperties = values;
        embeddedPreview?.UpdateProperties(previewProperties);
    }

    private async Task PublishPreviewStatusAsync(string state, string fileName, string message)
    {
        if (WorkspaceView.CoreWebView2 == null)
        {
            return;
        }

        var detail = new { state, fileName, message };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-preview-status', {{ detail: {Serialize(detail)} }}));";
        try
        {
            await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            // The WebView is closing with the client window.
        }
    }

    private async Task PublishMarkupStatusAsync(string state, string message)
    {
        if (WorkspaceView.CoreWebView2 == null)
        {
            return;
        }

        var detail = new { state, message };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', {{ detail: {Serialize(detail)} }}));";
        try
        {
            await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            // The WebView is closing with the client window.
        }
    }

    private void DisposeClientResources()
    {
        bootstrapLifetime.Cancel();
        if (reviewOverlay != null)
        {
            reviewOverlay.MessageReceived -= OnReviewOverlayMessageReceived;
            reviewOverlay.ActivityChanged -= ApplyPreviewSurfaces;
            reviewOverlay.Shutdown();
            reviewOverlay = null;
        }
        HideEmbeddedPreview(false);
        EmbeddedPreviewHost.Child = null;
        embeddedPreview?.Dispose();
        embeddedPreview = null;
        apiClient.Dispose();
        bootstrapLifetime.Dispose();
    }

    private static bool TryReadNumber(IReadOnlyDictionary<string, object> payload, string name, out double value)
    {
        value = 0;
        if (!payload.TryGetValue(name, out var raw) || raw == null)
        {
            return false;
        }

        try
        {
            value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(activeCompanyId)) request.Headers.Add("X-Company-Id", activeCompanyId);
        return request;
    }

    private static Task<bool> IsValidCacheAsync(string path, long expectedLength, string expectedSha256)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) return Task.FromResult(false);
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var hash = SHA256.Create())
        {
            var actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
            return Task.FromResult(string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string ReadApiError(string json, string fallback)
    {
        try
        {
            var problem = new JavaScriptSerializer().Deserialize<ProblemResponse>(json);
            return problem?.Detail ?? problem?.Title ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class VersionResponse
    {
        public Guid Id { get; set; }
        public long FileLength { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ProblemResponse
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class WorkspaceStateRequestContext
    {
        public WorkspaceStateRequestContext(
            Guid projectId,
            string projectCode,
            string currentUsername,
            IReadOnlyCollection<WorkspaceDocumentStateRequest> documents)
        {
            ProjectId = projectId;
            ProjectCode = projectCode;
            CurrentUsername = currentUsername;
            Documents = documents;
        }

        public Guid ProjectId { get; }
        public string ProjectCode { get; }
        public string CurrentUsername { get; }
        public IReadOnlyCollection<WorkspaceDocumentStateRequest> Documents { get; }
    }

    private sealed class PreviewHostBounds
    {
        public PreviewHostBounds(
            double left,
            double top,
            double width,
            double height,
            double viewportWidth,
            double viewportHeight,
            bool visible)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
            Visible = visible;
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
        public double ViewportWidth { get; }
        public double ViewportHeight { get; }
        public bool Visible { get; }
    }
}
