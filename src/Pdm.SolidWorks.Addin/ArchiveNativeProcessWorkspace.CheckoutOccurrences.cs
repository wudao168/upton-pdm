using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

internal static partial class ArchiveNativeProcessWorkspace
{
    internal static string ReferenceBaselineDirectory => Path.Combine(Path.GetTempPath(), "UPLM", "reference-baseline");

    // Each acquisition owns one reader. Requests remain serial across discovery
    // passes and keep the original read-only, per-source verification.
    internal sealed class CheckoutOccurrenceReader
    {
        private readonly ISldWorks caller;
        private string job;
        private Request request;
        private Process worker;
        private long workerStarted;
        private int readCount;
        private int readerId;
        private bool closed;

        internal CheckoutOccurrenceReader(ISldWorks caller) => this.caller = caller;
        internal bool HasReads => readCount != 0;

        internal async Task<ArchivedMissingOccurrence[]> ReadAsync(string source, string configuration, string sourceHash,
            CancellationToken token)
        {
            if (closed) throw new InvalidOperationException("本次获取的历史缺件读取已结束。");
            token.ThrowIfCancellationRequested();
            source = Path.GetFullPath(source);
            if (!IsCheckoutBaselinePath(source)) throw new InvalidOperationException("历史缺件原件不在受控暂存目录中，尚未获取权限。");
            using var original = ProtectSavedPropertySource(source);
            CheckInUploadHashes.VerifySource(source, sourceHash, token);
            job ??= Path.Combine(Jobs, Guid.NewGuid().ToString("N"));
            var sequence = readCount + 1;
            var read = new SavedPropertyRead
            {
                Source = source, SourceSha256 = sourceHash, DocumentKind = (int)CadDocumentKind.Assembly,
                Configurations = new[] { configuration }, ReadOccurrences = true
            };
            token.ThrowIfCancellationRequested();
            if (worker == null) Start();
            WriteCheckoutMessage(CheckoutMessagePath(job, sequence, "request"), read);
            var result = CheckoutMessagePath(job, sequence, "result");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(result))
            {
                token.ThrowIfCancellationRequested();
                if (worker.HasExited) throw WorkerFailure();
                if (elapsed.Elapsed >= TimeSpan.FromMinutes(4))
                    throw new TimeoutException("核对历史缺件时等待独立 SolidWorks 超时，原窗口及工作图档已保留：" + Path.GetFileName(source));
                await Task.Delay(150, token);
            }
            token.ThrowIfCancellationRequested();
            var reply = Json().Deserialize<CheckoutOccurrenceReply>(File.ReadAllText(result));
            var owner = ReadOwnedCheckoutReader(job, worker.Id, workerStarted, request.CallerSolidWorksId);
            if (reply == null || reply.JobId != request.JobId || reply.Sequence != sequence || reply.ReaderId != owner.Id
                || readerId != 0 && readerId != owner.Id || !PdmAddin.PathsEqual(reply.Source, read.Source)
                || !string.Equals(reply.Configuration, configuration, StringComparison.Ordinal)
                || !string.Equals(reply.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase) || reply.Occurrences == null)
                throw new InvalidOperationException("历史缺件读取结果与本次图档、配置或独立进程不一致，尚未获取权限：" + Path.GetFileName(source));
            CheckInUploadHashes.VerifySource(source, sourceHash, token);
            readerId = owner.Id;
            readCount = sequence;
            return reply.Occurrences;
        }

