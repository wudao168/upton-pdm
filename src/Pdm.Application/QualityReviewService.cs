using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class QualityReviewService(IPdmRepository repository, IProjectOplRepository issues,
    IQualityReviewStore store, TimeProvider clock) : IQualityIssueGate
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).Date);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static Guid Root(Project p) => p.RootProjectId ?? p.ParentProjectId ?? p.Id;
    private static string Name(string? value) => string.IsNullOrWhiteSpace(value) ? "姓名待补充" : value.Trim();
    private async Task<Guid> CompanyAsync(string actor, CancellationToken ct)
    {
        var company = TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。");
        var user = await repository.FindUserAsync(actor, ct);
        if (user is null || !user.IsActive || !await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, company, ct))
            throw new UnauthorizedAccessException("当前账号无权访问所选公司。");
        return company;
    }
    private async Task<bool> CompanyCanAsync(string actor, string permission, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, ct);
        var user = await repository.FindUserAsync(actor, ct);
        return user is not null && AuthorizationPolicy.GlobalAllows(await repository.GetAuthorizationSettingsAsync(ct), user, company, permission);
    }
    private async Task<Guid> RequireCompanyAsync(string actor, string permission, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, ct);
        if (!await CompanyCanAsync(actor, permission, ct)) throw new UnauthorizedAccessException("未获得当前公司的此项质量管理权限，请联系授权人员在权限中心配置。");
        return company;
    }
    private async Task<Project> RequireProjectAsync(Guid id, string actor, string? permission, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, ct);
        var project = await repository.FindProjectAsync(id, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.OrganizationId != company) throw new UnauthorizedAccessException("该项目不属于当前公司。");
        if (permission is not null && !await ProjectCanAsync(id, actor, permission, ct))
            throw new UnauthorizedAccessException("未获得该项目的此项质量管理权限，请联系授权人员在权限中心配置。");
        return project;
    }
    private Task<bool> ProjectCanAsync(Guid id, string actor, string code, CancellationToken ct) => AuthorizationPolicy.CanAsync(repository, id, actor, code, ct);
    private async Task<QualityReviewCatalog> LoadAsync(Guid company, long expected, CancellationToken ct)
    {
        var catalog = await store.ReadAsync(company, ct);
        if (catalog.RowVersion != expected) throw new PdmConflictException("质量台账已被其他人更新，请刷新后重试。");
        return catalog;
    }
    private async Task<QualityReviewCatalog> SaveAsync(QualityReviewCatalog catalog, string actor, string action, Guid id, CancellationToken ct,
        string detail = "质量复盘记录已更新")
    {
        if (!await store.SaveAsync(catalog, catalog.RowVersion, ct)) throw new PdmConflictException("质量台账已被其他人更新，请刷新后重试。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "QualityReview", id.ToString(), detail), ct);
        return catalog with { RowVersion = catalog.RowVersion + 1 };
    }
    private static string Text(string? value, int length, string label, bool required = false)
    {
        var result = value?.Trim() ?? "";
        if (required && result.Length == 0) throw new PdmRuleException($"请填写{label}。");
        if (result.Length > length) throw new PdmRuleException($"{label}不能超过{length}字。");
        return result;
    }
    private static void Id(Guid id) { if (id == Guid.Empty) throw new PdmRuleException("记录标识无效，请刷新后重试。"); }
    private async Task<IReadOnlyList<Project>> ProjectsAsync(Guid company, CancellationToken ct) =>
        (await repository.ListProjectHeadersAsync(ct)).Where(p => p.OrganizationId == company).ToArray();
    private async Task<IReadOnlyList<QualityPerson>> PeopleAsync(Guid company, CancellationToken ct)
    {
        var people = new List<QualityPerson>();
        foreach (var user in (await repository.ListUsersAsync(ct)).Where(u => u.IsActive))
            if (await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, company, ct)) people.Add(new(user.Username, Name(user.DisplayName)));
        return people.OrderBy(u => u.DisplayName).ToArray();
    }
    private async Task<string> PersonAsync(string username, Guid company, CancellationToken ct)
    {
        var user = await repository.FindUserAsync(username?.Trim() ?? "", ct);
        if (user is null || !user.IsActive || !await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, company, ct)) throw new PdmRuleException("请选择有当前公司访问范围的在职人员。");
        return user.Username;
    }
    private async Task<IReadOnlyList<QualityIssueRow>> RowsAsync(QualityReviewCatalog catalog, IReadOnlyList<Project> projects,
        string actor, CancellationToken ct, Guid? projectId = null)
    {
        var directory = (await repository.ListUsersAsync(ct)).ToDictionary(u => u.Username, StringComparer.OrdinalIgnoreCase);
        var profiles = catalog.Profiles.ToDictionary(p => p.IssueId);
        var map = projects.ToDictionary(p => p.Id);
        var result = new List<QualityIssueRow>();
        var rootIds = projectId is { } target ? new[] { Root(map[target]) } : projects.Select(Root).Distinct().ToArray();
        var read = new Dictionary<Guid, bool>();
        foreach (var rootId in rootIds)
        {
            if (!map.ContainsKey(rootId)) continue;
            foreach (var issue in await issues.ListAsync(rootId, ct))
            {
                var actualId = issue.RelatedProjectId ?? issue.ProjectId;
                if (!map.TryGetValue(actualId, out var project)) continue;
                if (projectId is { } filter && filter != rootId && actualId != filter) continue;
                if (!read.TryGetValue(actualId, out var canRead))
                    read[actualId] = canRead = await ProjectCanAsync(actualId, actor, QualityReviewPermissions.IssueView, ct);
                // A project-scoped view never inherits access to all child projects from the root.
                if (projectId is not null && !canRead) continue;
                result.Add(new(issue, profiles.GetValueOrDefault(issue.Id), rootId, project.Code, project.Name,
                    Name(directory.GetValueOrDefault(issue.Owner)?.DisplayName), Name(directory.GetValueOrDefault(issue.CreatedBy)?.DisplayName), canRead));
            }
        }
        return result.OrderByDescending(r => r.Profile?.OccurredOn ?? DateOnly.MinValue).ThenByDescending(r => r.Issue.CreatedAt).ToArray();
    }
    public async Task<QualityReviewWorkspace> WorkspaceAsync(string actor, UserRole role, CancellationToken ct)
    {
        var company = await RequireCompanyAsync(actor, QualityReviewPermissions.View, ct);
        var catalog = await store.ReadAsync(company, ct);
        var projects = await ProjectsAsync(company, ct);
        var rows = await RowsAsync(catalog, projects, actor, ct);
        var detail = await CompanyCanAsync(actor, QualityReviewPermissions.Detail, ct);
        var visible = detail ? catalog : catalog with { Profiles = [], Summaries = [] };
        visible = visible with { Experiences = visible.Experiences.Select(e => e with { RecommendedProjectIds = e.ProjectTags.Count == 0 ? []
            : projects.Where(p => p.IsActive && Root(p) == p.Id && e.ProjectTags.Any(tag => new[] { p.Code, p.Name, p.DeviceModel, p.ProjectTypeCode }
                .Any(value => value?.Contains(tag, StringComparison.OrdinalIgnoreCase) == true))
                && !catalog.Adoptions.Any(a => a.ExperienceId == e.Id && a.ProjectId == p.Id)).Select(p => p.Id).ToArray() }).ToArray() };
        var capabilities = new QualityCapabilities(false,
            await CompanyCanAsync(actor, QualityReviewPermissions.Manage, ct), await CompanyCanAsync(actor, QualityReviewPermissions.Improve, ct),
            await CompanyCanAsync(actor, QualityReviewPermissions.Verify, ct), await CompanyCanAsync(actor, QualityReviewPermissions.Experience, ct), false)
        {
            CanViewDetails = detail, CanApproveImprovements = await CompanyCanAsync(actor, QualityReviewPermissions.ImprovementApprove, ct),
            CanExecuteImprovements = await CompanyCanAsync(actor, QualityReviewPermissions.ImprovementExecute, ct),
            CanCloseImprovements = await CompanyCanAsync(actor, QualityReviewPermissions.ImprovementClose, ct),
            CanManageSettings = await CompanyCanAsync(actor, QualityReviewPermissions.Settings, ct)
        };
        return new(visible, detail ? rows : [], QualityReviewRules.Metrics(catalog, rows, Today),
            projects.Select(p => new QualityProject(p.Id, p.Code, p.Name, Root(p))).ToArray(), await PeopleAsync(company, ct), capabilities,
            Today.AddDays(1 - catalog.Settings.WindowDays), Today);
    }
    public async Task<ProjectRetrospectiveWorkspace> ProjectAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, actor, null, ct);
        var summary = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.SummaryView, ct);
        var issueView = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueView, ct);
        if (!summary && !issueView) throw new UnauthorizedAccessException("没有查看此项目总结或质量问题的权限。");
        var catalog = await store.ReadAsync(project.OrganizationId!.Value, ct);
        var projects = await ProjectsAsync(catalog.CompanyId, ct);
        var summarySources = new Dictionary<Guid, RetrospectiveSource>();
        if (summary && Root(project) == projectId)
        {
            var sourceIds = catalog.Summaries.Select(item => item.ProjectId).ToHashSet();
            foreach (var child in projects.Where(p => p.Id != projectId && Root(p) == projectId && sourceIds.Contains(p.Id)))
            {
                if (!await ProjectCanAsync(child.Id, actor, QualityReviewPermissions.SummaryView, ct)) continue;
                summarySources[child.Id] = new(child.Code,
                    await ProjectCanAsync(child.Id, actor, QualityReviewPermissions.SummaryManage, ct),
                    await ProjectCanAsync(child.Id, actor, QualityReviewPermissions.SummaryReview, ct),
                    await ProjectCanAsync(child.Id, actor, QualityReviewPermissions.SummaryPublish, ct));
            }
        }
        var adoptions = summary ? catalog.Adoptions.Where(a => a.ProjectId == projectId).ToArray() : [];
        var ids = adoptions.Select(a => a.ExperienceId).ToHashSet();
        return new(projectId, catalog.RowVersion, summary ? catalog.Summaries.Where(s => s.ProjectId == projectId || summarySources.ContainsKey(s.ProjectId)).ToArray() : [],
            issueView ? await RowsAsync(catalog, projects, actor, ct, projectId) : [], catalog.Experiences.Where(e => ids.Contains(e.Id)).ToArray(), adoptions,
            await ProjectCanAsync(projectId, actor, QualityReviewPermissions.SummaryManage, ct),
            await ProjectCanAsync(projectId, actor, QualityReviewPermissions.SummaryReview, ct),
            await ProjectCanAsync(projectId, actor, QualityReviewPermissions.AdoptionConfirm, ct))
        {
            SummarySources = summarySources,
            CanViewIssues = issueView,
            CanPublish = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.SummaryPublish, ct),
            CanManageIssues = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueManage, ct),
            CanVerifyIssues = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueVerify, ct),
            CanCloseIssues = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueClose, ct),
            CanReopenIssues = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueReopen, ct),
            CanVoidIssues = await ProjectCanAsync(projectId, actor, QualityReviewPermissions.IssueVoid, ct),
            Themes = catalog.Themes.Where(t => t.IsActive).ToArray(), People = await PeopleAsync(catalog.CompanyId, ct)
        };
    }
    public async Task<QualityIssueWorkspace> IssueAsync(Guid projectId, Guid issueId, string actor, UserRole role, CancellationToken ct)
    {
        await RequireProjectAsync(projectId, actor, QualityReviewPermissions.IssueView, ct);
        var view = await ProjectAsync(projectId, actor, role, ct);
        var row = view.Issues.SingleOrDefault(r => r.Issue.Id == issueId) ?? throw new PdmNotFoundException("问题不存在或不在可访问项目范围内。");
        var actual = row.Issue.RelatedProjectId ?? row.Issue.ProjectId;
        return new(view.RowVersion, row, view.Themes, view.People,
            await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueManage, ct),
            await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueVerify, ct),
            await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueClose, ct),
            await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueReopen, ct),
            await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueVoid, ct));
    }
    public async Task<QualitySettingsWorkspace> SettingsAsync(string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await store.ReadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.View, ct), ct);
        return new(catalog.RowVersion, catalog.Settings);
    }
    public async Task<QualityReviewCatalog> SaveSettingsAsync(SaveQualityReviewSettings input, string actor, UserRole role, CancellationToken ct)
    {
        var company = await RequireCompanyAsync(actor, QualityReviewPermissions.Settings, ct);
        var catalog = await LoadAsync(company, input.ExpectedRowVersion, ct);
        if (input.WindowDays is < 1 or > 3650 || input.EventThreshold is < 1 or > 10000 || input.ProjectThreshold is < 1 or > 10000)
            throw new PdmRuleException("统计窗口为1至3650天，事件及项目阈值为1至10000。");
        return await SaveAsync(catalog with { Settings = new(input.WindowDays, input.EventThreshold, input.ProjectThreshold) }, actor, "quality.settings.save", company, ct);
    }
    public async Task<QualityReviewCatalog> SaveThemeAsync(Guid id, SaveQualityTheme input, string actor, UserRole role, CancellationToken ct)
    {
        Id(id);
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.Manage, ct), input.ExpectedRowVersion, ct);
        var title = Text(input.Name, 100, "问题主题", true);
        if (catalog.Themes.Any(t => t.Id != id && Same(t.Name, title))) throw new PdmRuleException("同名问题主题已存在，请使用已有主题。");
        if (!input.IsActive && catalog.Improvements.Any(i => i.ThemeId == id && i.State is not ("已关闭" or "已撤销")))
            throw new PdmRuleException("此主题仍有进行中的改善，暂不能停用。");
        var theme = new QualityTheme(id, title, Text(input.Description, 2000, "主题说明"), input.IsActive);
        return await SaveAsync(catalog with { Themes = catalog.Themes.Where(t => t.Id != id).Append(theme).ToArray() }, actor, "quality.theme.save", id, ct);
    }
    public async Task<QualityReviewCatalog> DeleteThemeAsync(Guid id, long expectedRowVersion, string actor, UserRole role, CancellationToken ct)
    {
        Id(id);
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.Manage, ct), expectedRowVersion, ct);
        var theme = catalog.Themes.FirstOrDefault(t => t.Id == id) ?? throw new PdmNotFoundException("共性问题主题不存在或已删除，请刷新后重试。");
        QualityReviewRules.RequireThemeUnreferenced(catalog, id);
        return await SaveAsync(catalog with { Themes = catalog.Themes.Where(t => t.Id != id).ToArray() }, actor, "quality.theme.delete", id, ct,
            $"已删除共性问题主题：{theme.Name}");
    }
    public async Task<QualityReviewCatalog> SaveIssueAsync(Guid projectId, Guid issueId, SaveQualityIssueProfile input,
        string actor, UserRole role, CancellationToken ct)
    {
        var view = await IssueAsync(projectId, issueId, actor, role, ct);
        var issue = view.Row.Issue;
        var actual = issue.RelatedProjectId ?? issue.ProjectId;
        var catalog = await LoadAsync(await CompanyAsync(actor, ct), input.ExpectedRowVersion, ct);
        var before = catalog.Profiles.FirstOrDefault(p => p.IssueId == issueId)
            ?? new QualityIssueProfile(issueId, "", "", "普通", null, null, null, null, "", "", "", "未验证", "", [], "", default);
        // Historical classification and event relationships are read-only. Do not clear them when a simplified
        // client omits those fields, or recreate the retired operations through an older API client.
        var next = new QualityIssueProfile(issueId, Text(input.Domain, 60, "问题类型"), before.Category, input.Severity,
            input.OccurredOn, input.ThemeId, before.DuplicateOfIssueId, before.RecurrenceOfIssueId,
            Text(input.ImmediateAction, 4000, "临时措施"), Text(input.RootCause, 4000, "根因分析"), Text(input.CorrectiveAction, 4000, "纠正措施"),
            input.VerificationResult, Text(input.EvidenceNote, 4000, "验证证据"), (input.EvidenceAttachmentIds ?? []).Distinct().ToArray(), actor, clock.GetUtcNow())
            { VerifiedBy = before.VerifiedBy, VerifiedAt = before.VerifiedAt, VerifiedIssueRowVersion = before.VerifiedIssueRowVersion,
                CorrectiveActionBy = before.CorrectiveActionBy };
        if (next.Severity is not ("普通" or "重大" or "严重") || next.VerificationResult is not ("未验证" or "通过" or "未通过"))
            throw new PdmRuleException("请选择有效的严重程度和验证结果。");
        if (next.Domain is not ("" or "质量" or "设计" or "采购" or "生产" or "进度" or "成本" or "其他")) throw new PdmRuleException("请选择列表中的问题类型。");
        if (before.Domain == "质量" && next.Domain != "质量") throw new PdmRuleException("已纳入质量闭环的问题不能通过更改类型退出闭环；错误记录请按权限作废并说明原因。");
        if (next.OccurredOn > Today) throw new PdmRuleException("实际发生日期不能晚于今天；日期不详请留空。");
        if (next.ThemeId is { } theme && !catalog.Themes.Any(t => t.Id == theme && t.IsActive)) throw new PdmRuleException("所选主题不存在或已停用。");
        var manageChanged = before.Domain != next.Domain || before.Severity != next.Severity
            || before.OccurredOn != next.OccurredOn || before.ThemeId != next.ThemeId || before.ImmediateAction != next.ImmediateAction
            || before.RootCause != next.RootCause || before.CorrectiveAction != next.CorrectiveAction;
        var verifyChanged = before.VerificationResult != next.VerificationResult || before.EvidenceNote != next.EvidenceNote
            || !before.EvidenceAttachmentIds.Order().SequenceEqual(next.EvidenceAttachmentIds.Order());
        verifyChanged |= !manageChanged && next.VerificationResult != "未验证" && before.VerifiedIssueRowVersion != issue.RowVersion;
        if (manageChanged && !await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueManage, ct)
            || verifyChanged && !await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueVerify, ct)
            || !manageChanged && !verifyChanged && !await ProjectCanAsync(actual, actor, QualityReviewPermissions.IssueManage, ct))
            throw new UnauthorizedAccessException("没有修改本次质量信息或验证结果的权限。");
        if (issue.Status == "已完成" && (before.CorrectiveAction != next.CorrectiveAction || before.RootCause != next.RootCause || verifyChanged))
            throw new PdmRuleException("已关闭问题的措施和验证结果需要先重开问题再修改。");
        if (next.EvidenceAttachmentIds.Any(id => !issue.Attachments.Any(a => a.Id == id))) throw new PdmRuleException("验证附件必须属于当前问题，请刷新附件后重试。");
        if (next.VerificationResult == "通过" && next.EvidenceNote.Length == 0 && next.EvidenceAttachmentIds.Count == 0)
            throw new PdmRuleException("通过验证时必须提供证据说明或问题附件。");
        if (before.CorrectiveAction != next.CorrectiveAction) next = next with { CorrectiveActionBy = actor };
        if (verifyChanged && next.VerificationResult == "通过" && next.Domain == "质量" && next.Severity is "重大" or "严重")
            RequireIndependentIssueVerifier(issue, next, actor);
        if (verifyChanged) next = next with { VerifiedBy = actor, VerifiedAt = clock.GetUtcNow(), VerifiedIssueRowVersion = issue.RowVersion };
        // Updating the remedy invalidates an earlier verification until an authorized verifier confirms it again.
        if (manageChanged && (before.CorrectiveAction != next.CorrectiveAction || before.RootCause != next.RootCause) && !verifyChanged)
            next = next with { VerificationResult = "未验证", VerifiedBy = null, VerifiedAt = null, VerifiedIssueRowVersion = null };
        return await SaveAsync(catalog with { Profiles = catalog.Profiles.Where(p => p.IssueId != issueId).Append(next).ToArray() }, actor, "quality.issue.save", issueId, ct);
    }
    public async Task RequireTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct)
    {
        var project = await RequireProjectAsync(issue.RelatedProjectId ?? issue.ProjectId, actor, null, ct);
        var profile = (await store.ReadAsync(project.OrganizationId!.Value, ct)).Profiles.FirstOrDefault(p => p.IssueId == issue.Id);
        if (profile?.Domain != "质量") return;
        if (!await CanTransitionAsync(issue, nextStatus, actor, ct)) throw new UnauthorizedAccessException("没有在该项目执行本次质量问题状态操作的权限。");
        if (nextStatus != "已完成" || issue.Status == "已完成") return;
        if (string.IsNullOrWhiteSpace(issue.Solution) || string.IsNullOrWhiteSpace(profile.CorrectiveAction)
            || profile.VerificationResult != "通过" || profile.VerifiedAt is null
            || string.IsNullOrWhiteSpace(profile.EvidenceNote) && profile.EvidenceAttachmentIds.Count == 0)
            throw new PdmRuleException("请先在质量闭环中填写解决措施、完成验证并提供证据，再确认关闭问题。");
        if (profile.VerifiedIssueRowVersion != issue.RowVersion) throw new PdmRuleException("问题内容在验证后发生变化或已重开，请重新验证当前问题后关闭。");
        if (profile.Severity is "重大" or "严重") RequireIndependentIssueVerifier(issue, profile, profile.VerifiedBy ?? "");
        if (profile.EvidenceAttachmentIds.Any(id => !issue.Attachments.Any(a => a.Id == id)))
            throw new PdmRuleException("验证使用的附件已被移除，请重新验证后关闭。");
        if ((profile.Severity is "重大" or "严重" || profile.RecurrenceOfIssueId.HasValue) && string.IsNullOrWhiteSpace(profile.RootCause))
            throw new PdmRuleException("重大或复发问题需填写根因分析后才能关闭。");
    }
    public async Task RequireSaveAsync(OplIssue original, OplIssue candidate, string actor, CancellationToken ct)
    {
        if (original.Status != "已完成" || candidate.Status != "已完成") return;
        var changed = original.Description != candidate.Description || original.Priority != candidate.Priority
            || original.Owner != candidate.Owner || original.DueDate != candidate.DueDate || original.Solution != candidate.Solution
            || original.RelatedProjectId != candidate.RelatedProjectId || original.ExpectedCompletionDate != candidate.ExpectedCompletionDate
            || !original.Attachments.OrderBy(a => a.Id).SequenceEqual(candidate.Attachments.OrderBy(a => a.Id));
        if (!changed) return;
        var project = await RequireProjectAsync(original.RelatedProjectId ?? original.ProjectId, actor, null, ct);
        var profile = (await store.ReadAsync(project.OrganizationId!.Value, ct)).Profiles.FirstOrDefault(p => p.IssueId == original.Id);
        if (profile?.Domain == "质量")
            throw new PdmRuleException("已关闭质量问题的描述、措施、责任及附件已冻结，请先重开问题，再修改并重新验证。");
    }
    private static void RequireIndependentIssueVerifier(OplIssue issue, QualityIssueProfile profile, string verifier)
    {
        if (string.IsNullOrWhiteSpace(verifier) || Same(verifier, issue.Owner) || Same(verifier, issue.CreatedBy) || Same(verifier, profile.CorrectiveActionBy))
            throw new PdmRuleException("重大或严重质量问题须由提交人、负责人及措施执行人之外的已授权人员独立验证。");
    }
    public async Task<bool> CanTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct)
    {
        var project = await RequireProjectAsync(issue.RelatedProjectId ?? issue.ProjectId, actor, null, ct);
        var profile = (await store.ReadAsync(project.OrganizationId!.Value, ct)).Profiles.FirstOrDefault(p => p.IssueId == issue.Id);
        if (profile?.Domain != "质量") return true;
        var code = nextStatus == "已完成" ? QualityReviewPermissions.IssueClose : nextStatus == "已作废" ? QualityReviewPermissions.IssueVoid
            : issue.Status is "已完成" or "已作废" ? QualityReviewPermissions.IssueReopen : QualityReviewPermissions.IssueManage;
        return await ProjectCanAsync(project.Id, actor, code, ct);
    }
}
