using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Upton.Pdm.PreviewAgent;

internal sealed partial class AgentMainForm : Form
{
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(220, 228, 239);
    private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
    private readonly Label status = TextLabel("正在检查连接…", 9, false, Muted);
    private readonly Label machine = TextLabel("", 12);
    private readonly Label runStatus = TextLabel("● 代理运行中", 10, false, Color.SeaGreen);
    private readonly Button pause = ActionButton("暂停接收新任务");
    private readonly Button trayButton = ActionButton("收起到托盘");
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly Timer refreshTimer = new Timer { Interval = 1000 };
    private readonly Label notice = TextLabel("", 9, false, Color.Firebrick);
    private readonly TextBox nameBox = new TextBox();
    private readonly TextBox serverBox = new TextBox();
    private readonly TextBox tokenBox = new TextBox { UseSystemPasswordChar = true };
    private readonly TextBox workBox = new TextBox();
    private readonly NumericUpDown portBox = Number(1, 65535);
    private readonly NumericUpDown timeoutBox = Number(1, 120);
    private readonly NumericUpDown retentionBox = Number(1, 365);
    private readonly CheckBox startup = new CheckBox { Text = "登录 Windows 后自动启动", AutoSize = true };
    private readonly CheckBox showWindow = new CheckBox { Text = "启动时显示主窗口", AutoSize = true };
    private readonly CheckBox dedicatedHost = new CheckBox { Text = "专用转图电脑：自动关闭残留的 SolidWorks 进程", AutoSize = true };
    private readonly ComboBox closeBehavior = Choice("最小化到系统托盘", "完成已接收任务后退出代理");
    private readonly Label connection = TextLabel("尚未测试连接", 10, false, Muted);
    private readonly Label settingsMessage = TextLabel("连接及处理参数在已接收任务完成后生效。", 9, false, Muted);
    private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    private List<AgentBatch> snapshot = new List<AgentBatch>();
    private int tick;
    private bool refreshing, checkingConnection, connected, allowExit, hadPendingSettings;
    private string checkedServer = "";
    private string startupCheckResult;
    private DateTime? startupCheckedUtc;
    private DateTime lastPrunedUtc = DateTime.UtcNow.Date;

