using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record QualityInspectionRecord(Guid Id, Guid ProjectId, string Kind, string Station, string Title,
    string? Remark, string FileName, string StorageRelativePath, long FileLength, string Sha256, string UploadedBy, DateTimeOffset UploadedAt);

public interface IQualityInspectionRepository
{
    Task<IReadOnlyList<QualityInspectionRecord>> ListAsync(Guid projectId, CancellationToken cancellationToken);
    Task<QualityInspectionRecord?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> TryAddAsync(QualityInspectionRecord record, StoredProjectFileUpload? archive, CancellationToken cancellationToken);
}

public sealed class QualityInspectionService(IQualityInspectionRepository records, IPdmRepository repository, IFileStorage storage,
    IProjectFileStorage projectFileStorage)
{
    private async Task RequireAccessAsync(Guid projectId, string actor, UserRole role, bool write, CancellationToken cancellationToken, string kind = "incoming")
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("无权访问该项目内容。");
        if (write) await QualityUploadPolicy.RequireAsync(repository, projectId, kind, actor, role, cancellationToken);
    }

    public async Task<IReadOnlyList<QualityInspectionRecord>> ListAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, false, cancellationToken);
        return await records.ListAsync(projectId, cancellationToken);
    }

    public async Task<UploadSession> StartAsync(Guid projectId, string fileName, long length, string sha256, string actor, UserRole role, CancellationToken cancellationToken, string kind = "incoming")
    {
        await RequireAccessAsync(projectId, actor, role, true, cancellationToken, kind);
        ValidateFileName(fileName);
        return await storage.StartUploadAsync(projectId, fileName, length, sha256, cancellationToken, actor);
    }

    public async Task<QualityInspectionRecord> CompleteAsync(Guid projectId, Guid sessionId, string kind, string? station, string title, string? remark, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAccessAsync(projectId, actor, role, true, cancellationToken, kind);
        if (kind is not ("incoming" or "assembly" or "preAcceptance" or "finalAcceptance")) throw new PdmRuleException("检验类型无效。");
        station = station?.Trim() ?? "";
        title = (title ?? "").Trim();
        remark = remark?.Trim();
        if (title.Length is < 1 or > 200 || station.Length > 100 || remark?.Length > 2000)
            throw new PdmRuleException("请填写记录名称（最多200字）、工站（最多100字）和备注（最多2000字）。");
        var session = await storage.GetUploadSessionAsync(sessionId, cancellationToken);
        session.RequireOwner(actor);
        if (session.ProjectId != projectId) throw new PdmConflictException("上传会话与检验项目不匹配。");
        ValidateFileName(session.FileName);
        var acceptance = kind is "preAcceptance" or "finalAcceptance";
        var id = acceptance ? sessionId : Guid.NewGuid();
        var kindLabel = kind switch { "incoming" => "来料检验", "assembly" => "装配验收", "preAcceptance" => "预验收", _ => "终验收" };
        if (acceptance && await records.FindAsync(id, cancellationToken) is { } existing) return ConfirmRetry(existing);

        Project? project = null;
        ProjectFolder? folder = null;
        if (acceptance)
        {
            project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
            folder = await repository.FindProjectFolderByKeyAsync(projectId, "acceptance", cancellationToken)
                ?? throw new PdmNotFoundException("项目的验收资料目录不存在，请联系管理员检查目录模板后重试。");
        }

        var path = Path.Combine("验收资料", kindLabel, id.ToString("N"), Path.GetFileName(session.FileName));
        var stored = await storage.CompleteUploadAsync(sessionId, path, cancellationToken);
        var record = new QualityInspectionRecord(id, projectId, kind, station, title, remark, session.FileName, stored.RelativePath, stored.Length, stored.Sha256, actor, stored.StoredAt);
        StoredProjectFileUpload? archive = null;
        if (project is not null && folder is not null)
        {
            // Keep the quality original separate from the file-library copy so deletion or purging there cannot break the quality record.
            var source = new ProjectFileVersion(id, Guid.Empty, 1, record.FileName, project.VaultLocation,
                record.StorageRelativePath, record.FileLength, record.Sha256, actor, record.UploadedAt, null);
            archive = await projectFileStorage.CopyVersionAsync(source, folder.RootProjectId, folder.Id,
                project.VaultLocation, actor, cancellationToken);
        }

        try
        {
            if (await records.TryAddAsync(record, archive, cancellationToken)) return record;
        }
        catch (PdmNotFoundException) when (archive is not null)
        {
            await projectFileStorage.DiscardAsync(archive, CancellationToken.None);
            throw;
        }
        if (archive is not null) await projectFileStorage.DiscardAsync(archive, CancellationToken.None);
        var saved = await records.FindAsync(id, cancellationToken) ?? throw new PdmConflictException("验收记录保存状态已变更，请刷新后重试。");
        return ConfirmRetry(saved);

        QualityInspectionRecord ConfirmRetry(QualityInspectionRecord saved) =>
            saved.ProjectId == projectId && saved.Kind == kind && saved.UploadedBy == actor
                ? saved : throw new PdmConflictException("该上传会话已用于其他验收记录，请重新选择文件上传。");
    }

    private static void ValidateFileName(string fileName)
    {
        if (!new[] { ".pdf", ".png", ".jpg", ".jpeg", ".xlsx", ".xls", ".csv", ".zip", ".txt", ".docx" }.Contains(Path.GetExtension(fileName).ToLowerInvariant()))
            throw new PdmRuleException("检验记录支持PDF、图片、Excel、CSV、ZIP、TXT和Word文件。");
    }

    public async Task<(QualityInspectionRecord Record, Stream Content)> DownloadAsync(Guid id, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var record = await records.FindAsync(id, cancellationToken) ?? throw new PdmNotFoundException("检验记录不存在。");
        await RequireAccessAsync(record.ProjectId, actor, role, false, cancellationToken);
        var project = await repository.FindProjectAsync(record.ProjectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        return (record, await storage.OpenReadAsync(StorageLocationPolicy.ResolveUnder(project.VaultLocation, record.StorageRelativePath), cancellationToken));
    }
}
