using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.PreviewAgent.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args);
        if (options.Silent)
        {
            return RunSilentAsync(options).GetAwaiter().GetResult();
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(options));
        return 0;
    }

    private static async Task<int> RunSilentAsync(SetupOptions options)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "uplm-preview-agent-setup.log");
        var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string message)
        {
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.WriteLine(message);
        }
        try
        {
            Log(options.Uninstall ? "开始卸载转图代理…" : "开始安装转图代理…");
            var runner = new SetupRunner(options, Log);
            var ok = options.Uninstall ? await runner.UninstallAsync() : await runner.InstallAsync();
            Log(ok ? "完成。" : "失败，请查看以上日志。");
            return ok ? 0 : 1;
        }
        catch (Exception exception)
        {
            Log($"失败：{exception.Message}");
            return 1;
        }
        finally
        {
            Log($"日志文件：{logPath}");
            writer.Dispose();
        }
    }
}
