using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Upton.Pdm.Desktop;

internal static class DesktopExternalRequestStore
{
    private static readonly string RequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        "desktop-request.txt");

    public static void Write(string[] arguments)
    {
        var directory = Path.GetDirectoryName(RequestPath)
            ?? throw new InvalidOperationException("无法确定PDM客户端请求目录。");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            RequestPath,
            (arguments ?? Array.Empty<string>()).Select(Encode),
            Encoding.ASCII);
    }

    public static string[] ReadAndDelete()
    {
        if (!File.Exists(RequestPath)) return Array.Empty<string>();

        try
        {
            return File.ReadAllLines(RequestPath, Encoding.ASCII)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(Decode)
                .ToArray();
        }
        finally
        {
            try { File.Delete(RequestPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
