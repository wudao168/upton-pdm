using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record OplAttachment(Guid Id, string Name, long Length, Guid? StorageProjectId = null, string? StorageRoot = null);
public sealed record OplIssue(Guid Id, Guid ProjectId, int Number, string Description, string Priority, string Status,
    string Owner, DateOnly? DueDate, string Solution, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, string UpdatedBy, DateTimeOffset UpdatedAt, long RowVersion,
    IReadOnlyList<OplAttachment> Attachments, string Source = "Manual", string? ExternalId = null,
    Guid? RelatedProjectId = null, Guid? AttachmentProjectId = null, string? CreatedByDisplayName = null,
    DateOnly? ExpectedCompletionDate = null);
public sealed record OplInput(string Description, string Priority, string Status, string Owner, DateOnly? DueDate,
    string Solution, long ExpectedRowVersion, IReadOnlyList<Guid> AttachmentIds, Guid? RelatedProjectId = null, bool NotifyGroup = false)
{
    private DateOnly? expectedCompletionDate;
    public DateOnly? ExpectedCompletionDate
    {
        get => expectedCompletionDate;
        init { expectedCompletionDate = value; HasExpectedCompletionDate = true; }
    }
    [JsonIgnore]
    public bool HasExpectedCompletionDate { get; private init; }
}
public sealed record CompleteOplInput(long ExpectedRowVersion, bool NotifyGroup = false);
public sealed record OplUpload(string Name, byte[] Content);
public sealed record OplVisitorInput(string Description, Guid? RelatedProjectId = null, string Priority = "普通", string Status = "待处理", string Owner = "", DateOnly? DueDate = null, string Solution = "");
public sealed record OplPerson(string Username, string DisplayName, bool CanReceiveAssignment = true);
public sealed record OplReleaseRecord(Guid ReleasePackageId, Guid ProjectId, string PackageNumber, string BomType,
    int DrawingCount, DateTimeOffset PublishedAt);
public sealed record OplRow(OplIssue Issue, bool CanEdit, bool CanComplete)
{
    public IReadOnlyList<OplReleaseRecord> Releases { get; init; } = [];
}
public sealed record OplProject(Guid Id, string Code, string Name);
public sealed record OplView(IReadOnlyList<OplRow> Rows, IReadOnlyList<OplPerson> People, OplProject RootProject, IReadOnlyList<OplProject> Subprojects);
public interface IProjectOplRepository
{
    Task<IReadOnlyList<OplIssue>> ListAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> CountOpenAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct);
    Task<OplIssue> SaveAsync(OplIssue issue, long expectedVersion, CancellationToken ct, UserNotification? assignment = null);
}
public interface IProjectOplStorage
{
    Task WriteAsync(string vaultRoot, Guid projectId, Guid attachmentId, byte[] content, CancellationToken ct);
    Task<Stream> OpenAsync(string vaultRoot, Guid projectId, Guid attachmentId, CancellationToken ct);
    void Delete(string vaultRoot, Guid projectId, Guid attachmentId);
}

