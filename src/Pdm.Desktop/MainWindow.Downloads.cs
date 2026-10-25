using System;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Upton.Pdm.Desktop;

public partial class MainWindow
{
    private readonly SemaphoreSlim downloadDialogGate = new(1, 1);

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            args.Cancel = true;
            using var deferral = args.GetDeferral();
            await downloadDialogGate.WaitAsync();
            try
            {
                // 文件弹窗必须在 WebView2 事件返回后显示，避免嵌套消息循环阻塞下载。
                await Dispatcher.InvokeAsync(() =>
                {
                    if (bootstrapLifetime.IsCancellationRequested) return;
                    var path = SelectDownloadPath(args.ResultFilePath);
                    if (path == null) return;
                    args.ResultFilePath = path;
                    args.Cancel = false;
                });
            }
            finally
            {
                downloadDialogGate.Release();
            }
        }
        catch (Exception exception)
        {
            AppendClientDiagnostic($"下载保存窗口处理失败：{exception}");
            if (!bootstrapLifetime.IsCancellationRequested)
            {
                System.Windows.MessageBox.Show(this, "无法打开文件保存窗口，请重新下载后重试。", "UPLM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    protected virtual string? SelectDownloadPath(string suggestedPath)
    {
        var fileName = Path.GetFileName(suggestedPath);
        var dialog = new SaveFileDialog
        {
            Title = "保存下载文件",
            FileName = string.IsNullOrWhiteSpace(fileName) ? "下载文件" : fileName,
            DefaultExt = Path.GetExtension(fileName),
            Filter = "所有文件 (*.*)|*.*",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
