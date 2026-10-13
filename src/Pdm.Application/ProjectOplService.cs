using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record OplAttachment(Guid Id, string Name, long Length, Guid? StorageProjectId = null);
public sealed record OplIssue(Guid Id, Guid ProjectId, int Number, string Description, string Priority, string Status,
    string Owner, DateOnly? DueDate, string Solution, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, string UpdatedBy, DateTimeOffset UpdatedAt, long RowVersion,
    IReadOnlyList<OplAttachment> Attachments, string Source = "Manual", string? ExternalId = null,
    Guid? RelatedProjectId = null, Guid? AttachmentProjectId = null, string? CreatedByDisplayName = null);
public sealed record OplInput(string Description, string Priority, string Status, string Owner, DateOnly? DueDate,
    string Solution, long ExpectedRowVersion, IReadOnlyList<Guid> AttachmentIds, Guid? RelatedProjectId = null);
public sealed record CompleteOplInput(long ExpectedRowVersion);
public sealed record OplUpload(string Name, byte[] Content);
public sealed record OplVisitorInput(string Description, Guid? RelatedProjectId = null, string Priority = "普通", string Status = "待处理", string Owner = "", DateOnly? DueDate = null, string Solution = "");
public sealed record OplPerson(string Username, string DisplayName);
public sealed record OplRow(OplIssue Issue, bool CanEdit, bool CanComplete);
public sealed record OplProject(Guid Id, string Code, string Name);
public sealed record OplView(IReadOnlyList<OplRow> Rows, IReadOnlyList<OplPerson> People, OplProject RootProject, IReadOnlyList<OplProject> Subprojects);
public interface IProjectOplRepository
{
    Task<IReadOnlyList<OplIssue>> ListAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> CountOpenAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct);
    Task<OplIssue> SaveAsync(OplIssue issue, long expectedVersion, CancellationToken ct);
}
public interface IProjectOplStorage
{
    Task WriteAsync(string vaultRoot, Guid projectId, Guid attachmentId, byte[] content, CancellationToken ct);
    Task<Stream> OpenAsync(string vaultRoot, Guid projectId, Guid attachmentId, CancellationToken ct);
    void Delete(string vaultRoot, Guid projectId, Guid attachmentId);
}

