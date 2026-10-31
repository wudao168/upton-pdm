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

internal sealed class AgentMainForm : Form
{
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(220, 228, 239);
    private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
    private readonly Panel[] pages = new Panel[3];
    private readonly Button[] navigation = new Button[3];
    private readonly Label title = TextLabel("首页", 21, true);
    private readonly Label subtitle = TextLabel("查看当前任务和实时处理日志", 10, false, Muted);
    private readonly Label status = TextLabel("正在检查连接…", 9, false, Muted);
    private readonly Label machine = TextLabel("", 12);
    private readonly Label runStatus = TextLabel("● 代理运行中", 10, false, Color.SeaGreen);
    private readonly Button pause = ActionButton("暂停接收新任务");
    private readonly Button trayButton = ActionButton("收起到托盘");
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly Timer refreshTimer = new Timer { Interval = 1000 };
    private readonly Label[] metrics = new Label[4];
    private readonly Label currentTitle = TextLabel("当前没有待处理任务", 15, true);
    private readonly Label currentMeta = TextLabel("", 9, false, Muted);
    private readonly Label currentStage = TextLabel("等待服务器发起转图请求", 10, false, Muted);
    private readonly Label currentCount = TextLabel("", 10);
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Height = 12 };
    private readonly Label notice = TextLabel("", 9, false, Color.Firebrick);
    private readonly DataGridView logGrid = Grid();
    private readonly ComboBox logLevel = Choice("全部级别", "信息", "成功", "提醒", "失败");
    private readonly CheckBox autoScroll = new CheckBox { Text = "自动滚动", Checked = true, AutoSize = true };
    private readonly DataGridView historyGrid = Grid();
    private readonly DataGridView filesGrid = Grid();
    private readonly DateTimePicker dateFrom = DatePicker(DateTime.Today.AddDays(-7));
    private readonly DateTimePicker dateTo = DatePicker(DateTime.Today);
    private readonly ComboBox historyStatus = Choice("全部状态", "排队中", "处理中", "成功", "部分成功", "失败");
    private readonly TextBox search = new TextBox { Width = 220 };
    private readonly Label historySummary = TextLabel("", 10);
    private readonly Label historyPage = TextLabel("", 9, false, Muted);
    private readonly Label detailTitle = TextLabel("选择一批任务查看详情", 11, true);
    private readonly Label detailMeta = TextLabel("", 9, false, Muted);
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
    private DateTime filterFrom = DateTime.Today.AddDays(-7), filterTo = DateTime.Today;
    private string filterState = "全部状态", filterText = "", historyStamp = "", logStamp = "";
    private int pageIndex, tick;
    private Guid? selectedBatch;
    private bool refreshing, checkingConnection, connected, renderingHistory, allowExit, hadPendingSettings;
    private string checkedServer = "";
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
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Color.FromArgb(240, 246, 253), Padding = new Padding(12, 18, 12, 0) };
        machine.Text = "▣  " + Program.Options.Name; machine.SetBounds(18, 22, 180, 34); sidebar.Controls.Add(machine);
        var labels = new[] { "首页", "处理记录", "代理设置" };
        var icons = new[] { "⌂", "▤", "⚙" };
        for (var index = 0; index < 3; index++)
        {
            var target = index;
            var button = ActionButton(icons[index] + "    " + labels[index]);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(15, 0, 0, 0);
            button.SetBounds(8, 77 + index * 58, 194, 48);
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, __) => SelectPage(target);
            navigation[index] = button; sidebar.Controls.Add(button);
        }
        var sidebarFooter = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(15, 8, 0, 10) };
        runStatus.Dock = DockStyle.Top; runStatus.Height = 32;
        var version = TextLabel("版本 " + typeof(Program).Assembly.GetName().Version.ToString(3), 9, false, Muted); version.Dock = DockStyle.Bottom;
        sidebarFooter.Controls.Add(runStatus); sidebarFooter.Controls.Add(version); sidebar.Controls.Add(sidebarFooter);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18) };
        var header = new Panel { Dock = DockStyle.Top, Height = 80 };
        title.SetBounds(0, 0, 430, 39); subtitle.SetBounds(0, 43, 670, 27);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 355, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 3, 0, 0) };
        trayButton.Width = 124; pause.Width = 196;
        actions.Controls.Add(trayButton); actions.Controls.Add(pause);
        header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(actions);
        content.Controls.Add(pageHost); content.Controls.Add(header);
        Controls.Add(content); Controls.Add(sidebar); Controls.Add(bottom);
        pages[0] = BuildHome(); pages[1] = BuildHistory(); pages[2] = BuildSettings();
        foreach (var page in pages) { page.Dock = DockStyle.Fill; pageHost.Controls.Add(page); }
        pause.Click += (_, __) => { Program.TogglePause(); RenderHome(); };
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
        FormClosed += (_, __) => { refreshTimer.Stop(); refreshTimer.Dispose(); tray.Visible = false; tray.Dispose(); http.Dispose(); };
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

    internal void SelectPage(int index)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == index;
            navigation[i].BackColor = i == index ? Color.FromArgb(221, 235, 255) : Color.FromArgb(240, 246, 253);
            navigation[i].ForeColor = i == index ? Blue : ForeColor;
            navigation[i].Font = new Font(Font, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        pages[index].BringToFront();
        title.Text = new[] { "首页", "处理记录", "代理设置" }[index];
        subtitle.Text = new[] { "查看当前任务和实时处理日志", "每次发起记为一批，可展开查看批次内的文件处理结果", "管理服务器连接与代理运行方式" }[index];
        pause.Visible = index == 0; trayButton.Visible = index == 0;
    }

    private Panel BuildHome()
    {
        var page = new Panel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 216));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        var captions = new[] { "今日发起", "成功", "失败 / 部分成功", "处理中 / 排队" };
        var colors = new[] { Blue, Color.SeaGreen, Color.Firebrick, Color.DarkOrange };
        for (var i = 0; i < 4; i++)
        {
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var card = Card(); card.Padding = new Padding(18, 11, 8, 9); card.Margin = new Padding(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 12);
            var caption = TextLabel(captions[i], 10, false, Muted); caption.Dock = DockStyle.Top; caption.Height = 25;
            metrics[i] = TextLabel("0 次", 22, true, colors[i]); metrics[i].Dock = DockStyle.Bottom; metrics[i].Height = 40;
            card.Controls.Add(caption); card.Controls.Add(metrics[i]); stats.Controls.Add(card, i, 0);
        }
        var current = Card(); current.Padding = new Padding(18, 12, 18, 12); current.Margin = Padding.Empty;
        var heading = TextLabel("当前处理任务", 13, true); heading.Dock = DockStyle.Top; heading.Height = 32;
        var taskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 4, 0, 0) };
        foreach (var height in new[] { 30, 37, 29, 18, 32 }) taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        taskLayout.Controls.Add(currentMeta, 0, 0); taskLayout.Controls.Add(currentTitle, 0, 1); taskLayout.Controls.Add(currentStage, 0, 2);
        var progressPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 3, 0, 3) }; progressPanel.Controls.Add(progress);
        taskLayout.Controls.Add(progressPanel, 0, 3); taskLayout.Controls.Add(currentCount, 0, 4);
        foreach (var label in new[] { currentMeta, currentTitle, currentStage, currentCount }) label.Dock = DockStyle.Fill;
        current.Controls.Add(taskLayout); current.Controls.Add(heading);
        notice.Dock = DockStyle.Fill;
        var logs = Card(); logs.Padding = new Padding(1); logs.Margin = Padding.Empty;
        var logHeader = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(14, 8, 8, 6) };
        var logTitle = TextLabel("实时处理日志", 12, true); logTitle.Dock = DockStyle.Left; logTitle.Width = 220;
        var logActions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 380, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var copy = ActionButton("复制日志"); copy.Width = 100; copy.Height = 31;
        copy.Click += (_, __) => CopyLogs(Program.Journal.RecentLogs());
        autoScroll.Margin = new Padding(10, 7, 10, 0); logLevel.Width = 126;
        logLevel.SelectedIndexChanged += (_, __) => { logStamp = ""; RenderLogs(); };
        logActions.Controls.Add(copy); logActions.Controls.Add(autoScroll); logActions.Controls.Add(logLevel);
        logHeader.Controls.Add(logTitle); logHeader.Controls.Add(logActions);
        logGrid.Columns.Add("time", "时间"); logGrid.Columns.Add("level", "级别"); logGrid.Columns.Add("message", "处理内容");
        logGrid.Columns[0].FillWeight = 22; logGrid.Columns[1].FillWeight = 10; logGrid.Columns[2].FillWeight = 68;
        logs.Controls.Add(logGrid); logs.Controls.Add(logHeader);
        layout.Controls.Add(stats, 0, 0); layout.Controls.Add(current, 0, 1); layout.Controls.Add(notice, 0, 2); layout.Controls.Add(logs, 0, 3);
        page.Controls.Add(layout); return page;
    }

    private Panel BuildHistory()
    {
        var page = new Panel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        historySummary.Dock = DockStyle.Fill;
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
        var separator = TextLabel("至", 9); separator.Width = 20;
        filters.Controls.Add(dateFrom); filters.Controls.Add(separator); filters.Controls.Add(dateTo);
        historyStatus.Width = 130; filters.Controls.Add(historyStatus);
        var searchLabel = TextLabel("搜索", 9); searchLabel.Width = 42; search.Width = 190;
        filters.Controls.Add(searchLabel); filters.Controls.Add(search);
        var query = ActionButton("查询", true); query.Width = 78; query.Height = 31;
        query.Click += (_, __) =>
        {
            if (dateFrom.Value.Date > dateTo.Value.Date) { MessageBox.Show(this, "开始日期不能晚于结束日期。", "处理记录", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            filterFrom = dateFrom.Value.Date; filterTo = dateTo.Value.Date; filterText = search.Text.Trim(); filterState = historyStatus.Text; pageIndex = 0; historyStamp = ""; RenderHistory();
        };
        filters.Controls.Add(query);
        foreach (var column in new[] { "批次", "发起时间", "发起人 / 来源", "项目", "文件数", "结果", "耗时", "状态" }) historyGrid.Columns.Add("c" + historyGrid.Columns.Count, column);
        var weights = new[] { 7, 17, 19, 12, 7, 14, 8, 10 };
        for (var i = 0; i < weights.Length; i++) historyGrid.Columns[i].FillWeight = weights[i];
        historyGrid.CellClick += (_, args) => { if (args.RowIndex >= 0) SelectBatch((Guid)historyGrid.Rows[args.RowIndex].Tag); };
        var details = Card(); details.Padding = new Padding(14, 10, 14, 8); details.Margin = new Padding(0, 10, 0, 0);
        var detailHeader = new Panel { Dock = DockStyle.Top, Height = 61 };
        detailTitle.Dock = DockStyle.Top; detailTitle.Height = 27; detailMeta.Dock = DockStyle.Bottom; detailMeta.Height = 28;
        var batchLogs = ActionButton("查看本批日志"); batchLogs.Dock = DockStyle.Right; batchLogs.Width = 130;
        batchLogs.Click += (_, __) => ShowBatchLogs();
        detailHeader.Controls.Add(detailTitle); detailHeader.Controls.Add(detailMeta); detailHeader.Controls.Add(batchLogs);
        foreach (var column in new[] { "文件名称", "处理结果", "耗时", "说明" }) filesGrid.Columns.Add("c" + filesGrid.Columns.Count, column);
        filesGrid.Columns[0].FillWeight = 40; filesGrid.Columns[1].FillWeight = 13; filesGrid.Columns[2].FillWeight = 12; filesGrid.Columns[3].FillWeight = 45;
        details.Controls.Add(filesGrid); details.Controls.Add(detailHeader);
        var pagination = new Panel { Dock = DockStyle.Fill }; historyPage.Dock = DockStyle.Left; historyPage.Width = 470;
        var next = ActionButton("下一页"); next.Dock = DockStyle.Right; next.Width = 88;
        var previous = ActionButton("上一页"); previous.Dock = DockStyle.Right; previous.Width = 88;
        previous.Click += (_, __) => { if (pageIndex > 0) { pageIndex--; historyStamp = ""; RenderHistory(); } };
        next.Click += (_, __) => { if ((pageIndex + 1) * 30 < FilteredHistory().Count) { pageIndex++; historyStamp = ""; RenderHistory(); } };
        pagination.Controls.Add(historyPage); pagination.Controls.Add(previous); pagination.Controls.Add(next);
        layout.Controls.Add(historySummary, 0, 0); layout.Controls.Add(filters, 0, 1); layout.Controls.Add(historyGrid, 0, 2); layout.Controls.Add(details, 0, 3); layout.Controls.Add(pagination, 0, 4);
        page.Controls.Add(layout); return page;
    }

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
        snapshot = Program.Journal.Snapshot(); RenderHome(); RenderHistory(); RenderLogs();
        status.Text = (connected ? "● 已连接服务器" : "● " + (string.IsNullOrWhiteSpace(Program.Options.ServerUrl) ? "未配置服务器" : "服务器暂未连接"))
            + "    |    " + (string.IsNullOrWhiteSpace(Program.Options.ServerUrl) ? "请在代理设置中填写地址" : Program.Options.ServerUrl)
            + "    |    本机端口 " + Program.Options.Port + (Program.Listening ? "" : "（未监听）");
        status.ForeColor = connected && Program.Listening ? Color.SeaGreen : Muted;
        runStatus.Text = Program.ExitRequested ? "● 正在完成任务后退出" : !Program.Listening ? "● 代理未开始监听" : Program.Paused ? "● 已暂停接收" : "● 代理运行中";
        runStatus.ForeColor = Program.Paused || !Program.Listening ? Color.DarkOrange : Color.SeaGreen;
        machine.Text = "▣  " + Program.Options.Name;
        tray.Text = "阿普顿PLM转图代理 · " + (Program.ExitRequested ? "等待退出" : Program.Busy ? "正在处理" : Program.Paused ? "已暂停" : "空闲");
        if (Program.SettingsPending) { settingsMessage.Text = "设置等待生效：已接收任务完成后自动保存。"; hadPendingSettings = true; }
        else if (hadPendingSettings) { settingsMessage.Text = Program.RuntimeError ?? "设置已保存并生效。"; hadPendingSettings = false; }
    }

    private void RenderHome()
    {
        var today = snapshot.Where(batch => batch.ReceivedUtc.ToLocalTime().Date == DateTime.Today).ToList();
        metrics[0].Text = today.Count + " 次";
        metrics[1].Text = today.Count(batch => batch.Status == "成功") + " 次";
        metrics[2].Text = today.Count(batch => batch.Status == "失败" || batch.Status == "部分成功") + " 次";
        metrics[3].Text = snapshot.Count(batch => !batch.FinishedUtc.HasValue) + " 次";
        var batch = snapshot.FirstOrDefault(item => item.Status == "处理中") ?? snapshot.LastOrDefault(item => !item.FinishedUtc.HasValue);
        currentTitle.Text = batch == null ? "当前没有待处理任务" : !string.IsNullOrWhiteSpace(batch.Context.RootFileName) ? batch.Context.RootFileName
            : batch.Files.Count == 1 ? batch.Files[0].FileName : batch.Files.Count > 1 ? "本批处理 " + batch.Files.Count + " 个图档" : "正在接收本批图档";
        currentMeta.Text = batch == null ? "代理已就绪，等待服务器发起转图请求" : "批次 #" + batch.Number.ToString("D4") + "    项目 " + Value(batch.Context.ProjectCode)
            + "    发起人 " + Value(batch.Context.Initiator) + "    来源 " + Value(batch.Context.Source);
        currentStage.Text = batch?.Stage ?? "等待服务器发起转图请求";
        progress.Maximum = Math.Max(1, batch?.Files.Count ?? 1); progress.Value = Math.Min(progress.Maximum, batch?.Completed ?? 0);
        currentCount.Text = batch == null ? "" : "已处理 " + batch.Completed + " / " + batch.Files.Count + " 个图档    成功 " + batch.SuccessCount
            + " · 失败 " + batch.FailedCount + "    引用 " + batch.ReferenceFiles + " 个文件    已用时 " + Elapsed(batch.StartedUtc ?? batch.ReceivedUtc, batch.FinishedUtc);
        pause.Text = Program.Paused ? "恢复接收新任务" : "暂停接收新任务"; pause.Enabled = !Program.ExitRequested;
        notice.Text = Program.Journal.StorageError ?? Program.RuntimeError ?? (Program.ExitRequested ? "已停止接收新任务，剩余任务完成后自动退出。" : "暂停接收后，当前及已排队任务仍会继续完成。");
        notice.ForeColor = Program.Journal.StorageError != null || Program.RuntimeError != null ? Color.Firebrick : Muted;
    }

    private void RenderLogs()
    {
        var all = Program.Journal.RecentLogs();
        var items = all.Where(item => logLevel.Text == "全部级别" || item.Level == logLevel.Text).ToList();
        var stamp = items.Count + "|" + items.LastOrDefault()?.TimeUtc.Ticks + "|" + logLevel.Text;
        if (stamp == logStamp) return; logStamp = stamp;
        var top = logGrid.FirstDisplayedScrollingRowIndex;
        logGrid.Rows.Clear();
        foreach (var item in items) logGrid.Rows.Add(item.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Level, item.Message);
        if (logGrid.RowCount > 0) logGrid.FirstDisplayedScrollingRowIndex = autoScroll.Checked ? logGrid.RowCount - 1 : Math.Max(0, Math.Min(top, logGrid.RowCount - 1));
    }

    private List<AgentBatch> FilteredHistory() => snapshot.Where(batch => batch.ReceivedUtc.ToLocalTime().Date >= filterFrom
        && batch.ReceivedUtc.ToLocalTime().Date <= filterTo && (filterState == "全部状态" || batch.Status == filterState)
        && (filterText.Length == 0 || new[] { batch.Context.ProjectCode, batch.Context.Initiator, batch.Context.RootFileName, batch.Number.ToString(), string.Join(" ", batch.Files.Select(file => file.FileName)) }
            .Any(value => value?.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0))).ToList();

    private void RenderHistory()
    {
        if (renderingHistory) return;
        var filtered = FilteredHistory();
        historySummary.Text = "累计发起 " + Program.Journal.TotalRequests + " 次    |    当前筛选 " + filtered.Count + " 批：成功 " + filtered.Count(item => item.Status == "成功")
            + " · 失败 " + filtered.Count(item => item.Status == "失败") + " · 部分成功 " + filtered.Count(item => item.Status == "部分成功") + " · 处理中 / 排队 " + filtered.Count(item => !item.FinishedUtc.HasValue);
        pageIndex = Math.Min(pageIndex, Math.Max(0, (filtered.Count - 1) / 30));
        var page = filtered.Skip(pageIndex * 30).Take(30).ToList();
        var stamp = pageIndex + "|" + string.Join("|", page.Select(batch => batch.Id + batch.Status + batch.Completed + batch.Returned));
        historyPage.Text = "共 " + filtered.Count + " 批 · 第 " + (pageIndex + 1) + " / " + Math.Max(1, (int)Math.Ceiling(filtered.Count / 30.0)) + " 页，每页 30 批";
        if (stamp == historyStamp) return; historyStamp = stamp; renderingHistory = true;
        try
        {
            historyGrid.Rows.Clear();
            foreach (var item in page)
            {
                var row = historyGrid.Rows[historyGrid.Rows.Add("#" + item.Number.ToString("D4"), item.ReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    Value(item.Context.Initiator) + " / " + Value(item.Context.Source), Value(item.Context.ProjectCode), item.Files.Count,
                    item.SuccessCount + "成功 / " + item.FailedCount + "失败", Elapsed(item.StartedUtc ?? item.ReceivedUtc, item.FinishedUtc), item.Status)];
                row.Tag = item.Id; row.Selected = item.Id == selectedBatch;
            }
            if (selectedBatch.HasValue && page.Any(item => item.Id == selectedBatch.Value)) SelectBatch(selectedBatch.Value);
            else if (page.Count > 0) SelectBatch(page[0].Id);
            else { selectedBatch = null; detailTitle.Text = "当前筛选范围内没有记录"; detailMeta.Text = ""; filesGrid.Rows.Clear(); }
        }
        finally { renderingHistory = false; }
    }

    private void SelectBatch(Guid id)
    {
        selectedBatch = id;
        var batch = snapshot.Single(item => item.Id == id);
        detailTitle.Text = "批次 #" + batch.Number.ToString("D4") + " · " + batch.Status;
        detailMeta.Text = "项目 " + Value(batch.Context.ProjectCode) + "    处理 " + batch.Files.Count + " 个图档，引用 " + batch.ReferenceFiles + " 个文件"
            + (batch.Error == null ? "" : "    " + batch.Error);
        filesGrid.Rows.Clear();
        foreach (var file in batch.Files) filesGrid.Rows.Add(file.FileName, file.Status, file.StartedUtc.HasValue ? Elapsed(file.StartedUtc.Value, file.FinishedUtc) : "—", file.Message ?? "等待处理");
    }

    private void ShowBatchLogs()
    {
        if (!selectedBatch.HasValue) return;
        var batch = Program.Journal.ReadBatch(selectedBatch.Value);
        using (var window = new Form { Text = "批次 #" + batch.Number.ToString("D4") + " · 处理日志", ClientSize = new Size(950, 520), StartPosition = FormStartPosition.CenterParent, Font = Font, Icon = Icon })
        {
            var grid = Grid(); grid.Columns.Add("time", "时间"); grid.Columns.Add("level", "级别"); grid.Columns.Add("message", "处理内容");
            grid.Columns[0].FillWeight = 23; grid.Columns[1].FillWeight = 10; grid.Columns[2].FillWeight = 67;
            foreach (var item in batch.Logs) grid.Rows.Add(item.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Level, item.Message);
            var copy = ActionButton("复制本批日志"); copy.Dock = DockStyle.Right; copy.Width = 150; copy.Click += (_, __) => CopyLogs(batch.Logs);
            var open = ActionButton("打开记录目录"); open.Dock = DockStyle.Left; open.Width = 150;
            open.Click += (_, __) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Program.Options.DataDirectory, "history")) { UseShellExecute = true });
            var actions = new Panel { Dock = DockStyle.Bottom, Height = 36 }; actions.Controls.Add(copy); actions.Controls.Add(open);
            window.Controls.Add(grid); window.Controls.Add(actions); window.ShowDialog(this);
        }
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
