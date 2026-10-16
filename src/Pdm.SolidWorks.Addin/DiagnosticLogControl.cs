using System;
using System.Drawing;
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
    private readonly ToolTip toolTip = new ToolTip();
    private readonly string version;
    private int displayedRevision = -1;

    public DiagnosticLogControl(string version)
    {
        this.version = version;
        Dock = DockStyle.Fill;
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 7, RowCount = 1, Padding = new Padding(0, 0, 0, 6), Margin = Padding.Empty };
        for (var column = 0; column < 7; column++)
        {
            toolbar.ColumnStyles.Add(column % 2 == 0
                ? new ColumnStyle(SizeType.Percent, 25)
                : new ColumnStyle(SizeType.Absolute, 6));
        }
        var copy = CreateButton("复制记录");
        var export = CreateButton("导出诊断");
        toolTip.SetToolTip(copy, "复制本次运行的最近 200 条操作记录");
        toolTip.SetToolTip(export, "导出操作记录和历史技术日志，供问题排查使用");
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
        toolbar.Controls.Add(copy, 0, 0);
        toolbar.Controls.Add(export, 2, 0);
        toolbar.Layout += (_, __) =>
        {
            var minimumWidth = Math.Max(TextRenderer.MeasureText(copy.Text, copy.Font).Width, TextRenderer.MeasureText(export.Text, export.Font).Width) + 4;
            var gap = (int)Math.Round(toolbar.ColumnStyles[1].Width);
            var span = (toolbar.ClientSize.Width - 3 * gap) / 4 < minimumWidth ? 3 : 1;
            if (toolbar.GetColumnSpan(copy) == span) return;
            toolbar.SuspendLayout();
            toolbar.SetColumnSpan(copy, span);
            toolbar.SetColumn(export, span == 3 ? 4 : 2);
            toolbar.SetColumnSpan(export, span);
            toolbar.ResumeLayout();
        };
        Controls.Add(content);
        Controls.Add(status);
        Controls.Add(toolbar);
        timer.Tick += (_, __) => { if (Visible) RefreshLog(false); };
        VisibleChanged += (_, __) => { if (Visible) RefreshLog(true); };
        timer.Start();
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(31, 49, 72),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(122, 122, 122);
        return button;
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
        if (disposing)
        {
            timer.Dispose();
            toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
