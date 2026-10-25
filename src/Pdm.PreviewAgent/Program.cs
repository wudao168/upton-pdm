using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.PreviewAgent;

/// <summary>
/// 转图电脑上的转图代理：接收API服务器打包的源文件，调用本机SolidWorks转换程序生成PDF/STEP，再把结果打包返回。
/// 部署要求：本目录（或其 preview-worker 子目录）放置 Upton.Pdm.SolidWorks.PreviewWorker.exe 与 SolidWorks 互操作程序集，并安装 SolidWorks。
/// </summary>
internal static partial class Program
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly PersistentWorker Worker = new PersistentWorker();
    private static readonly SemaphoreSlim ConversionGate = new SemaphoreSlim(1, 1);
    private static int waitingRequests;
    private static string currentTask = string.Empty;

    [STAThread]
    private static int Main(string[] args) => RunApplication(args);

    private static async Task HandleClientAsync(TcpClient client, AgentOptions options)
    {
        AgentBatch batch = null;
        using (client)
        using (var stream = client.GetStream())
        try
        {
            var request = await AgentHttpRequest.ReadAsync(stream);
            if (request is null) return;
            var path = request.Path.TrimEnd('/');
            if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 200, new
                {
                    status = "ok",
                    machine = Environment.MachineName,
                    name = options.Name,
                    busy = Paused || ConversionGate.CurrentCount == 0,
                    processing = ConversionGate.CurrentCount == 0,
                    accepting = !Paused,
                    windowCreated = WindowCreated,
                    windowVisible = WindowVisible,
                    queued = Volatile.Read(ref waitingRequests),
                    currentTask,
                    agentVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
                    workerPath = options.WorkerPath,
                    workerExists = File.Exists(options.WorkerPath),
                    workerVersion = File.Exists(options.WorkerPath) ? FileVersionInfo.GetVersionInfo(options.WorkerPath).FileVersion : null,
                    timeoutMinutes = options.TimeoutMinutes,
                    workRoot = options.WorkRoot,
                    solidWorksPid = Worker.SolidWorksPid, solidWorksRunning = Worker.SolidWorksRunning,
                    conversionStage = Worker.Stage, completed = Worker.Completed, total = Worker.Total, progressUpdatedAt = Worker.UpdatedAt,
                    workerSession = Worker.IsRunning ? $"转换工作进程运行中（PID {Worker.ProcessId}）" : "转换工作进程未启动"
                });
                return;
            }
            if (!string.Equals(path, "/convert", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 404, new { error = "未知的接口。" });
                return;
            }
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 405, new { error = "转换接口只接受POST。" });
                return;
            }
            if (!string.IsNullOrEmpty(options.Token)
                && !string.Equals(request.Header("X-Pdm-Agent-Token"), options.Token, StringComparison.Ordinal))
            {
                await WriteJsonAsync(stream, 401, new { error = "转图代理访问令牌不正确。" });
                return;
            }

            lock (RuntimeGate)
            {
                if (!Paused)
                {
                    batch = Journal.Begin(Convert.ToString(client.Client?.RemoteEndPoint));
                    Interlocked.Increment(ref waitingRequests);
                }
            }
            if (batch == null)
            {
                await WriteJsonAsync(stream, 503, new { error = "代理已暂停接收新任务，请稍后重试或在代理首页恢复接收。" });
                return;
            }

            var jobRoot = Path.Combine(options.WorkRoot, Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(jobRoot, "sources");
            var outputRoot = Path.Combine(jobRoot, "outputs");
            await ConversionGate.WaitAsync();
            Interlocked.Decrement(ref waitingRequests);
            CurrentBatchId = batch.Id;
            Journal.Update(batch.Id, item => { item.Status = "处理中"; item.StartedUtc = DateTime.UtcNow; item.Stage = "正在接收和校验源文件"; });
            try
            {
                Directory.CreateDirectory(sourceRoot);
                Directory.CreateDirectory(outputRoot);
                var requestPath = Path.Combine(jobRoot, "request.zip");
                using (var requestFile = File.Create(requestPath))
                {
                    await request.CopyBodyToAsync(requestFile, options.MaxRequestBytes);
                }

                var manifest = ReadManifest(requestPath, sourceRoot);
                if (manifest.Jobs.Count == 0) throw new InvalidOperationException("转换请求中没有任务。");
                var convertJobs = manifest.Jobs.Where(job => job.Convert).ToList();
                if (convertJobs.Count == 0) throw new InvalidDataException("转换请求没有需要处理的图档。");
                Journal.Update(batch.Id, item =>
                {
                    item.Context = manifest.Context ?? new AgentBatchContext();
                    item.ReferenceFiles = manifest.Jobs.Count - convertJobs.Count;
                    item.Files = convertJobs.Select(job => new AgentFileRecord { DocumentId = job.DocumentId, FileName = job.FileName }).ToList();
                });
                Journal.Log("信息", "源文件校验完成：处理 " + convertJobs.Count + " 个图档，引用文件 " + (manifest.Jobs.Count - convertJobs.Count) + " 个。", batch.Id);
                currentTask = $"{convertJobs.Count} 项：{string.Join("、", convertJobs.Take(3).Select(job => job.FileName))}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 收到转换任务 {convertJobs.Count} 项（含只作引用的文件共 {manifest.Jobs.Count} 个），来源 {client.Client?.RemoteEndPoint}");
                RunWorker(options, jobRoot, sourceRoot, outputRoot, manifest);
                var resultPath = Path.Combine(jobRoot, "worker-result.json");
                var result = Serializer.Deserialize<WorkerResult>(File.ReadAllText(resultPath, Encoding.UTF8));
                Journal.Update(batch.Id, item =>
                {
                    foreach (var file in item.Files)
                    {
                        var outcome = result.Items?.SingleOrDefault(value => value.DocumentId == file.DocumentId);
                        var success = outcome?.Success ?? (result.Items == null && result.Success);
                        file.Status = success ? "成功" : "失败";
                        file.Message = success ? "转换完成" : AgentText.Safe(outcome?.Error, "图档转换失败，请核对源文件及引用后重试，技术详情已记录日志。");
                        if (!file.FinishedUtc.HasValue) file.FinishedUtc = DateTime.UtcNow;
                    }
                });
                var successfulJobs = convertJobs.Where(job => result.Items == null || result.Items.Any(item => item.DocumentId == job.DocumentId && item.Success)).ToList();

                var missing = successfulJobs
                    .Where(job => !File.Exists(Path.Combine(outputRoot, job.OutputName)))
                    .Select(job => job.OutputName)
                    .ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException($"以下预览文件未生成：{string.Join("、", missing.Take(5))}");

                var responsePath = Path.Combine(jobRoot, "response.zip");
                using (var fileStream = File.Create(responsePath))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, true))
                {
                    var resultEntry = archive.CreateEntry("result.json", CompressionLevel.Fastest);
                    using (var resultStream = resultEntry.Open())
                    {
                        var payload = Encoding.UTF8.GetBytes(Serializer.Serialize(new { result.Success, result.Error, result.Items, result.AssemblyPreviewVersion }));
                        resultStream.Write(payload, 0, payload.Length);
                    }
                    // 只回传需要转出的结果；引用文件的结果服务端不会使用，回传只是浪费带宽和时间。
                    foreach (var job in successfulJobs)
                    {
                        archive.CreateEntryFromFile(Path.Combine(outputRoot, job.OutputName), "outputs/" + job.OutputName, CompressionLevel.Fastest);
                    }
                }
                using (var responseFile = File.OpenRead(responsePath))
                {
                    await AgentHttpRequest.WriteResponseAsync(stream, 200, "application/zip", responseFile.Length, responseFile);
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 转换结束：成功 {successfulJobs.Count} 项，失败 {convertJobs.Count - successfulJobs.Count} 项");
                Journal.Complete(batch.Id, true);
            }
            finally
            {
                currentTask = string.Empty;
                CurrentBatchId = null;
                ConversionGate.Release();
                var resultFile = Path.Combine(jobRoot, "worker-result.json");
                var succeeded = false;
                try { succeeded = File.Exists(resultFile) && Serializer.Deserialize<WorkerResult>(File.ReadAllText(resultFile, Encoding.UTF8))?.Success == true; } catch (Exception) { }
                if (succeeded) TryDeleteDirectory(jobRoot);
                else
                {
                    // 保留清单和结果用于定位，不长期积累整套模型和重复压缩包。
                    TryDeleteDirectory(sourceRoot);
                    TryDeleteDirectory(outputRoot);
                    foreach (var archiveName in new[] { "request.zip", "response.zip" })
                        try { File.Delete(Path.Combine(jobRoot, archiveName)); } catch (Exception) { }
                    Console.Error.WriteLine("失败任务诊断保留在：" + jobRoot);
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] 转换失败：{exception.Message}");
            Journal.Diagnostic(exception.ToString());
            var message = "转图任务未完成，请检查源文件、引用及转换程序状态后重试；技术详情已保存在代理诊断日志。";
            if (exception is InvalidDataException || exception is FileNotFoundException)
                message = "转图源文件不完整或无法读取，请核对本批文件后重新发起。";
            if (exception is TimeoutException) message = "图档转换等待超时，请检查SolidWorks状态和图档引用后重新发起。";
            if (batch != null) Journal.Complete(batch.Id, false, message);
            else Journal.Log("提醒", message);
            try
            {
                await WriteJsonAsync(stream, 500, new { error = message });
            }
            catch (Exception)
            {
            }
        }
    }

    private static AgentManifest ReadManifest(string requestPath, string sourceRoot)
    {
        using (var archive = ZipFile.OpenRead(requestPath))
        {
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("转换请求缺少 manifest.json。");
            AgentManifest manifest;
            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                manifest = Serializer.Deserialize<AgentManifest>(reader.ReadToEnd())
                    ?? throw new InvalidOperationException("转换请求的 manifest.json 无效。");
            }
            foreach (var job in manifest.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.FileName) || string.IsNullOrWhiteSpace(job.OutputName))
                    throw new InvalidOperationException("转换请求的 manifest.json 字段不完整。");
                var entry = archive.GetEntry("sources/" + job.FileName)
                    ?? throw new InvalidOperationException($"转换请求缺少源文件{job.FileName}。");
                entry.ExtractToFile(Path.Combine(sourceRoot, job.FileName), true);
            }
            return manifest;
        }
    }

    private static void RunWorker(AgentOptions options, string jobRoot, string sourceRoot, string outputRoot, AgentManifest manifest)
    {
        var jobs = manifest.Jobs.Where(job => job.Convert).ToList();
        // 本次只需要引用文件时（例如整批都已经转好，只是重发引用树）不必启动 SolidWorks。
        if (jobs.Count == 0) return;
        var workerManifest = new WorkerManifest
        {
            Jobs = jobs.Select(job => new WorkerJob
            {
                DocumentId = job.DocumentId,
                SourcePath = Path.Combine(sourceRoot, job.FileName),
                OutputPath = Path.Combine(outputRoot, job.OutputName),
                Kind = job.Kind,
                ReleaseRevision = job.ReleaseRevision,
                QrContent = job.QrContent,
                AssemblyPreview = job.AssemblyPreview,
                Configuration = job.Configuration
            }).ToList()
        };
        var manifestPath = Path.Combine(jobRoot, "worker-manifest.json");
        var resultPath = Path.Combine(jobRoot, "worker-result.json");
        File.WriteAllText(manifestPath, Serializer.Serialize(workerManifest), new UTF8Encoding(false));

        var outcome = Worker.TryRun(options, manifestPath, resultPath);
        if (outcome == PersistentOutcome.Success) return;
        if (outcome == PersistentOutcome.ConversionFailed) { VerifyWorkerResult(resultPath); return; }
        // 常驻会话不可用（首次启动失败、进程已退出等）时回落到一次性调用，保证转图不受影响。
        RunWorkerOnce(options, manifestPath, resultPath);
        VerifyWorkerResult(resultPath);
    }

    private static void RunWorkerOnce(AgentOptions options, string manifestPath, string resultPath)
    {
        var startInfo = new ProcessStartInfo(options.WorkerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(options.WorkerPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Arguments = string.Join(" ", new[] { manifestPath, resultPath }
            .Concat(options.WorkerArguments)
            .Select(argument => "\"" + (argument ?? string.Empty).Replace("\"", "\\\"") + "\""));
        using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动SolidWorks转换程序。"))
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(options.TimeoutMinutes).TotalMilliseconds))
            {
                try { process.Kill(); } catch (Exception) { }
                throw new InvalidOperationException($"SolidWorks转换超过{options.TimeoutMinutes}分钟，已终止。");
            }
            var output = standardOutput.GetAwaiter().GetResult();
            var error = standardError.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"SolidWorks转换进程失败：{(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
        }
    }

    private static void VerifyWorkerResult(string resultPath)
    {
        if (!File.Exists(resultPath)) throw new InvalidOperationException("SolidWorks转换未生成结果文件。");
        var result = Serializer.Deserialize<WorkerResult>(File.ReadAllText(resultPath, Encoding.UTF8));
        if (result == null) throw new InvalidOperationException("SolidWorks转换结果文件无效。");
        if (!result.Success && result.Items == null) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "SolidWorks转换失败。" : result.Error);
    }

    private enum PersistentOutcome
    {
        Success,
        ConversionFailed,
        Unavailable
    }

    /// <summary>
    /// 常驻 SolidWorks 会话：转图程序以 --serve 模式启动一次并跨请求复用 SolidWorks 实例，
    /// 避免每次请求都重新启动 SolidWorks（启动一次约十几秒）。
    /// </summary>
    private sealed class PersistentWorker
    {
        private readonly object gate = new object();
        private Process process;
        private StreamWriter input;
        private StreamReader output;

        public bool IsRunning => process != null && !process.HasExited;

        public int ProcessId => process?.Id ?? 0;
        public int SolidWorksPid { get; private set; }
        public bool SolidWorksRunning { get { try { using (var cad = Process.GetProcessById(SolidWorksPid)) return !cad.HasExited; } catch { return false; } } }
        public string Stage { get; private set; } = "尚未执行";
        public int Completed { get; private set; }
        public int Total { get; private set; }
        public string UpdatedAt { get; private set; }

        public PersistentOutcome TryRun(AgentOptions options, string manifestPath, string resultPath)
        {
            lock (gate)
            {
                try
                {
                    if (!EnsureStarted(options)) return PersistentOutcome.Unavailable;
                    input.WriteLine(Serializer.Serialize(new { Manifest = manifestPath, Result = resultPath }));
                    input.Flush();
                    Stage = "等待转换程序响应";
                    Completed = Total = 0;
                    UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    var deadline = DateTime.UtcNow.AddMinutes(options.TimeoutMinutes);
                    while (true)
                    {
                        var responseTask = output.ReadLineAsync();
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero || !responseTask.Wait(remaining))
                        {
                            Stage = $"SolidWorks转换超过{options.TimeoutMinutes}分钟，已终止。";
                            Stop();
                            throw new TimeoutException(Stage);
                        }
                        var line = responseTask.Result;
                        if (string.IsNullOrWhiteSpace(line)) return PersistentOutcome.Unavailable;
                        var payload = Serializer.Deserialize<WorkerResult>(line);
                        if (payload == null) return PersistentOutcome.Unavailable;
                        UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        if (payload.Progress)
                        {
                            SolidWorksPid = payload.SolidWorksPid; Stage = payload.Stage; Completed = payload.Completed; Total = payload.Total;
                            if (CurrentBatchId.HasValue) Journal.Progress(CurrentBatchId.Value, payload.Stage, payload.Completed);
                            if (CurrentBatchId.HasValue && payload.Item != null)
                            {
                                Journal.Update(CurrentBatchId.Value, batch =>
                                {
                                    var file = batch.Files.Single(item => item.DocumentId == payload.Item.DocumentId);
                                    file.Status = payload.Item.Success ? "成功" : "失败";
                                    file.Message = payload.Item.Success ? "转换完成" : AgentText.Safe(payload.Item.Error, "图档转换失败，请核对源文件及引用后重试，技术详情已记录日志。");
                                    file.FinishedUtc = DateTime.UtcNow;
                                    Journal.Log(payload.Item.Success ? "成功" : "失败", file.FileName + "：" + file.Message, batch.Id);
                                });
                                if (!string.IsNullOrWhiteSpace(payload.Item.Error)) Journal.Diagnostic(payload.Item.Error);
                            }
                            continue;
                        }
                        Stage = payload.Success ? "转换完成" : "转换失败：" + payload.Error;
                        return payload.Success ? PersistentOutcome.Success : PersistentOutcome.ConversionFailed;
                    }
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] 常驻SolidWorks会话不可用：{exception.Message}");
                    Stop();
                    return PersistentOutcome.Unavailable;
                }
            }
        }

        private bool EnsureStarted(AgentOptions options)
        {
            if (process != null && !process.HasExited) return true;
            Stop();
            var startInfo = new ProcessStartInfo(options.WorkerPath)
            {
                WorkingDirectory = Path.GetDirectoryName(options.WorkerPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = string.Join(" ", new[] { "--serve" }
                    .Concat(options.WorkerArguments)
                    .Select(argument => "\"" + (argument ?? string.Empty).Replace("\"", "\\\"") + "\""))
            };
            var started = Process.Start(startInfo);
            if (started == null) return false;
            process = started;
            input = started.StandardInput;
            output = started.StandardOutput;
            input.AutoFlush = true;
            started.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data)) Console.Error.WriteLine(eventArgs.Data);
            };
            started.BeginErrorReadLine();
            var readyTask = output.ReadLineAsync();
            if (!readyTask.Wait(TimeSpan.FromMinutes(1)) || string.IsNullOrWhiteSpace(readyTask.Result))
            {
                Stop();
                return false;
            }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 已启动常驻SolidWorks会话（PID {process.Id}）");
            return true;
        }

        internal void Stop()
        {
            try { input?.Dispose(); } catch (Exception) { }
            try { if (process != null && !process.HasExited) process.Kill(); } catch (Exception) { }
            try { process?.Dispose(); } catch (Exception) { }
            input = null;
            output = null;
            process = null;
        }

        internal void Close()
        {
            lock (gate)
            {
                // Closing stdin lets the worker dispose its own SolidWorks session; never close a user's separate CAD process.
                try { input?.Dispose(); input = null; if (process != null && !process.HasExited) process.WaitForExit(15000); }
                catch (Exception error) when (error is IOException || error is InvalidOperationException) { Journal.Diagnostic(error.ToString()); }
                finally { Stop(); }
            }
        }
    }

    private static async Task WriteJsonAsync(Stream stream, int statusCode, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(Serializer.Serialize(payload));
        using (var buffer = new MemoryStream(bytes))
        {
            await AgentHttpRequest.WriteResponseAsync(stream, statusCode, "application/json; charset=utf-8", bytes.Length, buffer);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
        }
    }

    private sealed class AgentManifest
    {
        public List<AgentJob> Jobs { get; set; } = new List<AgentJob>();
        public AgentBatchContext Context { get; set; }
    }

    private sealed class AgentJob
    {
        public Guid DocumentId { get; set; }
        public string Kind { get; set; }
        public string FileName { get; set; }
        public string OutputName { get; set; }
        // 老版本服务端不带该字段，缺省按“需要转出”处理（引用文件只作参考、不转出时明确传 false）。
        public bool Convert { get; set; } = true;
        public string ReleaseRevision { get; set; }
        public string QrContent { get; set; }
        public bool AssemblyPreview { get; set; }
        public string Configuration { get; set; }
    }

    private sealed class WorkerManifest
    {
        public List<WorkerJob> Jobs { get; set; }
    }

    private sealed class WorkerJob
    {
        public Guid DocumentId { get; set; }
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string Kind { get; set; }
        public string ReleaseRevision { get; set; }
        public string QrContent { get; set; }
        public bool AssemblyPreview { get; set; }
        public string Configuration { get; set; }
    }

    private sealed class WorkerResult
    {
        public WorkerItem Item { get; set; }
        public int AssemblyPreviewVersion { get; set; }
        public bool Progress { get; set; }
        public int SolidWorksPid { get; set; }
        public string Stage { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<WorkerItem> Items { get; set; }
    }

    private sealed class WorkerItem
    {
        public Guid DocumentId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
