#nullable disable
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PluginSettingsDialog : Form
{
    private readonly TextBox serverAddress = new TextBox();
    private readonly Button testConnectionButton = new Button();
    private readonly Label connectionStatus = new Label();
    private readonly Label installedVersion = new Label();
    private readonly Label availableVersion = new Label();
    private readonly Label updateStatus = new Label();
    private readonly Label lastCheckedAt = new Label();
    private readonly ProgressBar updateProgress = new ProgressBar();
    private readonly CheckBox automaticUpdates = new CheckBox();
    private readonly CheckBox blockMissingReferences = new CheckBox();
    private readonly ComboBox plannedTaskScrollInterval = new ComboBox();
    private readonly Button checkUpdateButton = new Button();
    private readonly Button installUpdateButton = new Button();
    private readonly Button saveButton = new Button();
    private readonly Button cancelButton = new Button();
    private readonly Button cleanupCopiesButton = new Button();
    private readonly Label cleanupCopiesStatus = new Label();
    private readonly Label cacheUsage = new Label();
    private readonly NumericUpDown cacheLimit = new NumericUpDown();
    private readonly Button cleanupCacheButton = new Button();
    public Func<Task<ControlledCacheUsage>> ReadCacheUsage { get; set; }
    public Func<Task<ControlledCacheUsage>> CleanControlledCache { get; set; }
    private Func<Task<ArchiveTemporaryCopyCleanupResult>> cleanTemporaryCopies;
    private readonly Timer refreshTimer = new Timer();
    private readonly Func<PluginUpdateSnapshot> readSnapshot;
    private readonly Func<string, Task<PluginConnectionResult>> testConnection;
    private readonly Func<string, Task<PluginUpdateSnapshot>> checkUpdate;
    private readonly Func<string, Task<PluginUpdateSnapshot>> installUpdate;
    private readonly Func<PluginSettings, Task<PluginConnectionResult>> saveSettings;
    private bool actionRunning;

    public Func<Task<ArchiveTemporaryCopyCleanupResult>> CleanTemporaryCopies
    {
        get => cleanTemporaryCopies;
        set { cleanTemporaryCopies = value; cleanupCopiesButton.Enabled = !actionRunning && value != null; }
    }

    public PluginSettingsDialog(
        PluginSettings initialSettings,
        PluginUpdateSnapshot initialSnapshot,
        Func<PluginUpdateSnapshot> readSnapshot,
        Func<string, Task<PluginConnectionResult>> testConnection,
        Func<string, Task<PluginUpdateSnapshot>> checkUpdate,
        Func<string, Task<PluginUpdateSnapshot>> installUpdate,
        Func<PluginSettings, Task<PluginConnectionResult>> saveSettings)
    {
        initialSettings ??= new PluginSettings();
        this.readSnapshot = readSnapshot ?? throw new ArgumentNullException(nameof(readSnapshot));
        this.testConnection = testConnection ?? throw new ArgumentNullException(nameof(testConnection));
        this.checkUpdate = checkUpdate ?? throw new ArgumentNullException(nameof(checkUpdate));
        this.installUpdate = installUpdate ?? throw new ArgumentNullException(nameof(installUpdate));
        this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));

        Text = "插件设置";
        DialogWindowSizing.CenterOnOwnerScreen(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 820);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 145F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        Controls.Add(root);

        var connectionGroup = new GroupBox { Text = "服务器连接", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        var connectionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
            ColumnCount = 4,
            RowCount = 3,
            Margin = Padding.Empty
        };
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        connectionGroup.Controls.Add(connectionLayout);

        connectionLayout.Controls.Add(new Label
        {
            Text = "服务器地址",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0)
        }, 0, 0);
        serverAddress.Text = initialSettings.ServerAddress ?? string.Empty;
        serverAddress.Dock = DockStyle.Fill;
        serverAddress.AutoSize = false;
        serverAddress.Margin = Padding.Empty;
        serverAddress.AccessibleName = "服务器地址";
        connectionLayout.Controls.Add(serverAddress, 1, 0);
        testConnectionButton.Text = "测试连接";
        testConnectionButton.Dock = DockStyle.Fill;
        ConfigureDialogButton(testConnectionButton);
        testConnectionButton.Margin = Padding.Empty;
        testConnectionButton.Click += async (_, _) => await RunConnectionTestAsync();
        connectionLayout.Controls.Add(testConnectionButton, 3, 0);
        connectionStatus.Text = "修改地址前可先测试；测试失败不会覆盖原配置。";
        connectionStatus.ForeColor = Color.FromArgb(95, 108, 124);
        connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
        connectionStatus.Dock = DockStyle.Fill;
        connectionLayout.SetColumnSpan(connectionStatus, 4);
        connectionLayout.Controls.Add(connectionStatus, 0, 1);
        var addressHint = new Label
        {
            Text = "支持IP、域名和端口，例如：http://192.168.2.8:5173",
            ForeColor = Color.FromArgb(95, 108, 124),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        };
        connectionLayout.SetColumnSpan(addressHint, 4);
        connectionLayout.Controls.Add(addressHint, 0, 2);
        root.Controls.Add(connectionGroup, 0, 0);

        var taskGroup = new GroupBox { Text = "任务计划与存档选项", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        var taskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 5, 10, 5), ColumnCount = 3, RowCount = 2 };
        taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        taskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        taskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
        taskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        taskLayout.Controls.Add(new Label { Text = "任务滚动间隔", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        plannedTaskScrollInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        plannedTaskScrollInterval.Items.AddRange(new object[] { "15 秒", "30 秒", "60 秒" });
        plannedTaskScrollInterval.SelectedIndex = initialSettings.PlannedTaskScrollIntervalSeconds == 15 ? 0
            : initialSettings.PlannedTaskScrollIntervalSeconds == 60 ? 2 : 1;
        plannedTaskScrollInterval.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        plannedTaskScrollInterval.Margin = Padding.Empty;
        plannedTaskScrollInterval.AccessibleName = "任务滚动间隔";
        taskLayout.Controls.Add(plannedTaskScrollInterval, 1, 0);
        blockMissingReferences.Text = "存档警告由管理员在系统管理中统一配置";
        blockMissingReferences.Enabled = false;
        blockMissingReferences.AccessibleName = "引用缺失时阻止存档";
        blockMissingReferences.Checked = ArchiveMissingReferencePolicy.BlockMissingReferences;
        blockMissingReferences.Dock = DockStyle.Fill;
        taskLayout.Controls.Add(blockMissingReferences, 0, 1);
        taskLayout.SetColumnSpan(blockMissingReferences, 3);
        taskGroup.Controls.Add(taskLayout);
        root.Controls.Add(taskGroup, 0, 1);

        var updateGroup = new GroupBox { Text = "版本与更新", Dock = DockStyle.Fill, Margin = Padding.Empty };
        var updateLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
            ColumnCount = 3,
            RowCount = 7
        };
        updateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        updateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        updateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (var row = 0; row < 6; row++) updateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        updateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        updateGroup.Controls.Add(updateLayout);

        AddValueRow(updateLayout, 0, "当前版本", installedVersion);
        AddValueRow(updateLayout, 1, "最新版本", availableVersion);
        AddValueRow(updateLayout, 2, "更新状态", updateStatus);
        AddValueRow(updateLayout, 3, "最后检查", lastCheckedAt);
        updateProgress.Dock = DockStyle.Fill;
        updateProgress.Minimum = 0;
        updateProgress.Maximum = 100;
        updateLayout.Controls.Add(new Label { Text = "下载进度", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        updateLayout.Controls.Add(updateProgress, 1, 4);
        updateLayout.SetColumnSpan(updateProgress, 2);
        automaticUpdates.Text = "自动检查并下载更新（安装需手动确认）";
        automaticUpdates.Checked = initialSettings.AutomaticUpdatesEnabled;
        automaticUpdates.Dock = DockStyle.Fill;
        updateLayout.Controls.Add(automaticUpdates, 0, 5);
        updateLayout.SetColumnSpan(automaticUpdates, 3);

        var updateActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        checkUpdateButton.Text = "检查更新";
        ConfigureDialogButton(checkUpdateButton);
        checkUpdateButton.Click += async (_, _) => await RunUpdateActionAsync(false);
        installUpdateButton.Text = "安装更新";
        ConfigureDialogButton(installUpdateButton);
        installUpdateButton.Margin = Padding.Empty;
        installUpdateButton.Click += async (_, _) => await RunUpdateActionAsync(true);
        updateActions.Controls.Add(installUpdateButton);
        updateActions.Controls.Add(checkUpdateButton);
        updateLayout.Controls.Add(updateActions, 0, 6);
        updateLayout.SetColumnSpan(updateActions, 3);
        root.Controls.Add(updateGroup, 0, 2);

        var cleanupGroup = new GroupBox { Text = "本地副本与缓存", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        var cleanupLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 6), ColumnCount = 2, RowCount = 6 };
        cleanupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cleanupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        cleanupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        cleanupLayout.Controls.Add(new Label { Text = "清理未使用的存档上传副本", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        cleanupCopiesButton.Text = "立即清理";
        cleanupCopiesButton.AccessibleName = "清理存档临时副本";
        ConfigureDialogButton(cleanupCopiesButton);
        cleanupCopiesButton.Margin = Padding.Empty;
        cleanupCopiesButton.Enabled = false;
        cleanupCopiesButton.Click += async (_, _) => await CleanTemporaryCopiesAsync();
        cleanupLayout.Controls.Add(cleanupCopiesButton, 1, 0);
        var cleanupPath = new Label { Text = ArchiveTemporaryCopyCleaner.DirectoryPath, AutoEllipsis = true, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(95, 108, 124), AccessibleName = "存档临时副本目录" };
        cleanupLayout.Controls.Add(cleanupPath, 0, 1);
        cleanupLayout.SetColumnSpan(cleanupPath, 2);
        cleanupCopiesStatus.Text = "清理完成后显示数量和释放空间。";
        cleanupCopiesStatus.Dock = DockStyle.Fill;
        cleanupCopiesStatus.ForeColor = Color.FromArgb(95, 108, 124);
        cleanupLayout.Controls.Add(cleanupCopiesStatus, 0, 2);
        cleanupLayout.SetColumnSpan(cleanupCopiesStatus, 2);
        var limitRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        limitRow.Controls.Add(new Label { Text = "缓存上限（GB）", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        cacheLimit.Minimum = 1; cacheLimit.Maximum = 1024; cacheLimit.Width = 70;
        initialSettings.Normalize(); cacheLimit.Value = initialSettings.CacheLimitGigabytes;
        cacheLimit.AccessibleName = "缓存容量上限（GB）"; limitRow.Controls.Add(cacheLimit);
        cleanupLayout.Controls.Add(limitRow, 0, 3);
        cleanupCacheButton.Text = "清理缓存"; cleanupCacheButton.AccessibleName = "安全清理受控缓存";
        ConfigureDialogButton(cleanupCacheButton); cleanupCacheButton.Margin = Padding.Empty;
        cleanupCacheButton.Click += async (_, _) => await RunCacheActionAsync(true);
        cleanupLayout.Controls.Add(cleanupCacheButton, 1, 3);
        cleanupLayout.Controls.Add(new Label { Text = "编辑中、含修改及其所需引用自动保留", Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(95, 108, 124) }, 0, 4); cleanupLayout.SetColumnSpan(cleanupLayout.GetControlFromPosition(0, 4), 2);
        cacheUsage.Text = "正在统计缓存占用…"; cacheUsage.Dock = DockStyle.Fill;
        cacheUsage.ForeColor = Color.FromArgb(95, 108, 124);
        cleanupLayout.Controls.Add(cacheUsage, 0, 5); cleanupLayout.SetColumnSpan(cacheUsage, 2);
        Shown += async (_, _) => await RunCacheActionAsync(false);
        cleanupGroup.Controls.Add(cleanupLayout);
        root.Controls.Add(cleanupGroup, 0, 3);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        saveButton.Text = "保存";
        ConfigureDialogButton(saveButton);
        saveButton.Margin = Padding.Empty;
        saveButton.UseVisualStyleBackColor = false;
        saveButton.BackColor = Color.FromArgb(21, 126, 77);
        saveButton.ForeColor = Color.White;
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.Click += async (_, _) => await SaveAsync();
        cancelButton.Text = "取消";
        ConfigureDialogButton(cancelButton);
        cancelButton.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(saveButton);
        footer.Controls.Add(cancelButton);
        root.Controls.Add(footer, 0, 4);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ApplySnapshot(initialSnapshot ?? new PluginUpdateSnapshot());
        refreshTimer.Interval = 500;
        refreshTimer.Tick += (_, _) =>
        {
            if (!actionRunning) ApplySnapshot(this.readSnapshot());
        };
        refreshTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    private static void AddValueRow(TableLayoutPanel layout, int row, string caption, Label value)
    {
        layout.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.AutoEllipsis = true;
        layout.Controls.Add(value, 1, row);
        layout.SetColumnSpan(value, 2);
    }

    private async Task RunConnectionTestAsync()
    {
        if (actionRunning) return;
        SetActionRunning(true);
        connectionStatus.ForeColor = Color.FromArgb(95, 108, 124);
        connectionStatus.Text = "正在测试连接…";
        try
        {
            var result = await testConnection(serverAddress.Text);
            connectionStatus.ForeColor = Color.FromArgb(0, 128, 96);
            connectionStatus.Text = result?.Message ?? "连接成功";
        }
        catch (Exception exception)
        {
            connectionStatus.ForeColor = Color.FromArgb(192, 48, 48);
            connectionStatus.Text = exception.Message;
        }
        finally
        {
            SetActionRunning(false);
        }
    }

    private async Task CleanTemporaryCopiesAsync()
    {
        if (actionRunning || cleanTemporaryCopies == null) return;
        SetActionRunning(true);
        cleanupCopiesStatus.ForeColor = Color.FromArgb(95, 108, 124);
        cleanupCopiesStatus.Text = "正在清理存档临时副本…";
        try
        {
            var result = await cleanTemporaryCopies();
            if (!IsDisposed)
            {
                cleanupCopiesStatus.Text = result.Message;
                cleanupCopiesStatus.ForeColor = result.SkippedItems == 0 ? Color.FromArgb(0, 128, 96) : Color.FromArgb(170, 105, 20);
            }
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("手动清理存档临时副本", error.ToString());
            if (!IsDisposed)
            {
                cleanupCopiesStatus.ForeColor = Color.FromArgb(192, 48, 48);
                cleanupCopiesStatus.Text = error is InvalidOperationException
                    && (error.Message.StartsWith("当前有图档操作", StringComparison.Ordinal) || error.Message.StartsWith("临时", StringComparison.Ordinal)) ? error.Message
                    : "无法完成清理，请检查临时目录权限或文件占用后重试；详细原因已记录在日志中。";
            }
        }
        finally { if (!IsDisposed) SetActionRunning(false); }
    }

    private async Task RunCacheActionAsync(bool clean)
    {
        var action = clean ? CleanControlledCache : ReadCacheUsage;
        if (actionRunning || action == null) return;
        if (clean) SetActionRunning(true);
        cleanupCacheButton.Enabled = false;
        cacheUsage.Text = clean ? "正在核对并清理可删除的缓存…" : "正在统计缓存占用…";
        try { var result = await action(); if (!IsDisposed) cacheUsage.Text = result.Message; }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("本地缓存管理", error.ToString());
            if (!IsDisposed) cacheUsage.Text = error is InvalidOperationException && error.Message.StartsWith("当前有图档操作", StringComparison.Ordinal)
                ? error.Message : "缓存未能完成核对，文件已保留，请稍后重试。";
        }
        finally { if (!IsDisposed) { if (clean) SetActionRunning(false); else cleanupCacheButton.Enabled = !actionRunning && CleanControlledCache != null; } }
    }

    private async Task RunUpdateActionAsync(bool install)
    {
        if (actionRunning) return;
        SetActionRunning(true);
        try
        {
            var snapshot = install
                ? await installUpdate(serverAddress.Text)
                : await checkUpdate(serverAddress.Text);
            ApplySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            var snapshot = readSnapshot();
            snapshot.Status = exception.Message;
            snapshot.Busy = false;
            ApplySnapshot(snapshot);
        }
        finally
        {
            SetActionRunning(false);
        }
    }

    private async Task SaveAsync()
    {
        if (actionRunning) return;
        SetActionRunning(true);
        connectionStatus.ForeColor = Color.FromArgb(95, 108, 124);
        connectionStatus.Text = "正在验证并应用设置…";
        try
        {
            var result = await saveSettings(new PluginSettings
            {
                PlannedTaskScrollIntervalSeconds = plannedTaskScrollInterval.SelectedIndex == 0 ? 15
                    : plannedTaskScrollInterval.SelectedIndex == 2 ? 60 : 30,
                ServerAddress = serverAddress.Text,
                AutomaticUpdatesEnabled = automaticUpdates.Checked,
                CacheLimitGigabytes = (int)cacheLimit.Value,
                BlockArchiveOnMissingReferences = blockMissingReferences.Checked,
            });
            connectionStatus.ForeColor = Color.FromArgb(0, 128, 96);
            connectionStatus.Text = result?.Message ?? "设置已保存";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            connectionStatus.ForeColor = Color.FromArgb(192, 48, 48);
            connectionStatus.Text = exception.Message;
        }
        finally
        {
            SetActionRunning(false);
        }
    }

    private void SetActionRunning(bool running)
    {
        actionRunning = running;
        serverAddress.Enabled = !running;
        automaticUpdates.Enabled = !running;
        plannedTaskScrollInterval.Enabled = !running;
        cacheLimit.Enabled = !running;
        cleanupCacheButton.Enabled = !running && CleanControlledCache != null;
        testConnectionButton.Enabled = !running;
        checkUpdateButton.Enabled = !running;
        saveButton.Enabled = !running;
        cancelButton.Enabled = !running;
        cleanupCopiesButton.Enabled = !running && cleanTemporaryCopies != null;
        var snapshot = readSnapshot();
        installUpdateButton.Text = snapshot.ReadyToInstall ? "安装更新" : "下载并安装";
        installUpdateButton.Enabled = !running && snapshot.UpdateAvailable && !snapshot.Busy;
    }

    private void ApplySnapshot(PluginUpdateSnapshot snapshot)
    {
        snapshot = snapshot ?? new PluginUpdateSnapshot();
        installUpdateButton.Text = snapshot.ReadyToInstall ? "安装更新" : "下载并安装";
        installedVersion.Text = DisplayVersion(snapshot.InstalledVersion);
        availableVersion.Text = DisplayVersion(snapshot.AvailableVersion);
        updateStatus.Text = snapshot.Status ?? string.Empty;
        lastCheckedAt.Text = snapshot.LastCheckedAt.HasValue
            ? snapshot.LastCheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "尚未检查";
        updateProgress.Style = snapshot.ProgressPercentage.HasValue
            ? ProgressBarStyle.Continuous
            : snapshot.Busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        updateProgress.Value = snapshot.ProgressPercentage.HasValue
            ? Math.Max(0, Math.Min(100, snapshot.ProgressPercentage.Value))
            : 0;
        installUpdateButton.Enabled = !actionRunning && snapshot.UpdateAvailable && !snapshot.Busy;
        checkUpdateButton.Enabled = !actionRunning && !snapshot.Busy;
    }

    private static string DisplayVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "—";
        var value = version.Trim();
        return value.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? value : string.Concat("V", value);
    }

    private static void ConfigureDialogButton(Button button)
    {
        button.AutoSize = false;
        button.Size = new Size(96, 30);
        button.Margin = new Padding(0, 0, 6, 0);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = false;
        button.FlatStyle = FlatStyle.Flat;
    }
}
