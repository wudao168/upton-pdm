using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Microsoft.Web.WebView2.Core;
using Upton.Pdm.LocalSettings;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace Upton.Pdm.Desktop;

public partial class MainWindow : Window
{
    private const string UiHost = "appassets.pdm.local";
    private const int WindowMessageSystemCommand = 0x0112;
    private const int MenuStartWithWindows = 0x1FF0;
    private const int MenuExit = 0x1FE0;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuByCommand = 0x0000;
    private const uint MenuChecked = 0x0008;
    private const uint MenuUnchecked = 0x0000;
    private readonly string[] startupArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    private readonly bool startedWithWindows = Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
    private readonly HttpClient apiClient = new() { BaseAddress = new Uri("http://127.0.0.1:5080"), Timeout = TimeSpan.FromMinutes(5) };
    private readonly SolidWorksOpenBridge solidWorksBridge = new();
    private string accessToken = string.Empty;
    private EDrawingsPreviewControl? embeddedPreview;
    private PreviewHostBounds? previewBounds;
    private bool previewDocumentReady;
    private int previewRequestGeneration;
    private HwndSource? windowSource;
    private IntPtr systemMenu;
    private bool startWithWindows;
    private bool allowClose;
    private bool trayNoticeShown;
    private WinForms.NotifyIcon? trayIcon;
    private WinForms.ToolStripMenuItem? trayStartupItem;
    private string[]? pendingExternalRequestArgs;
    private bool workspaceNavigationReady;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ApplyPreviewBounds();
        StateChanged += OnWindowStateChanged;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
        System.Windows.Application.Current.SessionEnding += OnSessionEnding;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (startedWithWindows)
        {
            HideToNotificationArea(false);
        }

