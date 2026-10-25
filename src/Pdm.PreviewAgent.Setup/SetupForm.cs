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
    private readonly TextBox folderBox = new TextBox();
    private readonly Button installButton = new Button();
    private readonly Button upgradeButton = new Button();
    private readonly Button uninstallButton = new Button();
    private readonly Button closeButton = new Button();
    private readonly Timer saveTimer = new Timer { Interval = 500 };
    private bool runningOperation;

    public SetupForm(SetupOptions options)
    {
        this.options = options;
        Text = "UPLM 转图代理 1.2.0 · 安装与升级";
        Width = 820;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 580);
        Font = new Font("Microsoft YaHei UI", 9F);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(12, 10, 12, 0),
            Text = "请在需要转图、且已安装 SolidWorks 的目标服务器上运行。新装请填写PLM地址和管理员账号后点击“一键安装并登记”；"
                 + "已有代理请点击“升级现有代理”，保留原连接配置，无需重新登录PLM。安装完成后使用带首页、处理记录和代理设置的前台窗口。"
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Top, Height = 300, ColumnCount = 3, Padding = new Padding(12, 6, 12, 6) };
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
        AddRow(table, "安装目录", folderBox, options.InstallDirectory, string.Empty);
        var browse = new Button { Text = "选择目录", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using (var dialog = new FolderBrowserDialog { Description = "选择目标转图代理的安装目录", SelectedPath = folderBox.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath; };
        table.Controls.Remove(table.GetControlFromPosition(2, 7)); table.Controls.Add(browse, 2, 7);
        var tokenButton = new Button { Text = "生成令牌", Dock = DockStyle.Fill, Height = 26 };
        tokenButton.Click += (_, _) => tokenBox.Text = SetupOptions.GenerateToken();
        table.Controls.Remove(table.GetControlFromPosition(2, 4));
        table.Controls.Add(tokenButton, 2, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 6, 12, 6), FlowDirection = FlowDirection.LeftToRight };
        installButton.Text = "一键安装并登记";
        installButton.Width = 150;
        installButton.Height = 30;
        installButton.Click += async (_, _) => await RunAsync(false);
        upgradeButton.Text = "升级现有代理（保留配置）";
        upgradeButton.Width = 190; upgradeButton.Height = 30;
        upgradeButton.Enabled = System.IO.File.Exists(System.IO.Path.Combine(options.InstallDirectory, "PdmPreviewAgent.json"));
        upgradeButton.Click += async (_, _) => await RunAsync(false, true);
        uninstallButton.Text = "卸载本机代理";
        uninstallButton.Width = 130;
        uninstallButton.Height = 30;
        uninstallButton.Click += async (_, _) => await RunAsync(true);
        closeButton.Text = "关闭";
        closeButton.Width = 90;
        closeButton.Height = 30;
        closeButton.Click += (_, _) => Close();
        actions.Controls.Add(installButton);
        actions.Controls.Add(upgradeButton);
        actions.Controls.Add(uninstallButton);
        actions.Controls.Add(closeButton);

        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = Color.White;

        Controls.Add(logBox);
        Controls.Add(actions);
        Controls.Add(table);
        Controls.Add(info);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveFields(); };
        foreach (var box in new[] { serverBox, userBox, passwordBox, portBox, tokenBox, timeoutBox, nameBox, folderBox })
            box.TextChanged += (_, _) => { saveTimer.Stop(); saveTimer.Start(); };
        FormClosing += (_, e) => { if (runningOperation) { e.Cancel = true; return; } SaveFields(); };
        FormClosed += (_, _) => saveTimer.Dispose();
    }

    private void SaveFields()
    {
        options.ServerUrl = serverBox.Text.Trim();
        options.Name = nameBox.Text.Trim();
        options.Username = userBox.Text.Trim();
        options.Password = passwordBox.Text;
        options.Token = tokenBox.Text.Trim();
        options.InstallDirectory = folderBox.Text.Trim();
        upgradeButton.Enabled = !runningOperation && options.InstallDirectory.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0
            && System.IO.File.Exists(System.IO.Path.Combine(options.InstallDirectory, "PdmPreviewAgent.json"));
        options.TimeoutMinutes = int.TryParse(timeoutBox.Text.Trim(), out var timeout) && timeout > 0 ? timeout : 30;
        options.Port = int.TryParse(portBox.Text.Trim(), out var port) && port > 0 ? port : 5199;
        try { SetupPreferences.Save(options); }
        catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.Security.Cryptography.CryptographicException)
        { logBox.AppendText("配置保存失败：" + SetupRunner.Describe(exception) + Environment.NewLine); }
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

    private async Task RunAsync(bool uninstall, bool upgrade = false)
    {
        SaveFields();
        if (!System.IO.Path.IsPathRooted(options.InstallDirectory)) { MessageBox.Show(this, "请填写有效的安装目录完整路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (uninstall && MessageBox.Show(this, "将卸载所选目录中的转图代理：\r\n" + options.InstallDirectory + "\r\n确认继续？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!uninstall && !upgrade && !confirmOverwrite()) return;
        options.ServerUrl = serverBox.Text.Trim();
        options.Username = userBox.Text.Trim();
        options.Password = passwordBox.Text;
        options.Token = tokenBox.Text.Trim();
        options.TimeoutMinutes = int.TryParse(timeoutBox.Text.Trim(), out var timeout) && timeout > 0 ? timeout : 30;
        options.Port = int.TryParse(portBox.Text.Trim(), out var port) && port > 0 ? port : 5199;

        installButton.Enabled = false;
        upgradeButton.Enabled = false;
        uninstallButton.Enabled = false;
        runningOperation = true;
        logBox.Clear();
        try
        {
            var runner = new SetupRunner(options, message => { logBox.AppendText(message + Environment.NewLine); logBox.Update(); });
            var ok = uninstall ? await runner.UninstallAsync() : upgrade ? await runner.UpgradeAsync() : await runner.InstallAsync();
            tokenBox.Text = options.Token;
            SaveFields();
            runningOperation = false;
            MessageBox.Show(this, ok ? (uninstall ? "卸载完成。" : upgrade ? "升级完成，原配置已保留，前台代理已启动。" : "安装完成，本机已登记为PLM的转图服务器，前台代理已启动。") : "操作未完成，请查看日志。",
                Text, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (ok && !uninstall) Close();
        }
        catch (Exception exception)
        {
            SetupRunner.RecordDiagnostic(exception.ToString());
            var message = SetupRunner.Describe(exception);
            logBox.AppendText("失败：" + message + Environment.NewLine);
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            runningOperation = false;
            if (!IsDisposed) { installButton.Enabled = true; uninstallButton.Enabled = true; SaveFields(); }
        }
    }

    private bool confirmOverwrite() =>
        !System.IO.Directory.Exists(options.InstallDirectory)
        || MessageBox.Show(this, $"目标目录已存在：\n{options.InstallDirectory}\n\n继续将覆盖其中的转图代理文件，是否继续？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
