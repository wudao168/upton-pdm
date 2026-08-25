using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalMaterialAttachmentStorage(IOptions<PdmStorageOptions> options, TimeProvider timeProvider) : IMaterialAttachmentStorage
{
    private static readonly IReadOnlySet<string> Model3DExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".sldprt", ".sldasm", ".step", ".stp", ".igs", ".iges", ".x_t", ".x_b", ".sat"
    };

    private static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv",
        ".jpg", ".jpeg", ".png", ".zip", ".rar", ".7z"
    };

    private readonly PdmStorageOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MaterialAttachmentUploadSession> StartUploadAsync(
        Guid materialId,
        string materialCode,
        MaterialAttachmentKind kind,
        string fileName,
        long totalLength,
        string expectedSha256,
        string storageRoot,
        string owner,
        CancellationToken cancellationToken)
    {
        if (totalLength <= 0) throw new PdmRuleException("上传附件不能为空。");
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new PdmRuleException("必须提供64位SHA-256。");

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            throw new PdmRuleException("附件文件名不能包含路径。");
        ValidateExtension(kind, safeName);

        var normalizedRoot = StorageLocationPolicy.Normalize(storageRoot);
        var session = new MaterialAttachmentUploadSession(
            Guid.NewGuid(), materialId, RequiredPathSegment(materialCode), kind, safeName, totalLength,
            settings.ChunkSizeBytes, expectedSha256.ToUpperInvariant(), 0, normalizedRoot, owner,
            timeProvider.GetUtcNow().AddHours(settings.UploadLifetimeHours));
        Directory.CreateDirectory(GetSessionDirectory(session.Id));
        await WriteMetadataAsync(session, cancellationToken);
        return session;
    }

    public async Task<MaterialAttachmentUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken)
    {
        if (chunkIndex < 0) throw new PdmRuleException("分块序号不能小于0。");
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        if (session.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("上传会话已过期。");

        var expectedChunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
        if (chunkIndex >= expectedChunks) throw new PdmRuleException("分块序号超出文件范围。");

        var chunkPath = Path.Combine(GetSessionDirectory(sessionId), $"{chunkIndex:D8}.part");
        var temporaryPath = chunkPath + ".tmp";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await content.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (output.Length > session.ChunkSize || (chunkIndex < expectedChunks - 1 && output.Length != session.ChunkSize))
                throw new PdmRuleException("分块大小不符合会话约定。");
        }

        File.Move(temporaryPath, chunkPath, true);
        var received = Directory.EnumerateFiles(GetSessionDirectory(sessionId), "*.part").Sum(path => new FileInfo(path).Length);
        var updated = session with { ReceivedLength = received };
        await WriteMetadataAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<StoredMaterialAttachment> CompleteUploadAsync(Guid sessionId, string actor, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        if (session.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("上传会话已过期。");
        if (session.ReceivedLength != session.TotalLength)
            throw new PdmConflictException($"上传尚未完成：{session.ReceivedLength}/{session.TotalLength}字节。");

        var folder = session.Kind == MaterialAttachmentKind.Model3D ? "3D" : "Documents";
        var storedAt = timeProvider.GetUtcNow();
        var relativePath = Path.Combine(session.MaterialCode, folder, storedAt.ToString("yyyyMM"), $"{session.Id:N}_{session.FileName}");
        var targetPath = StorageLocationPolicy.ResolveUnder(session.StorageRoot, relativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new PdmRuleException("附件目标路径无效。");
        Directory.CreateDirectory(targetDirectory);

        var assembledPath = Path.Combine(GetSessionDirectory(sessionId), "assembled.tmp");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(assembledPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var expectedChunks = (int)Math.Ceiling((double)session.TotalLength / session.ChunkSize);
            for (var index = 0; index < expectedChunks; index++)
            {
                var chunkPath = Path.Combine(GetSessionDirectory(sessionId), $"{index:D8}.part");
                if (!File.Exists(chunkPath)) throw new PdmConflictException($"缺少分块{index}。");
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
            throw new PdmConflictException("附件SHA-256校验失败。");
        }
        if (File.Exists(targetPath)) throw new PdmConflictException("附件目标文件已经存在，不能覆盖受控文件。");

        File.Move(assembledPath, targetPath);
        File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
        Directory.Delete(GetSessionDirectory(sessionId), true);
        return new(session.MaterialId, session.Kind, session.FileName, session.StorageRoot, relativePath, session.TotalLength, actualSha256, storedAt);
    }

    public async Task VerifyAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(attachment.StorageRoot, attachment.StorageRelativePath);
        if (!File.Exists(path)) throw new PdmNotFoundException("料品附件文件不存在。");
        var info = new FileInfo(path);
        if (info.Length != attachment.FileLength) throw new PdmConflictException("料品附件长度校验失败。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actualSha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("料品附件SHA-256校验失败。");
    }

    public Task<Stream> OpenReadAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(attachment.StorageRoot, attachment.StorageRelativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DiscardAsync(StoredMaterialAttachment file, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(file.StorageRoot, file.RelativePath);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string GetSessionDirectory(Guid sessionId) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot), sessionId.ToString("N"));

    private async Task<MaterialAttachmentUploadSession> ReadMetadataAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetSessionDirectory(sessionId), "material-attachment-session.json");
        if (!File.Exists(path)) throw new PdmNotFoundException("料品附件上传会话不存在。");
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MaterialAttachmentUploadSession>(input, jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("料品附件上传会话元数据损坏。");
    }

    private async Task WriteMetadataAsync(MaterialAttachmentUploadSession session, CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(session.Id);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "material-attachment-session.json");
        var temporaryPath = finalPath + ".tmp";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(output, session, jsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, finalPath, true);
    }

    private static void RequireOwner(MaterialAttachmentUploadSession session, string actor)
    {
        if (!string.Equals(session.Owner, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只能继续本人发起的附件上传会话。");
    }

    private static void ValidateExtension(MaterialAttachmentKind kind, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var allowed = kind == MaterialAttachmentKind.Model3D ? Model3DExtensions : DocumentExtensions;
        if (!allowed.Contains(extension))
            throw new PdmRuleException(kind == MaterialAttachmentKind.Model3D
                ? "3D附件仅支持SLDPRT、SLDASM、STEP、STP、IGS、IGES、X_T、X_B和SAT格式。"
                : "资料附件仅支持常用文档、图片和ZIP/RAR/7Z压缩包格式。");
    }

    private static string RequiredPathSegment(string value)
    {
        var segment = value.Trim();
        if (string.IsNullOrWhiteSpace(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(Path.DirectorySeparatorChar) || segment.Contains(Path.AltDirectorySeparatorChar))
            throw new PdmRuleException("物料编码不能用于附件存档目录。");
        return segment;
    }
}