public sealed class ProjectOplService(IPdmRepository projects, IProjectOplRepository issues, IProjectOplStorage storage, TimeProvider clock, IServerStoragePaths? paths = null)
{
    public const int MaxFileLength = 10 * 1024 * 1024;
    public const string AssignmentCategory = "OplResponsePending";
    public static string AssignmentKey(Guid issueId) => $"opl-response:{issueId:D}";
    private static readonly string[] Priorities = ["紧急", "高", "普通", "低"];
    private static readonly string[] States = ["待处理", "处理中", "待确认", "已完成", "已作废"];
    private async Task<(Project Root, bool Manager)> AccessAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await projects.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await projects.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目的开口问题。");
        var root = project.ParentProjectId is null ? project : await projects.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct)
            ?? throw new PdmNotFoundException("主项目不存在。");
        if (root.Id != projectId && !await projects.HasProjectContentReadAccessAsync(root.Id, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该主项目的开口问题。");
        var manager = await AuthorizationPolicy.CanAsync(projects, root.Id, actor, "project.opl.manage", ct);
        return (root, manager);
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static async Task<bool> CanRespondAsync(IPdmRepository repository, Guid projectId, string actor, CancellationToken ct)
    {
        var context = await AuthorizationPolicy.CreateContextAsync(repository, projectId, actor, ct);
        if (context is null) return false;
        var settings = await repository.GetAuthorizationSettingsAsync(ct);
        bool Allows(AuthorizationPolicy.Context resource, string code) => AuthorizationPolicy.Decide(settings, resource, code).Allowed;
        return Allows(context, ProjectCenterPermissions.Tab("opl")) && Allows(context, "project.opl.view")
            && Allows(context, ProjectCenterPermissions.Write) && Allows(context, "project.opl.edit")
            && (context.Project.Id == context.Root.Id || Allows(context with { Project = context.Root }, PermissionCodes.ProjectContentView));
    }

    private static bool Open(OplIssue issue) => issue.Status is not ("已完成" or "已作废");

    private async Task<UserNotification?> AssignmentAsync(Project root, OplIssue issue, OplIssue? previous, CancellationToken ct)
    {
        if (!Open(issue) || issue.Owner.Length == 0) return null;
        var changed = previous is null || !Same(previous.Owner, issue.Owner) || previous.RelatedProjectId != issue.RelatedProjectId || !Open(previous);
        if (!changed && issue.ExpectedCompletionDate is not null && issue.Solution.Length > 0) return null;
        var target = issue.RelatedProjectId ?? root.Id;
        if (!await CanRespondAsync(projects, target, issue.Owner, ct) || !await NotificationRecipientPolicy.CanReceiveAsync(projects, issue.Owner, ct))
        {
            if (changed) throw new PdmRuleException("请选择能编辑该项目OPL并接收业务待办的负责人。");
            return null;
        }
        return new(Guid.NewGuid(), issue.Owner, AssignmentCategory, "OPL问题待回填",
            "请填写解决措施和预计完成日期。", target, null, AssignmentKey(issue.Id), issue.UpdatedAt, null, issue.DueDate);
    }
    public async Task<OplView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct, bool includePeople = true)
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        var writable = await AuthorizationPolicy.CanAsync(projects, root.Id, actor, ProjectCenterPermissions.Write, ct);
        var records = (await issues.ListAsync(root.Id, ct)).OrderByDescending(x => x.Number).ToArray();
        var editableProjects = new HashSet<Guid>();
        if (await CanRespondAsync(projects, projectId, actor, ct))
        {
            editableProjects.Add(projectId);
            if (projectId == root.Id)
                foreach (var childId in records.Where(x => manager || Same(x.CreatedBy, actor) || Same(x.Owner, actor))
                    .Select(x => x.RelatedProjectId).OfType<Guid>().Distinct())
                    if (await CanRespondAsync(projects, childId, actor, ct)) editableProjects.Add(childId);
        }
        var rows = records.Select(x => new OplRow(x, editableProjects.Contains(x.RelatedProjectId ?? root.Id)
            && (manager || Same(x.CreatedBy, actor) || Same(x.Owner, actor)), writable && (manager || Same(x.CreatedBy, actor)))).ToArray();
        var people = new List<OplPerson>();
        if (includePeople) foreach (var user in await projects.ListUsersAsync(ct))
            if (user.IsActive && await projects.HasProjectContentReadAccessAsync(root.Id, user.Username, user.Role, ct))
                people.Add(new(user.Username, string.IsNullOrWhiteSpace(user.DisplayName) || Same(user.DisplayName, user.Username)
                    ? "姓名待补充" : user.DisplayName, await NotificationRecipientPolicy.CanReceiveAsync(projects, user.Username, ct)
                    && await CanRespondAsync(projects, projectId, user.Username, ct)));
        var subprojects = includePeople ? (await projects.ListProjectsForUserAsync(actor, role, ct))
            .Where(x => x.ParentProjectId is not null && (x.RootProjectId ?? x.ParentProjectId) == root.Id && x.CanReadContent)
            .OrderBy(x => x.Code).Select(x => new OplProject(x.Id, x.Code, x.Name)).ToArray() : [];
        if (includePeople && rows.Length > 0)
        {
            var histories = new Dictionary<Guid, List<OplReleaseRecord>>();
            var issueIds = rows.Select(row => row.Issue.Id).ToHashSet();
            foreach (var id in subprojects.Select(item => item.Id).Prepend(root.Id))
            {
                foreach (var package in (await projects.ListReleasePackagesAsync(id, ct))
                    .Where(item => item.State == ReleasePackageState.Published && item.PublishedAt.HasValue
                        && item.RelatedOplIssueIds.Any(issueIds.Contains)))
                {
                    var bomType = package.Scope switch
                    {
                        ReleaseScope.StandardSupplement => "标准件",
                        ReleaseScope.ElectricalSupplement => "电气件",
                        ReleaseScope.NonStandardSupplement => "非标件",
                        _ => "历史发布"
                    };
                    var record = new OplReleaseRecord(package.Id, id, package.Number, bomType, package.DrawingDocumentCount, package.PublishedAt!.Value);
                    foreach (var issueId in package.RelatedOplIssueIds.Distinct().Where(issueIds.Contains))
                    {
                        if (!histories.TryGetValue(issueId, out var history)) histories[issueId] = history = [];
                        history.Add(record);
                    }
                }
            }
            rows = rows.Select(row => row with { Releases = histories.GetValueOrDefault(row.Issue.Id, [])
                .OrderByDescending(item => item.PublishedAt).ThenBy(item => item.ReleasePackageId).ToArray() }).ToArray();
        }
        return new(rows, people, new(root.Id, root.Code, root.Name), subprojects);
    }
    public async Task<IReadOnlyList<Guid>> ValidateReleaseLinksAsync(Guid projectId, IReadOnlyList<Guid> ids,
        string actor, UserRole role, CancellationToken ct)
    {
        var (root, _) = await AccessAsync(projectId, actor, role, ct);
        var available = (await issues.ListAsync(root.Id, ct)).ToDictionary(item => item.Id);
        var selected = ids.Distinct().ToArray();
        foreach (var id in selected)
        {
            if (!available.TryGetValue(id, out var issue) || issue.Status == "已作废")
                throw new PdmRuleException("所选OPL问题不存在、已作废或不属于当前主项目，请刷新后重新选择。");
            if (issue.RelatedProjectId is Guid childId && (projectId != root.Id && childId != projectId
                || !await projects.HasProjectContentReadAccessAsync(childId, actor, role, ct)))
                throw new PdmRuleException("所选OPL问题不在当前发布项目的可访问范围内。");
        }
        return selected;
    }
    public async Task<OplIssue> SaveAsync(Guid projectId, Guid? id, OplInput input, IReadOnlyList<OplUpload> uploads, string actor, UserRole role, CancellationToken ct, string source = "Manual")
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        var existing = id is null ? null : (await issues.ListAsync(root.Id, ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new PdmNotFoundException("开口问题不存在。");
        if (existing is not null && (projectId != root.Id && existing.RelatedProjectId != projectId
            || !await CanRespondAsync(projects, existing.RelatedProjectId ?? root.Id, actor, ct)))
            throw new UnauthorizedAccessException("当前账号无权编辑该项目范围内的开口问题。");
        if (projectId != root.Id && input.RelatedProjectId != projectId)
            throw new PdmRuleException("请在所属主项目中调整关联子项目。");
        if (input.RelatedProjectId is Guid relatedId)
        {
            var related = await projects.FindProjectAsync(relatedId, ct);
            if (related is null || related.ParentProjectId is null || (related.RootProjectId ?? related.ParentProjectId) != root.Id)
                throw new PdmRuleException("关联子项目必须属于当前主项目。");
            if (!await projects.HasProjectContentReadAccessAsync(relatedId, actor, role, ct)) throw new UnauthorizedAccessException("无权选择该子项目。");
            if (existing is not null && existing.RelatedProjectId != relatedId && !await CanRespondAsync(projects, relatedId, actor, ct))
                throw new UnauthorizedAccessException("无权将该问题转到所选子项目。");
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
        var vault = string.IsNullOrWhiteSpace(paths?.OplAttachmentRoot) ? (await projects.GetSystemSettingsAsync(ct)).VaultRoot : paths.OplAttachmentRoot;
        var added = uploads.Select(x => new OplAttachment(Guid.NewGuid(), Path.GetFileName(x.Name.Replace('\\', '/')), x.Content.Length, root.Id, vault)).ToArray();
        var value = new OplIssue(existing?.Id ?? Guid.NewGuid(), root.Id, existing?.Number ?? 0, input.Description.Trim(), input.Priority, input.Status,
            owner, input.DueDate, input.Solution.Trim(), existing?.CreatedBy ?? actor, existing?.CreatedAt ?? now,
            input.Status == "已完成" ? existing?.CompletedAt ?? now : null, actor, now, existing?.RowVersion ?? 0,
            (existing?.Attachments.Where(x => retained.Contains(x.Id)) ?? []).Concat(added).ToArray(), existing?.Source ?? source, existing?.ExternalId,
            input.RelatedProjectId, existing?.AttachmentProjectId, existing?.CreatedByDisplayName,
            input.HasExpectedCompletionDate ? input.ExpectedCompletionDate : existing?.ExpectedCompletionDate);
        var assignment = await AssignmentAsync(root, value, existing, ct);
        try
        {
            for (var i = 0; i < added.Length; i++) await storage.WriteAsync(vault, root.Id, added[i].Id, uploads[i].Content, ct);
            value = await issues.SaveAsync(value, input.ExpectedRowVersion, ct, assignment);
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

    public async Task RequireSpeechAccessAsync(Guid projectId, Guid? issueId, Guid? relatedProjectId, string actor, UserRole role, CancellationToken ct)
    {
        var (root, manager) = await AccessAsync(projectId, actor, role, ct);
        if (projectId != root.Id && relatedProjectId != projectId)
            throw new PdmRuleException("请在所属主项目中调整关联子项目。");
        if (relatedProjectId is Guid relatedId)
        {
            var related = await projects.FindProjectAsync(relatedId, ct);
            if (related is null || related.ParentProjectId is null || (related.RootProjectId ?? related.ParentProjectId) != root.Id
                || !await projects.HasProjectContentReadAccessAsync(relatedId, actor, role, ct))
                throw new UnauthorizedAccessException("无权在所选子项目中录入问题。");
        }
        if (issueId is null) return;
        var existing = (await issues.ListAsync(root.Id, ct)).SingleOrDefault(issue => issue.Id == issueId)
            ?? throw new PdmNotFoundException("开口问题不存在。");
        if (projectId != root.Id && existing.RelatedProjectId != projectId
            || !(manager || Same(existing.CreatedBy, actor) || Same(existing.Owner, actor))
            || !await CanRespondAsync(projects, existing.RelatedProjectId ?? root.Id, actor, ct))
            throw new UnauthorizedAccessException("当前账号无权编辑该项目范围内的开口问题。");
        if (relatedProjectId is Guid target && relatedProjectId != existing.RelatedProjectId && !await CanRespondAsync(projects, target, actor, ct))
            throw new UnauthorizedAccessException("无权将该问题转到所选子项目。");
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
        var vault = string.IsNullOrWhiteSpace(paths?.OplAttachmentRoot) ? (await projects.GetSystemSettingsAsync(ct)).VaultRoot : paths.OplAttachmentRoot;
        var attachments = uploads.Select(x => new OplAttachment(Guid.NewGuid(), Path.GetFileName(x.Name.Replace('\\', '/')), x.Content.Length, root.Id, vault)).ToArray();
        var value = new OplIssue(Guid.NewGuid(), root.Id, 0, input.Description.Trim(), input.Priority, input.Status, owner, input.DueDate, input.Solution.Trim(), actor, now, input.Status == "已完成" ? now : null, actor, now, 0, attachments,
            "DingTalk", null, input.RelatedProjectId, null, displayName);
        var assignment = await AssignmentAsync(root, value, null, ct);
        try
        {
            for (var i = 0; i < attachments.Length; i++) await storage.WriteAsync(vault, root.Id, attachments[i].Id, uploads[i].Content, ct);
            value = await issues.SaveAsync(value, 0, ct, assignment);
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
        var issue = (await issues.ListAsync(root.Id, ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new PdmNotFoundException("附件不存在或已被移除。");
        if (projectId != root.Id && issue.RelatedProjectId != projectId
            || issue.RelatedProjectId is Guid childId && !await projects.HasProjectContentReadAccessAsync(childId, actor, role, ct))
            throw new UnauthorizedAccessException("无权查看该项目范围内的开口问题附件。");
        var file = issue.Attachments.SingleOrDefault(x => x.Id == attachmentId)
            ?? throw new PdmNotFoundException("附件不存在或已被移除。");
        var vault = file.StorageRoot ?? (string.IsNullOrWhiteSpace(paths?.LegacyOplAttachmentRoot)
            ? (await projects.GetSystemSettingsAsync(ct)).VaultRoot : paths.LegacyOplAttachmentRoot);
        return (file, await storage.OpenAsync(vault, file.StorageProjectId ?? issue.AttachmentProjectId ?? root.Id, attachmentId, ct));
    }
}
