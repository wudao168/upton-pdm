using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.PreviewAgent.Setup;

/// <summary>
/// 转图代理的一键安装/卸载：解包 → 写配置 → 放通防火墙 → 注册开机自启 → 启动 → 验证 /health → 登录PLM并把本机登记为转图服务器。
/// </summary>
internal sealed class SetupRunner
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private readonly Action<string> log;
    private readonly SetupOptions options;
    private string monitorToken;
    private string monitorIdentity;
    private bool monitorLoginFailed;

    public SetupRunner(SetupOptions options, Action<string> log)
    {
        this.options = options;
        this.log = log ?? (_ => { });
    }

    public async Task<string> ReadStatusAsync()
    {
        string local;
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        {
            var health = Serializer.Deserialize<AgentStatus>(await client.GetStringAsync($"http://127.0.0.1:{options.Port}/health"));
            local = $"{health.Name ?? health.Machine}：{(health.Busy ? "执行中" : "空闲")}；本机排队 {health.Queued} 项\r\n当前任务：{(string.IsNullOrWhiteSpace(health.CurrentTask) ? "无" : health.CurrentTask)}";
            if (!string.IsNullOrWhiteSpace(health.ConversionStage)) local += $"\r\nSolidWorks：{(health.SolidWorksRunning ? "运行中（PID " + health.SolidWorksPid + "）" : "未运行")}\r\n转换阶段：{health.ConversionStage}；已完成 {health.Completed}/{health.Total}；最近进度：{health.ProgressUpdatedAt}";
        }
        if (string.IsNullOrWhiteSpace(options.ServerUrl) || string.IsNullOrWhiteSpace(options.Password)) return local + "\r\n填写PLM账号后可读取服务器队列。";
        try
        {
            var identity = options.ServerUrl + "\n" + options.Username + "\n" + options.Password;
            if (identity != monitorIdentity) { monitorIdentity = identity; monitorToken = null; monitorLoginFailed = false; }
            if (monitorLoginFailed) return local + "\r\nPLM队列登录失败，请更正服务器地址或账号密码。";
            if (string.IsNullOrWhiteSpace(monitorToken))
            {
                try { monitorToken = await LoginAsync(options.ServerUrl.TrimEnd('/')); }
                catch { monitorLoginFailed = true; throw; }
            }
            using (var client = NewClient(monitorToken))
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"{options.ServerUrl.TrimEnd('/')}/api/system-settings/preview-agent/queue");
                if (!response.IsSuccessStatusCode) { if ((int)response.StatusCode == 401) monitorToken = null; return local + "\r\nPLM队列读取失败：" + (int)response.StatusCode; }
                var queue = Serializer.Deserialize<QueueStatus>(await response.Content.ReadAsStringAsync());
                return local + $"\r\nPLM调度排队：{string.Join("；", queue.Scheduling.Queued)}\r\nPLM执行：{string.Join("；", queue.Scheduling.Executing.Select(item => item.Server + "：" + item.Task))}\r\n待完成发布单：{string.Join("；", queue.PendingPublishing)}";
            }
        }
        catch (Exception exception) { return local + "\r\nPLM队列暂不可读：" + Describe(exception); }
    }

    private sealed class AgentStatus { public string Name { get; set; } public string Machine { get; set; } public bool Busy { get; set; } public bool? Processing { get; set; } public int Queued { get; set; } public string CurrentTask { get; set; } public bool SolidWorksRunning { get; set; } public int SolidWorksPid { get; set; } public string ConversionStage { get; set; } public int Completed { get; set; } public int Total { get; set; } public string ProgressUpdatedAt { get; set; } }
    private sealed class QueueStatus { public SchedulingStatus Scheduling { get; set; } public string[] PendingPublishing { get; set; } }
    private sealed class SchedulingStatus { public string[] Queued { get; set; } public ExecutionStatus[] Executing { get; set; } }
    private sealed class ExecutionStatus { public string Server { get; set; } public string Task { get; set; } }

    public async Task<bool> InstallAsync()
    {
        ValidateInstallDirectory(false);
        if (options.Port < 1 || options.Port > 65535) throw new InvalidOperationException("代理端口必须在1到65535之间。");
        if (options.TimeoutMinutes < 1 || options.TimeoutMinutes > 120) throw new InvalidOperationException("转换超时必须在1到120分钟之间。");
        if (string.IsNullOrWhiteSpace(options.ServerUrl)) throw new InvalidOperationException("请填写 PLM 服务器地址，例如 http://192.168.2.8:5173。");
        if (!Uri.TryCreate(options.ServerUrl.TrimEnd('/'), UriKind.Absolute, out var serverUri) || serverUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("PLM 服务器地址格式不正确，请填写 http:// 或 https:// 开头的完整地址。");
        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("请填写具有系统设置权限的管理员账号与密码（用于把本机登记为转图服务器）。");
        if (string.IsNullOrWhiteSpace(options.Token)) options.Token = SetupOptions.GenerateToken();

        var serverUrl = await ResolveServerUrlAsync();
        options.ServerUrl = serverUrl;
        log($"   已连接PLM服务器：{serverUrl}");
        var agentDirectory = options.InstallDirectory;
        log($"安装目录：{agentDirectory}");

        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        {
            AgentStatus installedStatus = null;
            try { installedStatus = Serializer.Deserialize<AgentStatus>(await client.GetStringAsync($"http://127.0.0.1:{options.Port}/health")); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (installedStatus != null && ((installedStatus.Processing ?? installedStatus.Busy) || installedStatus.Queued > 0))
                throw new InvalidOperationException("本机正在转换图纸或仍有排队任务，请等任务完成后再更新代理。");
        }
        StopAgentProcesses();
        StopInstalledManagerAndWorker();

        log("1/7 释放转图代理文件…");
        ExtractPayload(agentDirectory);
        log($"1/7 已释放 {EnumeratePayload().Count()} 个文件。");

        log("2/7 写入转图代理配置…");
        WriteAgentConfiguration(agentDirectory);

        log("3/7 放通防火墙端口…");
        RunTool("netsh", $"advfirewall firewall delete rule name=\"{SetupOptions.FirewallRuleName}\"");
        var firewall = RunTool("netsh", $"advfirewall firewall add rule name=\"{SetupOptions.FirewallRuleName}\" dir=in action=allow protocol=TCP localport={options.Port}");
        if (firewall.ExitCode != 0) { RecordDiagnostic(firewall.Output); throw new InvalidOperationException("防火墙规则添加失败，请以管理员身份运行，并检查系统防火墙服务后重试。"); }

        log("4/7 注册开机自启…");
        var agentExe = Path.Combine(agentDirectory, "Upton.Pdm.PreviewAgent.exe");
        var managerExe = Path.Combine(agentDirectory, "UplmPreviewAgentSetup.exe");
        var currentExe = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(managerExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(currentExe, managerExe, true);
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName}\" /F");
        var task = RunTool("schtasks", $"/Create /TN \"{SetupOptions.DefaultTaskName}\" /TR \"\\\"{agentExe}\\\" --startup\" /SC ONLOGON /IT /RL HIGHEST /F");
        if (task.ExitCode != 0) { RecordDiagnostic(task.Output); throw new InvalidOperationException("代理登录启动任务创建失败，请检查管理员权限及Windows任务计划服务后重试。"); }
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName} Manager\" /F");

        log("5/7 启动转图代理…");
        StopAgentProcesses();
        Process.Start(new ProcessStartInfo(agentExe) { WorkingDirectory = agentDirectory, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
        var health = await WaitForHealthAsync(agentExe);
        if (health is null) throw new InvalidOperationException($"转图代理启动后 /health 探测失败，请查看 {Path.Combine(agentDirectory, "setup.log")} 与代理窗口输出。");
        log($"5/7 代理已就绪：机器 {health.Machine}，转换程序存在={health.WorkerExists}");
        if (!health.WorkerExists) throw new InvalidOperationException("代理未找到 SolidWorks 转换程序，请检查安装目录下的 preview-worker 子目录。");

        log("6/7 登录PLM…");
        string token;
        try
        {
            token = await LoginAsync(serverUrl);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"登录PLM失败：{Describe(exception)}");
        }
        log("6/7 登录成功。");

        log("7/7 登记本机为转图服务器…");
        var localAddress = SetupOptions.DetectLocalAddress();
        var agentUrl = $"http://{localAddress}:{options.Port}";
        await RegisterAsync(serverUrl, token, agentUrl);
        var probe = await ProbeAsync(serverUrl, token);
        log($"7/7 {probe}");
        if (probe.StartsWith("服务器侧连通性测试未通过", StringComparison.Ordinal) || probe.Contains("无法连接转图服务器"))
        {
            log("   服务器无法访问本机代理，将移除本机登记，其他转图服务器继续使用。");
            using (var client = NewClient(token))
            {
                try
                {
                    await client.PostAsync($"{serverUrl}/api/system-settings/preview-agent/unregister", new StringContent(Serializer.Serialize(new { agentUrl = $"http://{SetupOptions.DetectLocalAddress()}:{options.Port}" }), Encoding.UTF8, "application/json"));
                }
                catch (Exception)
                {
                }
            }
            throw new InvalidOperationException($"PLM服务器无法访问本机代理（{agentUrl}）：{probe} 。请确认服务器与本机网络互通、本机防火墙已放通 {options.Port} 端口，然后重新运行本安装程序。");
        }
        log("安装完成：本转图电脑已登记到PLM，后续正式发布会自动使用本机转换。");
        return true;
    }

    public async Task<bool> UpgradeAsync()
    {
        ValidateInstallDirectory(true);
        var configuration = Path.Combine(options.InstallDirectory, "PdmPreviewAgent.json");
        if (!File.Exists(configuration)) throw new InvalidOperationException("目标目录未安装转图代理，请先使用“一键安装并登记”，或选择正确的安装目录。");
        var hash = FileHash(configuration);
        var extractionRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UPLM-PreviewAgent-Setup"));
        var temporary = Path.Combine(extractionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            log("1/3 校验并准备完整更新文件，保留现有服务器、端口及访问令牌…");
            ExtractPayload(Path.Combine(temporary, "payload"));
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "Update-PreviewAgent.ps1", "files.json" })
                using (var source = assembly.GetManifestResourceStream("upgrade." + name))
                using (var target = File.Create(Path.Combine(temporary, name)))
                {
                    if (source == null) throw new InvalidOperationException("安装包缺少升级校验文件，请使用完整安装包重试。");
                    source.CopyTo(target);
                }
            log("2/3 更新代理和转换程序，切换为交互式前台启动；失败自动恢复原文件和任务…");
            var script = Path.Combine(temporary, "Update-PreviewAgent.ps1");
            var result = await Task.Run(() => RunTool("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script
                + "\" -InstallRoot \"" + options.InstallDirectory + "\" -PreserveProcessId " + Process.GetCurrentProcess().Id, Encoding.UTF8));
            File.WriteAllText(Path.Combine(options.InstallDirectory, "upgrade-installer.log"), Upton.Pdm.ClientShared.ClientLogRedaction.Clean(result.Output), new UTF8Encoding(false));
            if (result.ExitCode != 0) throw new InvalidOperationException("升级未完成，请根据上方中文提示检查代理状态；原文件和回退结果保留在安装目录的备份文件夹中。");
            if (FileHash(configuration) != hash) throw new InvalidOperationException("升级后的原连接配置校验不一致，请检查备份配置后重试。");
            var manager = Path.Combine(options.InstallDirectory, "UplmPreviewAgentSetup.exe");
            var current = assembly.Location;
            if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(manager), StringComparison.OrdinalIgnoreCase)) File.Copy(current, manager, true);
            log("3/3 升级完成，原连接配置已保留，前台代理已启动。");
            return true;
        }
        finally
        {
            // Only this invocation's randomly named extraction directory is removed.
            if (Directory.Exists(temporary) && Path.GetFullPath(temporary).StartsWith(extractionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(temporary, true);
        }
    }

    private static string FileHash(string path)
    {
        using (var input = File.OpenRead(path))
        using (var algorithm = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(algorithm.ComputeHash(input));
    }

    public async Task<bool> UninstallAsync()
    {
        ValidateInstallDirectory(true);
        log("1/5 停止转图代理…");
        StopAgentProcesses();
        log("2/5 删除开机自启…");
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName}\" /F");
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName} Manager\" /F");
        log("3/5 删除防火墙规则…");
        RunTool("netsh", $"advfirewall firewall delete rule name=\"{SetupOptions.FirewallRuleName}\"");
        if (!string.IsNullOrWhiteSpace(options.ServerUrl) && !string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password))
        {
            log("4/5 通知PLM移除本机登记…");
            try
            {
                var token = await LoginAsync(options.ServerUrl.TrimEnd('/'));
                using (var client = NewClient(token))
                {
                    var response = await client.PostAsync($"{options.ServerUrl.TrimEnd('/')}/api/system-settings/preview-agent/unregister", new StringContent(Serializer.Serialize(new { agentUrl = $"http://{SetupOptions.DetectLocalAddress()}:{options.Port}" }), Encoding.UTF8, "application/json"));
                    log(response.IsSuccessStatusCode ? "4/5 已移除本机登记，其他转图服务器保留。" : $"4/5 通知PLM失败：{(int)response.StatusCode}");
                }
            }
            catch (Exception exception)
            {
                log($"4/5 通知PLM失败（可稍后在设置页切换为“本机”）：{exception.Message}");
            }
        }
        else
        {
            log("4/5 未填写PLM管理员账号，跳过服务器侧切换（可在设置页把图纸转换改回“本机”）。");
        }

        log("5/5 删除安装目录…");
        try
        {
            if (Directory.Exists(options.InstallDirectory))
            {
                var currentExe = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                var root = Path.GetFullPath(options.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (currentExe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                        if (!string.Equals(file, currentExe, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                    foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) Directory.Delete(directory);
                    if (!MoveFileEx(currentExe, null, 4) || !MoveFileEx(root, null, 4))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    log("代理已卸载，当前管理程序文件将在重启电脑后移除。");
                }
                else Directory.Delete(root, true);
            }
        }
        catch (Exception exception)
        {
            log($"   删除安装目录失败：{exception.Message}");
            return false;
        }
        log("卸载完成。");
        return true;
    }

    private void ValidateInstallDirectory(bool requireInstalled)
    {
        if (string.IsNullOrWhiteSpace(options.InstallDirectory) || !Path.IsPathRooted(options.InstallDirectory))
            throw new InvalidOperationException("请选择转图代理的独立安装文件夹，例如 C:\\Program Files\\UPLM Preview Agent。");
        var root = Path.GetFullPath(options.InstallDirectory).TrimEnd('\\', '/');
        var protectedRoots = new[] { Path.GetPathRoot(root), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (protectedRoots.Any(path => !string.IsNullOrWhiteSpace(path) && string.Equals(root, path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能使用磁盘根目录或系统公共目录，请选择转图代理的独立安装文件夹。");
        var installed = File.Exists(Path.Combine(root, "Upton.Pdm.PreviewAgent.exe"));
        if (requireInstalled && (!installed || !File.Exists(Path.Combine(root, "PdmPreviewAgent.json"))))
            throw new InvalidOperationException("所选目录不是已安装的转图代理，请检查安装目录后重试。");
        if (!requireInstalled && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !installed)
            throw new InvalidOperationException("所选目录已有其他文件，请选择空文件夹或已有转图代理的专用安装目录。");
        options.InstallDirectory = root;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

    private static List<ManifestResource> EnumeratePayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = new List<ManifestResource>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith("payload.worker.", StringComparison.Ordinal))
                resources.Add(new ManifestResource(name, "preview-worker", name.Substring("payload.worker.".Length)));
            else if (name.StartsWith("payload.agent.", StringComparison.Ordinal))
                resources.Add(new ManifestResource(name, string.Empty, name.Substring("payload.agent.".Length)));
        }
        return resources;
    }

    private void ExtractPayload(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(Path.Combine(targetDirectory, "preview-worker"));
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in EnumeratePayload())
        {
            var target = Path.Combine(targetDirectory, resource.RelativeDirectory, resource.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var source = assembly.GetManifestResourceStream(resource.Name))
            using (var file = File.Create(target))
            {
                if (source is null) throw new InvalidOperationException($"安装包缺少资源 {resource.Name}。");
                source.CopyTo(file);
            }
        }
    }

    private void WriteAgentConfiguration(string agentDirectory)
    {
        var configuration = new Dictionary<string, object>
        {
            ["Port"] = options.Port,
            ["ServerUrl"] = options.ServerUrl,
            ["ShowMainWindow"] = true,
            ["CloseToTray"] = true,
            ["StartWithWindows"] = true,
            ["Name"] = options.Name,
            ["BindAddress"] = "0.0.0.0",
            ["Token"] = options.Token,
            ["WorkerPath"] = options.WorkerPath ?? string.Empty,
            ["WorkRoot"] = options.WorkRoot ?? string.Empty,
            ["TimeoutMinutes"] = options.TimeoutMinutes
        };
        File.WriteAllText(Path.Combine(agentDirectory, "PdmPreviewAgent.json"), Serializer.Serialize(configuration), new UTF8Encoding(false));
    }

    private void StopAgentProcesses()
    {
        var agentExe = Path.Combine(options.InstallDirectory, "Upton.Pdm.PreviewAgent.exe");
        foreach (var process in Process.GetProcessesByName("Upton.Pdm.PreviewAgent"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, agentExe, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private void StopInstalledManagerAndWorker()
    {
        var paths = new[] { Path.Combine(options.InstallDirectory, "UplmPreviewAgentSetup.exe"),
            Path.Combine(options.InstallDirectory, "preview-worker", "Upton.Pdm.SolidWorks.PreviewWorker.exe") };
        foreach (var name in new[] { "UplmPreviewAgentSetup", "Upton.Pdm.SolidWorks.PreviewWorker" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            if (process.Id == Process.GetCurrentProcess().Id) continue;
            if (!paths.Any(path => string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (name == "Upton.Pdm.SolidWorks.PreviewWorker" && process.WaitForExit(3000)) continue;
            process.Kill();
            process.WaitForExit(10000);
        }
    }

    private async Task<HealthResult> WaitForHealthAsync(string agentExe)
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 1));
                try
                {
                    var json = await client.GetStringAsync($"http://127.0.0.1:{options.Port}/health");
                    var result = Serializer.Deserialize<HealthResult>(json);
                    if (result is not null && string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)) return result;
                }
                catch (Exception)
                {
                    if (!IsProcessRunning(agentExe)) return null;
                }
            }
        }
        return null;
    }

    private static bool IsProcessRunning(string agentExe) =>
        Process.GetProcessesByName("Upton.Pdm.PreviewAgent").Any(process =>
        {
            try { return string.Equals(process.MainModule?.FileName, agentExe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        });

    private async Task<string> LoginAsync(string serverUrl)
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var payload = Serializer.Serialize(new { username = options.Username, password = options.Password });
            var response = await client.PostAsync($"{serverUrl}/api/auth/login", new StringContent(payload, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"PLM登录失败（{(int)response.StatusCode}）：{serverUrl}/api/auth/login 。请确认账号有权限、密码正确（该地址需能从本机访问PLM）。");
            var body = await response.Content.ReadAsStringAsync();
            var login = Serializer.Deserialize<LoginResult>(body);
            if (login is null || string.IsNullOrWhiteSpace(login.AccessToken)) throw new InvalidOperationException("PLM登录返回内容无效。");
            return login.AccessToken;
        }
    }

    private async Task RegisterAsync(string serverUrl, string token, string agentUrl)
    {
        using (var client = NewClient(token))
        {
            var payload = Serializer.Serialize(new { name = options.Name, agentUrl, agentToken = options.Token, timeoutMinutes = options.TimeoutMinutes });
            var response = await client.PostAsync($"{serverUrl}/api/system-settings/preview-agent/register", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) { RecordDiagnostic(body); throw new InvalidOperationException($"登记转图服务器失败（HTTP {(int)response.StatusCode}），请检查当前账号的系统设置权限及代理地址后重试。"); }
            var result = Serializer.Deserialize<RegisterResult>(body);
            log($"   已登记：{result?.AgentUrl ?? agentUrl}（超时 {result?.TimeoutMinutes ?? options.TimeoutMinutes} 分钟）");
        }
    }

    private async Task<string> ProbeAsync(string serverUrl, string token)
    {
        using (var client = NewClient(token))
        {
            var payload = Serializer.Serialize(new { agentUrl = string.Empty, agentToken = options.Token, timeoutMinutes = options.TimeoutMinutes });
            var response = await client.PostAsync($"{serverUrl}/api/system-settings/preview-agent/test", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) { RecordDiagnostic(body); return $"服务器侧连通性测试未通过（HTTP {(int)response.StatusCode}），请检查目标服务器地址、端口和防火墙。"; }
            var result = Serializer.Deserialize<ProbeResult>(body);
            return result?.Message ?? "服务器侧连通性测试完成。";
        }
    }

    private static HttpClient NewClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// 解析可用的PLM地址：优先用填写的地址；连不上时自动尝试同一主机的 5173 / 5080 端口。
    /// （API 默认只把 5080 绑定在服务器本机回环，局域网访问走 5173。）
    /// </summary>
    private async Task<string> ResolveServerUrlAsync()
    {
        var entered = options.ServerUrl.Trim().TrimEnd('/');
        var candidates = new List<string> { entered };
        if (Uri.TryCreate(entered, UriKind.Absolute, out var uri))
        {
            foreach (var port in new[] { 5173, 5080 })
            {
                if (uri.Port == port) continue;
                var builder = new UriBuilder(uri) { Port = port };
                candidates.Add(builder.Uri.ToString().TrimEnd('/'));
            }
        }
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    var json = await client.GetStringAsync($"{candidate}/health");
                    if (json.IndexOf("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase) >= 0
                        || json.IndexOf("\"status\": \"ok\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!string.Equals(candidate, entered, StringComparison.OrdinalIgnoreCase))
                            log($"   填写地址 {entered} 无法访问，已自动改用 {candidate}。");
                        return candidate;
                    }
                    failures.Add($"{candidate}：/health 返回内容异常");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{candidate}：{Describe(exception)}");
            }
        }
        throw new InvalidOperationException(
            "无法访问PLM服务器，请填写“浏览器里打开PLM的地址”（局域网一般是 http://<服务器IP>:5173；5080端口只对服务器本机开放）。已尝试："
            + string.Join("；", failures));
    }

    /// <summary>把 HttpRequestException 里的内层原因（拒绝连接/超时/DNS）取出来，便于排查。</summary>
    internal static string Describe(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return "权限不足，请以管理员身份运行，并检查安装目录的读写权限。";
        if (exception is IOException) return "文件读写未完成，请检查文件是否被占用、目录权限和磁盘空间后重试。";
        if (exception is HttpRequestException) return "无法连接PLM服务器，请检查服务器地址、网络和防火墙后重试。";
        if (!(exception is OperationCanceledException) && (exception.Message == null || !exception.Message.Any(value => value >= '\u4e00' && value <= '\u9fff')
            || exception.Message.Contains("Exception") || exception.Message.Contains("System."))) return "操作未完成，请检查输入信息和安装日志后重试。";
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current is System.Threading.Tasks.TaskCanceledException || current is OperationCanceledException
                ? "连接超时（可能被防火墙拦截或地址不可达）"
                : current.Message?.Trim();
            if (!string.IsNullOrWhiteSpace(message) && message.Any(value => value >= '\u4e00' && value <= '\u9fff') && !messages.Contains(message)) messages.Add(message);
        }
        return string.Join(" ← ", messages);
    }

    internal static void RecordDiagnostic(string text)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "preview-agent-setup-diagnostics.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + " " + Upton.Pdm.ClientShared.ClientLogRedaction.Clean(text) + Environment.NewLine, Encoding.UTF8);
    }

    private static ToolResult RunTool(string fileName, string arguments, Encoding encoding = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (encoding != null) { startInfo.StandardOutputEncoding = encoding; startInfo.StandardErrorEncoding = encoding; }
        using (var process = Process.Start(startInfo))
        {
            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            process.WaitForExit(60000);
            return new ToolResult(process.HasExited ? process.ExitCode : -1, output);
        }
    }

    private sealed class ManifestResource
    {
        public ManifestResource(string name, string relativeDirectory, string fileName)
        {
            Name = name;
            RelativeDirectory = relativeDirectory;
            FileName = fileName;
        }

        public string Name { get; }
        public string RelativeDirectory { get; }
        public string FileName { get; }
    }

    private sealed class ToolResult
    {
        public ToolResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        public int ExitCode { get; }
        public string Output { get; }
    }

    private sealed class HealthResult
    {
        public string Status { get; set; }
        public string Machine { get; set; }
        public bool WorkerExists { get; set; }
    }

    private sealed class LoginResult
    {
        public string AccessToken { get; set; }
    }

    private sealed class RegisterResult
    {
        public bool Ok { get; set; }
        public string AgentUrl { get; set; }
        public int TimeoutMinutes { get; set; }
        public string Message { get; set; }
    }

    private sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
    }
}