        private void Start()
        {
            Directory.CreateDirectory(job);
            request = CreateRequest(caller, job);
            using (var foreground = Process.GetProcessById(request.CallerSolidWorksId))
                request.CheckoutCallerStartedUtcTicks = foreground.StartTime.ToUniversalTime().Ticks;
            var executable = Path.Combine(Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location), "Upton.Pdm.CadArchiveWorker.exe");
            if (!File.Exists(executable)) throw new InvalidOperationException("独立读取组件缺失，请完整更新 SolidWorks 插件后再获取权限。");
            WriteCheckoutMessage(Path.Combine(job, RequestName), request);
            var input = Path.Combine(job, "reader-request.json");
            WriteCheckoutMessage(input, new { ReadCheckoutOccurrences = true });
            var output = Path.Combine(job, "result.json");
            worker = Process.Start(new ProcessStartInfo(executable, "\"" + input + "\" \"" + output + "\"")
            { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (worker == null) throw new InvalidOperationException("独立读取组件未启动，尚未获取权限。");
            workerStarted = worker.StartTime.ToUniversalTime().Ticks;
        }

        internal Task CompleteAsync(CancellationToken token) => CloseAsync(true, token);

        internal async Task DisposeAsync()
        {
            try { await CloseAsync(false, CancellationToken.None); }
            catch (Exception error) { PluginDiagnosticLog.Record("历史缺件读取清理", error.ToString()); }
        }

        private async Task CloseAsync(bool completed, CancellationToken token)
        {
            if (closed) return;
            closed = true;
            if (job == null) return;
            try
            {
                if (worker == null) return;
                if (completed && !worker.HasExited)
                {
                    WriteCheckoutMessage(Path.Combine(job, "checkout-finish.json"), readCount);
                    var timer = Stopwatch.StartNew();
                    while (!worker.HasExited && timer.Elapsed < TimeSpan.FromSeconds(20))
                        await Task.Delay(150, token);
                }
                if (!worker.HasExited && completed)
                    throw new IOException("独立 SolidWorks 未能正常退出，已停止获取权限并保留当前窗口。");
                var finish = Path.Combine(job, "checkout-finished.json");
                if (completed && (worker.ExitCode != 0 || !File.Exists(finish)
                    || Json().Deserialize<int>(File.ReadAllText(finish)) != readCount))
                    throw WorkerFailure();
            }
            finally
            {
                try
                {
                    if (worker != null && !worker.HasExited)
                    {
                        File.WriteAllText(Path.Combine(job, "cancel"), "本次历史缺件核对已取消");
                        await StopOwnedReaderAsync(worker, workerStarted, request.CallerSolidWorksId, job);
                    }
                    if (worker != null) await StopRemainingCheckoutReaderAsync(job, worker.Id, workerStarted, request.CallerSolidWorksId);
                }
                finally
                {
                    worker?.Dispose();
                    ArchiveNativeJournalScope.Recover(job);
                    DeleteJob(job);
                }
            }
            if (completed) PluginDiagnosticLog.Record("历史缺件核对", "使用一个独立 SolidWorks 进程读取 " + readCount + " 份装配基准；进程 " + readerId + " 已结束。");
        }

        private Exception WorkerFailure()
        {
            var result = Path.Combine(job, "result.json");
            var diagnostic = result + ".diagnostic.txt";
            if (File.Exists(diagnostic)) PluginDiagnosticLog.Record("历史缺件核对", File.ReadAllText(diagnostic));
            var message = File.Exists(result) ? Json().Deserialize<Response>(File.ReadAllText(result))?.Error : null;
            return new InvalidOperationException(message ?? "历史缺件的独立读取未完成，尚未获取权限，原窗口及工作图档已保留。");
        }
    }

    private static void WriteCheckoutMessage(string path, object value)
    {
        var pending = path + ".tmp";
        File.WriteAllText(pending, Json().Serialize(value));
        File.Move(pending, path);
    }

    private static string CheckoutMessagePath(string job, int sequence, string kind) =>
        Path.Combine(job, "checkout-" + sequence.ToString(CultureInfo.InvariantCulture) + "." + kind + ".json");

    private static bool IsCheckoutBaselinePath(string path)
    {
        string ContainerEntry(string directory) => ManagedCadPaths.IsWithin(path, directory)
            ? path.Substring(directory.Length + 1).Split(Path.DirectorySeparatorChar)[0] : string.Empty;
        if (Guid.TryParseExact(ContainerEntry(ReferenceBaselineDirectory), "N", out _)) return true;
        var compact = ContainerEntry(ManagedCadPaths.CompactWorkspaceRoot);
        return compact.Length == 17 && compact[0] == 's' && compact.Skip(1).All(Uri.IsHexDigit);
    }

    private static Owner ReadOwnedCheckoutReader(string job, int workerId, long workerStarted, int callerId)
    {
        var owner = Json().Deserialize<Owner>(File.ReadAllText(Path.Combine(job, "native-owner.json")));
        if (owner == null || owner.WorkerId != workerId || owner.WorkerStarted != workerStarted
            || owner.Id == callerId || owner.Id == workerId || owner.Id <= 0 || owner.Started < workerStarted)
            throw new InvalidOperationException("无法确认历史缺件读取进程的归属，原 SolidWorks 窗口已保留。");
        return owner;
    }

    private static async Task StopRemainingCheckoutReaderAsync(string job, int workerId, long workerStarted, int callerId)
    {
        if (!File.Exists(Path.Combine(job, "native-owner.json"))) return;
        var owner = ReadOwnedCheckoutReader(job, workerId, workerStarted, callerId);
        try
        {
            using var reader = Process.GetProcessById(owner.Id);
            if (reader.HasExited || reader.ProcessName != "SLDWORKS" || reader.StartTime.ToUniversalTime().Ticks != owner.Started) return;
            reader.Kill();
            if (!await Task.Run(() => reader.WaitForExit(5000)))
                throw new IOException("本次获取的独立读取进程未能结束，未继续获取编辑权限。");
        }
        catch (ArgumentException) { } // The owned process has already exited.
    }

