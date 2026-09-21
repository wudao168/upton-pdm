using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class PdmPreviewWorkerOptions
{
    public const string SectionName = "Pdm:PreviewWorker";

    public string WorkerPath { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        "preview-worker",
        "Upton.Pdm.SolidWorks.PreviewWorker.exe");

    public int TimeoutMinutes { get; set; } = 30;
}

public sealed class SolidWorksServerPreviewConverter : IServerPreviewConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PdmPreviewWorkerOptions settings;
    private readonly IPdmRepository? repository;
    private readonly HttpClient httpClient;

    public SolidWorksServerPreviewConverter(IOptions<PdmPreviewWorkerOptions> options, IPdmRepository? repository = null, HttpClient? httpClient = null)
    {
        settings = options.Value;
        this.repository = repository;
        this.httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(
        ReleasePackage package,
        Project project,
        IReadOnlyList<ReleasePreviewSource> sources,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var conversionSettings = repository is null
            ? PreviewConversionSettings.Default
            : (await repository.GetSystemSettingsAsync(cancellationToken)).PreviewConversion ?? PreviewConversionSettings.Default;
        if (conversionSettings.TimeoutMinutes is < 1 or > 120) throw new PdmRuleException("图纸转换超时必须设置为1到120分钟。");
        var remote = conversionSettings.Mode == PreviewConversionMode.Remote;
        if (remote && !Uri.TryCreate(conversionSettings.NormalizedAgentUrl, UriKind.Absolute, out _))
            throw new PdmRuleException("远程转图服务器地址未配置，请在系统设置中填写转图电脑地址（例如 http://192.168.2.50:5199）。");
        if (!remote && !OperatingSystem.IsWindows())
            throw new PdmRuleException("本机SolidWorks转换只能在Windows服务器运行；如需在其他电脑转图，请在系统设置中选择远程转图服务器。");

        var duplicateFileName = sources
            .GroupBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFileName is not null)
            throw new PdmRuleException($"发布引用树包含重名源文件{duplicateFileName.Key}，无法安全重建SolidWorks引用关系。");

        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var previewRelativeRoot = Path.Combine(".release-previews", package.Id.ToString("N"));
        var previewRoot = StorageLocationPolicy.ResolveUnder(vaultRoot, previewRelativeRoot);
        var stagingPreviewRoot = StorageLocationPolicy.ResolveUnder(stagingDirectory, "previews");
        var workRoot = Path.Combine(Path.GetTempPath(), "UPTON-PLM", "release-preview", package.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(workRoot, "sources");
        var outputRoot = Path.Combine(workRoot, "outputs");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        ResetDirectory(previewRoot);
        ResetDirectory(stagingPreviewRoot);

        try
        {
            var jobs = new List<PreviewJob>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = StorageLocationPolicy.ResolveUnder(vaultRoot, source.StorageRelativePath);
                await VerifySourceAsync(source, sourcePath, cancellationToken);
                var extension = source.Kind == DocumentKind.Drawing ? ".pdf" : ".step";
                var outputFileName = $"{source.DocumentId:N}_{Path.GetFileNameWithoutExtension(source.FileName)}{extension}";
                jobs.Add(new PreviewJob(
                    source.DocumentId,
                    source.Kind,
                    source.FileName,
                    sourcePath,
                    Path.Combine(outputRoot, outputFileName)));
            }

            if (remote)
                await RunRemoteAgentAsync(conversionSettings, jobs, workRoot, outputRoot, cancellationToken);
            else
                await RunLocalWorkerAsync(conversionSettings, jobs, sourceRoot, workRoot, outputRoot, cancellationToken);

            var artifacts = new Dictionary<Guid, DocumentPreviewArtifact>();
            foreach (var source in sources)
            {
                var job = jobs.Single(item => item.DocumentId == source.DocumentId);
                if (!File.Exists(job.OutputPath) || new FileInfo(job.OutputPath).Length == 0)
                {
                    throw new PdmRuleException(remote
                        ? $"转图服务器未生成图档{source.DrawingNumber}的预览文件。"
                        : $"服务器未生成图档{source.DrawingNumber}的预览文件。");
                }
                var outputName = Path.GetFileName(job.OutputPath);
                var vaultPreviewPath = Path.Combine(previewRoot, outputName);
                var stagingPreviewPath = Path.Combine(stagingPreviewRoot, outputName);
                File.Copy(job.OutputPath, vaultPreviewPath, true);
                File.Copy(job.OutputPath, stagingPreviewPath, true);
                var sha256 = await ComputeSha256Async(vaultPreviewPath, cancellationToken);
                artifacts[source.DocumentId] = new DocumentPreviewArtifact(
                    source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    Path.GetRelativePath(vaultRoot, vaultPreviewPath),
                    new FileInfo(vaultPreviewPath).Length,
                    sha256,
                    source.SourceSha256);
            }
            return artifacts;
        }
        catch
        {
            DeleteDirectory(previewRoot);
            DeleteDirectory(stagingPreviewRoot);
            throw;
        }
        finally
        {
            DeleteDirectory(workRoot);
        }
    }

    /// <summary>本机转换：把源文件复制到临时工作目录后调用SolidWorks转换程序。</summary>
    private async Task RunLocalWorkerAsync(
        PreviewConversionSettings conversion,
        IReadOnlyList<PreviewJob> jobs,
        string sourceRoot,
        string workRoot,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        _ = outputRoot;
        var workerPath = Path.GetFullPath(settings.WorkerPath);
        if (!File.Exists(workerPath)) throw new PdmRuleException($"服务器SolidWorks转换程序不存在：{workerPath}");
        var workerJobs = new List<PreviewWorkerJob>();
        foreach (var job in jobs)
        {
            var workspaceSourcePath = Path.Combine(sourceRoot, job.FileName);
            File.Copy(job.SourcePath, workspaceSourcePath, false);
            workerJobs.Add(new PreviewWorkerJob(job.DocumentId, workspaceSourcePath, job.OutputPath, job.Kind.ToString()));
        }
        var manifestPath = Path.Combine(workRoot, "manifest.json");
        var resultPath = Path.Combine(workRoot, "result.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new PreviewWorkerManifest(workerJobs), JsonOptions), cancellationToken);
        await RunWorkerAsync(workerPath, conversion.TimeoutMinutes, manifestPath, resultPath, cancellationToken);
        var result = JsonSerializer.Deserialize<PreviewWorkerResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), JsonOptions)
            ?? throw new PdmRuleException("服务器转换程序没有返回有效结果。");
        if (!result.Success)
            throw new PdmRuleException(string.IsNullOrWhiteSpace(result.Error) ? "服务器生成STEP/PDF失败。" : result.Error);
    }

    /// <summary>远程转换：把源文件打包发给转图电脑上的转图代理，取回转换结果。</summary>
    private async Task RunRemoteAgentAsync(
        PreviewConversionSettings conversion,
        IReadOnlyList<PreviewJob> jobs,
        string workRoot,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(workRoot, "request.zip");
        var manifest = new PreviewAgentManifest(jobs
            .Select(job => new PreviewAgentJob(job.DocumentId, job.Kind.ToString(), job.FileName, Path.GetFileName(job.OutputPath)))
            .ToArray());
        using (var archive = ZipFile.Open(requestPath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var stream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
            foreach (var job in jobs)
            {
                var fileEntry = archive.CreateEntry($"sources/{job.FileName}", CompressionLevel.Fastest);
                await using var target = fileEntry.Open();
                await using var source = File.OpenRead(job.SourcePath);
                await source.CopyToAsync(target, cancellationToken);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(conversion.TimeoutMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{conversion.NormalizedAgentUrl}/convert")
        {
            Content = new StreamContent(File.OpenRead(requestPath))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        if (!string.IsNullOrWhiteSpace(conversion.AgentToken)) request.Headers.Add(PreviewAgentProtocol.TokenHeader, conversion.AgentToken);

        // 只有本方法自己的计时器到点才算“超过N分钟”；HttpClient 自身超时、连接中断等取消按真实耗时和原因反馈，
        // 否则 15 秒的连接超时或 HttpClient 默认 100 秒超时都会被误报成“超过60分钟”。
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw RemoteTimeout(conversion);
        }
        catch (OperationCanceledException exception)
        {
            throw new PdmRuleException($"调用转图服务器在{DescribeElapsed(stopwatch.Elapsed)}后被中断（等不到转图电脑响应）：{DescribeReason(exception)}");
        }
        catch (HttpRequestException exception)
        {
            throw new PdmRuleException($"无法连接转图服务器{conversion.NormalizedAgentUrl}（等待{DescribeElapsed(stopwatch.Elapsed)}）：{DescribeReason(exception)}");
        }

        using (response)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(timeout.Token);
                    throw new PdmRuleException($"转图服务器返回错误({(int)response.StatusCode})：{PreviewAgentProtocol.DescribeError(detail)}");
                }
                var responsePath = Path.Combine(workRoot, "response.zip");
                await using (var content = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var file = File.Create(responsePath))
                    await content.CopyToAsync(file, timeout.Token);
                await MaterializeAgentResponseAsync(responsePath, jobs);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw RemoteTimeout(conversion);
            }
            catch (OperationCanceledException exception)
            {
                throw new PdmRuleException($"读取转图服务器结果在{DescribeElapsed(stopwatch.Elapsed)}后被中断：{DescribeReason(exception)}");
            }
        }
        _ = outputRoot;
    }

    /// <summary>解析转图代理返回的压缩包：校验 result.json 并把每个图档的 STEP/PDF 落到输出路径。</summary>
    private static async Task MaterializeAgentResponseAsync(string responsePath, IReadOnlyList<PreviewJob> jobs)
    {
        using (var archive = ZipFile.OpenRead(responsePath))
        {
            var resultEntry = archive.GetEntry("result.json") ?? throw new PdmRuleException("转图服务器没有返回结果文件。");
            PreviewWorkerResult result;
            await using (var stream = resultEntry.Open())
            {
                result = await JsonSerializer.DeserializeAsync<PreviewWorkerResult>(stream, JsonOptions)
                    ?? throw new PdmRuleException("转图服务器结果文件无效。");
            }
            if (!result.Success)
                throw new PdmRuleException(string.IsNullOrWhiteSpace(result.Error) ? "转图服务器生成STEP/PDF失败。" : result.Error);
            foreach (var job in jobs)
            {
                var outputName = Path.GetFileName(job.OutputPath);
                var outputEntry = archive.GetEntry($"outputs/{outputName}")
                    ?? throw new PdmRuleException($"转图服务器未返回预览文件{outputName}。");
                outputEntry.ExtractToFile(job.OutputPath, true);
            }
        }
    }

    private static PdmRuleException RemoteTimeout(PreviewConversionSettings conversion) =>
        new($"调用转图服务器超过{conversion.TimeoutMinutes}分钟，已终止本次发布转换。");

    private static string DescribeElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{elapsed.TotalMinutes:0.#}分钟" : $"{elapsed.TotalSeconds:0.#}秒";

    private static string DescribeReason(Exception exception) =>
        exception.InnerException is null || exception.InnerException.Message == exception.Message
            ? exception.Message
            : $"{exception.Message}（{exception.InnerException.Message}）";

    private async Task RunWorkerAsync(string workerPath, int timeoutMinutes, string manifestPath, string resultPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(workerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.ArgumentList.Add(resultPath);
        using var process = Process.Start(startInfo) ?? throw new PdmRuleException("服务器SolidWorks转换进程无法启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new PdmRuleException($"服务器生成STEP/PDF超过{timeoutMinutes}分钟，已终止本次发布转换。");
        }
        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(errorOutput) ? standardOutput : errorOutput;
            throw new PdmRuleException($"服务器SolidWorks转换失败：{detail.Trim()}");
        }
        if (!File.Exists(resultPath)) throw new PdmRuleException("服务器SolidWorks转换未生成结果文件。");
    }

    private static async Task VerifySourceAsync(ReleasePreviewSource source, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new PdmRuleException($"发布源文件不存在：{source.FileName}");
        var info = new FileInfo(path);
        if (info.Length != source.FileLength) throw new PdmConflictException($"发布源文件{source.FileName}大小已变化，请重新创建发布包。");
        var sha256 = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException($"发布源文件{source.FileName}指纹已变化，请重新创建发布包。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }

    private static void ResetDirectory(string path)
    {
        DeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed record PreviewWorkerManifest(IReadOnlyList<PreviewWorkerJob> Jobs);
    private sealed record PreviewWorkerJob(Guid DocumentId, string SourcePath, string OutputPath, string Kind);
    private sealed record PreviewWorkerResult(bool Success, string? Error);

    private sealed record PreviewJob(Guid DocumentId, DocumentKind Kind, string FileName, string SourcePath, string OutputPath);
    private sealed record PreviewAgentManifest(IReadOnlyList<PreviewAgentJob> Jobs);
    private sealed record PreviewAgentJob(Guid DocumentId, string Kind, string FileName, string OutputName);
}

/// <summary>API服务器与转图电脑上“转图代理”之间的约定（zip 包结构：manifest.json + sources/**；返回 result.json + outputs/**）。</summary>
public static class PreviewAgentProtocol
{
    public const string TokenHeader = "X-Pdm-Agent-Token";
    public const string HealthPath = "/health";
    public const string ConvertPath = "/convert";

    public static string DescribeError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "无错误详情";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return Truncate(error.GetString());
        }
        catch (JsonException)
        {
        }
        return Truncate(body);
    }

    private static string Truncate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= 500 ? text : text[..500];
    }
}
