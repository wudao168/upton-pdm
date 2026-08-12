using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WpfMessageBox = System.Windows.MessageBox;

namespace Upton.Pdm.Desktop;

public partial class MainWindow : Window
{
    private const string UiHost = "appassets.pdm.local";
    private readonly string[] startupArgs = Environment.GetCommandLineArgs();
    private readonly HttpClient apiClient = new() { BaseAddress = new Uri("http://127.0.0.1:5080"), Timeout = TimeSpan.FromMinutes(5) };
    private string accessToken = string.Empty;
    private EDrawingsPreviewControl? embeddedPreview;
    private PreviewHostBounds? previewBounds;
    private bool previewDocumentReady;
    private int previewRequestGeneration;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ApplyPreviewBounds();
        Closed += (_, _) => DisposeClientResources();
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
        WorkspaceView.NavigationCompleted += (_, args) =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (!args.IsSuccess)
            {
                WpfMessageBox.Show(this, $"页面加载失败：{args.WebErrorStatus}", "UPTON PDM");
            }
            else if (startupArgs.Length >= 5 && string.Equals(startupArgs[1], "--compare", StringComparison.OrdinalIgnoreCase))
            {
                var script = $"window.dispatchEvent(new CustomEvent('pdm-open-version-compare', {{ detail: {{ documentId: {Serialize(startupArgs[2])}, leftVersionId: {Serialize(startupArgs[3])}, rightVersionId: {Serialize(startupArgs[4])} }} }}));";
                _ = WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
            }
        };
        WorkspaceView.Source = new Uri($"https://{UiHost}/index.html");
    }

    private static string Serialize(object value) => new JavaScriptSerializer().Serialize(value);

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

        if (type == "credentials-save" && TryReadCredentials(message, out var username, out var password))
        {
            TryUpdateRememberedCredentials(() => RememberedCredentialsStore.Save(username, password));
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

        if ((type == "open-document" || type == "preview-document") &&
            message.TryGetValue("payload", out var payloadValue) &&
            payloadValue is Dictionary<string, object> payload)
        {
            _ = PreviewDocumentAsync(payload);
        }
    }

    private async Task PublishRememberedCredentialsAsync()
    {
        if (WorkspaceView.CoreWebView2 is null)
        {
            return;
        }

        var remembered = RememberedCredentialsStore.TryLoad(out var username, out var password);
        var detail = new { username, password, remember = remembered };
        var script = $"window.dispatchEvent(new CustomEvent('pdm-remembered-credentials', {{ detail: {Serialize(detail)} }}));";
        await WorkspaceView.CoreWebView2.ExecuteScriptAsync(script);
    }

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
        return !string.IsNullOrWhiteSpace(username);
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
                $"账号和密码保存失败。\n\n{exception.Message}",
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
                ?? throw new InvalidOperationException("该图档尚无已存档版本，不能只读预览。");
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
        if (!previewDocumentReady || previewBounds is not { Visible: true } bounds
            || bounds.Width < 80 || bounds.Height < 80
            || WorkspaceView.ActualWidth <= 0 || WorkspaceView.ActualHeight <= 0)
        {
            PreviewFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = bounds.ViewportWidth > 0 ? WorkspaceView.ActualWidth / bounds.ViewportWidth : 1d;
        var scaleY = bounds.ViewportHeight > 0 ? WorkspaceView.ActualHeight / bounds.ViewportHeight : 1d;
        var origin = WorkspaceView.TranslatePoint(new System.Windows.Point(0, 0), RootGrid);
        var left = Math.Max(0, origin.X + bounds.Left * scaleX);
        var top = Math.Max(0, origin.Y + bounds.Top * scaleY);
        var width = Math.Min(bounds.Width * scaleX, Math.Max(0, RootGrid.ActualWidth - left));
        var height = Math.Min(bounds.Height * scaleY, Math.Max(0, RootGrid.ActualHeight - top));
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
    }

    private void HideEmbeddedPreview(bool closeDocument)
    {
        Interlocked.Increment(ref previewRequestGeneration);
        previewDocumentReady = false;
        PreviewFrame.Visibility = Visibility.Collapsed;
        if (closeDocument)
        {
            embeddedPreview?.CloseDocument();
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
