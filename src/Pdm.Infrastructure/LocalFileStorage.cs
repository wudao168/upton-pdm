using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalFileStorage(IOptions<PdmStorageOptions> options, IPdmRepository repository, TimeProvider timeProvider) : IFileStorage
{
    private readonly PdmStorageOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken, string? owner = null)
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
        if (string.Equals(Path.GetExtension(safeName), ".dwg", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("系统不再接收DWG文件；三维预览请使用STP/STEP，二维工程图请使用PDF。");
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
            timeProvider.GetUtcNow().AddHours(settings.UploadLifetimeHours), owner);
        var sessionDirectory = GetSessionDirectory(session.Id);
        Directory.CreateDirectory(sessionDirectory);
        await WriteMetadataAsync(session, cancellationToken);
        return session;
    }

    public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ReadMetadataAsync(sessionId, cancellationToken);

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
        var receipt = await ReadCompletionAsync(sessionId, cancellationToken);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.File.RelativePath.Replace('\\', '/'), relativeTargetPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException("该上传会话已有固定目标，不能改写到其他图档。");
            var receiptProject = await repository.FindProjectAsync(receipt.Session.ProjectId, cancellationToken)
                ?? throw new PdmNotFoundException("项目不存在。");
            var receiptPath = StorageLocationPolicy.ResolveUnder(receiptProject.VaultLocation, receipt.File.RelativePath);
            if (File.Exists(receiptPath))
            {
                await VerifyFileHashAsync(receiptPath, receipt.File, cancellationToken);
                if (receipt.File.RelativePath.Split('\\', '/').Any(segment => segment.Equals(".versions", StringComparison.OrdinalIgnoreCase)))
                    File.SetAttributes(receiptPath, File.GetAttributes(receiptPath) | FileAttributes.ReadOnly);
                var completedDirectory = GetSessionDirectory(sessionId);
                if (Directory.Exists(completedDirectory)) Directory.Delete(completedDirectory, true);
                return receipt.File;
            }
        }
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

        var stored = new StoredFile(Path.GetRelativePath(project.VaultLocation, targetPath), session.TotalLength, actualSha256, timeProvider.GetUtcNow());
        await WriteCompletionAsync(new UploadCompletion(session, stored), cancellationToken);
        File.Move(assembledPath, targetPath);
        if (relativeTargetPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => string.Equals(segment, ".versions", StringComparison.OrdinalIgnoreCase)))
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
        Directory.Delete(GetSessionDirectory(sessionId), true);
        return stored;
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

    public async Task ValidateStoredFileMetadataAsync(Project project, StoredFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await ValidateVersionReadMetadataAsync(project, file, cancellationToken);
    }

    public async Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidateStoredFileMetadata(project, file);

        await VerifyFileHashAsync(path, file, cancellationToken);
    }

    public async Task VerifyVersionReadAsync(Project project, StoredFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await ValidateVersionReadMetadataAsync(project, file, cancellationToken);
        await VerifyFileHashAsync(path, file, cancellationToken);
    }

    private static async Task<string> ValidateVersionReadMetadataAsync(Project project, StoredFile file, CancellationToken cancellationToken)
    {
        var path = ValidateStoredFileMetadata(project, file, allowFormalDrawing: true, allowLegacyWritableFormal: true);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return path;

        // Early formal drawing publications did not set ReadOnly. Repair only a validated
        // formal source, holding a read lock through hash verification and attribute repair.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (input.Length != file.Length || !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("正式图纸内容与版本记录不一致，已停止打开。请联系管理员核查文件完整性。");
        cancellationToken.ThrowIfCancellationRequested();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return path;
    }

    private static async Task VerifyFileHashAsync(string path, StoredFile file, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException("待存档文件SHA-256校验失败。");
        }
    }

    /// <summary>
    /// 校验转图生成的发布预览文件：预览存放在 .release-previews 只读目录（不是 .versions），
    /// 这里同样按长度和 SHA-256 校验，避免把发布预览当成历史版本文件拒绝掉。
    /// </summary>
    public async Task VerifyPreviewFileAsync(Project project, DocumentPreviewArtifact preview, CancellationToken cancellationToken)
    {
        if (preview.Format is not (DocumentPreviewFormat.Step or DocumentPreviewFormat.Pdf))
            throw new PdmRuleException("发布预览文件格式无效。");
        var relativeSegments = preview.StorageRelativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (relativeSegments.Length == 0
            || (!string.Equals(relativeSegments[0], ".release-previews", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(relativeSegments[0], ".versions", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PdmRuleException("发布预览文件必须存放在独立的.release-previews只读目录中。");
        }
        var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, preview.StorageRelativePath);
        if (!File.Exists(path)) throw new PdmNotFoundException("发布预览文件不存在。");
        var info = new FileInfo(path);
        if (info.Length != preview.FileLength) throw new PdmConflictException("发布预览文件大小与转换记录不一致。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actual, preview.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("发布预览文件SHA-256校验失败。");
    }

    private static string ValidateStoredFileMetadata(Project project, StoredFile file, bool allowFormalDrawing = false, bool allowLegacyWritableFormal = false)
    {
        var relativeSegments = file.RelativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        // Released drawings are generated under a separate immutable source directory.
        // Accept that layout for controlled reads only; new check-ins still require .versions.
        var formalDrawing = allowFormalDrawing && relativeSegments.Length == 3
            && string.Equals(relativeSegments[0], ".release-formal", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(relativeSegments[1], "N", out _)
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(relativeSegments[2]), "N", out _)
            && string.Equals(Path.GetExtension(relativeSegments[2]), ".slddrw", StringComparison.OrdinalIgnoreCase);
        if (!formalDrawing && (relativeSegments.Length == 0 || !string.Equals(relativeSegments[0], ".versions", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PdmRuleException("历史版本文件必须存放在独立的.versions只读目录中。");
        }

        var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, file.RelativePath);
        if (!File.Exists(path))
        {
            throw new PdmNotFoundException("待存档的版本文件不存在。");
        }

        var info = new FileInfo(path);
        if (info.Length != file.Length)
        {
            throw new PdmConflictException("待存档文件大小与上传记录不一致。");
        }

        if ((info.Attributes & FileAttributes.ReadOnly) == 0 && !(formalDrawing && allowLegacyWritableFormal))
        {
            throw new PdmConflictException("历史版本文件不是只读文件，不能写入版本记录。");
        }
        return path;
    }

    public async Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken)
    {
        await VerifyStoredFileAsync(project, source, cancellationToken);
        var sourcePath = StorageLocationPolicy.ResolveUnder(project.VaultLocation, source.RelativePath);
        var targetPath = StorageLocationPolicy.ResolveUnder(project.VaultLocation, relativeTargetPath);
        if (File.Exists(targetPath))
        {
            throw new PdmConflictException("恢复版本目标文件已存在，不能覆盖。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);

        return new StoredFile(Path.GetRelativePath(project.VaultLocation, targetPath), source.Length, source.Sha256, timeProvider.GetUtcNow());
    }

    public async Task<StoredFile> CopyVersionAsync(Project sourceProject, Project targetProject, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken)
    {
        await VerifyStoredFileAsync(sourceProject, source, cancellationToken);
        var sourcePath = StorageLocationPolicy.ResolveUnder(sourceProject.VaultLocation, source.RelativePath);
        var targetPath = StorageLocationPolicy.ResolveUnder(targetProject.VaultLocation, relativeTargetPath);
        if (File.Exists(targetPath))
        {
            throw new PdmConflictException("目标项目版本文件已存在，不能覆盖。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = targetPath + ".tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            var info = new FileInfo(temporaryPath);
            if (info.Length != source.Length || !actual.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmConflictException("跨项目复制后的版本文件校验失败。");
            }

            File.Move(temporaryPath, targetPath);
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
            return new StoredFile(Path.GetRelativePath(targetProject.VaultLocation, targetPath), source.Length, actual, timeProvider.GetUtcNow());
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string GetSessionDirectory(Guid sessionId) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot), sessionId.ToString("N"));

    private async Task<UploadSession> ReadMetadataAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetSessionDirectory(sessionId), "session.json");
        if (!File.Exists(path))
        {
            var receipt = await ReadCompletionAsync(sessionId, cancellationToken);
            if (receipt is not null) return receipt.Session;
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

    private sealed record UploadCompletion(UploadSession Session, StoredFile File);

    private string CompletionPath(Guid sessionId) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot),
        "completed", sessionId.ToString("N") + ".json");

    private async Task<UploadCompletion?> ReadCompletionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = CompletionPath(sessionId);
        if (!File.Exists(path)) return null;
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UploadCompletion>(input, jsonOptions, cancellationToken);
    }

    private async Task WriteCompletionAsync(UploadCompletion receipt, CancellationToken cancellationToken)
    {
        var path = CompletionPath(receipt.Session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var output = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(output, receipt, jsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, true);
    }
}
