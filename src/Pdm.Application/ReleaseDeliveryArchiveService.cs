using System.Security.Cryptography;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>
/// 发布成品归档：发布（或转图补齐）后把发布目录里的成品汇总登记到项目的"机械发布/电气发布"目录，
/// 供网页端直接浏览下载；文件不复制，登记的是发布目录里已有的文件，重复执行不会生成重复版本。
/// </summary>
public sealed class ReleaseDeliveryArchiveService(
    IPdmRepository repository,
    IProjectFileRepository files,
    TimeProvider timeProvider)
{
    /// <summary>发布目录里的过程文件不归档，只汇总成品（受控BOM、STEP/PDF、图纸压缩包等）。</summary>
    private static readonly string[] MetadataFileNames = ["manifest.json", "approval.json", "checksums.sha256"];

    public async Task<int> ArchiveAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken);
        if (package is null || package.State != ReleasePackageState.Published) return 0;
        if (string.IsNullOrWhiteSpace(package.PublishedPath) || !Directory.Exists(package.PublishedPath))
            throw new PdmRuleException("生产发布目录不可访问，请检查共享目录连接和权限后重试归档。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken);
        if (project is null) return 0;
        var folder = await repository.FindProjectFolderByKeyAsync(project.Id, ReleaseFolderKey(package.Scope), cancellationToken);
        if (folder is null) return 0;
        if (File.Exists(Path.Combine(package.PublishedPath, "checksums.sha256")))
            await ReleaseDeliveryFiles.VerifyChecksumsAsync(package.PublishedPath, cancellationToken);

        // 存储根取发布包上一级目录、相对路径带上发布包号：同一图档在后续增补/变更发布里重名也不会撞唯一索引。
        var packageDirectory = Path.GetDirectoryName(package.PublishedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? package.PublishedPath;
        var relativePrefix = Path.GetFileName(package.PublishedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var missing = new List<ReleaseArchiveFile>();
        foreach (var path in await ReleaseDeliveryFiles.ManifestFilesAsync(package.PublishedPath, cancellationToken))
        {
            var fileName = Path.GetFileName(path);
            if (MetadataFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;
            if (!new[] { ".xlsx", ".pdf", ".step", ".stp", ".zip" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            if (info.Length == 0) throw new PdmRuleException($"发布成品{fileName}为空，请重新生成后重试归档。");
            await using var input = File.OpenRead(path);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            missing.Add(new ReleaseArchiveFile(fileName, Path.Combine(relativePrefix, Path.GetRelativePath(package.PublishedPath, path)), info.Length, sha256));
        }
        if (missing.Count == 0) return 0;

        return await files.ArchiveReleaseAsync(
            folder.RootProjectId,
            folder.Id,
            packageDirectory,
            package.Number,
            missing,
            actor,
            package.PublishedAt ?? timeProvider.GetUtcNow(),
            cancellationToken);
    }

    /// <summary>机械发布（标准件/非标件/历史组合）汇总到"机械发布"，电气发布汇总到"电气发布"。</summary>
    private static string ReleaseFolderKey(ReleaseScope scope) =>
        scope is ReleaseScope.ElectricalLongLead or ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement
            ? "electrical.release"
            : "mechanical.release";
}
