using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectFileUploadSession(
    Guid Id,
    Guid ProjectId,
    Guid FolderId,
    string FileName,
    long TotalLength,
    int ChunkSize,
    string ExpectedSha256,
    long ReceivedLength,
    string StorageRoot,
    string Owner,
    DateTimeOffset ExpiresAt);

public sealed record StoredProjectFileUpload(
    Guid VersionId,
    Guid ProjectId,
    Guid FolderId,
    string FileName,
    string StorageRoot,
    string RelativePath,
    long Length,
    string Sha256,
    DateTimeOffset StoredAt);

public sealed record ProjectFileDownload(ProjectFile File, ProjectFileVersion Version, Stream Content);

/// <summary>发布成品归档项：文件已经存在于发布目录，这里只登记它的名称、位置、长度和指纹。</summary>
public sealed record ReleaseArchiveFile(string FileName, string StorageRelativePath, long FileLength, string Sha256);

public interface IProjectFileRepository
{
    Task<IReadOnlyList<ProjectFile>> ListAsync(Guid rootProjectId, Guid? folderId, bool includeDeleted, CancellationToken cancellationToken);
    Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFileVersion>> ListVersionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<ProjectFileVersion?> FindVersionAsync(Guid fileId, Guid versionId, CancellationToken cancellationToken);
    Task<ProjectFile> AddVersionAsync(StoredProjectFileUpload upload, string actor, string? comment, CancellationToken cancellationToken);
    Task<ProjectFile> RenameAsync(Guid fileId, string fileName, string actor, CancellationToken cancellationToken);
    Task<ProjectFile> UpdateDescriptionAsync(Guid fileId, string? description, string actor, CancellationToken cancellationToken);
    Task<ProjectFile> MoveAsync(Guid fileId, Guid folderId, string actor, CancellationToken cancellationToken);
    Task<ProjectFile> SetDeletedAsync(Guid fileId, bool deleted, string actor, CancellationToken cancellationToken);
    Task<bool> FolderHasFilesAsync(Guid folderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFileVersion>> PurgeDeletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
    /// <summary>
    /// 发布成品归档：把发布目录里的成品登记成发布目录下的只读项目文件（同名文件按指纹去重，重复执行不会生成重复版本）。
    /// </summary>
    Task<int> ArchiveReleaseAsync(
        Guid rootProjectId,
        Guid folderId,
        string storageRoot,
        string releaseNumber,
        IReadOnlyList<ReleaseArchiveFile> files,
        string actor,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}

public interface IProjectFileStorage
{
    Task<ProjectFileUploadSession> StartAsync(Guid projectId, Guid folderId, string fileName, long totalLength, string sha256, string storageRoot, string actor, CancellationToken cancellationToken);
    Task<ProjectFileUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken);
    Task<StoredProjectFileUpload> CompleteAsync(Guid sessionId, string actor, CancellationToken cancellationToken);
    Task CancelAsync(Guid sessionId, string actor, CancellationToken cancellationToken);
    Task VerifyAsync(ProjectFileVersion version, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(ProjectFileVersion version, CancellationToken cancellationToken);
    Task DiscardAsync(StoredProjectFileUpload upload, CancellationToken cancellationToken);
    Task DeleteVersionsAsync(IReadOnlyList<ProjectFileVersion> versions, CancellationToken cancellationToken);
    Task<StoredProjectFileUpload> CopyVersionAsync(
        ProjectFileVersion source,
        Guid targetProjectId,
        Guid targetFolderId,
        string targetStorageRoot,
        string actor,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("当前项目文件存储不支持跨项目复制版本。");
}
