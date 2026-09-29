using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Linq;

internal static class ClientUninstallBootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var principal = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                var executablePath = Assembly.GetExecutingAssembly().Location;
                using (var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = string.Join(" ", args.Select(argument => "\"" + argument.Replace("\"", "\\\"") + "\"")),
                    Verb = "runas",
                    UseShellExecute = true
                }))
                {
                    elevated.WaitForExit();
                    return elevated.ExitCode;
                }
            }

            var installRoot = ReadInstallRoot(args);
            var executableDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var scriptPath = Path.Combine(executableDirectory, "Uninstall-UPLMClient.ps1");
            if (!File.Exists(scriptPath)) throw new InvalidDataException("卸载工具不完整，请重新运行客户端升级安装包。");

            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UPLM", "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "client-uninstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -NoElevation -InstallRoot \"" + installRoot + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(startInfo))
            {
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                File.WriteAllText(logPath, output, Encoding.UTF8);
                if (process.ExitCode != 0) throw new InvalidOperationException("卸载失败，详细日志：" + logPath);
            }
            MessageBox.Show("UPLM 客户端和 SolidWorks 插件已卸载。", "UPLM 客户端卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "UPLM 客户端卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string ReadInstallRoot(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--install-root", StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM");
    }
}
