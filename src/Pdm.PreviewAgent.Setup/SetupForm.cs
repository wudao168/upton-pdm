using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.PreviewAgent.Setup;

internal sealed class SetupForm : Form
{
    private readonly SetupOptions options;
    private readonly TextBox serverBox = new TextBox();
    private readonly TextBox nameBox = new TextBox();
    private readonly TextBox userBox = new TextBox();
    private readonly TextBox passwordBox = new TextBox { UseSystemPasswordChar = true };
    private readonly TextBox portBox = new TextBox();
    private readonly TextBox tokenBox = new TextBox();
    private readonly TextBox timeoutBox = new TextBox();
    private readonly TextBox logBox = new TextBox();
    private readonly Button installButton = new Button();
    private readonly Button uninstallButton = new Button();
    private readonly Button closeButton = new Button();
    private readonly NotifyIcon trayIcon = new NotifyIcon();
    private readonly Timer saveTimer = new Timer { Interval = 500 };
    private readonly Timer statusTimer = new Timer { Interval = 5000 };
    private readonly TextBox statusBox = new TextBox { Dock = DockStyle.Top, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly SetupRunner monitor;
    private bool statusLoading;
    private bool exitRequested;

    public SetupForm(SetupOptions options)
    {
        this.options = options;
        monitor = new SetupRunner(options, null);
        Text = "UPLM 转图代理安装程序";
        Width = 720;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);
        Font = new Font("Microsoft YaHei UI", 9F);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(12, 10, 12, 0),
            Text = "在一台装了 SolidWorks 的电脑上运行本程序：填写“浏览器里打开PLM的地址”（局域网一般是 http://服务器IP:5173）和管理员账号，"
                 + "安装程序会自动完成文件释放、防火墙放通、开机自启、启动代理，并把本机登记为PLM的转图服务器。"
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Top, Height = 264, ColumnCount = 3, Padding = new Padding(12, 6, 12, 6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        AddRow(table, "PLM 服务器地址", serverBox, options.ServerUrl, "http://192.168.2.8:5173");
        AddRow(table, "管理员账号", userBox, options.Username, "admin");
        AddRow(table, "管理员密码", passwordBox, options.Password, string.Empty);
        AddRow(table, "本机代理端口", portBox, options.Port.ToString(), "5199");
        AddRow(table, "访问令牌", tokenBox, options.Token, "留空自动生成");
        AddRow(table, "转换超时（分钟）", timeoutBox, options.TimeoutMinutes.ToString(), "30");
        AddRow(table, "转图服务器名称", nameBox, options.Name, Environment.MachineName);
        var tokenButton = new Button { Text = "生成令牌", Dock = DockStyle.Fill, Height = 26 };
        tokenButton.Click += (_, _) => tokenBox.Text = SetupOptions.GenerateToken();
        table.Controls.Remove(table.GetControlFromPosition(2, 4));
        table.Controls.Add(tokenButton, 2, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 6, 12, 6), FlowDirection = FlowDirection.LeftToRight };
        installButton.Text = "一键安装并登记";
        installButton.Width = 150;
        installButton.Height = 30;
        installButton.Click += async (_, _) => await RunAsync(false);
        uninstallButton.Text = "卸载本机代理";
        uninstallButton.Width = 130;
        uninstallButton.Height = 30;
        uninstallButton.Click += async (_, _) => await RunAsync(true);
        closeButton.Text = "关闭";
        closeButton.Width = 90;
        closeButton.Height = 30;
        closeButton.Click += (_, _) => Close();
        actions.Controls.Add(installButton);
        actions.Controls.Add(uninstallButton);
        actions.Controls.Add(closeButton);

        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = Color.White;
        statusBox.Text = "运行与排队状态（每5秒更新）\r\n正在连接本机转图代理…";

        Controls.Add(logBox);
        Controls.Add(statusBox);
        Controls.Add(actions);
        Controls.Add(table);
        Controls.Add(info);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开转图代理管理", null, (_, _) => ShowWindow());
        menu.Items.Add("退出管理窗口（代理继续运行）", null, (_, _) => { exitRequested = true; Close(); });
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "UPLM 转图代理管理";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => ShowWindow();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveFields(); };
        foreach (var box in new[] { serverBox, userBox, passwordBox, portBox, tokenBox, timeoutBox, nameBox })
            box.TextChanged += (_, _) => { saveTimer.Stop(); saveTimer.Start(); };
        FormClosing += (_, e) => {
            SaveFields();
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        FormClosed += (_, _) => { saveTimer.Dispose(); statusTimer.Dispose(); trayIcon.Dispose(); menu.Dispose(); };
        statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) => { if (options.StartInTray) Hide(); statusTimer.Start(); await RefreshStatusAsync(); };
    }

    private async Task RefreshStatusAsync()
    {
        if (statusLoading) return;
        statusLoading = true;
        try { var status = await monitor.ReadStatusAsync(); if (!IsDisposed) statusBox.Text = status; }
        catch (Exception exception) { if (!IsDisposed) statusBox.Text = "本机代理未连接：" + SetupRunner.Describe(exception); }
        finally { statusLoading = false; }
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void SaveFields()
    {
        options.ServerUrl = serverBox.Text.Trim();
        options.Name = nameBox.Text.Trim();
        options.Username = userBox.Text.Trim();
        options.Password = passwordBox.Text;
        options.Token = tokenBox.Text.Trim();
        options.TimeoutMinutes = int.TryParse(timeoutBox.Text.Trim(), out var timeout) && timeout > 0 ? timeout : 30;
        options.Port = int.TryParse(portBox.Text.Trim(), out var port) && port > 0 ? port : 5199;
        try { SetupPreferences.Save(options); }
        catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.Security.Cryptography.CryptographicException)
        { logBox.AppendText("配置保存失败：" + exception.Message + Environment.NewLine); }
    }

    private static void AddRow(TableLayoutPanel table, string label, TextBox box, string value, string placeholder)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
        box.Dock = DockStyle.Fill;
        box.Text = value;
        table.Controls.Add(box, 1, row);
        var hint = new Label
        {
            Text = string.IsNullOrEmpty(placeholder) ? string.Empty : $"例：{placeholder}",
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        table.Controls.Add(hint, 2, row);
    }

    private async Task RunAsync(bool uninstall)
    {
        if (!uninstall && !confirmOverwrite()) return;
        SaveFields();
        options.ServerUrl = serverBox.Text.Trim();
        options.Username = userBox.Text.Trim();
        options.Password = passwordBox.Text;
        options.Token = tokenBox.Text.Trim();
        options.TimeoutMinutes = int.TryParse(timeoutBox.Text.Trim(), out var timeout) && timeout > 0 ? timeout : 30;
        options.Port = int.TryParse(portBox.Text.Trim(), out var port) && port > 0 ? port : 5199;

        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        logBox.Clear();
        try
        {
            var runner = new SetupRunner(options, message => { logBox.AppendText(message + Environment.NewLine); logBox.Update(); });
            var ok = uninstall ? await runner.UninstallAsync() : await runner.InstallAsync();
            tokenBox.Text = options.Token;
            SaveFields();
            MessageBox.Show(this, ok ? (uninstall ? "卸载完成。" : "安装完成，本机已登记为PLM的转图服务器。") : "操作未完成，请查看日志。",
                Text, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            var message = SetupRunner.Describe(exception);
            logBox.AppendText("失败：" + message + Environment.NewLine);
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            installButton.Enabled = true;
            uninstallButton.Enabled = true;
        }
    }

    private bool confirmOverwrite() =>
        !System.IO.Directory.Exists(options.InstallDirectory)
        || MessageBox.Show(this, $"目标目录已存在：\n{options.InstallDirectory}\n\n继续将覆盖其中的转图代理文件，是否继续？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
