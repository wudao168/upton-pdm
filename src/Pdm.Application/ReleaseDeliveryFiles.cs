using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>发布目录与补齐预览共用的文件校验、清单更新规则。</summary>
public static class ReleaseDeliveryFiles
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<bool> MatchesAsync(string path, long length, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || length <= 0 || new FileInfo(path).Length != length) return false;
            await using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string PreviewPath(string publishedPath, DocumentPreviewArtifact preview) =>
        StorageLocationPolicy.ResolveUnder(publishedPath, Path.Combine("previews", preview.Sha256, Path.GetFileName(preview.StorageRelativePath)));

    public static async Task<bool> IsDeliveredAsync(string publishedPath, DocumentPreviewArtifact preview, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(preview.StorageRelativePath);
        foreach (var path in new[] { PreviewPath(publishedPath, preview), Path.Combine(publishedPath, "previews", name), Path.Combine(publishedPath, name) })
            if (await MatchesAsync(path, preview.FileLength, preview.Sha256, cancellationToken)) return true;
        return false;
    }

    public static async Task RefreshManifestAsync(ReleasePackage package, IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews, CancellationToken cancellationToken)
    {
        var root = package.PublishedPath!;
        if (File.Exists(Path.Combine(root, "checksums.sha256")))
            await VerifyChecksumsAsync(root, cancellationToken, updatingPreviews: true);
        var path = Path.Combine(root, "manifest.json");
        var manifest = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject()
            : new JsonObject { ["id"] = package.Id.ToString(), ["number"] = package.Number };
        if (manifest["id"] is JsonNode id && !string.Equals(id.GetValue<string>(), package.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("生产目录的发布包清单身份不一致，已停止补图。");
        manifest["id"] = package.Id.ToString();
        manifest["projectId"] = package.ProjectId.ToString();
        var previousFiles = manifest["files"] is JsonArray frozenFiles
            ? frozenFiles.Select(file => file!.GetValue<string>())
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));
        // 旧预览保留在磁盘供历史追溯，但当前交付清单只允许正式版本当前绑定的预览。
        manifest["files"] = JsonSerializer.SerializeToNode(previousFiles
            .Where(file => file is not ("manifest.json" or "approval.json" or "checksums.sha256")
                && !IsPreview(file) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Concat(previews.Values.Select(preview => Path.GetRelativePath(root, PreviewPath(root, preview)).Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray());
        manifest["previews"] = JsonSerializer.SerializeToNode(previews, JsonOptions);
        await WriteAtomicAsync(path, manifest.ToJsonString(JsonOptions), cancellationToken);
        await WriteChecksumsAsync(root, cancellationToken);
    }

    public static async Task WriteChecksumsAsync(string root, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var path in (await ManifestFilesAsync(root, cancellationToken)).Concat(new[] { Path.Combine(root, "manifest.json"), Path.Combine(root, "approval.json") }.Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == "checksums.sha256" || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            lines.Add($"{hash}  {relative}");
        }
        await WriteAtomicAsync(Path.Combine(root, "checksums.sha256"), string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> ManifestFilesAsync(string root, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        if (File.Exists(manifestPath))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            if (document.RootElement.TryGetProperty("files", out var files))
                return files.EnumerateArray().Select(file => StorageLocationPolicy.ResolveUnder(root, file.GetString()!)).ToArray();
        }
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories);
    }

    public static bool IsPreview(string path) => !path.Replace('\\', '/').Split('/').Contains("standard-files", StringComparer.OrdinalIgnoreCase)
        && new[] { ".pdf", ".step", ".stp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task VerifyChecksumsAsync(string root, CancellationToken cancellationToken, bool updatingPreviews = false)
    {
        var checksums = Path.Combine(root, "checksums.sha256");
        if (!File.Exists(checksums)) throw new PdmConflictException("发布包缺少文件校验清单，不能安全恢复发布。");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(checksums, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new PdmConflictException("发布包文件校验清单格式不正确。");
            listed.Add(line[66..].Replace('\\', '/'));
            if (updatingPreviews && (line[66..] == "manifest.json" || IsPreview(line[66..]))) continue;
            var path = StorageLocationPolicy.ResolveUnder(root, line[66..]);
            if (!File.Exists(path) || !await MatchesAsync(path, new FileInfo(path).Length, line[..64], cancellationToken))
                throw new PdmConflictException($"发布成品{Path.GetFileName(path)}缺失或内容发生变化，已停止恢复发布。");
        }
        if (!updatingPreviews)
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), cancellationToken));
            if (!listed.Contains("manifest.json") || (manifest.RootElement.TryGetProperty("files", out var files)
                && files.EnumerateArray().Any(file => !listed.Contains(file.GetString()!.Replace('\\', '/')))))
                throw new PdmConflictException("发布包文件校验清单不完整，已停止恢复发布。");
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
