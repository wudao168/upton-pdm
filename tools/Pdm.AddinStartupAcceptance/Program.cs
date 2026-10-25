using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private const string GuidText = "{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    private const string StartupKey = @"Software\SolidWorks\AddInsStartup\" + GuidText;
    private static readonly List<object> Cycles = new List<object>();
    private static int Samples;
    private static bool ExpectDeferred;
    private static void CheckStartup()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(StartupKey))
            if (!Equals(key?.GetValue(""), 1)) throw new InvalidOperationException("启动加载设置未保持启用，已停止验收。");
        Samples++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--proxy") return VerifyDeferredStartup(args[1], args[2]);
        ExpectDeferred = args.Contains("--expect-deferred");
        Exception failure = null;
        try
        {
            CheckStartup();
            for (var index = 1; index <= 3; index++) Verify(index);
        }
        catch (Exception error) { failure = error; Console.WriteLine(error); }
        var result = new { succeeded = failure == null, cycles = Cycles, registrySamples = Samples, startupRegistryNotWritten = true, error = failure?.Message, finishedAt = DateTimeOffset.Now.ToString("O") };
        File.WriteAllText(args[0], new JavaScriptSerializer().Serialize(result));
        Console.WriteLine(failure == null ? "PASS 三次真实启动自动加载，启动勾选全程保留" : "FAIL 插件启动验收");
        return failure == null ? 0 : 1;
    }

    private static void Verify(int index)
    {
        var existingProcesses = Process.GetProcessesByName("SLDWORKS").Select(process => process.Id).ToArray();
        CheckStartup();
        var executable = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), @"SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe");
        SldWorks app = null; object addin = null;
        using (var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable) }))
        {
            var watch = Stopwatch.StartNew();
            Console.WriteLine("START " + index + " pid=" + process.Id);
            try
            {
                while (app == null && watch.Elapsed.TotalSeconds < 90 && !process.HasExited)
                {
                    CheckStartup();
                    try { app = FindProcess(process.Id); } catch (COMException) { }
                    if (app == null) Thread.Sleep(200);
                }
                if (app == null || app.GetProcessID() != process.Id || existingProcesses.Contains(process.Id)) throw new InvalidOperationException("独立SolidWorks启动未就绪，未接管已有会话。");
                app.Visible = false; app.UserControl = false;
                while (watch.Elapsed.TotalSeconds < 90 && !process.HasExited)
                {
                    CheckStartup();
                    try { if (app.StartupProcessCompleted) { addin = app.GetAddInObject(GuidText); if (addin != null) break; } }
                    catch (COMException) { }
                    Thread.Sleep(200);
                }
                if (addin == null) throw new InvalidOperationException("真实启动未自动加载UPLM，未使用手工加载掩盖故障。");
                var startupSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
                var codeBase = RegisteredPath();
                var modules = process.Modules.Cast<ProcessModule>().Where(module => string.Equals(module.FileName, codeBase, StringComparison.OrdinalIgnoreCase)).ToArray();
                // CLR托管程序集不保证出现在原生PEB模块列表中；以自动取得的插件对象和卸载/重载结果验收。
                for (var i = 0; i < 20; i++) { CheckStartup(); Thread.Sleep(100); }
                long? connectCallbackMs = null; var deferredSyncObserved = false;
                if (ExpectDeferred)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        CheckStartup();
                        var trace = ReadStartupTrace();
                        var connected = Regex.Match(trace, @"ConnectToSW success elapsedMs=(\d+) serverSync=deferred pid=" + process.Id + @"\b");
                        var synced = trace.IndexOf("SolidWorks startup complete; server configuration sync started pid=" + process.Id, StringComparison.Ordinal);
                        if (connected.Success && synced > connected.Index) { connectCallbackMs = long.Parse(connected.Groups[1].Value); deferredSyncObserved = true; break; }
                        Thread.Sleep(100);
                    }
                    if (!deferredSyncObserved) throw new InvalidOperationException("未观察到加载回调返回后、启动完成时再同步服务器的记录。");
                }
                int? unloaded = null, reloaded = null;
                if (index == 1)
                {
                    Marshal.ReleaseComObject(addin); addin = null;
                    unloaded = app.UnloadAddIn(codeBase); CheckStartup();
                    if (unloaded != 0) throw new InvalidOperationException("独立测试会话中的插件卸载失败。");
                    reloaded = app.LoadAddIn(codeBase); CheckStartup();
                    if (reloaded != 0) throw new InvalidOperationException("独立测试会话中的插件重新加载失败。");
                    addin = app.GetAddInObject(GuidText);
                    if (addin == null) throw new InvalidOperationException("重新加载后未取得插件对象。");
                }
                Cycles.Add(new { index, processId = process.Id, preexistingProcessIds = existingProcesses, autoLoaded = true, startupSeconds, connectCallbackMs, deferredSyncObserved, registeredModulePath = codeBase, registeredModuleVersion = FileVersionInfo.GetVersionInfo(codeBase).ProductVersion, nativeModuleObserved = modules.Length == 1, unloadStatus = unloaded, reloadStatus = reloaded });
                Console.WriteLine("PASS " + index + " automatic load " + startupSeconds + "s; startup=1");
            }
            catch (Exception error)
            {
                Console.WriteLine("STARTUP CORE ERROR: " + error);
                throw;
            }
            finally
            {
                if (addin != null && Marshal.IsComObject(addin)) Marshal.ReleaseComObject(addin);
                if (app != null)
                {
                    try { if (!process.HasExited) app.ExitApp(); } catch (COMException) { }
                    Marshal.FinalReleaseComObject(app);
                }
                if (!process.HasExited && !process.WaitForExit(20000)) { process.Kill(); process.WaitForExit(10000); throw new InvalidOperationException("独立验收进程退出超时，已仅关闭本工具创建的进程。"); }
                CheckStartup();
            }
        }
    }

    private static string ReadStartupTrace()
    {
        var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "addin-operations.log");
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Position = Math.Max(0, stream.Length - 256 * 1024);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        catch (IOException) { return string.Empty; }
    }

    private static string RegisteredPath()
    {
        using (var key = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + GuidText + @"\InprocServer32"))
        {
            var value = key?.GetValue("CodeBase") as string;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || !File.Exists(uri.LocalPath)) throw new InvalidOperationException("插件注册路径无效。");
            return uri.LocalPath;
        }
    }

    private static SldWorks FindProcess(int processId)
    {
        GetRunningObjectTable(0, out var table); CreateBindCtx(0, out var context); table.EnumRunning(out var iterator); var monikers = new IMoniker[1];
        try
        {
            while (iterator.Next(1, monikers, IntPtr.Zero) == 0)
                try
                {
                    monikers[0].GetDisplayName(context, null, out var name);
                    if (!string.Equals(name, "SolidWorks_PID_" + processId, StringComparison.OrdinalIgnoreCase)) continue;
                    table.GetObject(monikers[0], out var value); return (SldWorks)value;
                }
                finally { Marshal.ReleaseComObject(monikers[0]); }
            return null;
        }
        finally { Marshal.ReleaseComObject(iterator); Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(table); }
    }
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
}
