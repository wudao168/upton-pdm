using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalProgramTemplateStorage(IOptions<PdmStorageOptions> options, TimeProvider timeProvider) : IProgramTemplateStorage
{
    private static readonly IReadOnlySet<string> EvidenceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".xlsx", ".png", ".jpg", ".jpeg"
    };

    private readonly PdmStorageOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProgramTemplateUploadSession> StartUploadAsync(
        Guid revisionId,
        string templateCode,
        ProgramTemplateAttachmentKind kind,
        string fileName,
        long totalLength,
        string expectedSha256,
        string owner,
        CancellationToken cancellationToken)
    {
        if (totalLength <= 0) throw new PdmRuleException("上传文件不能为空。");
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new PdmRuleException("必须提供64位SHA-256。");
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            throw new PdmRuleException("上传文件名不能包含路径。");
        ValidateExtension(kind, safeName);
        templateCode = RequiredPathSegment(templateCode);

        var session = new ProgramTemplateUploadSession(
            Guid.NewGuid(), revisionId, templateCode, kind, safeName, totalLength, settings.ChunkSizeBytes,
            expectedSha256.ToUpperInvariant(), 0, owner, timeProvider.GetUtcNow().AddHours(settings.UploadLifetimeHours));
        Directory.CreateDirectory(GetSessionDirectory(session.Id));
        await WriteMetadataAsync(session, cancellationToken);
        return session;
    }

    public async Task<ProgramTemplateUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken)
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

    public async Task<StoredProgramTemplateFile> CompleteUploadAsync(Guid sessionId, string actor, CancellationToken cancellationToken)
    {
        var session = await ReadMetadataAsync(sessionId, cancellationToken);
        RequireOwner(session, actor);
        if (session.ExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("上传会话已过期。");
        if (session.ReceivedLength != session.TotalLength)
            throw new PdmConflictException($"上传尚未完成：{session.ReceivedLength}/{session.TotalLength}字节。");

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
            throw new PdmConflictException("上传文件SHA-256校验失败。");
        }
        if (session.Kind == ProgramTemplateAttachmentKind.Package) await ValidateZipAsync(assembledPath, cancellationToken);

        var root = StorageLocationPolicy.Normalize(settings.ProgramTemplateRoot);
        var storedAt = timeProvider.GetUtcNow();
        var relativePath = Path.Combine(session.TemplateCode, session.RevisionId.ToString("N"), session.Kind.ToString(), $"{session.Id:N}_{session.FileName}");
        var targetPath = StorageLocationPolicy.ResolveUnder(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new PdmRuleException("程序模板目标路径无效。"));
        if (File.Exists(targetPath)) throw new PdmConflictException("程序模板目标文件已经存在，不能覆盖受控文件。");
        File.Move(assembledPath, targetPath);
        File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
        Directory.Delete(GetSessionDirectory(sessionId), true);
        return new(session.RevisionId, session.Kind, session.FileName, relativePath, session.TotalLength, actualSha256, storedAt);
    }

    public async Task VerifyAsync(string relativePath, long length, string sha256, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(StorageLocationPolicy.Normalize(settings.ProgramTemplateRoot), relativePath);
        if (!File.Exists(path)) throw new PdmNotFoundException("程序模板文件不存在。");
        if (new FileInfo(path).Length != length) throw new PdmConflictException("程序模板文件长度校验失败。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actualSha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("程序模板文件SHA-256校验失败。");
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(StorageLocationPolicy.Normalize(settings.ProgramTemplateRoot), relativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DiscardAsync(StoredProgramTemplateFile file, CancellationToken cancellationToken)
    {
        var path = StorageLocationPolicy.ResolveUnder(StorageLocationPolicy.Normalize(settings.ProgramTemplateRoot), file.RelativePath);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string GetSessionDirectory(Guid sessionId) => Path.Combine(StorageLocationPolicy.Normalize(settings.UploadTempRoot), $"program-template-{sessionId:N}");

    private async Task<ProgramTemplateUploadSession> ReadMetadataAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetSessionDirectory(sessionId), "program-template-session.json");
        if (!File.Exists(path)) throw new PdmNotFoundException("程序模板上传会话不存在。");
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProgramTemplateUploadSession>(input, jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("程序模板上传会话元数据损坏。");
    }

    private async Task WriteMetadataAsync(ProgramTemplateUploadSession session, CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(session.Id);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "program-template-session.json");
        var temporaryPath = finalPath + ".tmp";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(output, session, jsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, finalPath, true);
    }

    private static async Task ValidateZipAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0) throw new PdmRuleException("ZIP程序包不能为空。");
            foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name)))
            {
                await using var stream = entry.Open();
                var buffer = new byte[1];
                await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, cancellationToken);
            }
        }
        catch (PdmRuleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new PdmRuleException("ZIP程序包损坏、格式不受支持或已加密。");
        }
    }

    private static void RequireOwner(ProgramTemplateUploadSession session, string actor)
    {
        if (!string.Equals(session.Owner, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只能继续本人发起的程序模板上传会话。");
    }

    private static void ValidateExtension(ProgramTemplateAttachmentKind kind, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (kind == ProgramTemplateAttachmentKind.Package && !string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("主程序包仅支持未加密ZIP格式。");
        if (kind == ProgramTemplateAttachmentKind.TestEvidence && !EvidenceExtensions.Contains(extension))
            throw new PdmRuleException("离线测试证据仅支持PDF、XLSX、PNG和JPG格式。");
    }

    private static string RequiredPathSegment(string value)
    {
        var segment = value.Trim();
        if (string.IsNullOrWhiteSpace(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(Path.DirectorySeparatorChar) || segment.Contains(Path.AltDirectorySeparatorChar))
            throw new PdmRuleException("程序模板编码不能用于存档目录。");
        return segment;
    }
}
