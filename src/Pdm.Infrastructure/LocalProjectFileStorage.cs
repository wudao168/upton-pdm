using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalProjectFileStorage(IOptions<PdmStorageOptions> options, TimeProvider timeProvider) : IProjectFileStorage
{
    private static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".md",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".mp4", ".mov", ".avi", ".mkv",
        ".zip", ".rar", ".7z"
    };
    private const long MaxFileLength = 2L * 1024 * 1024 * 1024;
    private readonly PdmStorageOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProjectFileUploadSession> StartAsync(Guid projectId, Guid folderId, string fileName, long totalLength, string sha256, string storageRoot, string actor, CancellationToken cancellationToken)
    {
        if (totalLength <= 0 || totalLength > MaxFileLength) throw new PdmRuleException("项目文件必须大于0字节且不能超过2GB。");
        if (!AllowedExtensions.Contains(Path.GetExtension(fileName))) throw new PdmRuleException("不支持该文件类型；可上传常用Office、PDF、图片、文本、视频和压缩包，禁止程序及脚本文件。");
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character))) throw new PdmRuleException("必须提供64位SHA-256。");
        var session = new ProjectFileUploadSession(Guid.NewGuid(), projectId, folderId, fileName, totalLength, settings.ChunkSizeBytes, sha256.ToUpperInvariant(), 0, StorageLocationPolicy.Normalize(storageRoot), actor, timeProvider.GetUtcNow().AddHours(settings.UploadLifetimeHours));
        Directory.CreateDirectory(SessionDirectory(session.Id));
        await WriteMetadataAsync(session, cancellationToken);
        return session;
    }

    public async Task<ProjectFileUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        if (session.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("上传会话已过期。");
        var chunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
        if (chunkIndex < 0 || chunkIndex >= chunks) throw new PdmRuleException("分块序号超出范围。");
        var path = Path.Combine(SessionDirectory(sessionId), $"{chunkIndex:D8}.part");
        var temp = path + ".tmp";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await content.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (output.Length > session.ChunkSize || (chunkIndex < chunks - 1 && output.Length != session.ChunkSize)) throw new PdmRuleException("分块大小不符合会话约定。");
        }
        File.Move(temp, path, true);
        var updated = session with { ReceivedLength = Directory.EnumerateFiles(SessionDirectory(sessionId), "*.part").Sum(item => new FileInfo(item).Length) };
        await WriteMetadataAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<StoredProjectFileUpload> CompleteAsync(Guid sessionId, string actor, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        if (session.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("上传会话已过期。");
        if (session.ReceivedLength != session.TotalLength) throw new PdmConflictException($"上传尚未完成：{session.ReceivedLength}/{session.TotalLength}字节。");
        var versionId = Guid.NewGuid();
        var relative = Path.Combine(".project-files", session.ProjectId.ToString("N"), "versions", versionId.ToString("N"), session.FileName);
        var target = StorageLocationPolicy.ResolveUnder(session.StorageRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var assembled = Path.Combine(SessionDirectory(sessionId), "assembled.tmp");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var chunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
            for (var index = 0; index < chunks; index++)
            {
                var chunk = Path.Combine(SessionDirectory(sessionId), $"{index:D8}.part");
                if (!File.Exists(chunk)) throw new PdmConflictException($"缺少分块{index}。");
                await using var input = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
            }
            await output.FlushAsync(cancellationToken);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(session.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(assembled); throw new PdmConflictException("文件SHA-256校验失败。"); }
        if (File.Exists(target)) throw new PdmConflictException("目标受控文件已存在。");
        File.Move(assembled, target);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        Directory.Delete(SessionDirectory(sessionId), true);
        return new(versionId, session.ProjectId, session.FolderId, session.FileName, session.StorageRoot, relative, session.TotalLength, actual, timeProvider.GetUtcNow());
    }

    public async Task CancelAsync(Guid sessionId, string actor, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        Directory.Delete(SessionDirectory(sessionId), true);
    }
    public async Task VerifyAsync(ProjectFileVersion version, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(version.StorageRoot, version.StorageRelativePath);
        if (!File.Exists(path)) throw new PdmNotFoundException("项目文件内容不存在。");
        var info = new FileInfo(path);
        if (info.Length != version.FileLength) throw new PdmConflictException("项目文件长度校验失败。");
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!actual.Equals(version.Sha256, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("项目文件SHA-256校验失败。");
    }
    public Task<Stream> OpenReadAsync(ProjectFileVersion version, CancellationToken cancellationToken) => Task.FromResult<Stream>(new FileStream(StorageLocationPolicy.ResolveUnder(version.StorageRoot, version.StorageRelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    public Task DiscardAsync(StoredProjectFileUpload upload, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(upload.StorageRoot, upload.RelativePath);
        if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        var directory = Path.GetDirectoryName(path); if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        return Task.CompletedTask;
    }
    public Task DeleteVersionsAsync(IReadOnlyList<ProjectFileVersion> versions, CancellationToken cancellationToken)
    {
        foreach (var version in versions)
        {
            var path = StorageLocationPolicy.ResolveUnder(version.StorageRoot, version.StorageRelativePath);
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        return Task.CompletedTask;
    }
    private string SessionDirectory(Guid id) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot), "project-files", id.ToString("N"));
    private async Task<ProjectFileUploadSession> ReadMetadataAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(SessionDirectory(id), "session.json");
        if (!File.Exists(path)) throw new PdmNotFoundException("项目文件上传会话不存在。");
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectFileUploadSession>(input, jsonOptions, cancellationToken) ?? throw new InvalidDataException("上传会话元数据损坏。");
    }
    private async Task WriteMetadataAsync(ProjectFileUploadSession session, CancellationToken cancellationToken)
    {
        var path = Path.Combine(SessionDirectory(session.Id), "session.json"); var temp = path + ".tmp";
        Directory.CreateDirectory(SessionDirectory(session.Id));
        await using (var output = File.Create(temp)) { await JsonSerializer.SerializeAsync(output, session, jsonOptions, cancellationToken); await output.FlushAsync(cancellationToken); }
        File.Move(temp, path, true);
    }
    private static void RequireOwner(ProjectFileUploadSession session, string actor) { if (!session.Owner.Equals(actor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("只能继续本人发起的上传会话。"); }
}