    internal static void RunCheckoutOccurrenceSession(ISldWorks app, string job)
    {
        if (!IsJob(job)) throw new InvalidOperationException("历史缺件作业的临时目录无效。");
        var request = Json().Deserialize<Request>(File.ReadAllText(Path.Combine(job, RequestName)));
        using var worker = Process.GetCurrentProcess();
        var owner = ReadOwnedCheckoutReader(job, worker.Id, worker.StartTime.ToUniversalTime().Ticks, request.CallerSolidWorksId);
        if (request.JobId != Path.GetFileName(job) || owner.Id != app.GetProcessID() || CurrentReader(job, owner.Id) == null)
            throw new InvalidOperationException("无法确认本次历史缺件作业的独立 SolidWorks 会话。");
        using var caller = Process.GetProcessById(request.CallerSolidWorksId);
        if (caller.StartTime.ToUniversalTime().Ticks != request.CheckoutCallerStartedUtcTicks)
            throw new InvalidOperationException("发起历史缺件读取的 SolidWorks 会话已变化。");

        var finish = Path.Combine(job, "checkout-finish.json");
        var cancel = Path.Combine(job, "cancel");
        var sequence = 1;
        var idle = Stopwatch.StartNew();
        app.UserControlBackground = true;
        while (!caller.HasExited && !File.Exists(cancel))
        {
            var input = CheckoutMessagePath(job, sequence, "request");
            if (File.Exists(input))
            {
                var read = Json().Deserialize<SavedPropertyRead>(File.ReadAllText(input));
                if (read == null || !ValidSavedPropertyRead(read) || !read.ReadOccurrences || read.ReadReferences
                    || read.DocumentKind != (int)CadDocumentKind.Assembly || read.Configurations.Length != 1
                    || !IsCheckoutBaselinePath(read.Source))
                    throw new InvalidOperationException("历史缺件的图档、配置或临时读取路径没有通过校验。");
                var occurrences = ReadCheckoutOccurrences(app, read, job);
                WriteCheckoutMessage(CheckoutMessagePath(job, sequence, "result"), new CheckoutOccurrenceReply
                {
                    JobId = request.JobId, Sequence = sequence, ReaderId = owner.Id, Source = read.Source,
                    Configuration = read.Configurations[0], Sha256 = read.SourceSha256, Occurrences = occurrences
                });
                sequence++;
                idle.Restart();
                continue;
            }
            if (File.Exists(finish))
            {
                if (Json().Deserialize<int>(File.ReadAllText(finish)) != sequence - 1 || app.GetDocumentCount() != 0)
                    throw new InvalidOperationException("独立 SolidWorks 仍有未完成的读取或已加载图档，尚未获取权限。");
                WriteCheckoutMessage(Path.Combine(job, "checkout-finished.json"), sequence - 1);
                return;
            }
            if (idle.Elapsed >= TimeSpan.FromMinutes(11))
                throw new TimeoutException("独立 SolidWorks 等待下一份历史缺件图档超时。");
            Thread.Sleep(150);
        }
    }

    private static ArchivedMissingOccurrence[] ReadCheckoutOccurrences(ISldWorks app, SavedPropertyRead read, string job)
    {
        if (app.GetDocumentCount() != 0 || !string.Equals(ArchiveFileFingerprint.Read(read.Source), read.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("独立 SolidWorks 的初始文档状态或存档原件内容无法确认。");
        var previousCommand = app.CommandInProgress;
        try
        {
            app.CommandInProgress = true;
            WriteProgress(job, Path.GetFileName(read.Source));
            var occurrences = PdmAddin.ReadArchivedAssemblyOccurrences(app, read.Source, read.Configurations[0]);
            if (app.GetDocumentCount() != 0 && (!app.CloseAllDocuments(true) || app.GetDocumentCount() != 0))
                throw new IOException("隔离读取器未能清理已读文档，已停止下一份图档的历史核对。");
            if (!string.Equals(ArchiveFileFingerprint.Read(read.Source), read.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("读取期间存档原件内容发生变化，尚未获取权限。");
            WriteProgress(job, Path.GetFileName(read.Source));
            return occurrences;
        }
        finally { app.CommandInProgress = previousCommand; }
    }

    internal sealed class CheckoutOccurrenceReply
    {
        public string JobId { get; set; }
        public int Sequence { get; set; }
        public int ReaderId { get; set; }
        public string Source { get; set; }
        public string Configuration { get; set; }
        public string Sha256 { get; set; }
        public ArchivedMissingOccurrence[] Occurrences { get; set; }
    }
}

public partial class PdmAddin
{
    public static void RunCheckoutOccurrenceSession(ISldWorks app, string job) =>
        ArchiveNativeProcessWorkspace.RunCheckoutOccurrenceSession(app, job);
}
