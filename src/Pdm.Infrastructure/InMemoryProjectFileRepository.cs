using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectFileRepository(TimeProvider timeProvider) : IProjectFileRepository
{
    private readonly ConcurrentDictionary<Guid, ProjectFile> files = new();
    private readonly ConcurrentDictionary<Guid, ProjectFileVersion> versions = new();

    public Task<IReadOnlyList<ProjectFile>> ListAsync(Guid rootProjectId, Guid? folderId, bool includeDeleted, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectFile>>(files.Values.Where(item => item.RootProjectId == rootProjectId && (folderId is null || item.FolderId == folderId) && (includeDeleted || item.DeletedAt is null)).OrderBy(item => item.FileName).ToArray());
    public Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken cancellationToken) => Task.FromResult(files.GetValueOrDefault(fileId));
    public Task<IReadOnlyList<ProjectFileVersion>> ListVersionsAsync(Guid fileId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectFileVersion>>(versions.Values.Where(item => item.ProjectFileId == fileId).OrderByDescending(item => item.VersionNumber).ToArray());
    public Task<ProjectFileVersion?> FindVersionAsync(Guid fileId, Guid versionId, CancellationToken cancellationToken) => Task.FromResult(versions.TryGetValue(versionId, out var version) && version.ProjectFileId == fileId ? version : null);
    public Task<ProjectFile> AddVersionAsync(StoredProjectFileUpload upload, string actor, string? comment, CancellationToken cancellationToken)
    {
        lock (files)
        {
            var file = files.Values.FirstOrDefault(item => item.FolderId == upload.FolderId && item.DeletedAt is null && item.FileName.Equals(upload.FileName, StringComparison.OrdinalIgnoreCase));
            var now = timeProvider.GetUtcNow();
            file ??= new ProjectFile(Guid.NewGuid(), upload.ProjectId, upload.FolderId, upload.FileName, actor, now, actor, now, null, null);
            var number = versions.Values.Where(item => item.ProjectFileId == file.Id).Select(item => item.VersionNumber).DefaultIfEmpty().Max() + 1;
            var version = new ProjectFileVersion(upload.VersionId, file.Id, number, upload.FileName, upload.StorageRoot, upload.RelativePath, upload.Length, upload.Sha256, actor, upload.StoredAt, comment);
            versions[version.Id] = version;
            file = file with { UpdatedBy = actor, UpdatedAt = now, CurrentVersion = version };
            files[file.Id] = file;
            return Task.FromResult(file);
        }
    }
    public Task<ProjectFile> RenameAsync(Guid fileId, string fileName, string actor, CancellationToken cancellationToken) => Update(fileId, item => item with { FileName = fileName, UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() });
    public Task<ProjectFile> MoveAsync(Guid fileId, Guid folderId, string actor, CancellationToken cancellationToken) => Update(fileId, item => item with { FolderId = folderId, UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() });
    public Task<ProjectFile> SetDeletedAsync(Guid fileId, bool deleted, string actor, CancellationToken cancellationToken) => Update(fileId, item => item with { DeletedAt = deleted ? timeProvider.GetUtcNow() : null, DeletedBy = deleted ? actor : null, UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() });
    public Task<bool> FolderHasFilesAsync(Guid folderId, CancellationToken cancellationToken) => Task.FromResult(files.Values.Any(item => item.FolderId == folderId));
    public Task<IReadOnlyList<ProjectFileVersion>> PurgeDeletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        lock (files)
        {
            var fileIds = files.Values.Where(item => item.DeletedAt < cutoff).Select(item => item.Id).ToHashSet();
            var removed = versions.Values.Where(item => fileIds.Contains(item.ProjectFileId)).ToArray();
            foreach (var version in removed) versions.TryRemove(version.Id, out _);
            foreach (var id in fileIds) files.TryRemove(id, out _);
            return Task.FromResult<IReadOnlyList<ProjectFileVersion>>(removed);
        }
    }
    private Task<ProjectFile> Update(Guid id, Func<ProjectFile, ProjectFile> update)
    {
        if (!files.TryGetValue(id, out var file)) throw new PdmNotFoundException("项目文件不存在。");
        var saved = update(file);
        if (files.Values.Any(item => item.Id != id && item.FolderId == saved.FolderId && item.DeletedAt is null && item.FileName.Equals(saved.FileName, StringComparison.OrdinalIgnoreCase))) throw new PdmConflictException("目标目录已存在同名文件。");
        files[id] = saved;
        return Task.FromResult(saved);
    }
}
