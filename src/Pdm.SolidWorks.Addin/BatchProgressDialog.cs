using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class BatchProgressDialog : Form
{
    private readonly Label status = new Label();
    private readonly Label currentFile = new Label();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly TextBox details = new TextBox();
    private readonly Button close = new Button();
    private readonly Timer autoCloseTimer = new Timer { Interval = 1000 };
    private bool canClose;
    private bool cancellationRequested;

    public event EventHandler CancelRequested;

    public BatchProgressDialog(int totalFiles)
    {
        Text = "整套提交存档";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 218);
        Font = new Font("Microsoft YaHei UI", 9F);

        status.AutoSize = false;
        status.Location = new Point(18, 16);
        status.Size = new Size(524, 25);
        status.Text = "正在准备整套提交…";

        currentFile.AutoEllipsis = true;
        currentFile.Location = new Point(18, 47);
        currentFile.Size = new Size(524, 22);

        progress.Location = new Point(18, 75);
        progress.Size = new Size(524, 20);
        progress.Minimum = 0;
        progress.Maximum = Math.Max(1, totalFiles);

        details.Location = new Point(18, 106);
        details.Size = new Size(524, 55);
        details.Multiline = true;
        details.ReadOnly = true;
        details.BorderStyle = BorderStyle.None;
        details.BackColor = SystemColors.Control;
        details.ScrollBars = ScrollBars.Vertical;
        details.TabStop = false;

        close.Text = "取消";
        close.Location = new Point(442, 174);
        close.Size = new Size(100, 30);
        close.Click += (_, __) => RequestCancelOrClose();
        autoCloseTimer.Tick += (_, __) =>
        {
            autoCloseTimer.Stop();
            if (canClose && !IsDisposed)
            {
                Close();
            }
        };

        Controls.Add(status);
        Controls.Add(currentFile);
        Controls.Add(progress);
        Controls.Add(details);
        Controls.Add(close);
    }

    public void SetStage(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetStage(message)));
            return;
        }

        status.Text = message ?? string.Empty;
        currentFile.Text = string.Empty;
        Invalidate(true);
    }

    public void ReportFile(int completed, int total, string fileName, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => ReportFile(completed, total, fileName, message)));
            return;
        }

        progress.Maximum = Math.Max(1, total);
        progress.Value = Math.Max(progress.Minimum, Math.Min(progress.Maximum, completed));
        status.Text = string.Concat("正在处理 ", Math.Min(completed + 1, total), " / ", total);
        currentFile.Text = fileName ?? string.Empty;
        details.Text = message ?? string.Empty;
        Invalidate(true);
    }

    public void Complete(string message, bool hasFailures)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Complete(message, hasFailures)));
            return;
        }

        progress.Value = progress.Maximum;
        status.Text = hasFailures ? "整套提交完成，部分文件处理失败。" : "整套提交完成。";
        currentFile.Text = string.Empty;
        details.Text = message ?? string.Empty;
        canClose = true;
        close.Text = "关闭";
        close.Enabled = true;
        close.Focus();
        Invalidate(true);
        if (!hasFailures)
        {
            autoCloseTimer.Start();
        }
    }

    public void Fail(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Fail(message)));
            return;
        }

        status.Text = "整套提交未完成。";
        currentFile.Text = string.Empty;
        details.Text = message ?? string.Empty;
        canClose = true;
        close.Text = "关闭";
        close.Enabled = true;
        close.Focus();
        Invalidate(true);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!canClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            RequestCancellation();
            eventArgs.Cancel = true;
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        autoCloseTimer.Stop();
        autoCloseTimer.Dispose();
        base.OnFormClosed(eventArgs);
    }

    private void RequestCancelOrClose()
    {
        if (canClose)
        {
            Close();
            return;
        }

        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (cancellationRequested)
        {
            return;
        }

        cancellationRequested = true;
        status.Text = "正在取消，请稍候…";
        currentFile.Text = string.Empty;
        close.Enabled = false;
        CancelRequested?.Invoke(this, EventArgs.Empty);
        Invalidate(true);
    }
}
