using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectFileService(
    IPdmRepository repository,
    IProjectFileRepository files,
    IProjectFileStorage storage,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ProjectFile>> ListAsync(Guid projectId, Guid? folderId, bool includeDeleted, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        if (folderId is Guid id) RequireFolder(folders, id, FolderAccess.View);
        var rootId = folders.First().RootProjectId;
        var result = await files.ListAsync(rootId, folderId, includeDeleted, cancellationToken);
        var visible = folders.Where(item => Has(item, FolderAccess.View)).Select(item => item.Id).ToHashSet();
        return result.Where(item => visible.Contains(item.FolderId)).ToArray();
    }

    public async Task<ProjectFolder> CreateFolderAsync(Guid projectId, Guid parentFolderId, string name, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        var parent = RequireBusinessFolder(folders, parentFolderId, FolderAccess.Edit);
        var folder = await repository.CreateProjectFolderAsync(projectId, parent.Id, NormalizeName(name, "文件夹名称"), cancellationToken);
        await AuditAsync(actor, "project.folder.create", nameof(ProjectFolder), folder.Id, $"新建文件夹：{folder.Name}", cancellationToken);
        return folder;
    }

    public async Task<ProjectFolder> RenameFolderAsync(Guid projectId, Guid folderId, string name, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        var folder = RequireBusinessFolder(folders, folderId, FolderAccess.Edit);
        if (folder.IsSystem) throw new PdmRuleException("系统预置目录不能重命名。");
        var saved = await repository.RenameProjectFolderAsync(projectId, folderId, NormalizeName(name, "文件夹名称"), cancellationToken);
        await AuditAsync(actor, "project.folder.rename", nameof(ProjectFolder), folderId, $"重命名文件夹：{folder.Name} -> {saved.Name}", cancellationToken);
        return saved;
    }

    public async Task<ProjectFolder> MoveFolderAsync(Guid projectId, Guid folderId, Guid parentFolderId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        var folder = RequireBusinessFolder(folders, folderId, FolderAccess.Edit);
        RequireBusinessFolder(folders, parentFolderId, FolderAccess.Edit);
        if (folder.IsSystem) throw new PdmRuleException("系统预置目录不能移动。");
        var saved = await repository.MoveProjectFolderAsync(projectId, folderId, parentFolderId, cancellationToken);
        await AuditAsync(actor, "project.folder.move", nameof(ProjectFolder), folderId, $"移动文件夹：{folder.Name}", cancellationToken);
        return saved;
    }

    public async Task DeleteFolderAsync(Guid projectId, Guid folderId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        var folder = RequireBusinessFolder(folders, folderId, FolderAccess.Delete);
        if (folder.IsSystem) throw new PdmRuleException("系统预置目录不能删除。");
        if (folders.Any(item => item.ParentFolderId == folderId) || await files.FolderHasFilesAsync(folderId, cancellationToken))
            throw new PdmConflictException("只能删除没有子目录和文件的空文件夹。");
        await repository.DeleteProjectFolderAsync(projectId, folderId, cancellationToken);
        await AuditAsync(actor, "project.folder.delete", nameof(ProjectFolder), folderId, $"删除空文件夹：{folder.Name}", cancellationToken);
    }

    public async Task<ProjectFileUploadSession> StartUploadAsync(Guid projectId, Guid folderId, string fileName, long totalLength, string sha256, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        RequireBusinessFolder(folders, folderId, FolderAccess.Upload);
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        return await storage.StartAsync(projectId, folderId, NormalizeName(fileName, "文件名"), totalLength, sha256, settings.VaultRoot, actor, cancellationToken);
    }

    public Task<ProjectFileUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken) =>
        storage.WriteChunkAsync(sessionId, chunkIndex, content, actor, cancellationToken);

    public async Task<ProjectFile> CompleteUploadAsync(Guid sessionId, string? comment, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var upload = await storage.CompleteAsync(sessionId, actor, cancellationToken);
        try
        {
            var folders = await VisibleFoldersAsync(upload.ProjectId, actor, role, cancellationToken);
            RequireBusinessFolder(folders, upload.FolderId, FolderAccess.Upload);
            var file = await files.AddVersionAsync(upload, actor, comment?.Trim(), cancellationToken);
            await AuditAsync(actor, "project.file.upload", nameof(ProjectFile), file.Id, $"上传项目文件：{file.FileName} · {upload.Sha256}", cancellationToken);
            return file;
        }
        catch
        {
            await storage.DiscardAsync(upload, cancellationToken);
            throw;
        }
    }

    public Task CancelUploadAsync(Guid sessionId, string actor, CancellationToken cancellationToken) => storage.CancelAsync(sessionId, actor, cancellationToken);

    public async Task<IReadOnlyList<ProjectFileVersion>> ListVersionsAsync(Guid projectId, Guid fileId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var file = await RequireFileAsync(projectId, fileId, actor, role, FolderAccess.View, cancellationToken);
        return await files.ListVersionsAsync(file.Id, cancellationToken);
    }

    public async Task<ProjectFileDownload> OpenDownloadAsync(Guid projectId, Guid fileId, Guid? versionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var file = await RequireFileAsync(projectId, fileId, actor, role, FolderAccess.Download, cancellationToken);
        if (file.DeletedAt is not null) throw new PdmConflictException("回收站文件需恢复后才能下载。");
        var version = versionId is Guid id ? await files.FindVersionAsync(fileId, id, cancellationToken) : file.CurrentVersion;
        if (version is null) throw new PdmNotFoundException("文件版本不存在。");
        await storage.VerifyAsync(version, cancellationToken);
        var content = await storage.OpenReadAsync(version, cancellationToken);
        await AuditAsync(actor, "project.file.download", nameof(ProjectFile), file.Id, $"下载项目文件：{version.FileName} · V{version.VersionNumber}", cancellationToken);
        return new(file, version, content);
    }

    public async Task<ProjectFile> RenameAsync(Guid projectId, Guid fileId, string fileName, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var file = await RequireFileAsync(projectId, fileId, actor, role, FolderAccess.Edit, cancellationToken);
        var normalized = NormalizeName(fileName, "文件名");
        if (!string.Equals(Path.GetExtension(file.FileName), Path.GetExtension(normalized), StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("重命名不能改变文件扩展名。");
        var saved = await files.RenameAsync(fileId, normalized, actor, cancellationToken);
        await AuditAsync(actor, "project.file.rename", nameof(ProjectFile), fileId, $"重命名文件：{file.FileName} -> {saved.FileName}", cancellationToken);
        return saved;
    }

    public async Task<ProjectFile> MoveAsync(Guid projectId, Guid fileId, Guid folderId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var file = await RequireFileAsync(projectId, fileId, actor, role, FolderAccess.Edit, cancellationToken);
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        RequireBusinessFolder(folders, folderId, FolderAccess.Edit);
        var saved = await files.MoveAsync(fileId, folderId, actor, cancellationToken);
        await AuditAsync(actor, "project.file.move", nameof(ProjectFile), fileId, $"移动文件：{file.FileName}", cancellationToken);
        return saved;
    }

    public async Task<ProjectFile> SetDeletedAsync(Guid projectId, Guid fileId, bool deleted, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var file = await RequireFileAsync(projectId, fileId, actor, role, FolderAccess.Delete, cancellationToken);
        var saved = await files.SetDeletedAsync(fileId, deleted, actor, cancellationToken);
        await AuditAsync(actor, deleted ? "project.file.delete" : "project.file.restore", nameof(ProjectFile), fileId, $"{(deleted ? "删除" : "恢复")}项目文件：{file.FileName}", cancellationToken);
        return saved;
    }

    private async Task<ProjectFile> RequireFileAsync(Guid projectId, Guid fileId, string actor, UserRole role, FolderAccess access, CancellationToken cancellationToken)
    {
        var file = await files.FindAsync(fileId, cancellationToken) ?? throw new PdmNotFoundException("项目文件不存在。");
        var folders = await VisibleFoldersAsync(projectId, actor, role, cancellationToken);
        var folder = RequireFolder(folders, file.FolderId, access);
        if (file.RootProjectId != folder.RootProjectId) throw new PdmNotFoundException("项目文件不存在。");
        return file;
    }

    private async Task<IReadOnlyList<ProjectFolder>> VisibleFoldersAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) throw new UnauthorizedAccessException("无权访问该项目内容。");
        var folders = await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
        if (folders.Count == 0) throw new PdmNotFoundException("项目文件夹不存在。");
        return folders;
    }

    private static ProjectFolder RequireFolder(IReadOnlyList<ProjectFolder> folders, Guid folderId, FolderAccess access)
    {
        var folder = folders.FirstOrDefault(item => item.Id == folderId) ?? throw new PdmNotFoundException("项目文件夹不存在。");
        if (!Has(folder, access)) throw new UnauthorizedAccessException("当前目录权限不足。");
        return folder;
    }

    private static ProjectFolder RequireBusinessFolder(IReadOnlyList<ProjectFolder> folders, Guid folderId, FolderAccess access)
    {
        var folder = RequireFolder(folders, folderId, access);
        if (folder.Purpose != ProjectFolderPurpose.Standard) throw new PdmRuleException("受控图档和发布目录不能通过网页维护普通文件。");
        return folder;
    }

    private static bool Has(ProjectFolder folder, FolderAccess access) => (folder.EffectiveAccess & access) == access;

    private static string NormalizeName(string value, string field)
    {
        var name = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new PdmRuleException($"{field}无效或包含非法字符。");
        return name;
    }

    private Task AuditAsync(string actor, string action, string entityType, Guid id, string details, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, id.ToString(), details), cancellationToken);
}
