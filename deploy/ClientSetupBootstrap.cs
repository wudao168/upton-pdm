using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal sealed class SetupRequest { public string ServerBaseUrl; public string InstallRoot; public string Mode; }

internal sealed class ClientSetupForm : Form
{
    private readonly TextBox serverInput = new TextBox();
    private readonly TextBox installRootInput = new TextBox();
    private readonly RadioButton installMode = new RadioButton();
    private readonly RadioButton repairMode = new RadioButton();
    private readonly RadioButton uninstallMode = new RadioButton();
    private readonly Button browseButton = new Button();
    private readonly Button executeButton = new Button();
    private readonly Button closeButton = new Button();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label status = new Label();
    private readonly RichTextBox log = new RichTextBox();
    private readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
    private string logPath;

    public ClientSetupForm(SetupRequest initial)
    {
        Text = "UPLM 客户端安装向导"; Width = 760; Height = 590; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var heading = new Label { Left = 24, Top = 18, Width = 690, Height = 28, Text = "UPLM 客户端和 SolidWorks 插件", Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold) };
        var description = new Label { Left = 24, Top = 48, Width = 690, Height = 34, Text = "请选择操作、服务器地址和安装位置。安装和修复会先清理旧客户端与旧插件。" };
        installMode.SetBounds(24, 94, 130, 24); installMode.Text = "安装 / 升级";
        repairMode.SetBounds(166, 94, 100, 24); repairMode.Text = "修复";
        uninstallMode.SetBounds(278, 94, 100, 24); uninstallMode.Text = "卸载";
        var serverLabel = new Label { Left = 24, Top = 132, Width = 180, Text = "服务器 IP / 地址：" };
        serverInput.SetBounds(190, 127, 430, 26); serverInput.Text = initial.ServerBaseUrl;
        var installLabel = new Label { Left = 24, Top = 168, Width = 160, Text = "客户端安装位置：" };
        installRootInput.SetBounds(190, 163, 430, 26); installRootInput.Text = initial.InstallRoot;
        browseButton.SetBounds(630, 162, 82, 28); browseButton.Text = "浏览…";
        status.SetBounds(24, 210, 690, 24); status.Text = "准备就绪。";
        progress.SetBounds(24, 238, 688, 20);
        log.SetBounds(24, 272, 688, 210); log.ReadOnly = true; log.BackColor = System.Drawing.Color.White; log.Font = new System.Drawing.Font("Consolas", 9F);
        executeButton.SetBounds(520, 500, 92, 30); closeButton.SetBounds(620, 500, 92, 30); closeButton.Text = "取消";
        AcceptButton = executeButton; CancelButton = closeButton;
        Controls.AddRange(new Control[] { heading, description, installMode, repairMode, uninstallMode, serverLabel, serverInput, installLabel, installRootInput, browseButton, status, progress, log, executeButton, closeButton });
        installMode.CheckedChanged += (_, __) => RefreshMode(); repairMode.CheckedChanged += (_, __) => RefreshMode(); uninstallMode.CheckedChanged += (_, __) => RefreshMode();
        browseButton.Click += (_, __) => BrowseInstallRoot(); executeButton.Click += (_, __) => StartOperation(); closeButton.Click += (_, __) => { if (!worker.IsBusy) Close(); };
        worker.DoWork += RunOperation; worker.ProgressChanged += (_, e) => AppendLog((string)e.UserState); worker.RunWorkerCompleted += CompleteOperation;
        if (string.Equals(initial.Mode, "Uninstall", StringComparison.OrdinalIgnoreCase)) uninstallMode.Checked = true;
        else if (string.Equals(initial.Mode, "Repair", StringComparison.OrdinalIgnoreCase)) repairMode.Checked = true; else installMode.Checked = true;
        RefreshMode();
    }

    private string SelectedMode { get { return uninstallMode.Checked ? "Uninstall" : repairMode.Checked ? "Repair" : "Install"; } }

    private void RefreshMode()
    {
        var uninstalling = SelectedMode == "Uninstall";
        serverInput.Enabled = !uninstalling; installRootInput.Enabled = !uninstalling; browseButton.Enabled = !uninstalling;
        executeButton.Text = uninstalling ? "卸载" : SelectedMode == "Repair" ? "修复" : "安装";
        status.Text = uninstalling ? "将卸载所选目录中的 UPLM 客户端和 SolidWorks 插件。" : "准备就绪。";
    }

    private void BrowseInstallRoot()
    {
        var selected = Directory.Exists(installRootInput.Text) ? Directory.GetParent(installRootInput.Text).FullName : string.Empty;
        using (var dialog = new FolderBrowserDialog { Description = "请选择 UPLM 安装目录的上级文件夹", SelectedPath = selected })
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) installRootInput.Text = Path.Combine(dialog.SelectedPath, "UPLM");
        }
    }

    private void StartOperation()
    {
        var request = new SetupRequest { Mode = SelectedMode, InstallRoot = NormalizeInstallRoot(installRootInput.Text) };
        request.ServerBaseUrl = request.Mode == "Uninstall" ? serverInput.Text : ValidateServerBaseUrl(serverInput.Text);
        installRootInput.Text = request.InstallRoot; ToggleControls(false); progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 30; log.Clear();
        AppendLog("已开始 " + (request.Mode == "Uninstall" ? "卸载" : request.Mode == "Repair" ? "修复" : "安装") + "。请勿关闭此窗口。"); worker.RunWorkerAsync(request);
    }

    private void RunOperation(object sender, DoWorkEventArgs eventArgs)
    {
        var request = (SetupRequest)eventArgs.Argument; string temporaryRoot = null;
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UPLM", "logs"); Directory.CreateDirectory(logDirectory);
            logPath = Path.Combine(logDirectory, "client-" + request.Mode.ToLowerInvariant() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            temporaryRoot = Path.Combine(Path.GetTempPath(), "UPLM-Client-Setup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporaryRoot);
            Report("[UPLM_STEP:0/6] 正在准备安装数据…"); var archivePath = Path.Combine(temporaryRoot, "SetupContent.zip"); ExtractEmbeddedArchive(Assembly.GetExecutingAssembly().Location, archivePath); ZipFile.ExtractToDirectory(archivePath, temporaryRoot); File.Delete(archivePath);
            var scriptPath = Path.Combine(temporaryRoot, "Run-ClientSetup.ps1"); if (!File.Exists(scriptPath)) throw new InvalidDataException("客户端安装数据不完整。");
            var arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -Elevated -Mode " + request.Mode + " -InstallRoot \"" + request.InstallRoot + "\"";
            if (request.Mode != "Uninstall") arguments += " -ServerBaseUrl \"" + request.ServerBaseUrl + "\"";
            using (var process = new Process { StartInfo = new ProcessStartInfo { FileName = "powershell.exe", Arguments = arguments, UseShellExecute = false, WorkingDirectory = temporaryRoot, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                var lines = new StringBuilder(); DataReceivedEventHandler receive = (_, line) => { if (string.IsNullOrWhiteSpace(line.Data)) return; lock (lines) lines.AppendLine(line.Data); Report(line.Data); };
                process.OutputDataReceived += receive; process.ErrorDataReceived += receive; process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit(); File.WriteAllText(logPath, lines.ToString(), Encoding.UTF8);
                if (process.ExitCode != 0) throw new InvalidOperationException("操作失败，退出码：" + process.ExitCode + "。详细日志：" + logPath);
            }
            eventArgs.Result = request.Mode;
        }
        finally { if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot)) try { Directory.Delete(temporaryRoot, true); } catch { } }
    }

    private void CompleteOperation(object sender, RunWorkerCompletedEventArgs eventArgs)
    {
        progress.Style = ProgressBarStyle.Continuous; progress.Value = eventArgs.Error == null ? 100 : 0; ToggleControls(true); closeButton.Text = "关闭";
        if (eventArgs.Error != null) { status.Text = "操作失败。请查看下方详细信息。"; AppendLog("错误：" + eventArgs.Error.Message); return; }
        status.Text = "操作已完成。"; AppendLog("完成。日志：" + logPath);
    }

    private void ToggleControls(bool enabled) { installMode.Enabled = enabled; repairMode.Enabled = enabled; uninstallMode.Enabled = enabled; executeButton.Enabled = enabled; closeButton.Enabled = enabled; RefreshMode(); }
    private void AppendLog(string message) { if (string.IsNullOrWhiteSpace(message)) return; log.AppendText(message + Environment.NewLine); log.SelectionStart = log.TextLength; log.ScrollToCaret(); if (message.StartsWith("[UPLM_STEP:", StringComparison.Ordinal)) { var end = message.IndexOf("] ", StringComparison.Ordinal); status.Text = end >= 0 ? message.Substring(end + 2) : message; } }
    private void Report(string message) { worker.ReportProgress(0, message); }
    private static string NormalizeInstallRoot(string value) { var path = Path.GetFullPath((value ?? string.Empty).Trim()); return string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), "UPLM", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "UPLM"); }
    private static string ValidateServerBaseUrl(string value) { Uri uri; if (!Uri.TryCreate((value ?? string.Empty).TrimEnd('/'), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new InvalidDataException("服务器地址必须是完整 HTTP/HTTPS 地址，例如 http://10.7.7.88:5173。"); return uri.AbsoluteUri.TrimEnd('/'); }

    private static void ExtractEmbeddedArchive(string executablePath, string archivePath)
    {
        using (var input = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length <= 24) throw new InvalidDataException("客户端安装文件已损坏。"); input.Position = input.Length - 24;
            using (var reader = new BinaryReader(input, Encoding.ASCII, true))
            {
                var magic = reader.ReadBytes(8); var archiveOffset = reader.ReadInt64(); var archiveLength = reader.ReadInt64();
                if (Encoding.ASCII.GetString(magic) != "UPLMSFX1" || archiveOffset < 0 || archiveLength <= 0 || archiveOffset + archiveLength != input.Length - 24) throw new InvalidDataException("客户端安装文件标记无效。");
                input.Position = archiveOffset; using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            }
        }
    }
}

internal static class ClientSetupBootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var request = new SetupRequest { ServerBaseUrl = ReadArgument(args, "--server") ?? "http://10.7.7.88:5173", InstallRoot = ReadArgument(args, "--install-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM"), Mode = ReadArgument(args, "--mode") ?? "Install" };
        Application.Run(new ClientSetupForm(request)); return 0;
    }
    private static string ReadArgument(string[] args, string name) { for (var index = 0; index < args.Length - 1; index++) if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1]; return null; }
}
