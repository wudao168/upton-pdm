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

public interface IProjectFileRepository
{
    Task<IReadOnlyList<ProjectFile>> ListAsync(Guid rootProjectId, Guid? folderId, bool includeDeleted, CancellationToken cancellationToken);
    Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFileVersion>> ListVersionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<ProjectFileVersion?> FindVersionAsync(Guid fileId, Guid versionId, CancellationToken cancellationToken);
    Task<ProjectFile> AddVersionAsync(StoredProjectFileUpload upload, string actor, string? comment, CancellationToken cancellationToken);
    Task<ProjectFile> RenameAsync(Guid fileId, string fileName, string actor, CancellationToken cancellationToken);
    Task<ProjectFile> MoveAsync(Guid fileId, Guid folderId, string actor, CancellationToken cancellationToken);
    Task<ProjectFile> SetDeletedAsync(Guid fileId, bool deleted, string actor, CancellationToken cancellationToken);
    Task<bool> FolderHasFilesAsync(Guid folderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFileVersion>> PurgeDeletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
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
}
