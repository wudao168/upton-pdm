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
    private readonly Button checkUpdateButton = new Button();
    private readonly Button installUpdateButton = new Button();
    private readonly Button saveButton = new Button();
    private readonly Button cancelButton = new Button();
    private readonly Timer refreshTimer = new Timer();
    private readonly Func<PluginUpdateSnapshot> readSnapshot;
    private readonly Func<string, Task<PluginConnectionResult>> testConnection;
    private readonly Func<string, Task<PluginUpdateSnapshot>> checkUpdate;
    private readonly Func<string, Task<PluginUpdateSnapshot>> installUpdate;
    private readonly Func<string, bool, Task<PluginConnectionResult>> saveSettings;
    private bool actionRunning;

    public PluginSettingsDialog(
        string initialServerAddress,
        bool automaticUpdatesEnabled,
        PluginUpdateSnapshot initialSnapshot,
        Func<PluginUpdateSnapshot> readSnapshot,
        Func<string, Task<PluginConnectionResult>> testConnection,
        Func<string, Task<PluginUpdateSnapshot>> checkUpdate,
        Func<string, Task<PluginUpdateSnapshot>> installUpdate,
        Func<string, bool, Task<PluginConnectionResult>> saveSettings)
    {
        this.readSnapshot = readSnapshot ?? throw new ArgumentNullException(nameof(readSnapshot));
        this.testConnection = testConnection ?? throw new ArgumentNullException(nameof(testConnection));
        this.checkUpdate = checkUpdate ?? throw new ArgumentNullException(nameof(checkUpdate));
        this.installUpdate = installUpdate ?? throw new ArgumentNullException(nameof(installUpdate));
        this.saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));

        Text = "插件设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 500);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 145F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        Controls.Add(root);

        var connectionGroup = new GroupBox { Text = "服务器连接", Dock = DockStyle.Fill };
        var connectionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
            ColumnCount = 3,
            RowCount = 3
        };
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        connectionGroup.Controls.Add(connectionLayout);

        connectionLayout.Controls.Add(new Label
        {
            Text = "服务器地址",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 0);
        serverAddress.Text = initialServerAddress ?? string.Empty;
        serverAddress.Dock = DockStyle.Fill;
        serverAddress.AccessibleName = "服务器地址";
        connectionLayout.Controls.Add(serverAddress, 1, 0);
        testConnectionButton.Text = "测试连接";
        testConnectionButton.Dock = DockStyle.Fill;
        testConnectionButton.Click += async (_, _) => await RunConnectionTestAsync();
        connectionLayout.Controls.Add(testConnectionButton, 2, 0);
        connectionStatus.Text = "修改地址前可先测试；测试失败不会覆盖原配置。";
        connectionStatus.ForeColor = Color.FromArgb(95, 108, 124);
        connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
        connectionStatus.Dock = DockStyle.Fill;
        connectionLayout.SetColumnSpan(connectionStatus, 3);
        connectionLayout.Controls.Add(connectionStatus, 0, 1);
        var addressHint = new Label
        {
            Text = "支持IP、域名和端口，例如：http://192.168.2.8:5173",
            ForeColor = Color.FromArgb(95, 108, 124),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        };
        connectionLayout.SetColumnSpan(addressHint, 3);
        connectionLayout.Controls.Add(addressHint, 0, 2);
        root.Controls.Add(connectionGroup, 0, 0);

        var updateGroup = new GroupBox { Text = "版本与更新", Dock = DockStyle.Fill };
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
        for (var row = 0; row < 6; row++) updateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29F));
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
        automaticUpdates.Text = "启用自动更新";
        automaticUpdates.Checked = automaticUpdatesEnabled;
        automaticUpdates.Dock = DockStyle.Fill;
        updateLayout.Controls.Add(automaticUpdates, 0, 5);
        updateLayout.SetColumnSpan(automaticUpdates, 3);

        var updateActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        checkUpdateButton.Text = "检查更新";
        checkUpdateButton.Width = 92;
        checkUpdateButton.Click += async (_, _) => await RunUpdateActionAsync(false);
        installUpdateButton.Text = "立即更新";
        installUpdateButton.Width = 92;
        installUpdateButton.Click += async (_, _) => await RunUpdateActionAsync(true);
        updateActions.Controls.Add(installUpdateButton);
        updateActions.Controls.Add(checkUpdateButton);
        updateLayout.Controls.Add(updateActions, 0, 6);
        updateLayout.SetColumnSpan(updateActions, 3);
        root.Controls.Add(updateGroup, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        saveButton.Text = "保存并应用";
        saveButton.AutoSize = true;
        saveButton.Click += async (_, _) => await SaveAsync();
        cancelButton.Text = "取消";
        cancelButton.AutoSize = true;
        cancelButton.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(saveButton);
        footer.Controls.Add(cancelButton);
        root.Controls.Add(footer, 0, 2);

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
            var result = await saveSettings(serverAddress.Text, automaticUpdates.Checked);
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
        testConnectionButton.Enabled = !running;
        checkUpdateButton.Enabled = !running;
        saveButton.Enabled = !running;
        cancelButton.Enabled = !running;
        var snapshot = readSnapshot();
        installUpdateButton.Enabled = !running && snapshot.UpdateAvailable && !snapshot.Busy;
    }

    private void ApplySnapshot(PluginUpdateSnapshot snapshot)
    {
        snapshot = snapshot ?? new PluginUpdateSnapshot();
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
}
