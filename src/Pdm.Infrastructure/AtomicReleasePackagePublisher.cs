using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class AtomicReleasePackagePublisher(TimeProvider timeProvider) : IReleasePackagePublisher
{
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".SLDPRT", ".SLDASM", ".SLDDRW" };

    public async Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        Directory.CreateDirectory(stagingDirectory);
        await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "mechanical-bom.xlsx"), BomWorkbook.Write(package.MechanicalBomSnapshot), cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.Write(package.ElectricalBomSnapshot), cancellationToken);
    }

    public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(StorageLocationPolicy.Normalize(project.VaultLocation), Path.Combine(".release-staging", package.Number));
        if (!Directory.Exists(stagingDirectory)) throw new PdmRuleException("发布包暂存目录不存在，请重新准备发布包。");
        ValidateFiles(Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories));
        return Task.CompletedTask;
    }

    public async Task<string> PublishAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var releaseRoot = StorageLocationPolicy.Normalize(project.ReleaseLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        if (!Directory.Exists(stagingDirectory))
        {
            throw new PdmRuleException($"发布暂存目录不存在：{stagingDirectory}");
        }

        var sourceFiles = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories);
        ValidateFiles(sourceFiles);

        Directory.CreateDirectory(releaseRoot);
        var finalDirectory = StorageLocationPolicy.ResolveUnder(releaseRoot, package.Number);
        if (Directory.Exists(finalDirectory))
        {
            var existingManifest = Path.Combine(finalDirectory, "manifest.json");
            if (File.Exists(existingManifest) && (await File.ReadAllTextAsync(existingManifest, cancellationToken)).Contains(package.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return finalDirectory;
            }

            throw new PdmConflictException("生产目录已存在同名但内容不同的发布包。 ");
        }

        var temporaryDirectory = StorageLocationPolicy.ResolveUnder(releaseRoot, $".{package.Number}.publishing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var sourceFile in sourceFiles)
            {
                var relative = Path.GetRelativePath(stagingDirectory, sourceFile);
                var target = StorageLocationPolicy.ResolveUnder(temporaryDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target, false);
            }

            var manifest = new
            {
                package.Id,
                package.Number,
                package.ProjectId,
                package.ReferenceSnapshotId,
                package.MechanicalBomRevision,
                package.ElectricalBomRevision,
                PublishedAt = timeProvider.GetUtcNow(),
                Files = sourceFiles.Select(path => Path.GetRelativePath(stagingDirectory, path).Replace('\\', '/')).OrderBy(path => path).ToArray()
            };
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "approval.json"), JsonSerializer.Serialize(package.ApprovalTasks, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);

            var checksums = new List<string>();
            foreach (var file in Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                await using var input = File.OpenRead(file);
                var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                checksums.Add($"{sha256}  {Path.GetRelativePath(temporaryDirectory, file).Replace('\\', '/')}");
            }

            await File.WriteAllLinesAsync(Path.Combine(temporaryDirectory, "checksums.sha256"), checksums, cancellationToken);
            Directory.Move(temporaryDirectory, finalDirectory);
            return finalDirectory;
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            throw;
        }
    }

    private static void RequireFile(IEnumerable<string> files, Func<string, bool> predicate, string description)
    {
        if (!files.Any(predicate))
        {
            throw new PdmRuleException($"发布暂存目录缺少{description}。 ");
        }
    }

    private static void ValidateFiles(IReadOnlyList<string> sourceFiles)
    {
        if (sourceFiles.Any(path => NativeExtensions.Contains(Path.GetExtension(path))))
            throw new PdmRuleException("生产发布包不能包含SolidWorks源文件。 ");
        RequireFile(sourceFiles, path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase), "PDF图纸");
        RequireFile(sourceFiles, path => string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase), "DWG图纸");
        RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "mechanical-bom.xlsx", StringComparison.OrdinalIgnoreCase), "机械BOM XLSX");
        RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "electrical-bom.xlsx", StringComparison.OrdinalIgnoreCase), "电气BOM XLSX");
    }
}
