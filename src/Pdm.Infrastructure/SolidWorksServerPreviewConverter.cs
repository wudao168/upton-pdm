using System.Diagnostics;
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

public sealed class SolidWorksServerPreviewConverter(IOptions<PdmPreviewWorkerOptions> options) : IServerPreviewConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PdmPreviewWorkerOptions settings = options.Value;

    public async Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(
        ReleasePackage package,
        Project project,
        IReadOnlyList<ReleasePreviewSource> sources,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PdmRuleException("SolidWorks服务器转换只能在Windows服务器运行。");
        if (settings.TimeoutMinutes is < 1 or > 120) throw new PdmRuleException("服务器预览转换超时必须设置为1到120分钟。");
        var workerPath = Path.GetFullPath(settings.WorkerPath);
        if (!File.Exists(workerPath)) throw new PdmRuleException($"服务器SolidWorks转换程序不存在：{workerPath}");

        var duplicateFileName = sources
            .GroupBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFileName is not null)
            throw new PdmRuleException($"发布引用树包含重名源文件{duplicateFileName.Key}，服务器无法安全重建SolidWorks引用关系。");

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
            var jobs = new List<PreviewWorkerJob>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = StorageLocationPolicy.ResolveUnder(vaultRoot, source.StorageRelativePath);
                await VerifySourceAsync(source, sourcePath, cancellationToken);
                var workspaceSourcePath = Path.Combine(sourceRoot, source.FileName);
                File.Copy(sourcePath, workspaceSourcePath, false);
                var extension = source.Kind == DocumentKind.Drawing ? ".pdf" : ".step";
                var outputFileName = $"{source.DocumentId:N}_{Path.GetFileNameWithoutExtension(source.FileName)}{extension}";
                jobs.Add(new PreviewWorkerJob(
                    source.DocumentId,
                    workspaceSourcePath,
                    Path.Combine(outputRoot, outputFileName),
                    source.Kind.ToString()));
            }

            var manifestPath = Path.Combine(workRoot, "manifest.json");
            var resultPath = Path.Combine(workRoot, "result.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new PreviewWorkerManifest(jobs), JsonOptions), cancellationToken);
            await RunWorkerAsync(workerPath, manifestPath, resultPath, cancellationToken);

            var result = JsonSerializer.Deserialize<PreviewWorkerResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), JsonOptions)
                ?? throw new PdmRuleException("服务器转换程序没有返回有效结果。");
            if (!result.Success)
                throw new PdmRuleException(string.IsNullOrWhiteSpace(result.Error) ? "服务器生成STEP/PDF失败。" : result.Error);

            var artifacts = new Dictionary<Guid, DocumentPreviewArtifact>();
            foreach (var source in sources)
            {
                var job = jobs.Single(item => item.DocumentId == source.DocumentId);
                if (!File.Exists(job.OutputPath) || new FileInfo(job.OutputPath).Length == 0)
                    throw new PdmRuleException($"服务器未生成图档{source.DrawingNumber}的预览文件。");
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

    private async Task RunWorkerAsync(string workerPath, string manifestPath, string resultPath, CancellationToken cancellationToken)
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
        timeout.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new PdmRuleException($"服务器生成STEP/PDF超过{settings.TimeoutMinutes}分钟，已终止本次发布转换。");
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
}