    internal AgentMainForm()
    {
        Text = "阿普顿 PLM · 转图服务代理";
        Font = new Font("Microsoft YaHei UI", 10.5F);
        BackColor = Color.FromArgb(245, 248, 252);
        ForeColor = Color.FromArgb(23, 36, 56);
        ClientSize = new Size(1320, 850);
        MinimumSize = new Size(1100, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.White, Padding = new Padding(20, 6, 12, 4) };
        status.Dock = DockStyle.Fill; bottom.Controls.Add(status);
        pageHost.Dock = DockStyle.Fill;
        pageHost.Controls.Add(BuildWorkbench());
        Controls.Add(pageHost); Controls.Add(bottom);
        pause.Click += (_, __) => { Program.TogglePause(); RefreshView(); };
        trayButton.Click += (_, __) => HideToTray();
        tray.Icon = Icon; tray.Text = "阿普顿 PLM 转图服务代理"; tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, __) => RestoreWindow());
        menu.Items.Add("退出代理", null, (_, __) => Program.RequestExit());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, __) => RestoreWindow();
        FormClosing += (_, args) =>
        {
            if (allowExit) return;
            args.Cancel = true;
            if (Program.Options.CloseToTray && !Program.ExitRequested) HideToTray(); else Program.RequestExit();
        };
        FormClosed += (_, __) => { refreshTimer.Stop(); refreshTimer.Dispose(); tray.Visible = false; tray.Dispose(); http.Dispose(); workHttp.Dispose(); };
        HandleCreated += (_, __) => Program.WindowCreated = true;
        VisibleChanged += (_, __) => Program.WindowVisible = Visible;
        Shown += (_, __) =>
        {
            if (!Program.Options.ShowMainWindow) HideToTray();
            refreshTimer.Start(); RefreshView();
        };
        refreshTimer.Tick += async (_, __) => await RefreshAsync();
        LoadSettings(); SelectPage(0);
    }

    internal void RestoreWindow()
    {
        Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate();
    }

    private void HideToTray() { Hide(); ShowInTaskbar = false; }

    internal void SelectPage(int index) { settingsPane.Visible = index == 2; }

    private Panel BuildSettings()
    {
        var page = new Panel();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 10, 0, 0) };
        var save = ActionButton("保存设置", true); save.Dock = DockStyle.Right; save.Width = 120; save.Click += (_, __) => SaveSettings();
        var cancel = ActionButton("取消"); cancel.Dock = DockStyle.Right; cancel.Width = 88; cancel.Click += (_, __) => LoadSettings();
        settingsMessage.Dock = DockStyle.Fill;
        footer.Controls.Add(settingsMessage); footer.Controls.Add(cancel); footer.Controls.Add(save);
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var windows = SettingsGroup("窗口与启动", 206, out var windowFields);
        windowFields.Controls.Add(startup, 0, 0); windowFields.SetColumnSpan(startup, 4);
        windowFields.Controls.Add(showWindow, 0, 1); windowFields.SetColumnSpan(showWindow, 4);
        AddField(windowFields, "关闭窗口时", closeBehavior, 0, 2); windowFields.SetColumnSpan(closeBehavior, 3);
        var trayHint = TextLabel("双击Windows右下角托盘图标可恢复主窗口，退出代理请使用托盘菜单。", 9, false, Muted); trayHint.Dock = DockStyle.Fill;
        windowFields.Controls.Add(trayHint, 0, 3); windowFields.SetColumnSpan(trayHint, 4);
        var processing = SettingsGroup("处理参数", 198, out var processFields);
        AddField(processFields, "转换超时（分钟）", timeoutBox, 0, 0);
        AddField(processFields, "记录保留（天）", retentionBox, 2, 0);
        var folder = new Panel { Dock = DockStyle.Fill, Height = 30 };
        var browse = ActionButton("浏览"); browse.Dock = DockStyle.Right; browse.Width = 65; browse.Height = 28;
        browse.Click += (_, __) => { using (var dialog = new FolderBrowserDialog { Description = "选择转图工作目录", SelectedPath = Directory.Exists(workBox.Text) ? workBox.Text : "" }) if (dialog.ShowDialog(this) == DialogResult.OK) workBox.Text = dialog.SelectedPath; };
        workBox.Dock = DockStyle.Fill; folder.Controls.Add(workBox); folder.Controls.Add(browse);
        AddField(processFields, "工作目录", folder, 0, 1); processFields.SetColumnSpan(folder, 3);
        var retentionHint = TextLabel("按批次保留处理结果和诊断记录；修改端口或令牌后，还需同步PLM中的转图服务器设置。", 9, false, Muted); retentionHint.Dock = DockStyle.Fill;
        processFields.Controls.Add(retentionHint, 0, 2); processFields.SetColumnSpan(retentionHint, 4);
        processFields.Controls.Add(dedicatedHost, 0, 3); processFields.SetColumnSpan(dedicatedHost, 4);
        var server = SettingsGroup("服务器连接", 216, out var serverFields);
        AddField(serverFields, "代理名称", nameBox, 0, 0); AddField(serverFields, "PLM 服务器地址", serverBox, 2, 0);
        AddField(serverFields, "本机代理端口", portBox, 0, 1);
        var tokenPanel = new Panel { Dock = DockStyle.Fill, Height = 30 };
        var visible = new CheckBox { Text = "显示", Dock = DockStyle.Right, Width = 61 };
        visible.CheckedChanged += (_, __) => tokenBox.UseSystemPasswordChar = !visible.Checked;
        tokenBox.Dock = DockStyle.Fill; tokenPanel.Controls.Add(tokenBox); tokenPanel.Controls.Add(visible);
        AddField(serverFields, "访问令牌", tokenPanel, 2, 1);
        var test = ActionButton("测试连接"); test.Width = 110; test.Height = 31;
        test.Click += async (_, __) => await ProbeConnectionAsync(serverBox.Text.Trim(), true);
        serverFields.Controls.Add(connection, 1, 2); serverFields.Controls.Add(test, 3, 2);
        body.Controls.Add(windows); body.Controls.Add(Spacer()); body.Controls.Add(processing); body.Controls.Add(Spacer()); body.Controls.Add(server);
        page.Controls.Add(body); page.Controls.Add(footer); return page;
    }

    private async Task RefreshAsync()
    {
        if (refreshing || IsDisposed) return;
        refreshing = true;
        try
        {
            await Program.ApplyPendingSettingsAsync();
            if (DateTime.UtcNow.Date > lastPrunedUtc)
            {
                await Task.Run(() => Program.Journal.Prune(Program.Options.HistoryRetentionDays));
                lastPrunedUtc = DateTime.UtcNow.Date;
            }
            if (IsDisposed) return;
            RefreshView();
            if (++tick % 5 == 0 || checkedServer != Program.Options.ServerUrl) _ = ProbeConnectionAsync(Program.Options.ServerUrl, false);
            if (Program.ExitRequested && !Program.Busy) { allowExit = true; Close(); }
        }
        catch (Exception error) { Program.Journal.Diagnostic(error.ToString()); notice.Text = "界面状态暂未刷新，请稍后重试，转图记录仍保留。"; }
        finally { refreshing = false; }
    }

    private void RefreshView()
    {
        snapshot = Program.Journal.Snapshot(); RenderWorkbench();
        status.Text = (connected ? "● 已连接服务器" : "● " + (string.IsNullOrWhiteSpace(Program.Options.ServerUrl) ? "未配置服务器" : "服务器暂未连接"))
            + "    |    " + (string.IsNullOrWhiteSpace(Program.Options.ServerUrl) ? "请在代理设置中填写地址" : Program.Options.ServerUrl)
            + "    |    本机端口 " + Program.Options.Port + (Program.Listening ? "" : "（未监听）");
        status.ForeColor = connected && Program.Listening ? Color.SeaGreen : Muted;
        runStatus.Text = Program.ExitRequested ? "● 正在完成任务后退出" : !Program.Listening ? "● 代理未开始监听" : Program.Paused ? "● 已暂停接收" : "● 代理运行中";
        runStatus.ForeColor = Program.Paused || !Program.Listening ? Color.DarkOrange : Color.SeaGreen;
        machine.Text = Program.Options.Name;
        tray.Text = "阿普顿PLM转图代理 · " + (Program.ExitRequested ? "等待退出" : Program.Busy ? "正在处理" : Program.Paused ? "已暂停" : "空闲");
        if (Program.SettingsPending) { settingsMessage.Text = "设置等待生效：已接收任务完成后自动保存。"; hadPendingSettings = true; }
        else if (hadPendingSettings) { settingsMessage.Text = Program.RuntimeError ?? "设置已保存并生效。"; hadPendingSettings = false; }
    }

    private void LoadSettings()
    {
        var options = Program.Options;
        nameBox.Text = options.Name; serverBox.Text = options.ServerUrl; tokenBox.Text = options.Token; workBox.Text = options.WorkRoot;
        portBox.Value = Math.Max(portBox.Minimum, Math.Min(portBox.Maximum, options.Port));
        timeoutBox.Value = Math.Max(timeoutBox.Minimum, Math.Min(timeoutBox.Maximum, options.TimeoutMinutes));
        retentionBox.Value = Math.Max(retentionBox.Minimum, Math.Min(retentionBox.Maximum, options.HistoryRetentionDays));
        startup.Checked = options.StartWithWindows; showWindow.Checked = options.ShowMainWindow; closeBehavior.SelectedIndex = options.CloseToTray ? 0 : 1;
        dedicatedHost.Checked = options.DedicatedConversionHost;
        settingsMessage.Text = "连接及处理参数在已接收任务完成后保存并生效。";
    }

    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(nameBox.Text)) { settingsMessage.Text = "请填写代理名称。"; return; }
        if (!Uri.TryCreate(serverBox.Text.Trim(), UriKind.Absolute, out var address) || (address.Scheme != "http" && address.Scheme != "https") || address.UserInfo.Length != 0)
        { settingsMessage.Text = "请填写有效的PLM服务器地址，例如 http://192.168.2.8:5173。"; return; }
        try { if (!Path.IsPathRooted(workBox.Text) || Path.GetFullPath(workBox.Text).Length < 4) throw new ArgumentException(); }
        catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { settingsMessage.Text = "请填写有效的工作目录绝对路径。"; return; }
        var next = Program.Options.Copy();
        next.Name = nameBox.Text.Trim(); next.ServerUrl = serverBox.Text.Trim().TrimEnd('/'); next.Token = tokenBox.Text;
        next.WorkRoot = Path.GetFullPath(workBox.Text); next.Port = (int)portBox.Value; next.TimeoutMinutes = (int)timeoutBox.Value; next.HistoryRetentionDays = (int)retentionBox.Value;
        next.StartWithWindows = startup.Checked; next.ShowMainWindow = showWindow.Checked; next.CloseToTray = closeBehavior.SelectedIndex == 0;
        next.DedicatedConversionHost = dedicatedHost.Checked;
        Program.QueueSettings(next); settingsMessage.Text = "设置等待生效：已接收任务完成后自动保存。";
    }

    private async Task ProbeConnectionAsync(string address, bool manual)
    {
        if (checkingConnection) return;
        checkingConnection = true;
        var success = false;
        try
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                var text = await http.GetStringAsync(address.TrimEnd('/') + "/health");
                var health = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                success = health != null && health.TryGetValue("status", out var value) && string.Equals(Convert.ToString(value), "ok", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException || error is ArgumentException || error is InvalidOperationException) { Program.Journal.Diagnostic(error.ToString()); }
        finally { checkingConnection = false; }
        if (IsDisposed) return;
        if (address == Program.Options.ServerUrl) { connected = success; checkedServer = address; }
        if (manual || serverBox.Text.Trim().TrimEnd('/') == address.TrimEnd('/'))
        {
            connection.Text = success ? "● 连接正常" : "● 无法连接，请检查地址和网络";
            connection.ForeColor = success ? Color.SeaGreen : Color.Firebrick;
        }
    }

    private void CopyLogs(IEnumerable<AgentLog> items)
    {
        try
        {
            var text = string.Join(Environment.NewLine, items.Select(item => item.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " [" + item.Level + "] " + item.Message));
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板暂时不可用，请稍后重试。", "复制日志", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "未提供" : value;
    private static string Elapsed(DateTime start, DateTime? end) => TimeSpan.FromSeconds(Math.Max(0, ((end ?? DateTime.UtcNow) - start).TotalSeconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    private static DateTimePicker DatePicker(DateTime date) => new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Value = date, Width = 128 };
    private static NumericUpDown Number(int minimum, int maximum) => new NumericUpDown { Minimum = minimum, Maximum = maximum, Width = 150 };
    private static ComboBox Choice(params string[] values)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 }; box.Items.AddRange(values); box.SelectedIndex = 0; return box;
    }
    private static Label TextLabel(string text, float size = 10, bool bold = false, Color? color = null) => new Label
    { Text = text, Font = new Font("Microsoft YaHei UI", Math.Max(10.5F, size), bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color ?? Color.FromArgb(23, 36, 56), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Height = 28, AutoSize = false };
    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, Height = 36, Width = 120, FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Blue, Cursor = Cursors.Hand, Margin = new Padding(5, 0, 0, 0) };
        button.FlatAppearance.BorderColor = primary ? Blue : Border; return button;
    }
    private static Panel Card()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        panel.Paint += (_, args) => { using (var pen = new Pen(Border)) args.Graphics.DrawRectangle(pen, 0, 0, panel.ClientSize.Width - 1, panel.ClientSize.Height - 1); };
        return panel;
    }
    private static Panel Spacer() => new Panel { Dock = DockStyle.Top, Height = 12 };
    private static Panel SettingsGroup(string heading, int height, out TableLayoutPanel fields)
    {
        var panel = Card(); panel.Dock = DockStyle.Top; panel.Height = height; panel.Padding = new Padding(18, 10, 18, 10);
        var label = TextLabel(heading, 12, true); label.Dock = DockStyle.Top; label.Height = 34;
        fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 4; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.Controls.Add(fields); panel.Controls.Add(label); return panel;
    }
    private static void AddField(TableLayoutPanel layout, string label, Control control, int column, int row)
    {
        var caption = TextLabel(label, 9); caption.Dock = DockStyle.Fill; caption.Margin = new Padding(0, 0, 8, 0);
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 5, 15, 4); layout.Controls.Add(caption, column, row); layout.Controls.Add(control, column + 1, row);
    }
    private static DataGridView Grid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, GridColor = Border, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 38, AllowUserToResizeRows = false, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(240, 245, 251), ForeColor = Color.FromArgb(40, 55, 76), Padding = new Padding(8, 0, 5, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Color.FromArgb(33, 47, 69), SelectionBackColor = Color.FromArgb(222, 237, 255), SelectionForeColor = Color.FromArgb(22, 39, 70), Padding = new Padding(8, 0, 5, 0) };
        grid.RowTemplate.Height = 34;
        grid.CellFormatting += (_, args) =>
        {
            var value = Convert.ToString(args.Value);
            if (value == "成功") args.CellStyle.ForeColor = Color.SeaGreen;
            else if (value == "失败") args.CellStyle.ForeColor = Color.Firebrick;
            else if (value == "处理中" || value == "部分成功") args.CellStyle.ForeColor = Color.DarkOrange;
        };
        return grid;
    }
}
