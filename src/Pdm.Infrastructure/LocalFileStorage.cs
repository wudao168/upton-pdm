using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalFileStorage(IOptions<PdmStorageOptions> options, IPdmRepository repository, TimeProvider timeProvider) : IFileStorage
{
    private readonly PdmStorageOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken)
    {
        if (totalLength <= 0)
        {
            throw new PdmRuleException("上传文件不能为空。 ");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new PdmRuleException("必须提供64位SHA-256。 ");
        }

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
        {
            throw new PdmRuleException("文件名不能包含路径。 ");
        }

        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");

        var session = new UploadSession(
            Guid.NewGuid(),
            projectId,
            safeName,
            totalLength,
            settings.ChunkSizeBytes,
            expectedSha256.ToUpperInvariant(),
            0,
            timeProvider.GetUtcNow().AddHours(settings.UploadLifetimeHours));
        var sessionDirectory = GetSessionDirectory(session.Id);
        Directory.CreateDirectory(sessionDirectory);
        await WriteMetadataAsync(session, cancellationToken);
        return session;
    }

    public async Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken)
    {
        if (chunkIndex < 0)
        {
            throw new PdmRuleException("分块序号不能小于0。 ");
        }

        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        if (session.ExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new PdmConflictException("上传会话已过期。 ");
        }

        var expectedChunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
        if (chunkIndex >= expectedChunks)
        {
            throw new PdmRuleException("分块序号超出文件范围。 ");
        }

        var chunkPath = Path.Combine(GetSessionDirectory(sessionId), $"{chunkIndex:D8}.part");
        var temporaryPath = chunkPath + ".tmp";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await content.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (output.Length > session.ChunkSize || (chunkIndex < expectedChunks - 1 && output.Length != session.ChunkSize))
            {
                throw new PdmRuleException("分块大小不符合会话约定。 ");
            }
        }

        File.Move(temporaryPath, chunkPath, true);
        var received = Directory.EnumerateFiles(GetSessionDirectory(sessionId), "*.part").Sum(path => new FileInfo(path).Length);
        var updated = session with { ReceivedLength = received };
        await WriteMetadataAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        var project = await repository.FindProjectAsync(session.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");
        if (session.ReceivedLength != session.TotalLength)
        {
            throw new PdmConflictException($"上传尚未完成：{session.ReceivedLength}/{session.TotalLength}字节。 ");
        }

        var targetPath = StorageLocationPolicy.ResolveUnder(project.VaultLocation, relativeTargetPath);
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new PdmRuleException("目标路径无效。 ");
        Directory.CreateDirectory(targetDirectory);
        var assembledPath = Path.Combine(GetSessionDirectory(sessionId), "assembled.tmp");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(assembledPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var expectedChunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
            for (var index = 0; index < expectedChunks; index++)
            {
                var chunkPath = Path.Combine(GetSessionDirectory(sessionId), $"{index:D8}.part");
                if (!File.Exists(chunkPath))
                {
                    throw new PdmConflictException($"缺少分块{index}。 ");
                }

                await using var input = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            await output.FlushAsync(cancellationToken);
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualSha256, session.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(assembledPath);
            throw new PdmConflictException("文件SHA-256校验失败。 ");
        }

        if (File.Exists(targetPath))
        {
            throw new PdmConflictException("目标文件已经存在，不能覆盖受控文件。 ");
        }

        File.Move(assembledPath, targetPath);
        Directory.Delete(GetSessionDirectory(sessionId), true);
        return new StoredFile(Path.GetRelativePath(project.VaultLocation, targetPath), session.TotalLength, actualSha256, timeProvider.GetUtcNow());
    }

    public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(absolutePath))
        {
            throw new PdmNotFoundException("文件不存在。 ");
        }

        Stream stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken)
    {
        try
        {
            var normalized = StorageLocationPolicy.Normalize(location);
            return Task.FromResult(Directory.Exists(normalized));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PdmRuleException)
        {
            return Task.FromResult(false);
        }
    }

    private string GetSessionDirectory(Guid sessionId) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot), sessionId.ToString("N"));

    private async Task<UploadSession> ReadMetadataAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetSessionDirectory(sessionId), "session.json");
        if (!File.Exists(path))
        {
            throw new PdmNotFoundException("上传会话不存在。 ");
        }

        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UploadSession>(input, jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("上传会话元数据损坏。 ");
    }

    private async Task WriteMetadataAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(session.Id);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "session.json");
        var temporaryPath = finalPath + ".tmp";
        await using (var output = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(output, session, jsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, finalPath, true);
    }
}
