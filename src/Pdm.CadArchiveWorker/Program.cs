using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using Upton.Pdm.SolidWorks;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private const string AddinId = "{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--watch") return Watch(Path.GetFullPath(args[1]));
        if (args.Length != 2) return 2;
        var result = Path.GetFullPath(args[1]); Process owned = null; SldWorks app = null;
        var previewsResult = Path.Combine(Path.GetDirectoryName(result), "native-previews.json");
        var attemptsPath = Path.Combine(Path.GetDirectoryName(result), "native-preview-attempts.json");
        var retryPath = Path.Combine(Path.GetDirectoryName(result), "native-preview-retry.json");
        var archiveReady = false;
        var attempt = 0;
        try
        {
            var request = Json.Deserialize<Request>(File.ReadAllText(args[0]));
            var input = Path.GetFullPath(request.Directory); var work = Path.Combine(Path.GetDirectoryName(result), "work");
            if (!input.StartsWith(Path.GetDirectoryName(result) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("后台解析输入目录越界。");
            if (!IsNativeDocument(request.RootPath))
                throw new InvalidOperationException("根图档必须是SolidWorks零件、装配体或工程图；伴随文件只能随图档存档。");
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inputPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            archiveReady = request.ArchiveBeforePreviews && CoreReady(result);
            if (!archiveReady) PrepareInputs(input, work, hashes, inputPaths);
            var root = Path.GetFullPath(Path.Combine(work, Path.GetFileName(request.RootPath)));
            if (!root.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !archiveReady && !File.Exists(root))
                throw new InvalidOperationException("所选根图档不存在。");
            if (archiveReady)
            {
                attempt = File.Exists(retryPath) ? 1 : ReadAttempts(attemptsPath) + 1;
                Atomic(attemptsPath, Json.Serialize(new { Attempts = attempt }));
                PreviewState(result, previewsResult, null, true);
                if (File.Exists(retryPath)) File.Delete(retryPath);
            }
            var before = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToHashSet();
            app = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application", true));
            var processId = app.GetProcessID();
            if (before.Contains(processId))
            {
                Marshal.FinalReleaseComObject(app); app = null;
                throw new InvalidOperationException("未创建独立CAD解析进程，现有SolidWorks会话保持不变。");
            }
            owned = Process.GetProcessById(processId);
            using (var worker = Process.GetCurrentProcess())
                Atomic(Path.Combine(Path.GetDirectoryName(result), "native-owner.json"), Json.Serialize(new
                { Id = owned.Id, Started = owned.StartTime.ToUniversalTime().Ticks, WorkerId = worker.Id, WorkerStarted = worker.StartTime.ToUniversalTime().Ticks }));
            // Automation activation starts a private reader. Neither the registry nor
            // UnloadAddIn may change the user's global autoload preference.
            var loadedAddin = app.GetAddInObject(AddinId);
            if (loadedAddin != null)
            {
                if (Marshal.IsComObject(loadedAddin)) Marshal.ReleaseComObject(loadedAddin);
                throw new InvalidOperationException("后台独立解析会话加载了交互插件，已停止解析；用户插件启动设置保持不变，请稍后重试。");
            }
            app.Visible = false; app.UserControl = false;
            Action<string> progress = stage =>
            {
                try { Atomic(Path.Combine(Path.GetDirectoryName(result), "native-progress.json"), Json.Serialize(new { Stage = stage })); }
                catch (IOException) { } catch (UnauthorizedAccessException) { } // Optional progress cannot reject native files.
            };
            Action<string> checkpoint = value => Atomic(previewsResult, value);
            var json = archiveReady ? PdmAddin.ReadBrowserArchivePreviews(app, work, File.ReadAllText(result),
                File.Exists(previewsResult) ? File.ReadAllText(previewsResult) : null, progress, checkpoint, attempt < 3)
                : PdmAddin.ReadBrowserArchive(app, work, root, hashes, inputPaths, request.Previews, progress,
                    request.ArchiveBeforePreviews && request.Previews ? (Action<string>)(native =>
                    { Atomic(result, native); archiveReady = true; attempt = 1; Atomic(attemptsPath, Json.Serialize(new { Attempts = attempt })); }) : null,
                    request.ArchiveBeforePreviews ? checkpoint : null, request.ArchiveBeforePreviews);
            Atomic(archiveReady ? previewsResult : result, json); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(result + ".diagnostic.txt", error.ToString());
            var message = error is InvalidOperationException && error.Message.Any(c => c >= '\u4e00' && c <= '\u9fff')
                ? error.Message : "后台图档解析未完成，上传文件已保留，请核对设计树内文件后重试。";
            if (archiveReady) PreviewState(result, previewsResult, message, attempt < 3);
            else Atomic(result, Json.Serialize(new { Success = false, Error = message }));
            return 1;
        }
        finally
        {
            if (app != null && owned != null)
            {
                var isolationPath = Path.Combine(Path.GetDirectoryName(result), "native-session-isolation.json");
                try
                {
                    var interactive = app.GetAddInObject(AddinId);
                    var loaded = interactive != null;
                    if (interactive != null && Marshal.IsComObject(interactive)) Marshal.ReleaseComObject(interactive);
                    Atomic(isolationPath, Json.Serialize(new { CheckSucceeded = true, InteractiveAddinLoaded = loaded, ProcessId = owned.Id }));
                }
                catch (Exception error)
                {
                    try { Atomic(isolationPath, Json.Serialize(new { CheckSucceeded = false, ProcessId = owned.Id, Error = error.ToString() })); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            if (app != null) { try { if (owned != null) { app.CloseAllDocuments(true); app.ExitApp(); } } catch { } finally { Marshal.FinalReleaseComObject(app); } }
            if (owned != null) { if (!owned.HasExited && !owned.WaitForExit(15000)) owned.Kill(); owned.Dispose(); }
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
                    var previewsResult = Path.Combine(Path.GetDirectoryName(result), "native-previews.json");
                    var attemptsPath = Path.Combine(Path.GetDirectoryName(result), "native-preview-attempts.json");
                    var retry = File.Exists(Path.Combine(Path.GetDirectoryName(result), "native-preview-retry.json"));
                    var ready = CoreReady(result);
                    if (File.Exists(result) && !ready) continue;
                    if (ready && !retry && File.Exists(previewsResult) && !ReadStatus(previewsResult).PreviewsPending) continue;
                    var ownerPath = Path.Combine(Path.GetDirectoryName(result), "native-owner.json");
                    if (OwnerAlive(ownerPath))
                    {
                        if (OwnerWorkerAlive(ownerPath)) continue;
                        KillOwner(ownerPath);
                    }
                    if (ready && !retry && ReadAttempts(attemptsPath) >= 3)
                    { PreviewState(result, previewsResult, "预览多次未完成，原生图档已保留，可重试补齐预览。", false); continue; }
                    using (var process = Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location,
                        "\"" + request + "\" \"" + result + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    {
                        var deadline = DateTime.UtcNow.AddMinutes(30);
                        var nativeReady = ready;
                        while (!process.WaitForExit(1000))
                        {
                            if (!nativeReady && File.Exists(result))
                            {
                                var status = ReadStatus(result);
                                if (status.Success && status.PreviewsPending)
                                { nativeReady = true; deadline = DateTime.UtcNow.AddMinutes(30); }
                            }
                            if (DateTime.UtcNow < deadline) continue;
                            process.Kill();
                            process.WaitForExit(5000);
                            KillOwner(Path.Combine(Path.GetDirectoryName(request), "native-owner.json"));
                            if (nativeReady) PreviewState(result, previewsResult, "预览生成超时，已保留成功项；原生存档不受影响。", ReadAttempts(attemptsPath) < 3);
                            else Atomic(result, Json.Serialize(new { Success = false, Error = "后台解析超时，上传内容已保留，请稍后重试。" }));
                            break;
                        }
                        KillOwner(Path.Combine(Path.GetDirectoryName(request), "native-owner.json"));
                    }
                }
                Thread.Sleep(1000);
            }
        }
    }
    private static void Atomic(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        try
        {
            for (var retry = 0; ; retry++)
                try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); return; }
                catch (IOException) when (retry < 20) { Thread.Sleep(50); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void PrepareInputs(string input, string work, Dictionary<string, string> hashes, Dictionary<string, string> inputPaths)
    {
        if (Directory.Exists(work) && (File.GetAttributes(work) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("后台解析工作目录不能是外部链接。");
        foreach (var source in InputFiles(input).Where(path => IsNativeDocument(path)
            || new[] { ".step", ".stp", ".txt", ".xls", ".xlsx" }.Contains(Path.GetExtension(path).ToLowerInvariant())))
        {
            var relative = source.Substring(input.Length + 1); var name = Path.GetFileName(source); var destination = Path.Combine(work, name);
            string sourceHash; long length;
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(source))
            { length = stream.Length; sourceHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
            if (hashes.TryGetValue(name, out var previous))
            {
                if (previous == sourceHash && new FileInfo(destination).Length == length) continue;
                throw new InvalidOperationException("同名图档的内容不同，请明确命名后重试：" + name);
            }
            inputPaths[name] = relative.Replace('\\', '/'); hashes[name] = sourceHash;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("后台解析工作文件不能是外部链接：" + name);
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            File.Copy(source, destination, true); File.SetAttributes(destination, FileAttributes.Normal);
        }
    }
    private static bool IsNativeDocument(string path) =>
        new[] { ".sldasm", ".sldprt", ".slddrw" }.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
    private static IEnumerable<string> InputFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("后台解析输入目录不能包含外部链接。");
        foreach (var path in Directory.GetFiles(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("后台解析输入文件不能是外部链接：" + Path.GetFileName(path));
            yield return path;
        }
        foreach (var child in Directory.GetDirectories(directory))
            foreach (var path in InputFiles(child)) yield return path;
    }
    private static ResultStatus ReadStatus(string path)
    {
        try { return Json.Deserialize<ResultStatus>(File.ReadAllText(path)); }
        catch (IOException) { return new ResultStatus(); }
    }
    private static bool CoreReady(string path) => File.Exists(path) && ReadStatus(path) is ResultStatus status && status.Success && status.PreviewsPending;
    private static int ReadAttempts(string path) => File.Exists(path) ? Json.Deserialize<PreviewAttempts>(File.ReadAllText(path)).Attempts : 0;
    private static bool OwnerAlive(string path)
    {
        if (!File.Exists(path)) return false;
        var owner = Json.Deserialize<NativeOwner>(File.ReadAllText(path));
        try { using (var process = Process.GetProcessById(owner.Id)) return process.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase)
            && process.StartTime.ToUniversalTime().Ticks == owner.Started && !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void KillOwner(string path)
    {
        if (!OwnerAlive(path)) return;
        var owner = Json.Deserialize<NativeOwner>(File.ReadAllText(path));
        using (var process = Process.GetProcessById(owner.Id))
            if (process.StartTime.ToUniversalTime().Ticks == owner.Started) process.Kill();
    }
    private static bool OwnerWorkerAlive(string path)
    {
        var owner = Json.Deserialize<NativeOwner>(File.ReadAllText(path));
        if (owner.WorkerId == 0) return true; // Older workers do not identify their parent; never terminate one by guessing.
        try { using (var worker = Process.GetProcessById(owner.WorkerId)) return worker.ProcessName.Equals("Upton.Pdm.CadArchiveWorker", StringComparison.OrdinalIgnoreCase)
            && worker.StartTime.ToUniversalTime().Ticks == owner.WorkerStarted && !worker.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void PreviewState(string nativePath, string previewPath, string warning, bool pending)
    {
        var native = File.ReadAllText(nativePath); string sha;
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(native))).Replace("-", "");
        var value = Json.Deserialize<Dictionary<string, object>>(native);
        if (File.Exists(previewPath))
        {
            var previous = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(previewPath));
            if (previous.TryGetValue("ArchiveSourceSha256", out var prior) && string.Equals(prior as string, sha, StringComparison.OrdinalIgnoreCase)) value = previous;
        }
        value["Success"] = true; value["PreviewsPending"] = pending; value["ArchiveSourceSha256"] = sha;
        if (warning != null)
        {
            var warnings = value.TryGetValue("Warnings", out var list) && list is System.Collections.IEnumerable entries
                ? entries.Cast<object>().Select(entry => Convert.ToString(entry)).ToList() : new List<string>();
            warnings.Add(warning); value["Warnings"] = warnings.Distinct().ToArray();
        }
        Atomic(previewPath, Json.Serialize(value));
    }
    private sealed class Request { public string Directory { get; set; } public string RootPath { get; set; } public bool Previews { get; set; } public bool ArchiveBeforePreviews { get; set; } }
    private sealed class ResultStatus { public bool Success { get; set; } public bool PreviewsPending { get; set; } }
    private sealed class PreviewAttempts { public int Attempts { get; set; } }
    private sealed class NativeOwner { public int Id { get; set; } public long Started { get; set; } public int WorkerId { get; set; } public long WorkerStarted { get; set; } }
}