        try
        {
            await InitializeWorkspaceAsync();
        }
        catch (Exception exception)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            WpfMessageBox.Show(
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
        WorkspaceView.NavigationCompleted += async (_, args) =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (!args.IsSuccess)
            {
                WpfMessageBox.Show(this, $"页面加载失败：{args.WebErrorStatus}", "UPTON PDM");
            }
            else
            {
                workspaceNavigationReady = true;
                var requestArgs = pendingExternalRequestArgs ?? startupArgs;
                pendingExternalRequestArgs = null;
                await DispatchLaunchRequestAsync(requestArgs);
            }
            _ = PublishSolidWorksCapabilityAsync();
        };
        var uiVersion = File.GetLastWriteTimeUtc(indexFile).Ticks;
        WorkspaceView.Source = new Uri($"https://{UiHost}/index.html?v={uiVersion}");
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
            return;
        }

        if (type == "session-clear")
        {
            accessToken = string.Empty;
            HideEmbeddedPreview(true);
            return;
        }

        if (type == "document-selected" || type == "preview-host-hide")
        {
            HideEmbeddedPreview(true);
            return;
        }

        if (type == "preview-host-suspend")
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            PreviewPropertiesPopup.IsOpen = false;
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

    private void UpdateStartWithWindows(bool enabled)
    {
        try
        {
            DesktopStartupSettings.SetEnabled(enabled);
            startWithWindows = enabled;
            UpdateSystemMenuCheck();
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
            Description = "选择UPTON PDM本地缓存工作区",
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
            AppendMenu(systemMenu, MenuString, new UIntPtr(MenuStartWithWindows), "随电脑启动");
            AppendMenu(systemMenu, MenuString, new UIntPtr(MenuExit), "退出 UPTON PDM");
        }

        try
        {
            startWithWindows = DesktopStartupSettings.EnsureConfigured();
            UpdateSystemMenuCheck();
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
        if (command == MenuStartWithWindows)
        {
            UpdateStartWithWindows(!startWithWindows);
            handled = true;
        }
        else if (command == MenuExit)
        {
            allowClose = true;
            Close();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void UpdateSystemMenuCheck()
    {
        if (systemMenu != IntPtr.Zero)
        {
            CheckMenuItem(systemMenu, MenuStartWithWindows, MenuByCommand | (startWithWindows ? MenuChecked : MenuUnchecked));
        }
        if (trayStartupItem != null)
        {
            trayStartupItem.Checked = startWithWindows;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose) return;
        eventArgs.Cancel = true;
        HideToNotificationArea(true);
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState == WindowState.Minimized && !allowClose)
        {
            ShowInTaskbar = true;
            PreviewPropertiesPopup.IsOpen = false;
            return;
        }

        ApplyPreviewBounds();
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs) => ApplyPreviewBounds();

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs) => PreviewPropertiesPopup.IsOpen = false;

    private void InitializeTrayIcon()
    {
        var icon = LoadClientIcon();

        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("打开 UPTON PDM");
        trayStartupItem = new WinForms.ToolStripMenuItem("随电脑启动") { Checked = startWithWindows };
        var exitItem = new WinForms.ToolStripMenuItem("退出 UPTON PDM");
        openItem.Click += (_, _) => RestoreFromNotificationArea();
        trayStartupItem.Click += (_, _) => UpdateStartWithWindows(!startWithWindows);
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(openItem);
        menu.Items.Add(trayStartupItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "UPTON PDM",
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

    private void HideToNotificationArea(bool showNotice)
    {
        PreviewPropertiesPopup.IsOpen = false;
        ShowInTaskbar = false;
        Hide();
        if (showNotice && !trayNoticeShown && trayIcon != null)
        {
            trayNoticeShown = true;
            trayIcon.ShowBalloonTip(2500, "UPTON PDM", "客户端正在通知区域运行。双击图标可重新打开。", WinForms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromNotificationArea()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        Activate();
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

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr menu, uint item, uint check);

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
                "UPTON PDM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TryOpenLocalDocument(IReadOnlyDictionary<string, object> payload)
    {
        if (!payload.TryGetValue("localPath", out var pathValue))
        {
            WpfMessageBox.Show(this, "文档尚未下载到本地工作区。", "UPTON PDM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = pathValue as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            WpfMessageBox.Show(this, "本地文档不存在，请先获取权限或下载。", "UPTON PDM", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        PreviewFrame.Visibility = Visibility.Collapsed;
        PreviewPropertiesPopup.IsOpen = false;
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
            var version = versions.OrderByDescending(item => item.CreatedAt).FirstOrDefault()
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

            EnsureEmbeddedPreview();
            embeddedPreview!.OpenDocument(cachedFile);
            previewDocumentReady = true;
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
            PreviewFrame.Visibility = Visibility.Collapsed;
            PreviewPropertiesPopup.IsOpen = false;
            embeddedPreview?.CloseDocument();
            await PublishPreviewStatusAsync("error", string.Empty, exception.Message);
        }
    }

    private void EnsureEmbeddedPreview()
    {
        if (embeddedPreview != null)
        {
            return;
        }

        embeddedPreview = new EDrawingsPreviewControl();
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
        ApplyPreviewBounds();
    }

    private void ApplyPreviewBounds()
    {
        if (!IsActive || !IsVisible || WindowState == WindowState.Minimized
            || !previewDocumentReady || previewBounds is not { Visible: true } bounds
            || bounds.Width < 80 || bounds.Height < 80
            || WorkspaceView.ActualWidth <= 0 || WorkspaceView.ActualHeight <= 0)
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            PreviewPropertiesPopup.IsOpen = false;
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
            PreviewFrame.Visibility = Visibility.Collapsed;
            PreviewPropertiesPopup.IsOpen = false;
            return;
        }

        System.Windows.Controls.Canvas.SetLeft(PreviewFrame, left);
        System.Windows.Controls.Canvas.SetTop(PreviewFrame, top);
        PreviewFrame.Width = width;
        PreviewFrame.Height = height;
        PreviewFrame.Visibility = Visibility.Visible;
        PreviewPropertiesPopup.IsOpen = PreviewPropertiesGrid.Children.Count > 0;
    }

    private void HideEmbeddedPreview(bool closeDocument)
    {
        Interlocked.Increment(ref previewRequestGeneration);
        previewDocumentReady = false;
        PreviewFrame.Visibility = Visibility.Collapsed;
        PreviewPropertiesPopup.IsOpen = false;
        if (closeDocument)
        {
            embeddedPreview?.CloseDocument();
        }
    }

    private void UpdatePreviewProperties(IReadOnlyDictionary<string, object> payload)
    {
        PreviewPropertiesGrid.Children.Clear();
        PreviewPropertiesGrid.RowDefinitions.Clear();
        PreviewPropertiesGrid.ColumnDefinitions.Clear();
        PreviewPropertiesGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(64) });
        PreviewPropertiesGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var fields = new[]
        {
            (Label: "物料/图号", Key: "drawingNumber"),
            (Label: "名称", Key: "name"),
            (Label: "规格/型号", Key: "specification"),
            (Label: "材质", Key: "material"),
            (Label: "品牌", Key: "brand"),
            (Label: "表面处理", Key: "surfaceTreatment"),
            (Label: "版本", Key: "revision"),
            (Label: "状态", Key: "status"),
        };

        foreach (var field in fields)
        {
            if (!payload.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw as string)) continue;
            var row = PreviewPropertiesGrid.RowDefinitions.Count;
            PreviewPropertiesGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = field.Label,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 6, 4),
            };
            var value = new System.Windows.Controls.TextBlock
            {
                Text = (string)raw,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 6, 23)),
                Margin = new Thickness(0, 0, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 250,
            };
            System.Windows.Controls.Grid.SetRow(label, row);
            System.Windows.Controls.Grid.SetColumn(label, 0);
            System.Windows.Controls.Grid.SetRow(value, row);
            System.Windows.Controls.Grid.SetColumn(value, 1);
            PreviewPropertiesGrid.Children.Add(label);
            PreviewPropertiesGrid.Children.Add(value);
        }
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

    private void DisposeClientResources()
    {
        HideEmbeddedPreview(true);
        EmbeddedPreviewHost.Child = null;
        embeddedPreview?.Dispose();
        embeddedPreview = null;
        apiClient.Dispose();
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
