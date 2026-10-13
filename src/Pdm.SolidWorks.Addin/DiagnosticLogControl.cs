using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class DiagnosticLogControl : UserControl
{
    private readonly TextBox content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 44, Text = "显示本次运行最近200条记录。导出包含历史技术日志，可直接提交分析。" };
    private readonly Timer timer = new Timer { Interval = 1000 };
    private readonly string version;
    private int displayedRevision = -1;

    public DiagnosticLogControl(string version)
    {
        this.version = version;
        Dock = DockStyle.Fill;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 66, AutoSize = true, WrapContents = true };
        var refresh = new Button { Text = "刷新", AutoSize = true };
        var copy = new Button { Text = "复制记录", AutoSize = true };
        var export = new Button { Text = "导出诊断", AutoSize = true };
        refresh.Click += (_, __) => RefreshLog(true);
        copy.Click += (_, __) =>
        {
            try { Clipboard.SetText("插件版本：" + version + Environment.NewLine + PluginDiagnosticLog.Snapshot()); status.Text = "操作记录已复制；完整排查请使用“导出诊断”。"; }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException)
            { status.Text = "剪贴板暂时被占用，请重试或导出诊断文件。"; }
        };
        export.Click += async (_, __) =>
        {
            using (var picker = new SaveFileDialog { Title = "导出插件诊断日志", Filter = "诊断文本文件 (*.txt)|*.txt", FileName = "UPLM-插件诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                export.Enabled = false;
                status.Text = "正在汇总诊断日志…";
                var destination = picker.FileName;
                try
                {
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
                    await Task.Run(() => File.WriteAllText(destination, PluginDiagnosticLog.BuildReport(version, directory), new UTF8Encoding(true)));
                    if (!IsDisposed) status.Text = "已导出，可提交此文件分析：" + destination;
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                { if (!IsDisposed) status.Text = "导出失败，请选择可写入的本地文件夹后重试。"; }
                finally { if (!IsDisposed) export.Enabled = true; }
            }
        };
        toolbar.Controls.AddRange(new Control[] { refresh, copy, export });
        Controls.Add(content);
        Controls.Add(status);
        Controls.Add(toolbar);
        timer.Tick += (_, __) => { if (Visible) RefreshLog(false); };
        VisibleChanged += (_, __) => { if (Visible) RefreshLog(true); };
        timer.Start();
    }

    private void RefreshLog(bool force)
    {
        var revision = PluginDiagnosticLog.Revision;
        if (!force && revision == displayedRevision) return;
        displayedRevision = revision;
        content.Text = PluginDiagnosticLog.Snapshot();
        content.SelectionStart = content.TextLength;
        content.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
