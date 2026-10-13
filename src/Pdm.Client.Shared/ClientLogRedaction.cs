#nullable disable
using System;
using System.Text.RegularExpressions;

namespace Upton.Pdm.ClientShared;

public static class ClientLogRedaction
{
    public static string Clean(string text)
    {
        var safe = text ?? string.Empty;
        if (safe.Length > 32768) safe = safe.Substring(0, 32768) + " [内容已截断]";
        safe = Regex.Replace(safe, @"(?i)\bBearer\s+[^\s""',;]+", "Bearer [已隐藏]");
        const string keys = @"(?:access[_-]?token|refresh[_-]?token|token|api[_-]?key|client[_-]?secret|password|passwd|pwd|authorization|cookie|secret|密码|口令)";
        safe = Regex.Replace(safe, @"(?i)(\b" + keys + @"\b[""']?\s*[:=]\s*)(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')", "$1\"[已隐藏]\"");
        safe = Regex.Replace(safe, @"(?i)(\b" + keys + @"\b[""']?\s*[:=]\s*)([^\s""',;&}]+)", "$1[已隐藏]");
        safe = Regex.Replace(safe, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[登录凭据已隐藏]");
        return Regex.Replace(safe, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[已隐藏]@");
    }
}
