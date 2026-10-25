using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

// The window owns a separate message loop. SolidWorks COM work stays on its original STA.
internal sealed class ArchiveSession : IDisposable
{
    private static ArchiveSession current;
    private readonly CancellationTokenSource cancellation;
    private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private ArchiveSessionWindow window;
    private int finished;
    private string resultTitle;
    private string resultMessage;
    public static ArchiveSession Current => Volatile.Read(ref current);
    public Task Ready => ready.Task;
    public Task Closed => closed.Task;
    public CancellationToken Token => cancellation.Token;
    public bool IsClosed => closed.Task.IsCompleted;
    public bool IsFinished => Volatile.Read(ref finished) != 0;
    public bool WorkflowStarted { get; set; }
    public event EventHandler CancelRequested;

    private ArchiveSession(CancellationToken token) => cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

    public static ArchiveSession Start(IntPtr owner, CancellationToken token)
    {
        var session = new ArchiveSession(token);
        var existing = Interlocked.CompareExchange(ref current, session, null);
        if (existing != null)
        {
            session.cancellation.Dispose();
            return existing;
        }
        var nativeOwner = new NativeOwner(owner);
        var thread = new Thread(() =>
        {
            try
            {
                using (var form = new ArchiveSessionWindow(session))
                {
                    session.window = form;
                    form.Shown += (_, __) =>
                    {
                        form.Report("正在读取存档所需信息…", null, 0, 0);
                        form.Update(); session.ready.TrySetResult(true);
                    };
                    form.Show(nativeOwner);
                    Application.Run(form);
                }
            }
            catch (Exception error) { session.ready.TrySetException(error); }
            finally
            {
                nativeOwner.Restore();
                Interlocked.CompareExchange(ref current, null, session);
                session.closed.TrySetResult(true);
            }
        }) { IsBackground = true, Name = "UPLM 存档窗口" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return session;
    }

    public void Dispose() => cancellation.Dispose();
    public void CloseForShutdown()
    {
        cancellation.Cancel();
        Post(() => { Interlocked.Exchange(ref finished, 1); window.Finish("存档操作已停止。", "插件正在关闭，请保留本地文件，重新连接后核对服务器结果。"); window.Close(); });
    }

    private sealed class NativeOwner : IWin32Window
    {
        private readonly SynchronizationContext context;
        private readonly bool wasEnabled;
        public NativeOwner(IntPtr handle)
        {
            var root = handle == IntPtr.Zero ? handle : GetAncestor(handle, 2);
            Handle = root == IntPtr.Zero ? handle : root;
            context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            wasEnabled = Handle != IntPtr.Zero && IsWindowEnabled(Handle);
            // Disable/restore the CAD owner on its own thread, never via a cross-STA modal loop.
            if (wasEnabled) EnableWindow(Handle, false);
        }
        public IntPtr Handle { get; }
        public void Restore() { if (wasEnabled) context.Post(_ => EnableWindow(Handle, true), null); }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    }

    public T Invoke<T>(Func<T> action)
    {
        Ready.GetAwaiter().GetResult();
        if (IsClosed) throw new OperationCanceledException(Token);
        return window.InvokeRequired ? (T)window.Invoke(action) : action();
    }

    private void Post(Action action)
    {
        if (IsClosed) return;
        if (ready.Task.IsFaulted || ready.Task.IsCanceled) return;
        if (!ready.Task.IsCompleted) { _ = Ready.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) Post(action); }, TaskScheduler.Default); return; }
        try
        {
            if (window.InvokeRequired) window.BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { /* The user closed the finished session. */ }
    }

    public bool BringToFront()
    {
        if (IsClosed) return false;
        Post(() => { window.BringToFront(); window.Activate(); });
        return true;
    }

    public void SetStage(string message) => Post(() => { if (!IsFinished) window.Report(message, null, 0, 0); });
    public void ReportFile(int completed, int total, string fileName, string message) =>
        Post(() => { if (!IsFinished) window.Report(message, fileName, completed, total); });

    public void TrackFile(CadTreeNode node, string message, string versionText = null) =>
        Post(() => window.TrackFile(node, message, versionText));

    public void ReportPreparedFiles(IReadOnlyList<BatchOperationItem> items) =>
        Invoke(() => { window.ReportPreparedFiles(items); return true; });

    public void Complete(string message, bool failed = false, bool warnings = false)
    {
        // Queue the result after all already-queued progress entries.
        Post(() =>
        {
            if (IsFinished) return;
            PluginDiagnosticLog.Record(failed ? "失败" : "结果", message);
            resultTitle = failed ? "存档未完成，请查看原因和处理建议。" : warnings ? "存档已完成，请查看处理提醒。" : "存档操作已完成。";
            resultMessage = message;
            if (WorkflowStarted) window.Report("正在结束本次操作并核对清理状态…", null, 0, 0);
            else { Interlocked.Exchange(ref finished, 1); window.Finish(resultTitle, resultMessage); }
        });
    }
    public void Fail(string message) => Complete(message, true);
    public void FinishIfRunning() => Post(() =>
    {
        if (Interlocked.Exchange(ref finished, 1) == 0)
            window.Finish(resultTitle ?? "本次存档操作已停止。", resultMessage
                ?? "本地文件已保留。请查看本次过程记录；如已开始服务器提交，请刷新项目核对最终结果。");
    });

    public void RequestCancellation()
    {
        if (cancellation.IsCancellationRequested || IsFinished) return;
        cancellation.Cancel();
        Post(() => window.Report("正在取消，等待当前读取返回并核对服务器结果后安全停止…", null, 0, 0));
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public DialogResult ShowInline(Form form, bool selection = false) => Invoke(() => window.ShowInline(form, selection));
    public T ShowDialog<TForm, T>(Func<TForm> create, Func<TForm, DialogResult, T> capture) where TForm : Form => Invoke(() =>
    {
        using (var form = create()) return capture(form, window.ShowInline(form));
    });
    public DialogResult Confirm(string message, string caption, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton) => Invoke(() =>
    {
        window.RecordConfirmation(caption, message);
        using (var form = CreateQuestion(message, caption, buttons, defaultButton)) return window.ShowInline(form);
    });

    private static Form CreateQuestion(string message, string caption, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
    {
        var form = new Form { Text = caption, Size = new Size(540, 360), Font = new Font("Microsoft YaHei UI", 9F) };
        var text = new TextBox { Text = message, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var results = buttons == MessageBoxButtons.YesNoCancel ? new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }
            : buttons == MessageBoxButtons.YesNo ? new[] { DialogResult.Yes, DialogResult.No }
            : buttons == MessageBoxButtons.OKCancel ? new[] { DialogResult.OK, DialogResult.Cancel }
            : buttons == MessageBoxButtons.RetryCancel ? new[] { DialogResult.Retry, DialogResult.Cancel }
            : buttons == MessageBoxButtons.AbortRetryIgnore ? new[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore }
            : new[] { DialogResult.OK };
        var controls = results.Select(result => new Button { Text = result == DialogResult.Yes ? "是" : result == DialogResult.No ? "否"
            : result == DialogResult.Cancel ? "取消" : result == DialogResult.Retry ? "重试" : result == DialogResult.Abort ? "中止" : result == DialogResult.Ignore ? "忽略" : "确定",
            DialogResult = result, Width = 82, Height = 30 }).ToArray();
        foreach (var button in controls.Reverse()) actions.Controls.Add(button);
        form.Controls.Add(text); form.Controls.Add(actions);
        form.AcceptButton = controls[Math.Min(controls.Length - 1, (int)defaultButton / 256)];
        form.CancelButton = controls.Last();
        return form;
    }
}

internal sealed class ArchiveSessionWindow : Form
{
    private readonly ArchiveSession session;
    private readonly Label stage = new Label { Dock = DockStyle.Top, Height = 42, Text = "正在准备存档窗口…" };
    private readonly Label file = new Label { Dock = DockStyle.Top, Height = 28, AutoEllipsis = true, Visible = false };
    private readonly Label elapsed = new Label { Dock = DockStyle.Top, Height = 28, Text = "处理信息　　总耗时 00:00:00" };
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 4, Style = ProgressBarStyle.Marquee };
    private readonly TextBox history = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = int.MaxValue };
    private readonly Panel selection = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel information = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
        Padding = new Padding(8, 0, 0, 0), Margin = Padding.Empty };
    private readonly Panel processRecords = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Panel confirmation = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel confirmationActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
    private readonly Label confirmationTitle = new Label { Dock = DockStyle.Top, Height = 26, Text = "确认区：暂无待确认事项" };
    private readonly Button stop = new Button { Text = "取消存档", Width = 80, Height = 28, Dock = DockStyle.Top };
    private readonly Panel confirmationHost = new Panel { Dock = DockStyle.Top, Height = 290, Visible = false };
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private string lastEntry;
    private bool finished;
    private Form selectionForm;
    public int RefreshTicks { get; private set; }

    public ArchiveSessionWindow(ArchiveSession session)
    {
        this.session = session;
        Text = "UPLM 存档"; Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(1240, 860); MinimumSize = new Size(1200, 820);
        ShowInTaskbar = false; TopMost = true;
        DialogWindowSizing.CenterOnOwnerScreen(this);
        confirmationHost.Controls.Add(confirmation); confirmationHost.Controls.Add(confirmationActions); confirmationHost.Controls.Add(confirmationTitle);
        var informationHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        informationHeader.Controls.Add(new Label { Text = "过程记录（完整保留）", Dock = DockStyle.Bottom, Height = 26 });
        progress.Dock = DockStyle.Bottom;
        informationHeader.Controls.Add(progress); informationHeader.Controls.Add(file);
        informationHeader.Controls.Add(stage); informationHeader.Controls.Add(elapsed); informationHeader.Controls.Add(stop);
        processRecords.Controls.Add(history); processRecords.Controls.Add(confirmationHost);
        information.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        information.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        information.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        information.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        information.Controls.Add(informationHeader, 0, 0); information.Controls.Add(processRecords, 0, 1);
        information.Resize += (_, __) => AlignProcessRecords();
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(4) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66.66667F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33333F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selection.Margin = Padding.Empty;
        body.Controls.Add(selection, 0, 0); body.Controls.Add(information, 1, 0);
        selection.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "正在读取图档与结构，请稍候。检查完成后在此选择项目和图档。" });
        ArchiveCommandAppearance.Apply(stop, Color.FromArgb(230, 126, 34));
        stop.Click += (_, __) => CancelOrClose();
        Controls.Add(body);
        timer.Tick += (_, __) =>
        {
            RefreshTicks++;
            elapsed.Text = "处理信息　　总耗时 " + clock.Elapsed.ToString(@"hh\:mm\:ss");
        };
        timer.Start();
        FormClosing += (_, args) =>
        {
            if (finished || args.CloseReason != CloseReason.UserClosing) return;
            session.RequestCancellation(); stop.Enabled = false; args.Cancel = true;
        };
    }

    private void AlignProcessRecords()
    {
        var batch = selection.Controls.OfType<BatchOperationDialog>().FirstOrDefault(form => form.Visible);
        if (batch == null || !batch.IsHandleCreated) return;
        var bounds = information.RectangleToClient(batch.FileListContainer.RectangleToScreen(batch.FileListContainer.ClientRectangle));
        var top = Math.Max(0, bounds.Top);
        var bottom = Math.Max(0, information.ClientSize.Height - bounds.Bottom);
        if (information.RowStyles[0].Height == top && information.RowStyles[2].Height == bottom) return;
        information.RowStyles[0].Height = top;
        information.RowStyles[2].Height = bottom;
    }

    public void Report(string message, string fileName, int completed, int total)
    {
        if (finished) return;
        stage.Text = session.Token.IsCancellationRequested && (message == null || !message.Contains("取消"))
            ? "正在取消，等待当前步骤安全结束：" + message : message ?? string.Empty;
        file.Text = string.IsNullOrWhiteSpace(fileName) ? "" : "当前图档：" + fileName;
        file.Visible = !string.IsNullOrWhiteSpace(fileName);
        (selectionForm as BatchOperationDialog)?.SetWorkflowFile(fileName, message);
        if (total > 0) { progress.Style = ProgressBarStyle.Continuous; progress.Maximum = Math.Max(1, total); progress.Value = Math.Max(0, Math.Min(total, completed)); file.Text += "　已完成 " + Math.Min(completed, total) + " / " + total; }
        else progress.Style = ProgressBarStyle.Marquee;
        Append(message + (string.IsNullOrWhiteSpace(fileName) ? "" : "　" + fileName));
    }

    private void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == lastEntry) return;
        lastEntry = message;
        history.AppendText(DateTime.Now.ToString("HH:mm:ss") + "　" + message + Environment.NewLine);
    }
    public void RecordConfirmation(string caption, string message) => Append(caption + Environment.NewLine + message);

    public void Finish(string title, string message)
    {
        finished = true; clock.Stop(); stage.Text = title; file.Text = "";
        Append(title); Append(message); progress.Style = ProgressBarStyle.Continuous;
        stop.Text = "关闭"; stop.Enabled = true;
        (selectionForm as BatchOperationDialog)?.FinishWorkflow();
        confirmationTitle.Text = "确认区：本次操作已结束";
    }

    public DialogResult ShowInline(Form form, bool isSelection = false)
    {
        if (session.Token.IsCancellationRequested) return DialogResult.Cancel;
        var panel = isSelection ? selection : confirmation;
        var previous = panel.Controls.Cast<Control>().ToArray();
        var previousActions = confirmationActions.Controls.Cast<Control>().ToArray();
        var previousAccept = AcceptButton; var previousCancel = CancelButton;
        var previousSelectionEnabled = selection.Enabled;
        if (isSelection && form is BatchOperationDialog) stop.Hide();
        if (!isSelection) selection.Enabled = false;
        confirmationHost.Visible = !isSelection;
        foreach (var control in previous) control.Hide();
        if (!isSelection) foreach (var control in previousActions) control.Hide();
        form.TopMost = false; form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None;
        form.MinimumSize = Size.Empty;
        form.Dock = DockStyle.Fill;
        if (!isSelection)
        {
            form.Dock = DockStyle.Top; form.Height = Math.Max(220, form.Height);
            confirmationTitle.Text = "请确认：" + form.Text;
            Append("等待确认：" + form.Text);
        }
        panel.Controls.Add(form);
        if (isSelection && form is BatchOperationDialog batchLayout)
        {
            batchLayout.FileListContainer.LocationChanged += (_, __) => AlignProcessRecords();
            batchLayout.FileListContainer.SizeChanged += (_, __) => AlignProcessRecords();
        }
        var done = false; var result = DialogResult.Cancel;
        var buttons = EnumerateControls(form).OfType<Button>().Where(button => button.DialogResult != DialogResult.None).ToArray();
        var parents = buttons.ToDictionary(button => button, button => button.Parent);
        var closeHandlers = new Dictionary<Button, EventHandler>();
        foreach (var button in buttons)
        {
            if (!isSelection) confirmationActions.Controls.Add(button);
            EventHandler handler = (_, __) => { form.DialogResult = button.DialogResult; form.Close(); };
            closeHandlers[button] = handler; button.Click += handler;
        }
        FormClosingEventHandler closing = (_, args) =>
        {
            if (args.Cancel) return;
            result = form.DialogResult; args.Cancel = true; done = true;
        };
        form.FormClosing += closing;
        AcceptButton = form.AcceptButton; CancelButton = form.CancelButton;
        form.Show();
        AlignProcessRecords();
        while (!done && !session.Token.IsCancellationRequested && !IsDisposed)
        {
            // This nested pump belongs only to the presentation STA; it never dispatches CAD work.
            Application.DoEvents();
            Thread.Sleep(10);
        }
        form.FormClosing -= closing;
        foreach (var pair in closeHandlers) pair.Key.Click -= pair.Value;
        AcceptButton = previousAccept; CancelButton = previousCancel;
        if (isSelection)
        {
            selectionForm?.Dispose(); selectionForm = form;
            if (form is BatchOperationDialog batch)
            {
                batch.SetWorkflowBusy();
                batch.CancelOperationButton.Click += (_, __) => CancelOrClose();
                stop.Hide();
            }
            else form.Enabled = false;
        }
        else
        {
            panel.Controls.Remove(form);
            foreach (var button in buttons) parents[button].Controls.Add(button);
            foreach (var control in previous) control.Show();
            foreach (var control in previousActions) control.Show();
            selection.Enabled = previousSelectionEnabled;
            confirmationTitle.Text = "确认区：暂无待确认事项";
            confirmationHost.Hide();
            Append("确认结果：" + form.Text + "　" + ResultText(result));
        }
        return session.Token.IsCancellationRequested ? DialogResult.Cancel : result;
    }

    private void CancelOrClose()
    {
        if (finished) Close();
        else
        {
            session.RequestCancellation(); stop.Enabled = false;
            if (selectionForm is BatchOperationDialog batch) batch.CancelOperationButton.Enabled = false;
        }
    }

    public void TrackFile(CadTreeNode node, string message, string versionText)
    {
        (selectionForm as BatchOperationDialog)?.SetWorkflowFile(node, message, versionText);
        Append(node.FileName + "：" + message);
    }

    public void ReportPreparedFiles(IReadOnlyList<BatchOperationItem> items)
    {
        if (!(selectionForm is BatchOperationDialog batch)) return;
        batch.SetPreparedWorkflow(items);
        Report("检查与准备完成，正在按已确认的范围自动存档，无需再次操作。", null, 0, 0);
    }

    private static string ResultText(DialogResult result) => result == DialogResult.OK || result == DialogResult.Yes ? "已确认"
        : result == DialogResult.No ? "选择否" : "已取消";
    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in EnumerateControls(control)) yield return child;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); selectionForm?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class ArchiveSelection
{
    public DialogResult Result;
    public BatchOperationKind Operation;
    public IReadOnlyList<BatchOperationItem> SelectedItems;
    public Guid? SelectedProjectId;
    public string SelectedProjectDisplay;
    public string ChangeNote;
    public bool KeepEditing;
    public string HandoffTo;
    public bool ArchiveToStandardLibrary;
    public int StandardLibraryDrawingType;
    public string AdditionalDrawingFolder;

    public static ArchiveSelection Read(BatchOperationDialog dialog, DialogResult result) => new ArchiveSelection
    {
        Result = result, Operation = dialog.Operation, SelectedItems = dialog.SelectedItems,
        SelectedProjectId = dialog.SelectedProjectId, SelectedProjectDisplay = dialog.SelectedProjectDisplay,
        ChangeNote = dialog.ChangeNote, KeepEditing = dialog.KeepEditing, HandoffTo = dialog.HandoffTo,
        ArchiveToStandardLibrary = dialog.ArchiveToStandardLibrary, StandardLibraryDrawingType = dialog.StandardLibraryDrawingType,
        AdditionalDrawingFolder = dialog.AdditionalDrawingFolder
    };
}
