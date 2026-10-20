using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using Upton.Pdm.SolidWorks;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private const string Startup = @"Software\SolidWorks\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--watch") return Watch(Path.GetFullPath(args[1]));
        if (args.Length != 2) return 2;
        var result = Path.GetFullPath(args[1]); Process owned = null; SldWorks app = null;
        object startup = null; bool hadValue = false, startupChanged = false; RegistryValueKind startupKind = RegistryValueKind.DWord;
        void RestoreStartup()
        {
            if (!startupChanged) return;
            using (var key = Registry.CurrentUser.CreateSubKey(Startup)) { if (hadValue) key.SetValue("", startup, startupKind); else key.DeleteValue("", false); }
            startupChanged = false;
        }
        try
        {
            var request = Json.Deserialize<Request>(File.ReadAllText(args[0]));
            var input = Path.GetFullPath(request.Directory); var work = Path.Combine(Path.GetDirectoryName(result), "work");
            if (!input.StartsWith(Path.GetDirectoryName(result) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("后台解析输入目录越界。");
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inputPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                .Where(path => new[] { ".sldasm", ".sldprt", ".slddrw" }.Contains(Path.GetExtension(path).ToLowerInvariant())))
            {
                var relative = source.Substring(input.Length + 1); var name = Path.GetFileName(source); var destination = Path.Combine(work, name);
                if (inputPaths.ContainsKey(name)) throw new InvalidOperationException("图档存在同名来源，请明确命名后重试：" + name);
                inputPaths[name] = relative.Replace('\\', '/');
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
                File.Copy(source, destination, true);
                File.SetAttributes(destination, FileAttributes.Normal);
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(source))
                    hashes[name] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            var root = Path.GetFullPath(Path.Combine(work, Path.GetFileName(request.RootPath)));
            if (!root.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(root))
                throw new InvalidOperationException("所选根图档不存在。");
            using (var key = Registry.CurrentUser.OpenSubKey(Startup))
            {
                hadValue = key?.GetValueNames().Contains("") == true;
                if (hadValue) { startup = key.GetValue(""); startupKind = key.GetValueKind(""); }
            }
            using (var key = Registry.CurrentUser.CreateSubKey(Startup)) key.SetValue("", 0, RegistryValueKind.DWord);
            startupChanged = true;
            var before = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToHashSet();
            owned = Process.Start(new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp", "SOLIDWORKS", "SLDWORKS.exe"))
                { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            Atomic(Path.Combine(Path.GetDirectoryName(result), "native-owner.json"), Json.Serialize(new { Id = owned.Id, Started = owned.StartTime.ToUniversalTime().Ticks }));
            for (var i = 0; i < 120 && app == null && !owned.HasExited; i++) { app = Find(owned.Id); if (app == null) Thread.Sleep(1000); }
            if (app == null || before.Contains(app.GetProcessID()) || app.GetProcessID() != owned.Id)
                throw new InvalidOperationException("独立CAD解析进程未就绪，未接管现有会话。");
            RestoreStartup();
            app.Visible = false; app.UserControl = false;
            var json = PdmAddin.ReadBrowserArchive(app, work, root, hashes, inputPaths, request.Previews,
                stage => Atomic(Path.Combine(Path.GetDirectoryName(result), "native-progress.json"), Json.Serialize(new { Stage = stage })));
            Atomic(result, json); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(result + ".diagnostic.txt", error.ToString());
            var message = error is InvalidOperationException && error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff')
                ? error.Message : "后台图档解析未完成，上传文件已保留，请核对设计树内文件后重试。";
            Atomic(result, Json.Serialize(new { Success = false, Error = message })); return 1;
        }
        finally
        {
            if (app != null) { try { app.CloseAllDocuments(true); app.ExitApp(); } catch { } finally { Marshal.FinalReleaseComObject(app); } }
            if (owned != null) { if (!owned.HasExited && !owned.WaitForExit(15000)) owned.Kill(); owned.Dispose(); }
            RestoreStartup();
        }
    }
    private static int Watch(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var mutex = new Mutex(false, "Local\\UPLM-CadArchiveWorker"))
        {
            if (!mutex.WaitOne(0)) return 0;
            File.WriteAllText(Path.Combine(directory, "worker-ready.json"), Json.Serialize(new { ProcessId = Process.GetCurrentProcess().Id, StartedAt = DateTime.UtcNow }));
            while (true)
            {
                File.WriteAllText(Path.Combine(directory, "worker-heartbeat.txt"), DateTime.UtcNow.ToString("O"));
                foreach (var request in Directory.GetDirectories(directory).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                    .Select(path => Path.Combine(path, "native-request.json")).Where(File.Exists))
                {
                    var result = Path.Combine(Path.GetDirectoryName(request), "native-result.json");
                    if (File.Exists(result)) continue;
                    using (var process = Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location,
                        "\"" + request + "\" \"" + result + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    {
                        if (!process.WaitForExit(30 * 60 * 1000))
                        {
                            process.Kill();
                            var ownerPath = Path.Combine(Path.GetDirectoryName(request), "native-owner.json");
                            if (File.Exists(ownerPath))
                            {
                                var owner = Json.Deserialize<NativeOwner>(File.ReadAllText(ownerPath));
                                try { using (var cad = Process.GetProcessById(owner.Id))
                                    if (cad.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase) && cad.StartTime.ToUniversalTime().Ticks == owner.Started) cad.Kill(); }
                                catch (ArgumentException) { }
                            }
                            Atomic(result, Json.Serialize(new { Success = false, Error = "后台解析超时，上传内容已保留，请稍后重试。" }));
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }
    }
    private static void Atomic(string path, string text)
    {
        var temporary = path + ".tmp"; File.WriteAllText(temporary, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path); File.Move(temporary, path);
    }
    private static SldWorks Find(int id)
    {
        GetRunningObjectTable(0, out var table); CreateBindCtx(0, out var context); table.EnumRunning(out var iterator); var values = new IMoniker[1];
        try { while (iterator.Next(1, values, IntPtr.Zero) == 0) try { values[0].GetDisplayName(context, null, out var name);
                if (name != "SolidWorks_PID_" + id) continue; table.GetObject(values[0], out var value); return (SldWorks)value;
            } finally { Marshal.ReleaseComObject(values[0]); } return null;
        } finally { Marshal.ReleaseComObject(iterator); Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(table); }
    }
    private sealed class Request { public string Directory { get; set; } public string RootPath { get; set; } public bool Previews { get; set; } }
    private sealed class NativeOwner { public int Id { get; set; } public long Started { get; set; } }
}
