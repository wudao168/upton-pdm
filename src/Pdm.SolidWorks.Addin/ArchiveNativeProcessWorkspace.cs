using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using Microsoft.Win32;

namespace Upton.Pdm.SolidWorks;

// Uses the existing private CAD reader. The engineer's SolidWorks process never
// opens, activates or closes the native archive copies.
internal static class ArchiveNativeProcessWorkspace
{
    private const string RequestName = "native-isolation-request.json";
    private const string ReaderJobVariable = "UPLM_NATIVE_ARCHIVE_JOB";
    private static ISldWorks activeReader;
    private static string activeJob;
    private static int activeReaderId;
    private static long activeReaderStarted;
    private static Action<int, long> readerStarted;
    private static string Jobs => Path.Combine(Path.GetTempPath(), "UPTON-PDM", "native-isolation", "jobs");
    private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(uint reserved, out IBindCtx context);

    internal static ISldWorks StartReader(string job, Action<int, long> started)
    {
        if (!IsJob(job) || !File.Exists(Path.Combine(job, RequestName))) return null;
        var request = Json().Deserialize<Request>(File.ReadAllText(Path.Combine(job, RequestName)));
        if (request.JobId != Path.GetFileName(job) || !File.Exists(request.SolidWorksExecutable)
            || !string.Equals(Path.GetFileName(request.SolidWorksExecutable), "SLDWORKS.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("独立归档启动信息不完整，未操作原 SolidWorks 会话。");
        var startInfo = new ProcessStartInfo(request.SolidWorksExecutable)
        { WorkingDirectory = Path.GetDirectoryName(request.SolidWorksExecutable), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        startInfo.EnvironmentVariables[ReaderJobVariable] = job;
        using (ArchiveNativeJournalScope.Begin(job, request.SolidWorksExecutable))
        using (var process = Process.Start(startInfo))
        {
            if (process == null || process.Id == request.CallerSolidWorksId)
                throw new InvalidOperationException("未能创建独立归档会话，原 SolidWorks 已保留，尚未上传。");
            started(process.Id, process.StartTime.ToUniversalTime().Ticks);
            var timer = Stopwatch.StartNew();
            while (!process.HasExited && timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (File.Exists(Path.Combine(job, "cancel"))) throw new OperationCanceledException();
                var app = FromRunningProcess(process.Id);
                if (app != null && app.GetProcessID() == process.Id)
                {
                    PrepareOwnedReader(app, process, request.CallerSolidWorksId, job);
                    activeReader = app; activeJob = job; activeReaderId = process.Id;
                    activeReaderStarted = process.StartTime.ToUniversalTime().Ticks; readerStarted = started;
                    return app;
                }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException("独立归档进程启动未完成，尚未上传，原窗口已保留。启动进程："
                + process.Id + (process.HasExited ? "；退出码：" + process.ExitCode : "；连接等待超时。"));
        }
    }

    internal static ISldWorks CurrentReader(string job, int id)
        => IsJob(job) && string.Equals(activeJob, job, StringComparison.OrdinalIgnoreCase) && activeReaderId == id ? activeReader : null;

    private static ISldWorks FreshValidationReader(string job)
    {
        if (CurrentReader(job, activeReaderId) == null || readerStarted == null)
            throw new InvalidOperationException("未能确认专用核对会话的来源，尚未上传。");
        var request = Json().Deserialize<Request>(File.ReadAllText(Path.Combine(job, RequestName)));
        var owner = Json().Deserialize<Owner>(File.ReadAllText(Path.Combine(job, "native-owner.json")));
        using (var worker = Process.GetCurrentProcess())
        using (var previous = Process.GetProcessById(activeReaderId))
        {
            if (activeReaderId == request.CallerSolidWorksId || owner.Id != activeReaderId || owner.Started != activeReaderStarted
                || owner.WorkerId != worker.Id || owner.WorkerStarted != worker.StartTime.ToUniversalTime().Ticks
                || previous.StartTime.ToUniversalTime().Ticks != activeReaderStarted || activeReader.GetDocumentCount() != 0)
                throw new InvalidOperationException("临时图档或专用进程身份尚未核对完成，未切换读取会话，原工作窗口已保留。");
            File.WriteAllText(Path.Combine(job, "native-validation-reader.json"), Json().Serialize(new
            { PreparationId = activeReaderId, PreparationStarted = activeReaderStarted, PreparedDocumentsClosed = true }));
            // Every package file has already been saved and checked. End only the
            // owned preparation process so same-name native caches cannot survive.
            previous.Kill();
            if (!previous.WaitForExit(5000)) throw new InvalidOperationException("专用准备会话尚未结束，未开始输出核对。");
        }
        return StartReader(job, readerStarted);
    }

    private static void PrepareOwnedReader(ISldWorks app, Process process, int callerId, string job)
    {
        var started = process.StartTime.ToUniversalTime().Ticks;
        if (!IsJob(job) || app.GetProcessID() != process.Id || process.Id == callerId)
            throw new InvalidOperationException("专用读取会话身份不一致，未操作任何插件。");
        var timer = Stopwatch.StartNew();
        while (!app.StartupProcessCompleted && !process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(45)) Thread.Sleep(200);
        if (!app.StartupProcessCompleted || process.HasExited || process.StartTime.ToUniversalTime().Ticks != started)
            throw new InvalidOperationException("专用读取会话启动尚未完成，原工作会话已保留。");
        if (IsReaderAddin(job, process.Id)) return;
        const string guid = "{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
        using (var startup = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\SOLIDWORKS\AddInsStartup\" + guid))
        using (var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SOLIDWORKS\AddIns\" + guid))
        {
            if (!File.Exists(Path.Combine(job, "reader-addin.json"))
                && Convert.ToInt32(startup?.GetValue("") ?? 0) == 0 && Convert.ToInt32(machine?.GetValue("") ?? 0) == 0)
            {
                File.WriteAllText(Path.Combine(job, "native-reader-isolation.json"), Json().Serialize(new
                { ProcessId = process.Id, Started = started, InteractiveStartupDisabled = true, StartupSettingsUnchanged = true }));
                return;
            }
        }
        // UnloadAddIn may persist a disabled startup flag only when SOLIDWORKS
        // exits, so an immediate registry comparison cannot make it safe.
        throw new InvalidOperationException("专用归档组件与本机插件版本不一致，尚未处理或上传图档。请同步更新本机 SolidWorks 插件和归档组件后重试，原窗口与启动设置已保留。");
    }

    private static ISldWorks FromRunningProcess(int id)
    {
        CreateBindCtx(0, out var context); IRunningObjectTable table = null; IEnumMoniker entries = null;
        try
        {
            context.GetRunningObjectTable(out table); table.EnumRunning(out entries); var values = new IMoniker[1];
            while (entries.Next(1, values, IntPtr.Zero) == 0)
            {
                var entry = values[0];
                try
                {
                    entry.GetDisplayName(context, null, out var name);
                    if (string.Equals(name, "SolidWorks_PID_" + id, StringComparison.OrdinalIgnoreCase))
                    { table.GetObject(entry, out var value); return value as ISldWorks; }
                }
                catch (UnauthorizedAccessException) { }
                finally { Marshal.ReleaseComObject(entry); }
            }
            return null;
        }
        finally
        {
            if (entries != null) Marshal.ReleaseComObject(entries);
            if (table != null) Marshal.ReleaseComObject(table);
            Marshal.ReleaseComObject(context);
        }
    }

    internal static bool IsJob(string path) => !string.IsNullOrWhiteSpace(path)
        && Guid.TryParseExact(Path.GetFileName(path), "N", out _)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Jobs), StringComparison.OrdinalIgnoreCase);

    internal static bool IsReaderStartup()
    {
        var job = System.Environment.GetEnvironmentVariable(ReaderJobVariable);
        return IsJob(job) && File.Exists(Path.Combine(job, RequestName));
    }

    internal static bool ConnectReaderAddin()
    {
        if (!IsReaderStartup()) return false;
        var job = System.Environment.GetEnvironmentVariable(ReaderJobVariable);
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(job, "reader-addin.json"), Json().Serialize(new ReaderAddin
            { Id = process.Id, Started = process.StartTime.ToUniversalTime().Ticks,
                AssemblyId = typeof(PdmAddin).Assembly.ManifestModule.ModuleVersionId.ToString("N") }));
        return true;
    }

    internal static bool IsReaderAddin(string job, int processId)
    {
        if (!IsJob(job)) return false;
        try
        {
            var path = Path.Combine(job, "reader-addin.json");
            if (!File.Exists(path)) return false;
            var value = Json().Deserialize<ReaderAddin>(File.ReadAllText(path));
            using (var process = Process.GetProcessById(processId))
                return value.Id == process.Id && value.Started == process.StartTime.ToUniversalTime().Ticks
                    && value.AssemblyId == typeof(PdmAddin).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static void WriteProgress(string job, string name)
    {
        if (File.Exists(Path.Combine(job, "cancel"))) throw new OperationCanceledException();
        try { File.WriteAllText(Path.Combine(job, "isolation-progress.json"), Json().Serialize(new Progress { FileName = name })); }
        catch (IOException) { } // An optional progress read must not interrupt native file processing.
    }

    internal static bool TryRun(ISldWorks app, string workingDirectory, out string result)
    {
        result = null;
        var job = Path.GetDirectoryName(Path.GetFullPath(workingDirectory));
        if (!IsJob(job) || Path.GetFileName(workingDirectory) != "work" || !File.Exists(Path.Combine(job, RequestName))) return false;
        var request = Json().Deserialize<Request>(File.ReadAllText(Path.Combine(job, RequestName)));
        if (request.JobId != Path.GetFileName(job) || request.CallerSolidWorksId == app.GetProcessID())
            throw new InvalidOperationException("独立归档会话身份不一致，未继续处理，原窗口已保留。");
        if (File.Exists(Path.Combine(job, "cancel"))) throw new OperationCanceledException();
        app.UserControlBackground = true;
        var response = new Response { Success = true, JobId = request.JobId };
        var previousCommand = app.CommandInProgress;
        try
        {
        app.CommandInProgress = true;
        using (var workspace = new ArchiveNativeCopyWorkspace(app, request.Inventory, request.Requested,
            Path.Combine(job, "native"), name => WriteProgress(job, name), () => FreshValidationReader(job)))
        {
            var output = Path.Combine(job, "output"); Directory.CreateDirectory(output);
            foreach (var file in workspace.Files)
            {
                var path = Path.Combine(output, Path.GetFileName(file.Source));
                File.Copy(file.Path, path);
                response.Files.Add(new CompletedFile { Source = file.Source, Path = path, SourceSha256 = file.SourceSha256,
                    ContentFrozen = file.ContentFrozen, Sha256 = ArchiveFileFingerprint.Read(path), Dependencies = file.Dependencies });
            }
        }
        }
        catch
        {
            File.WriteAllText(Path.Combine(job, "native-diagnostics.txt"), PluginDiagnosticLog.Snapshot());
            throw;
        }
        finally
        {
            try { (CurrentReader(job, activeReaderId) ?? app).CommandInProgress = previousCommand; }
            catch (COMException error) { PluginDiagnosticLog.Record("专用会话恢复", "批量调用状态未恢复；原始处理结果保留：" + error); }
        }
        result = Json().Serialize(response);
        return true;
    }

    internal static async Task<ArchiveNativeCopyWorkspace> CreateAsync(ISldWorks caller, IEnumerable<string> inventory,
        IReadOnlyDictionary<string, string> requested, Action<string> report, CancellationToken token)
    {
        var executable = Path.Combine(Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location), "Upton.Pdm.CadArchiveWorker.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("独立归档组件缺失，尚未上传任何文件。请完整更新 SolidWorks 插件后重试。");
        var job = Path.Combine(Jobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
        var callerId = caller.GetProcessID();
        var solidWorksExecutable = caller.GetExecutablePath();
        if (Directory.Exists(solidWorksExecutable)) solidWorksExecutable = Path.Combine(solidWorksExecutable, "SLDWORKS.exe");
        var request = new Request { JobId = Path.GetFileName(job), CallerSolidWorksId = callerId,
            SolidWorksExecutable = solidWorksExecutable,
            Inventory = inventory.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Requested = requested.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) };
        var retained = false;
        try
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(job, RequestName), Json().Serialize(request));
            var input = Path.Combine(job, "input"); Directory.CreateDirectory(input);
            var root = requested.First();
            File.Copy(root.Value, Path.Combine(input, Path.GetFileName(root.Key)));
            var workerRequest = Path.Combine(job, "reader-request.json"); var responsePath = Path.Combine(job, "result.json");
            File.WriteAllText(workerRequest, Json().Serialize(new { Directory = input, RootPath = Path.GetFileName(root.Key), Previews = false }));
            using (var process = Process.Start(new ProcessStartInfo(executable, "\"" + workerRequest + "\" \"" + responsePath + "\"")
            { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true }))
            {
                if (process == null) throw new InvalidOperationException("独立归档进程未能启动，尚未上传，原窗口已保留。");
                var workerStart = process.StartTime.ToUniversalTime().Ticks;
                string previous = null;
                try
                {
                    while (!process.HasExited)
                    {
                        token.ThrowIfCancellationRequested();
                        var current = ReadProgress(job);
                        if (current != null && current != previous) { previous = current; report?.Invoke(current); }
                        await Task.Delay(200, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    File.WriteAllText(Path.Combine(job, "cancel"), "取消本次独立归档");
                    await StopOwnedReaderAsync(process, workerStart, callerId, job);
                    throw;
                }
                var diagnostic = responsePath + ".diagnostic.txt";
                if (File.Exists(diagnostic)) PluginDiagnosticLog.Record("独立归档诊断", File.ReadAllText(diagnostic));
                var nativeDiagnostic = Path.Combine(job, "native-diagnostics.txt");
                if (File.Exists(nativeDiagnostic))
                {
                    var details = File.ReadAllText(nativeDiagnostic);
                    for (var offset = 0; offset < details.Length; offset += 3500)
                        PluginDiagnosticLog.Record("原生存档诊断", details.Substring(offset, Math.Min(3500, details.Length - offset)));
                }
                if (!File.Exists(responsePath)) throw new InvalidOperationException("独立归档进程未返回完整结果，尚未上传。原窗口已保留，请查看诊断日志。");
                var response = Json().Deserialize<Response>(File.ReadAllText(responsePath));
                if (process.ExitCode != 0 || !response.Success)
                    throw new InvalidOperationException(!string.IsNullOrWhiteSpace(response.Error) && response.Error.Any(c => c >= '\u4e00' && c <= '\u9fff')
                        ? response.Error : "独立归档未完成，尚未上传。原图档和窗口已保留，请查看诊断日志。");
                var files = ValidateResult(job, request, response);
                retained = true;
                return ArchiveNativeCopyWorkspace.Attach(job, files);
            }
        }
        finally
        {
            ArchiveNativeJournalScope.Recover(job);
            if (!retained) DeleteJob(job);
        }
    }

    private static IReadOnlyList<ArchiveNativeCopyWorkspace.FileCopy> ValidateResult(string job, Request request, Response response)
    {
        if (!IsJob(job) || !response.Success || response.JobId != request.JobId || response.Files == null)
            throw new InvalidOperationException("独立归档结果与本次作业不一致，尚未上传。");
        var allowed = new HashSet<string>(request.Inventory.Concat(request.Requested.Keys), StringComparer.OrdinalIgnoreCase);
        var result = new List<ArchiveNativeCopyWorkspace.FileCopy>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedSources = new HashSet<string>(response.Files.Select(file => file.Source), StringComparer.OrdinalIgnoreCase);
        foreach (var file in response.Files)
        {
            var path = Path.GetFullPath(file.Path);
            if (!allowed.Contains(file.Source) || !sources.Add(file.Source)
                || !path.StartsWith(Path.GetFullPath(job) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path) != Path.GetFileName(file.Source) || !File.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || string.IsNullOrWhiteSpace(file.SourceSha256) || file.SourceSha256.Length != 64
                || (file.Dependencies ?? new string[0]).Any(source => !completedSources.Contains(source))
                || !string.Equals(ArchiveFileFingerprint.Read(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("独立归档文件范围或内容校验未通过，尚未上传。原图档已保留。");
            result.Add(new ArchiveNativeCopyWorkspace.FileCopy { Source = file.Source, Path = path,
                SourceSha256 = file.SourceSha256, ContentFrozen = file.ContentFrozen, Dependencies = file.Dependencies ?? new string[0] });
        }
        if (request.Requested.Keys.Any(path => !sources.Contains(path)))
            throw new InvalidOperationException("独立归档结果缺少已选图档，尚未上传。");
        return result;
    }

    private static string ReadProgress(string job)
    {
        try
        {
            var path = Path.Combine(job, "isolation-progress.json");
            return File.Exists(path) ? Json().Deserialize<Progress>(File.ReadAllText(path)).FileName : null;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static async Task StopOwnedReaderAsync(Process worker, long workerStart, int callerId, string job)
    {
        // The existing reader records both process identities before touching CAD.
        var ownerPath = Path.Combine(job, "native-owner.json");
        for (var attempt = 0; attempt < 150 && !worker.HasExited; attempt++)
        {
            if (File.Exists(ownerPath))
            {
                var owner = Json().Deserialize<Owner>(File.ReadAllText(ownerPath));
                if (owner.WorkerId != worker.Id || owner.WorkerStarted != workerStart || owner.Id == callerId
                    || owner.Started < workerStart)
                    throw new InvalidOperationException("独立归档进程身份核对未通过，未关闭任何 SolidWorks 会话。");
                try
                {
                    using (var native = Process.GetProcessById(owner.Id))
                        if (native.ProcessName == "SLDWORKS" && native.StartTime.ToUniversalTime().Ticks == owner.Started) native.Kill();
                }
                catch (ArgumentException) { } // The owned process has already exited.
                break;
            }
            await Task.Delay(200);
        }
        if (!worker.HasExited && !await Task.Run(() => worker.WaitForExit(15000)))
        {
            if (worker.StartTime.ToUniversalTime().Ticks != workerStart)
                throw new InvalidOperationException("独立归档启动进程身份已变化，未继续关闭。");
            worker.Kill();
            await Task.Run(() => worker.WaitForExit());
        }
    }

    private static void DeleteJob(string job)
    {
        if (!IsJob(job)) throw new InvalidOperationException("独立归档清理目录不属于本次临时作业。");
        try { if (Directory.Exists(job)) Directory.Delete(job, true); }
        catch (IOException error) { PluginDiagnosticLog.Record("独立归档清理", error.ToString()); }
    }

    internal sealed class Request
    {
        public string JobId { get; set; }
        public int CallerSolidWorksId { get; set; }
        public string SolidWorksExecutable { get; set; }
        public string[] Inventory { get; set; }
        public Dictionary<string, string> Requested { get; set; }
    }
    internal sealed class Response
    {
        public bool Success { get; set; }
        public string JobId { get; set; }
        public string Error { get; set; }
        public List<CompletedFile> Files { get; set; } = new List<CompletedFile>();
    }
    internal sealed class CompletedFile
    {
        public string Source { get; set; }
        public string Path { get; set; }
        public string SourceSha256 { get; set; }
        public string Sha256 { get; set; }
        public bool ContentFrozen { get; set; }
        public string[] Dependencies { get; set; }
    }
    private sealed class Progress { public string FileName { get; set; } }
    private sealed class Owner
    {
        public int Id { get; set; }
        public long Started { get; set; }
        public int WorkerId { get; set; }
        public long WorkerStarted { get; set; }
    }
    private sealed class ReaderAddin
    {
        public int Id { get; set; }
        public long Started { get; set; }
        public string AssemblyId { get; set; }
    }
}

public partial class PdmAddin
{
    public static ISldWorks CurrentNativeArchiveSession(string job, int processId)
        => ArchiveNativeProcessWorkspace.CurrentReader(job, processId);
}
