using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class SolidWorksServerPreviewConverter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AssemblyPreviewGates = new();

    public async Task<AssemblyPreviewFile> GenerateAssemblyAsync(ControlledOpenManifest manifest, Project project, CancellationToken cancellationToken, string? initiator = null)
    {
        if (repository is null || manifest.ForEdit || manifest.ProjectId != project.Id)
            throw new PdmRuleException("无法生成受控装配预览，请重新选择图档后重试。");
        var root = manifest.Files.Single(file => file.IsRoot && file.DocumentId == manifest.RootDocumentId);
        if (!string.Equals(Path.GetExtension(root.FileName), ".sldasm", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("当前图档不是SolidWorks装配体。");
        var files = manifest.Files.Where(file => !string.Equals(Path.GetExtension(file.FileName), ".slddrw", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new PdmRuleException("装配预览包含同名引用文件，请先核对文件身份后重试。");
        // Root bytes can remain unchanged while any descendant advances. Freeze and key the entire combination.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("assembly-preview-v1|"
            + manifest.RootDocumentId + "|" + manifest.RootVersionId + "|" + JsonSerializer.Serialize(files.OrderBy(file => file.DocumentId)))));
        var directory = StorageLocationPolicy.ResolveUnder(project.VaultLocation, Path.Combine(".assembly-previews", fingerprint));
        var output = Path.Combine(directory, "preview.easm");
        var metadata = Path.Combine(directory, "preview.json");
        var gate = AssemblyPreviewGates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(metadata))
            {
                var cached = JsonSerializer.Deserialize<AssemblyPreviewFile>(await File.ReadAllTextAsync(metadata, cancellationToken));
                if (cached is not null && cached.VersionId == manifest.RootVersionId
                    && await ReleaseDeliveryFiles.MatchesAsync(output, cached.FileLength, cached.Sha256, cancellationToken))
                    return cached with { Path = output };
            }
            var conversion = (await repository.GetSystemSettingsAsync(cancellationToken)).PreviewConversion ?? PreviewConversionSettings.Default;
            if (conversion.TimeoutMinutes is < 1 or > 120) throw new PdmRuleException("转图超时必须设置为1到120分钟。");
            var work = Path.Combine(Path.GetTempPath(), "UPTON-PLM", "assembly-preview", Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(work, "sources");
            var outputRoot = Path.Combine(work, "outputs");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            try
            {
                var jobs = new List<PreviewJob>();
                foreach (var file in files)
                {
                    var document = await repository.FindDocumentAsync(file.DocumentId, cancellationToken)
                        ?? throw new PdmNotFoundException("装配引用图档不存在，请刷新后重试。");
                    var version = await repository.FindDocumentVersionAsync(file.DocumentId, file.VersionId, cancellationToken)
                        ?? throw new PdmNotFoundException("装配引用版本不存在，请刷新后重试。");
                    var owner = document.ProjectId == project.Id ? project
                        : await repository.FindProjectAsync(document.ProjectId, cancellationToken)
                            ?? throw new PdmNotFoundException("装配引用项目不存在。");
                    var source = StorageLocationPolicy.ResolveUnder(owner.VaultLocation, version.StorageRelativePath);
                    if (version.Sha256 != file.Sha256 || version.FileLength != file.FileLength
                        || !await ReleaseDeliveryFiles.MatchesAsync(source, file.FileLength, file.Sha256, cancellationToken))
                        throw new PdmConflictException("装配预览源文件校验失败，请检查存储目录后重试。");
                    jobs.Add(new PreviewJob(file.DocumentId, file.IsRoot ? DocumentKind.Assembly : document.Kind, file.FileName,
                        source, Path.Combine(outputRoot, file.IsRoot ? "preview.easm" : file.DocumentId + ".easm"), file.Sha256,
                        Convert: file.IsRoot, AssemblyPreview: file.IsRoot, Configuration: file.Configuration));
                }
                var errors = conversion.Mode == PreviewConversionMode.Remote
                    ? await RunRemoteAgentAsync(conversion, jobs, work, outputRoot, cancellationToken,
                        context: new PreviewAgentContext(initiator, project.Code, "桌面客户端", root.FileName, manifest.Id.ToString("D")))
                    : await RunLocalWorkerAsync(conversion, jobs, sourceRoot, work, outputRoot, cancellationToken);
                if (errors.Count > 0)
                    throw new PdmRuleException("最新装配预览生成失败，请检查引用文件和转图日志后重试。");
                var generated = Path.Combine(outputRoot, "preview.easm");
                if (!File.Exists(generated) || new FileInfo(generated).Length == 0)
                    throw new PdmRuleException("未生成有效的最新装配预览，请检查转图日志后重试。");
                var result = new AssemblyPreviewFile(output, manifest.RootVersionId, new FileInfo(generated).Length,
                    await ComputeSha256Async(generated, cancellationToken));
                Directory.CreateDirectory(directory);
                File.Move(generated, output, true);
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(result), cancellationToken);
                return result;
            }
            finally { DeleteDirectory(work); }
        }
        finally { gate.Release(); }
    }
}
