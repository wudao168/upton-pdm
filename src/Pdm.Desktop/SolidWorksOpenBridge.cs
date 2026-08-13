using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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

    public async Task<string> SendAsync(Guid projectId, Guid documentId, Guid? versionId, string mode, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("当前电脑未安装SolidWorks或UPTON PDM插件，不能使用SolidWorks打开。");
        }

        var request = new { projectId, documentId, versionId, mode };
        var json = serializer.Serialize(request);
        if (await TrySendToRunningAddinAsync(json, cancellationToken).ConfigureAwait(false))
        {
            return "打开请求已发送到SolidWorks插件。";
        }

        if (Process.GetProcessesByName("SLDWORKS").Length > 0)
        {
            throw new InvalidOperationException("SolidWorks正在运行，但UPTON PDM插件尚未响应。请确认插件已启用并重新打开SolidWorks。");
        }

        QueueForStartup(json);
        var executable = FindSolidWorksExecutable() ?? throw new InvalidOperationException("未找到SolidWorks安装目录。");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        return "SolidWorks正在启动，插件加载后将自动打开受控图档。";
    }

    private static async Task<bool> TrySendToRunningAddinAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                {
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    return string.Equals(await reader.ReadLineAsync().ConfigureAwait(false), "accepted", StringComparison.Ordinal);
                }
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
