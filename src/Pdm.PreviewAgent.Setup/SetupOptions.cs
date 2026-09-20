using System;
using System.Collections.Generic;
using System.IO;

namespace Upton.Pdm.PreviewAgent.Setup;

internal sealed class SetupOptions
{
    public const string DefaultTaskName = "UPLM Preview Agent";
    public const string FirewallRuleName = "UPLM Preview Agent";

    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 5199;
    public int TimeoutMinutes { get; set; } = 30;
    public string Token { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UPLM Preview Agent");
    public string WorkerPath { get; set; } = string.Empty;
    public string WorkRoot { get; set; } = string.Empty;
    public bool Silent { get; set; }
    public bool Uninstall { get; set; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < (args?.Length ?? 0); index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("-", StringComparison.Ordinal) && !argument.StartsWith("/", StringComparison.Ordinal)) continue;
            var name = argument.TrimStart('-', '/');
            var separator = name.IndexOf('=');
            if (separator >= 0) { map[name.Substring(0, separator)] = name.Substring(separator + 1); continue; }
            if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal) && !args[index + 1].StartsWith("/", StringComparison.Ordinal)) map[name] = args[++index];
            else map[name] = "true";
        }
        if (map.TryGetValue("server", out var server)) options.ServerUrl = server.Trim();
        if (map.TryGetValue("user", out var user)) options.Username = user.Trim();
        if (map.TryGetValue("password", out var password)) options.Password = password;
        if (map.TryGetValue("port", out var portText) && int.TryParse(portText, out var port) && port > 0) options.Port = port;
        if (map.TryGetValue("timeout", out var timeoutText) && int.TryParse(timeoutText, out var timeout) && timeout > 0) options.TimeoutMinutes = timeout;
        if (map.TryGetValue("token", out var token)) options.Token = token.Trim();
        if (map.TryGetValue("dir", out var directory) && !string.IsNullOrWhiteSpace(directory)) options.InstallDirectory = directory.Trim();
        if (map.TryGetValue("worker", out var worker)) options.WorkerPath = worker.Trim();
        if (map.TryGetValue("workroot", out var workRoot)) options.WorkRoot = workRoot.Trim();
        options.Silent = map.ContainsKey("silent");
        options.Uninstall = map.ContainsKey("uninstall");
        return options;
    }

    public static string GenerateToken()
    {
        var bytes = new byte[24];
        using (var generator = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            generator.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    /// <summary>取本机第一个可用的局域网IPv4地址，用于生成登记给PLM的访问地址。</summary>
    public static string DetectLocalAddress()
    {
        try
        {
            foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch (Exception)
        {
        }
        return "127.0.0.1";
    }
}
