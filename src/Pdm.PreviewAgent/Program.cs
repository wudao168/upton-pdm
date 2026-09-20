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
internal static class Program
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    private static int Main(string[] args)
    {
        var options = AgentOptions.Load(args);
        if (!File.Exists(options.WorkerPath))
        {
            Console.Error.WriteLine($"SolidWorks转换程序不存在：{options.WorkerPath}");
            return 1;
        }

        var listener = new TcpListener(IPAddress.Parse(options.BindAddress), options.Port);
        try
        {
            listener.Start();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"转图代理启动失败（{options.BindAddress}:{options.Port}）：{exception.Message}。请确认端口未被占用并已在防火墙中放通。");
            return 1;
        }

        Console.WriteLine($"转图代理已启动：http://{options.BindAddress}:{options.Port}/");
        Console.WriteLine($"工作目录：{options.WorkRoot}；转换程序：{options.WorkerPath}；超时：{options.TimeoutMinutes} 分钟；访问令牌：{(string.IsNullOrEmpty(options.Token) ? "未设置" : "已设置")}");
        var stopping = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Set(); };
        while (!stopping.IsSet)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, options));
        }
        listener.Stop();
        return 0;
    }

    private static async Task HandleClientAsync(TcpClient client, AgentOptions options)
    {
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
                    agentVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
                    workerPath = options.WorkerPath,
                    workerExists = File.Exists(options.WorkerPath),
                    timeoutMinutes = options.TimeoutMinutes,
                    workRoot = options.WorkRoot
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

            var jobRoot = Path.Combine(options.WorkRoot, Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(jobRoot, "sources");
            var outputRoot = Path.Combine(jobRoot, "outputs");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            try
            {
                var requestPath = Path.Combine(jobRoot, "request.zip");
                using (var requestFile = File.Create(requestPath))
                {
                    await request.CopyBodyToAsync(requestFile, options.MaxRequestBytes);
                }

                var manifest = ReadManifest(requestPath, sourceRoot);
                if (manifest.Jobs.Count == 0) throw new InvalidOperationException("转换请求中没有任务。");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 收到转换任务 {manifest.Jobs.Count} 项，来源 {client.Client?.RemoteEndPoint}");
                RunWorker(options, jobRoot, sourceRoot, outputRoot, manifest);

                var missing = manifest.Jobs
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
                        var payload = Encoding.UTF8.GetBytes(Serializer.Serialize(new { Success = true, Error = (string)null }));
                        resultStream.Write(payload, 0, payload.Length);
                    }
                    foreach (var job in manifest.Jobs)
                    {
                        archive.CreateEntryFromFile(Path.Combine(outputRoot, job.OutputName), "outputs/" + job.OutputName, CompressionLevel.Fastest);
                    }
                }
                using (var responseFile = File.OpenRead(responsePath))
                {
                    await AgentHttpRequest.WriteResponseAsync(stream, 200, "application/zip", responseFile.Length, responseFile);
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 转换完成 {manifest.Jobs.Count} 项");
            }
            finally
            {
                TryDeleteDirectory(jobRoot);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] 转换失败：{exception.Message}");
            try
            {
                await WriteJsonAsync(stream, 500, new { error = exception.Message });
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
        var workerManifest = new WorkerManifest
        {
            Jobs = manifest.Jobs.Select(job => new WorkerJob
            {
                DocumentId = job.DocumentId,
                SourcePath = Path.Combine(sourceRoot, job.FileName),
                OutputPath = Path.Combine(outputRoot, job.OutputName),
                Kind = job.Kind
            }).ToList()
        };
        var manifestPath = Path.Combine(jobRoot, "worker-manifest.json");
        var resultPath = Path.Combine(jobRoot, "worker-result.json");
        File.WriteAllText(manifestPath, Serializer.Serialize(workerManifest), new UTF8Encoding(false));

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
        if (!File.Exists(resultPath)) throw new InvalidOperationException("SolidWorks转换未生成结果文件。");
        var result = Serializer.Deserialize<WorkerResult>(File.ReadAllText(resultPath, Encoding.UTF8));
        if (result == null) throw new InvalidOperationException("SolidWorks转换结果文件无效。");
        if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "SolidWorks转换失败。" : result.Error);
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
    }

    private sealed class AgentJob
    {
        public Guid DocumentId { get; set; }
        public string Kind { get; set; }
        public string FileName { get; set; }
        public string OutputName { get; set; }
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
    }

    private sealed class WorkerResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
