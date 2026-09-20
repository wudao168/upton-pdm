using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.PreviewAgent;

/// <summary>
/// 转图代理配置：默认读取程序目录下的 PdmPreviewAgent.json，也可用命令行参数覆盖。
/// 用法：Upton.Pdm.PreviewAgent.exe [--port 5199] [--token xxx] [--worker 路径] [--timeout 30] [--workRoot 路径]
/// </summary>
public sealed class AgentOptions
{
    public int Port { get; set; } = 5199;

    public string BindAddress { get; set; } = "0.0.0.0";

    public string Token { get; set; } = string.Empty;

    public string WorkerPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-worker", "Upton.Pdm.SolidWorks.PreviewWorker.exe");

    public int TimeoutMinutes { get; set; } = 30;

    /// <summary>单个转换请求（zip）大小上限，默认 1GB。</summary>
    public long MaxRequestBytes { get; set; } = 1073741824L;

    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "UPTON-PLM", "preview-agent");

    /// <summary>追加给转换程序的参数（预留，默认无）。</summary>
    public List<string> WorkerArguments { get; set; } = new List<string>();

    public static AgentOptions Load(string[] args)
    {
        var options = new AgentOptions();
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PdmPreviewAgent.json");
        if (File.Exists(configPath))
        {
            var json = new JavaScriptSerializer().Deserialize<AgentOptions>(File.ReadAllText(configPath, Encoding.UTF8));
            if (json != null)
            {
                if (!string.IsNullOrWhiteSpace(json.Token)) options.Token = json.Token.Trim();
                if (!string.IsNullOrWhiteSpace(json.WorkerPath)) options.WorkerPath = json.WorkerPath.Trim();
                if (!string.IsNullOrWhiteSpace(json.WorkRoot)) options.WorkRoot = json.WorkRoot.Trim();
                if (json.TimeoutMinutes > 0) options.TimeoutMinutes = json.TimeoutMinutes;
                if (json.Port > 0) options.Port = json.Port;
                if (!string.IsNullOrWhiteSpace(json.BindAddress)) options.BindAddress = json.BindAddress.Trim();
                if (json.MaxRequestBytes > 0) options.MaxRequestBytes = json.MaxRequestBytes;
                if (json.WorkerArguments != null && json.WorkerArguments.Count > 0) options.WorkerArguments = json.WorkerArguments;
            }
        }

        var overrides = ParseArguments(args);
        if (overrides.TryGetValue("port", out var portText) && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
            options.Port = port;
        if (overrides.TryGetValue("token", out var token)) options.Token = token;
        if (overrides.TryGetValue("worker", out var worker)) options.WorkerPath = worker;
        if (overrides.TryGetValue("workroot", out var workRoot)) options.WorkRoot = workRoot;
        if (overrides.TryGetValue("bind", out var bind)) options.BindAddress = bind;
        if (overrides.TryGetValue("timeout", out var timeoutText) && int.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) && timeout > 0)
            options.TimeoutMinutes = timeout;

        if (string.IsNullOrWhiteSpace(options.BindAddress)) options.BindAddress = "0.0.0.0";
        Directory.CreateDirectory(options.WorkRoot);
        return options;
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args == null) return result;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
            var name = argument.Substring(2);
            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                result[name.Substring(0, separator)] = name.Substring(separator + 1);
                continue;
            }
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                result[name] = args[++index];
        }
        return result;
    }
}
