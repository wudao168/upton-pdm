using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Upton.Pdm.Desktop;

internal sealed class SolidWorksOpenBridge
{
    private const string AddinClsid = "{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    public bool IsAvailable => FindSolidWorksExecutable() != null && IsAddinRegistered();

    public async Task<string> SendAsync(Guid projectId, Guid documentId, Guid? versionId, string mode, CancellationToken cancellationToken,
        Func<string, Task> reportWaiting)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("当前电脑未安装SolidWorks或UPLM插件，不能使用SolidWorks打开。");
        }

        var request = new { projectId, documentId, versionId, mode };
        var json = serializer.Serialize(request);
        if (await SolidWorksPipeSender.SendAsync(PipeName(), json, cancellationToken))
        {
            return "打开请求已发送到SolidWorks插件。";
        }

        if (IsSolidWorksRunning())
        {
            await reportWaiting("SolidWorks暂未就绪，可能正在退出或加载，正在等待，稍后将自动继续打开…");
            var result = await SolidWorksOpenWaiter.WaitAsync(IsSolidWorksRunning,
                () => SolidWorksPipeSender.SendAsync(PipeName(), json, cancellationToken, 500), cancellationToken);
            if (result == SolidWorksWaitResult.Accepted) return "打开请求已发送到SolidWorks插件。";
            if (result == SolidWorksWaitResult.TimedOut)
                return "SolidWorks仍未就绪，本次等待已结束。请待其退出或加载完成后重新点击打开；若持续如此，请检查UPLM加载项。";
        }

        cancellationToken.ThrowIfCancellationRequested();
        QueueForStartup(json);
        var executable = FindSolidWorksExecutable() ?? throw new InvalidOperationException("未找到SolidWorks安装目录。");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        return "SolidWorks正在启动，插件加载后将自动打开受控图档。";
    }

    private static bool IsSolidWorksRunning()
    {
        using (var current = Process.GetCurrentProcess())
        {
            var running = false;
            foreach (var process in Process.GetProcessesByName("SLDWORKS"))
            {
                using (process)
                {
                    try { running |= !process.HasExited && process.SessionId == current.SessionId; }
                    catch (InvalidOperationException) { /* Exited while taking the snapshot. */ }
                }
            }
            return running;
        }
    }

    private static void QueueForStartup(string json)
    {
        var path = PendingRequestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + ".new";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    private static bool IsAddinRegistered()
    {
        using (var key = Registry.ClassesRoot.OpenSubKey(string.Concat("CLSID\\", AddinClsid, "\\InprocServer32")))
        {
            var codeBase = key?.GetValue("CodeBase") as string;
            if (string.IsNullOrWhiteSpace(codeBase)) return false;
            return Uri.TryCreate(codeBase, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath);
        }
    }

    private static string? FindSolidWorksExecutable()
    {
        var known = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp", "SOLIDWORKS", "SLDWORKS.exe");
        if (File.Exists(known)) return known;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SLDWORKS.exe"))
        {
            var path = key?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    private static string PipeName()
    {
        var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? Environment.UserName;
        return string.Concat("UPTON.PDM.SolidWorks.", sid.Replace('-', '_').Replace('\\', '_'));
    }

    private static string PendingRequestPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "solidworks-open-request.json");
}
