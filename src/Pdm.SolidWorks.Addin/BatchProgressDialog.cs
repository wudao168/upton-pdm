using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class BatchProgressDialog : Form
{
    private readonly Label status = new Label();
    private readonly Label currentFile = new Label();
    private readonly TextBox details = new TextBox();
    private readonly Button close = new Button();
    private bool canClose;
    private bool cancellationRequested;
    private readonly ArchiveSession session;

    public event EventHandler CancelRequested;
    public bool IsSessionProxy => session != null;

    public BatchProgressDialog(int totalFiles)
    {
        session = ArchiveSession.Current;
        if (session != null) { session.CancelRequested += ForwardCancellation; return; }
        Text = "整套提交存档";
        DialogWindowSizing.CenterOnOwnerScreen(this);
        TopMost = true;
        Shown += (_, _) => { BringToFront(); Activate(); };
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 460);
        Font = new Font("Microsoft YaHei UI", 9F);

        status.AutoSize = false;
        status.Location = new Point(18, 16);
        status.Size = new Size(684, 25);
        status.Text = "正在准备整套提交…";

        currentFile.AutoEllipsis = true;
        currentFile.Location = new Point(18, 47);
        currentFile.Size = new Size(684, 22);

        details.Location = new Point(18, 75);
        details.Size = new Size(684, 320);
        details.Multiline = true;
        details.ReadOnly = true;
        details.BorderStyle = BorderStyle.None;
        details.BackColor = SystemColors.Control;
        details.ScrollBars = ScrollBars.Vertical;
        details.TabStop = false;

        close.Text = "取消";
        close.Location = new Point(602, 412);
        close.Size = new Size(100, 30);
        close.Click += (_, __) => RequestCancelOrClose();

        Controls.Add(status);
        Controls.Add(currentFile);
        Controls.Add(details);
        Controls.Add(close);
    }

    public void SetStage(string message)
    {
        if (session != null) { session.SetStage(message); return; }
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
        if (session != null) { session.ReportFile(completed, total, fileName, message); return; }
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => ReportFile(completed, total, fileName, message)));
            return;
        }

        status.Text = string.Concat("正在处理 ", Math.Min(completed + 1, total), " / ", total);
        currentFile.Text = fileName ?? string.Empty;
        details.Text = message ?? string.Empty;
        Invalidate(true);
    }

    public void Complete(string message, bool hasFailures, bool hasBomWarnings = false)
    {
        if (session != null) { session.Complete(message, hasFailures, hasBomWarnings); return; }
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Complete(message, hasFailures, hasBomWarnings)));
            return;
        }

        status.Text = hasFailures ? "整套提交未完成，部分文件处理失败。"
            : hasBomWarnings ? "存档已完成，请查看处理提醒。" : "整套提交完成。";
        PluginDiagnosticLog.Record(hasFailures ? "失败" : "结果", status.Text + "\r\n" + message);
        currentFile.Text = string.Empty;
        details.Text = message ?? string.Empty;
        canClose = true;
        close.Text = "关闭";
        close.Enabled = true;
        close.Focus();
        Invalidate(true);
        BringToFront();
        Activate();
    }

    public void Fail(string message)
    {
        if (session != null) { session.Fail(message); return; }
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Fail(message)));
            return;
        }

        status.Text = "整套提交未完成。";
        PluginDiagnosticLog.Record("失败", status.Text + "\r\n" + message);
        currentFile.Text = string.Empty;
        details.Text = message ?? string.Empty;
        canClose = true;
        close.Text = "关闭";
        close.Enabled = true;
        close.Focus();
        Invalidate(true);
    }

    public new void Show(IWin32Window owner) { if (session != null) session.BringToFront(); else base.Show(owner); }
    public new void BringToFront() { if (session != null) session.BringToFront(); else base.BringToFront(); }
    public new void Activate() { if (session != null) session.BringToFront(); else base.Activate(); }
    public new void Refresh() { if (session == null) base.Refresh(); }
    private void ForwardCancellation(object sender, EventArgs args) => CancelRequested?.Invoke(this, args);
    protected override void Dispose(bool disposing)
    {
        if (disposing && session != null) session.CancelRequested -= ForwardCancellation;
        base.Dispose(disposing);
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