public sealed class ProjectOplService(IPdmRepository projects, IProjectOplRepository issues, IProjectOplStorage storage, TimeProvider clock)
{
    public const int MaxFileLength = 10 * 1024 * 1024;
    private static readonly string[] Priorities = ["紧急", "高", "普通", "低"];
    private static readonly string[] States = ["待处理", "处理中", "待确认", "已完成", "已作废"];
    private async Task<(Project Root, bool Manager)> AccessAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await projects.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await projects.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目的开口问题。");
        var root = project.ParentProjectId is null ? project : await projects.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct)
            ?? throw new PdmNotFoundException("主项目不存在。");
        if (root.Id != projectId && !await projects.HasProjectContentReadAccessAsync(root.Id, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该主项目的开口问题。");
        var manager = role == UserRole.Administrator || TenantContext.Current?.HasRole("developer") == true
            || string.Equals(root.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
            || root.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase);
        return (root, manager);
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public async Task<OplView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct, bool includePeople = true)
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        var rows = (await issues.ListAsync(root.Id, ct)).OrderByDescending(x => x.Number)
            .Select(x => new OplRow(x, manager || Same(x.CreatedBy, actor) || Same(x.Owner, actor), manager || Same(x.CreatedBy, actor))).ToArray();
        var people = new List<OplPerson>();
        if (includePeople) foreach (var user in await projects.ListUsersAsync(ct))
            if (user.IsActive && await projects.HasProjectContentReadAccessAsync(root.Id, user.Username, user.Role, ct)) people.Add(new(user.Username, user.DisplayName));
        var subprojects = includePeople ? (await projects.ListProjectsForUserAsync(actor, role, ct))
            .Where(x => x.ParentProjectId is not null && (x.RootProjectId ?? x.ParentProjectId) == root.Id && x.CanReadContent)
            .OrderBy(x => x.Code).Select(x => new OplProject(x.Id, x.Code, x.Name)).ToArray() : [];
        return new(rows, people, new(root.Id, root.Code, root.Name), subprojects);
    }
    public async Task<OplIssue> SaveAsync(Guid projectId, Guid? id, OplInput input, IReadOnlyList<OplUpload> uploads, string actor, UserRole role, CancellationToken ct, string source = "Manual")
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        var existing = id is null ? null : (await issues.ListAsync(root.Id, ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new PdmNotFoundException("开口问题不存在。");
        if (input.RelatedProjectId is Guid relatedId)
        {
            var related = await projects.FindProjectAsync(relatedId, ct);
            if (related is null || related.ParentProjectId is null || (related.RootProjectId ?? related.ParentProjectId) != root.Id)
                throw new PdmRuleException("关联子项目必须属于当前主项目。");
            if (!await projects.HasProjectContentReadAccessAsync(relatedId, actor, role, ct)) throw new UnauthorizedAccessException("无权选择该子项目。");
        }
        if (existing is not null && !(manager || Same(existing.CreatedBy, actor) || Same(existing.Owner, actor))) throw new UnauthorizedAccessException("仅提交人、负责人或项目经理可以编辑此问题。");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 2000) throw new PdmRuleException("请填写问题描述，最多2000个字符。");
        if (!Priorities.Contains(input.Priority) || !States.Contains(input.Status)) throw new PdmRuleException("请选择有效的优先级和实施状态。");
        if (input.Solution is null || input.Solution.Length > 4000) throw new PdmRuleException("解决措施最多4000个字符。");
        if (input.Status == "待确认" && string.IsNullOrWhiteSpace(input.Solution)) throw new PdmRuleException("请先填写解决措施，再提交确认。");
        if (input.Status == "已完成" && existing?.Status != "已完成" && !(manager || existing is not null && Same(existing.CreatedBy, actor))) throw new UnauthorizedAccessException("请由提交人或项目经理确认完成。");
        var owner = input.Owner?.Trim() ?? "";
        if (owner.Length > 0)
        {
            var user = await projects.FindUserAsync(owner, ct);
            if (user is null || !user.IsActive || !await projects.HasProjectContentReadAccessAsync(root.Id, owner, user.Role, ct)) throw new PdmRuleException("请选择有该项目访问权限的在职负责人。");
        }
        var retained = input.AttachmentIds?.Distinct().ToArray() ?? [];
        if (retained.Any(key => existing?.Attachments.Any(x => x.Id == key) != true)) throw new PdmRuleException("附件不属于当前问题，请刷新后重试。");
        if (retained.Length + uploads.Count > 10 || uploads.Any(x => x.Content.Length == 0 || x.Content.Length > MaxFileLength || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 200)) throw new PdmRuleException("最多10个附件，单个附件应为1字节至10MB，文件名最多200个字符。");
        if (uploads.Sum(x => (long)x.Content.Length) > 30 * 1024 * 1024) throw new PdmRuleException("单次上传附件总大小不能超过30MB。");
        var now = clock.GetUtcNow();
        var added = uploads.Select(x => new OplAttachment(Guid.NewGuid(), Path.GetFileName(x.Name.Replace('\\', '/')), x.Content.Length, root.Id)).ToArray();
        var value = new OplIssue(existing?.Id ?? Guid.NewGuid(), root.Id, existing?.Number ?? 0, input.Description.Trim(), input.Priority, input.Status,
            owner, input.DueDate, input.Solution.Trim(), existing?.CreatedBy ?? actor, existing?.CreatedAt ?? now,
            input.Status == "已完成" ? existing?.CompletedAt ?? now : null, actor, now, existing?.RowVersion ?? 0,
            (existing?.Attachments.Where(x => retained.Contains(x.Id)) ?? []).Concat(added).ToArray(), existing?.Source ?? source, existing?.ExternalId,
            input.RelatedProjectId, existing?.AttachmentProjectId, existing?.CreatedByDisplayName);
        var vault = (await projects.GetSystemSettingsAsync(ct)).VaultRoot;
        try
        {
            for (var i = 0; i < added.Length; i++) await storage.WriteAsync(vault, root.Id, added[i].Id, uploads[i].Content, ct);
            value = await issues.SaveAsync(value, input.ExpectedRowVersion, ct);
        }
        catch
        {
            foreach (var file in added) storage.Delete(vault, root.Id, file.Id);
            throw;
        }
        // 移除的附件保留在文件库中供备份恢复，当前问题不再授权访问。
        await projects.AppendAuditAsync(new(Guid.NewGuid(), now, actor, id is null ? "project.opl.create" : "project.opl.update", nameof(OplIssue), value.Id.ToString(), $"开口问题 #{value.Number} · {value.Status} · 版本 {value.RowVersion}"), ct);
        return value;
    }
    public async Task<OplIssue> SubmitVisitorAsync(Project root, OplVisitorInput input, IReadOnlyList<OplUpload> uploads, string actor, string displayName, CancellationToken ct)
    {
        if (root.ParentProjectId is not null || !root.IsActive || root.OrganizationId != TenantContext.CompanyId) throw new PdmRuleException("项目入口已失效，请重新打开群入口。");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 2000) throw new PdmRuleException("请填写问题描述，最多2000个字符。");
        if (!Priorities.Contains(input.Priority) || !States.Contains(input.Status)) throw new PdmRuleException("请选择有效的优先级和实施状态。");
        if (input.Solution is null || input.Solution.Length > 4000) throw new PdmRuleException("解决措施最多4000个字符。");
        if (input.Status == "待确认" && string.IsNullOrWhiteSpace(input.Solution)) throw new PdmRuleException("请填写解决措施后再提交确认。");
        var owner = input.Owner?.Trim() ?? "";
        if (owner.Length > 0)
        {
            var user = await projects.FindUserAsync(owner, ct);
            if (user is null || !user.IsActive || !await projects.HasProjectContentReadAccessAsync(root.Id, user.Username, user.Role, ct)) throw new PdmRuleException("请选择当前项目的有效负责人。");
        }
        if (input.RelatedProjectId is Guid childId)
        {
            var child = await projects.FindProjectAsync(childId, ct);
            if (child is null || !child.IsActive || child.ParentProjectId is null || (child.RootProjectId ?? child.ParentProjectId) != root.Id || child.OrganizationId != root.OrganizationId) throw new PdmRuleException("请选择当前项目下的有效子项目。");
        }
        if (uploads.Count > 10 || uploads.Any(x => x.Content.Length == 0 || x.Content.Length > MaxFileLength || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 200)) throw new PdmRuleException("最多10个附件，单个附件应为1字节至10MB，文件名最多200个字符。");
        if (uploads.Sum(x => (long)x.Content.Length) > 30 * 1024 * 1024) throw new PdmRuleException("单次上传附件总大小不能超过30MB。");
        var now = clock.GetUtcNow();
        var attachments = uploads.Select(x => new OplAttachment(Guid.NewGuid(), Path.GetFileName(x.Name.Replace('\\', '/')), x.Content.Length, root.Id)).ToArray();
        var value = new OplIssue(Guid.NewGuid(), root.Id, 0, input.Description.Trim(), input.Priority, input.Status, owner, input.DueDate, input.Solution.Trim(), actor, now, input.Status == "已完成" ? now : null, actor, now, 0, attachments,
            "DingTalk", null, input.RelatedProjectId, null, displayName);
        var vault = (await projects.GetSystemSettingsAsync(ct)).VaultRoot;
        try
        {
            for (var i = 0; i < attachments.Length; i++) await storage.WriteAsync(vault, root.Id, attachments[i].Id, uploads[i].Content, ct);
            value = await issues.SaveAsync(value, 0, ct);
        }
        catch { foreach (var attachment in attachments) storage.Delete(vault, root.Id, attachment.Id); throw; }
        await projects.AppendAuditAsync(new(Guid.NewGuid(), now, actor, "project.opl.create", nameof(OplIssue), value.Id.ToString(), $"钉钉访客 {displayName} 提交开口问题 #{value.Number}"), ct);
        return value;
    }
    public async Task<OplIssue> CompleteAsync(Guid projectId, Guid id, long expectedRowVersion, string actor, UserRole role, CancellationToken ct)
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        var issue = (await issues.ListAsync(root.Id, ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new PdmNotFoundException("开口问题不存在。");
        if (!(manager || Same(issue.CreatedBy, actor))) throw new UnauthorizedAccessException("请由提交人或项目经理确认完成。");
        if (issue.RowVersion != expectedRowVersion) throw new PdmConflictException("问题已被其他人修改，请刷新后重试。");
        if (issue.Status == "已作废") throw new PdmRuleException("已作废的问题不能标记为完成。");
        if (issue.Status == "已完成") return issue;
        var now = clock.GetUtcNow();
        var saved = await issues.SaveAsync(issue with { Status = "已完成", CompletedAt = now, UpdatedBy = actor, UpdatedAt = now }, expectedRowVersion, ct);
        await projects.AppendAuditAsync(new(Guid.NewGuid(), now, actor, "project.opl.complete", nameof(OplIssue), id.ToString(), $"开口问题 #{saved.Number} · 确认完成 · 版本 {saved.RowVersion}"), ct);
        return saved;
    }
    public async Task<(OplAttachment Attachment, Stream Content)> OpenAsync(Guid projectId, Guid id, Guid attachmentId, string actor, UserRole role, CancellationToken ct)
    {
        var (root, _) = await AccessAsync(projectId, actor, role, ct);
        var issue = (await issues.ListAsync(root.Id, ct)).SingleOrDefault(x => x.Id == id);
        var file = issue?.Attachments.SingleOrDefault(x => x.Id == attachmentId)
            ?? throw new PdmNotFoundException("附件不存在或已被移除。");
        return (file, await storage.OpenAsync((await projects.GetSystemSettingsAsync(ct)).VaultRoot, file.StorageProjectId ?? issue!.AttachmentProjectId ?? root.Id, attachmentId, ct));
    }
}
