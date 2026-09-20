using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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

    public SetupRunner(SetupOptions options, Action<string> log)
    {
        this.options = options;
        this.log = log ?? (_ => { });
    }

    public async Task<bool> InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(options.ServerUrl)) throw new InvalidOperationException("请填写 PLM 服务器地址，例如 http://192.168.2.8:5173。");
        if (!Uri.TryCreate(options.ServerUrl.TrimEnd('/'), UriKind.Absolute, out var serverUri) || serverUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("PLM 服务器地址格式不正确，请填写 http:// 或 https:// 开头的完整地址。");
        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("请填写具有系统设置权限的管理员账号与密码（用于把本机登记为转图服务器）。");
        if (string.IsNullOrWhiteSpace(options.Token)) options.Token = SetupOptions.GenerateToken();

        var serverUrl = await ResolveServerUrlAsync();
        log($"   已连接PLM服务器：{serverUrl}");
        var agentDirectory = options.InstallDirectory;
        log($"安装目录：{agentDirectory}");

        log("1/7 释放转图代理文件…");
        ExtractPayload(agentDirectory);
        log($"1/7 已释放 {EnumeratePayload().Count()} 个文件。");

        log("2/7 写入转图代理配置…");
        WriteAgentConfiguration(agentDirectory);

        log("3/7 放通防火墙端口…");
        RunTool("netsh", $"advfirewall firewall delete rule name=\"{SetupOptions.FirewallRuleName}\"");
        var firewall = RunTool("netsh", $"advfirewall firewall add rule name=\"{SetupOptions.FirewallRuleName}\" dir=in action=allow protocol=TCP localport={options.Port}");
        if (firewall.ExitCode != 0) log($"   防火墙规则添加返回：{firewall.Output.Trim()}");

        log("4/7 注册开机自启…");
        var agentExe = Path.Combine(agentDirectory, "Upton.Pdm.PreviewAgent.exe");
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName}\" /F");
        var task = RunTool("schtasks", $"/Create /TN \"{SetupOptions.DefaultTaskName}\" /TR \"\\\"{agentExe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        if (task.ExitCode != 0) log($"   计划任务创建返回：{task.Output.Trim()}");

        log("5/7 启动转图代理…");
        StopAgentProcesses();
        Process.Start(new ProcessStartInfo(agentExe) { WorkingDirectory = agentDirectory, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
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
            log("   服务器无法访问本机代理，已自动把PLM切回“本机转换”，避免发布被卡住。");
            using (var client = NewClient(token))
            {
                try
                {
                    await client.PostAsync($"{serverUrl}/api/system-settings/preview-agent/unregister", new StringContent("{}", Encoding.UTF8, "application/json"));
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

    public async Task<bool> UninstallAsync()
    {
        log("1/5 停止转图代理…");
        StopAgentProcesses();
        log("2/5 删除开机自启…");
        RunTool("schtasks", $"/Delete /TN \"{SetupOptions.DefaultTaskName}\" /F");
        log("3/5 删除防火墙规则…");
        RunTool("netsh", $"advfirewall firewall delete rule name=\"{SetupOptions.FirewallRuleName}\"");
        if (!string.IsNullOrWhiteSpace(options.ServerUrl) && !string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password))
        {
            log("4/5 通知PLM恢复为本机转换…");
            try
            {
                var token = await LoginAsync(options.ServerUrl.TrimEnd('/'));
                using (var client = NewClient(token))
                {
                    var response = await client.PostAsync($"{options.ServerUrl.TrimEnd('/')}/api/system-settings/preview-agent/unregister", new StringContent("{}", Encoding.UTF8, "application/json"));
                    log(response.IsSuccessStatusCode ? "4/5 已通知PLM恢复为本机转换。" : $"4/5 通知PLM失败：{(int)response.StatusCode}");
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
            if (Directory.Exists(options.InstallDirectory)) Directory.Delete(options.InstallDirectory, true);
        }
        catch (Exception exception)
        {
            log($"   删除安装目录失败：{exception.Message}");
            return false;
        }
        log("卸载完成。");
        return true;
    }

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
            var payload = Serializer.Serialize(new { agentUrl, agentToken = options.Token, timeoutMinutes = options.TimeoutMinutes });
            var response = await client.PostAsync($"{serverUrl}/api/system-settings/preview-agent/register", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"登记转图服务器失败（{(int)response.StatusCode}）：{body}");
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
            if (!response.IsSuccessStatusCode) return $"服务器侧连通性测试未通过（{(int)response.StatusCode}）：{body}";
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
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current is System.Threading.Tasks.TaskCanceledException || current is OperationCanceledException
                ? "连接超时（可能被防火墙拦截或地址不可达）"
                : current.Message?.Trim();
            if (!string.IsNullOrWhiteSpace(message) && !messages.Contains(message)) messages.Add(message);
        }
        return string.Join(" ← ", messages);
    }

    private static ToolResult RunTool(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
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
