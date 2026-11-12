using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class QualityReviewTests
{
    [Fact]
    public async Task StoreIsCompanyIsolatedOptimisticAndSnapshotsAreCopied()
    {
        var store = new InMemoryQualityReviewStore();
        var company = Guid.NewGuid();
        var empty = await store.ReadAsync(company, default);
        var themes = new List<QualityTheme> { new(Guid.NewGuid(), "装配防错", "", true) };
        Assert.True(await store.SaveAsync(empty with { Themes = themes }, 0, default));
        themes.Clear();
        Assert.Single((await store.ReadAsync(company, default)).Themes);
        Assert.False(await store.SaveAsync(empty, 0, default));
        Assert.Empty((await store.ReadAsync(Guid.NewGuid(), default)).Themes);
    }

    [Fact]
    public void FrequencyCountsRealEventsAndRootProjectsWithoutInventingUnknownDates()
    {
        var today = new DateOnly(2026, 11, 11);
        var theme = new QualityTheme(Guid.NewGuid(), "紧固失效", "", true);
        var root = Guid.NewGuid(); var other = Guid.NewGuid();
        var first = Row(root, theme.Id, today.AddDays(-1));
        var reopened = first with { Issue = first.Issue with { Status = "处理中" } };
        var duplicate = Row(root, theme.Id, today) with { Profile = Profile(Guid.NewGuid(), theme.Id, today) with { DuplicateOfIssueId = first.Issue.Id } };
        var second = Row(other, theme.Id, today.AddDays(-2));
        var unknown = Row(root, theme.Id, null);
        var old = Row(root, theme.Id, today.AddDays(-95));
        var voided = Row(root, theme.Id, today); voided = voided with { Issue = voided.Issue with { Status = "已作废" } };
        var catalog = Empty() with { Themes = [theme] };
        var metric = Assert.Single(QualityReviewRules.Metrics(catalog, [first, reopened, duplicate, second, unknown, old, voided], today));
        Assert.Equal(2, metric.EventCount); Assert.Equal(2, metric.ProjectCount); Assert.Equal(1, metric.UnknownDateCount); Assert.True(metric.IsCandidate);
        var major = unknown with { Profile = unknown.Profile! with { Severity = "重大" } };
        Assert.True(Assert.Single(QualityReviewRules.Metrics(catalog, [major], today)).IsCandidate);
    }

    [Fact]
    public async Task CompanySummaryHidesRawIssuesAndRevocationAndCrossCompanyTakeEffect()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Administrator, default));
            await f.Grant(Fixture.Actor, QualityReviewPermissions.View);
            var issue = await f.Issue();
            var catalog = await f.Store.ReadAsync(f.Company, default);
            await f.Store.SaveAsync(catalog with { Profiles = [Profile(issue.Id, null, null)] }, catalog.RowVersion, default);
            var view = await f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Engineer, default);
            Assert.Empty(view.Issues); Assert.Empty(view.Catalog.Profiles); Assert.Empty(view.Catalog.Summaries);
            await f.Grant(Fixture.Actor, QualityReviewPermissions.Detail);
            Assert.Single((await f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Engineer, default)).Issues);
            await f.Grant(Fixture.Actor);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Administrator, default));
            f.Act(company: Guid.NewGuid());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Engineer, default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task QualityClosureRequiresVerificationForCurrentIssueVersionAndCannotDowngrade()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.GrantAll(Fixture.Actor);
            await f.GrantAll(Fixture.Reviewer);
            var issue = await f.Issue();
            await f.Service.RequireTransitionAsync(issue, "已完成", Fixture.Actor, default); // Ordinary legacy OPL remains usable.
            var input = new SaveQualityIssueProfile(0, "质量", "装配", "重大", f.Today, null, null, null,
                "暂停使用", "扭矩要求缺失", "增加扭矩检查", "未验证", "", []);
            var catalog = await f.Service.SaveIssueAsync(f.Project.Id, issue.Id, input, Fixture.Actor, UserRole.Engineer, default);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RequireTransitionAsync(issue, "已完成", Fixture.Actor, default));
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveIssueAsync(f.Project.Id, issue.Id,
                input with { ExpectedRowVersion = catalog.RowVersion, Domain = "进度" }, Fixture.Actor, UserRole.Engineer, default));
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveIssueAsync(f.Project.Id, issue.Id, input with { ExpectedRowVersion = catalog.RowVersion,
                VerificationResult = "通过", EvidenceNote = "本人验证" }, Fixture.Actor, UserRole.Engineer, default));
            f.Act(Fixture.Reviewer);
            catalog = await f.Service.SaveIssueAsync(f.Project.Id, issue.Id, input with { ExpectedRowVersion = catalog.RowVersion,
                VerificationResult = "通过", EvidenceNote = "已复测扭矩并留存检验数据" }, Fixture.Reviewer, UserRole.Engineer, default);
            f.Act();
            await f.Service.RequireTransitionAsync(issue, "已完成", Fixture.Actor, default);
            var updated = await f.Issues.SaveAsync(issue with { Solution = "调整了措施", Status = "处理中" }, issue.RowVersion, default);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RequireTransitionAsync(updated, "已完成", Fixture.Actor, default));
            await f.Grant(Fixture.Actor, QualityReviewPermissions.IssueView);
            Assert.False(await f.Service.CanTransitionAsync(updated, "已完成", Fixture.Actor, default));
            var other = await f.NewProject();
            await Assert.ThrowsAsync<PdmNotFoundException>(() => f.Service.IssueAsync(other.Id, issue.Id, Fixture.Actor, UserRole.Engineer, default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task ImprovementRequiresSamplesTimeIndependentVerifierAndPreservesClosedCycle()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.GrantAll(Fixture.Actor); await f.GrantAll(Fixture.Reviewer);
            var themeId = Guid.NewGuid(); var id = Guid.NewGuid(); var actionId = Guid.NewGuid();
            var catalog = await f.Service.SaveThemeAsync(themeId, new(0, "防松", "紧固件设计与装配防错", true), Fixture.Actor, UserRole.Engineer, default);
            var input = new SaveQualityImprovement(catalog.RowVersion, themeId, "增加防松标准", Fixture.Actor, "规范未包含扭矩复核", "后续适用项目无同类失效", f.Today.AddDays(10), 2, 2, 0,
                [new(actionId, "发布扭矩规范并培训", Fixture.Actor, f.Today.AddDays(1), false, "")]);
            catalog = await f.Service.SaveImprovementAsync(id, input, Fixture.Actor, UserRole.Engineer, default);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveImprovementAsync(Guid.NewGuid(), input with { ExpectedRowVersion = catalog.RowVersion }, Fixture.Actor, UserRole.Engineer, default));
            catalog = await f.Transition(id, catalog, "approve");
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveImprovementAsync(id, input with { ExpectedRowVersion = catalog.RowVersion, Goal = "放宽目标" }, Fixture.Actor, UserRole.Engineer, default));
            catalog = await f.Service.SaveImprovementAsync(id, input with { ExpectedRowVersion = catalog.RowVersion,
                Actions = [new(actionId, "发布扭矩规范并培训", Fixture.Actor, f.Today.AddDays(1), true, "培训签到与规范发布记录")] }, Fixture.Actor, UserRole.Engineer, default);
            catalog = await f.Transition(id, catalog, "observe");
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Transition(id, catalog, "submit-verification"));
            var applied = f.Today;
            f.Clock.Advance(TimeSpan.FromDays(3));
            var second = await f.NewProject();
            catalog = await f.Service.SaveObservationAsync(id, new(catalog.RowVersion, f.Project.Id, applied, f.Today, true, "新规范已实施并完成验证"), Fixture.Actor, UserRole.Engineer, default);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Transition(id, catalog, "submit-verification"));
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveObservationAsync(id, new(catalog.RowVersion, f.Project.Id, applied, f.Today, true, "重复样本"), Fixture.Actor, UserRole.Engineer, default));
            catalog = await f.Service.SaveObservationAsync(id, new(catalog.RowVersion, second.Id, applied, f.Today, true, "第二个适用项目实测通过"), Fixture.Actor, UserRole.Engineer, default);
            catalog = await f.Transition(id, catalog, "submit-verification");
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Transition(id, catalog, "verify", "已查证"));
            f.Act(Fixture.Reviewer);
            catalog = await f.Service.TransitionImprovementAsync(id, new(catalog.RowVersion, "verify", "独立检查两个项目证据通过"), Fixture.Reviewer, UserRole.Engineer, default);
            catalog = await f.Service.TransitionImprovementAsync(id, new(catalog.RowVersion, "close", "达到既定验证标准"), Fixture.Reviewer, UserRole.Engineer, default);
            Assert.Equal("已关闭", Assert.Single(catalog.Improvements).State);
            f.Act();
            catalog = await f.Transition(id, catalog, "reopen", "后续项目复发，重新分析");
            var reopened = Assert.Single(catalog.Improvements);
            Assert.Equal("草稿", reopened.State); Assert.Null(reopened.ClosedAt); Assert.Empty(reopened.Observations);
            var cycle = Assert.Single(reopened.PreviousCycles);
            Assert.Equal("已关闭", cycle.State); Assert.Equal(2, cycle.Observations.Count); Assert.NotNull(cycle.ClosedAt);
            Assert.Equal(Fixture.Reviewer, cycle.VerifiedBy);
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task PublishedSummaryFreezesIssuesWhileLiveBacklogAndNewRevisionRemainIndependent()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.GrantAll(Fixture.Actor);
            var issue = await f.Issue(); var id = Guid.NewGuid();
            var input = new SaveProjectRetrospective(0, "阶段总结", "设计阶段复盘", "主要工作完成", "优化防松检查", "遗留问题由负责人本周完成")
            { CoverageFrom = f.Today.AddDays(-10), CoverageTo = f.Today, TargetResults = "进度正常、预算待结算、质量遗留一项" };
            var catalog = await f.Service.SaveSummaryAsync(f.Project.Id, id, input, Fixture.Actor, UserRole.Engineer, default);
            foreach (var action in new[] { "submit", "review", "publish" }) catalog = await f.Service.TransitionSummaryAsync(f.Project.Id, id, new(catalog.RowVersion, action), Fixture.Actor, UserRole.Engineer, default);
            await f.Issues.SaveAsync(issue with { Description = "后来补充的详情", Status = "已完成" }, issue.RowVersion, default);
            var view = await f.Service.ProjectAsync(f.Project.Id, Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal("处理中", Assert.Single(Assert.Single(view.Summaries).Issues).Status);
            Assert.Equal("已完成", Assert.Single(view.Issues).Issue.Status);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveSummaryAsync(f.Project.Id, id, input with { ExpectedRowVersion = catalog.RowVersion }, Fixture.Actor, UserRole.Engineer, default));
            catalog = await f.Service.TransitionSummaryAsync(f.Project.Id, id, new(catalog.RowVersion, "revise"), Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal(2, catalog.Summaries.Count); Assert.Equal("已发布", catalog.Summaries.Single(s => s.Id == id).State);
            await f.Grant(Fixture.Actor, QualityReviewPermissions.SummaryView);
            view = await f.Service.ProjectAsync(f.Project.Id, Fixture.Actor, UserRole.Engineer, default);
            Assert.Empty(view.Issues); Assert.NotEmpty(view.Summaries.Single(s => s.Id == id).Issues);
        }
        finally { TenantContext.Clear(); }
    }

    private static QualityReviewCatalog Empty() => new(Guid.NewGuid(), 0, [], [], [], [], [], []);
    [Fact]
    public async Task ClosedQualityOplRejectsChangedSolutionAndRemovedEvidenceButAllowsUnchangedSave()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.GrantAll(Fixture.Actor);
            var issue = await f.Issue();
            var attachment = new OplAttachment(Guid.NewGuid(), "检验证据.pdf", 10, f.Project.Id, "D:/test");
            issue = await f.Issues.SaveAsync(issue with { Status = "已完成", Attachments = [attachment] }, issue.RowVersion, default);
            var catalog = await f.Store.ReadAsync(f.Company, default);
            await f.Store.SaveAsync(catalog with { Profiles = [Profile(issue.Id, null, f.Today) with { EvidenceAttachmentIds = [attachment.Id], VerificationResult = "通过" }] }, 0, default);
            var opl = new ProjectOplService(f.Repository, f.Issues, new EmptyStorage(), f.Clock, qualityGate: f.Service);
            OplInput Input(string solution, IReadOnlyList<Guid> attachments) => new(issue.Description, issue.Priority, issue.Status, issue.Owner,
                issue.DueDate, solution, issue.RowVersion, attachments, issue.RelatedProjectId);
            var solutionError = await Assert.ThrowsAsync<PdmRuleException>(() => opl.SaveAsync(f.Project.Id, issue.Id,
                Input("覆盖已经验证的解决措施", [attachment.Id]), [], Fixture.Actor, UserRole.Engineer, default));
            Assert.Contains("重开", solutionError.Message);
            var evidenceError = await Assert.ThrowsAsync<PdmRuleException>(() => opl.SaveAsync(f.Project.Id, issue.Id,
                Input(issue.Solution, []), [], Fixture.Actor, UserRole.Engineer, default));
            Assert.Contains("重开", evidenceError.Message);
            var saved = await opl.SaveAsync(f.Project.Id, issue.Id, Input(issue.Solution, [attachment.Id]), [], Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal("已完成", saved.Status); Assert.Equal(issue.Solution, saved.Solution); Assert.Single(saved.Attachments);
            var ordinary = await f.Issue();
            ordinary = await f.Issues.SaveAsync(ordinary with { Status = "已完成" }, ordinary.RowVersion, default);
            var ordinaryInput = new OplInput(ordinary.Description, ordinary.Priority, "已完成", ordinary.Owner, ordinary.DueDate,
                "普通问题保留原修改流程", ordinary.RowVersion, []);
            var ordinarySaved = await opl.SaveAsync(f.Project.Id, ordinary.Id, ordinaryInput, [], Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal("普通问题保留原修改流程", ordinarySaved.Solution);
        }
        finally { TenantContext.Clear(); }
    }

    private sealed class EmptyStorage : IProjectOplStorage
    {
        public Task WriteAsync(string root, Guid project, Guid id, byte[] content, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream> OpenAsync(string root, Guid project, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Delete(string root, Guid project, Guid id) { }
    }
    [Fact]
    public async Task RecurrenceCanLinkAuthorizedOtherProjectAndInheritsTheme()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.GrantAll(Fixture.Actor);
            var theme = Guid.NewGuid();
            var catalog = await f.Service.SaveThemeAsync(theme, new(0, "重复失效", "", true), Fixture.Actor, UserRole.Engineer, default);
            var original = await f.Issue();
            original = await f.Issues.SaveAsync(original with { Status = "已完成" }, original.RowVersion, default);
            await f.Store.SaveAsync(catalog with { Profiles = [Profile(original.Id, theme, f.Today.AddDays(-3))] }, catalog.RowVersion, default);
            catalog = await f.Store.ReadAsync(f.Company, default);
            var second = await f.NewProject();
            var recurrence = await f.Issues.SaveAsync(original with { Id = Guid.NewGuid(), ProjectId = second.Id, Status = "处理中", RowVersion = 0 }, 0, default);
            var panel = await f.Service.IssueAsync(second.Id, recurrence.Id, Fixture.Actor, UserRole.Engineer, default);
            Assert.Contains(panel.RelatedIssues, r => r.Issue.Id == original.Id);
            catalog = await f.Service.SaveIssueAsync(second.Id, recurrence.Id, new(catalog.RowVersion, "质量", "装配", "普通", f.Today,
                null, null, original.Id, "", "", "", "未验证", "", []), Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal(theme, catalog.Profiles.Single(p => p.IssueId == recurrence.Id).ThemeId);
            var settings = await f.Repository.GetAuthorizationSettingsAsync(default);
            await f.Repository.SaveAuthorizationSettingsAsync(settings with { Assignments = settings.Assignments.Select(a => a.Username == Fixture.Actor
                ? a with { Scope = "project", TargetId = second.Id } : a).ToArray() }, settings.Revision, default);
            panel = await f.Service.IssueAsync(second.Id, recurrence.Id, Fixture.Actor, UserRole.Engineer, default);
            Assert.DoesNotContain(panel.RelatedIssues, r => r.Issue.Id == original.Id);
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task ExperienceAdoptionPreservesEvidenceAndNotificationRequiresCompanyAndCurrentPermission()
    {
        var f = await Fixture.Create(); f.Act();
        try
        {
            await f.Grant(Fixture.Actor, QualityReviewPermissions.Definitions().Select(p => p.Code).Append(AuthorizationActions.BusinessNotifications).ToArray());
            var improvement = new QualityImprovement(Guid.NewGuid(), Guid.NewGuid(), "已验证改善", "已关闭", Fixture.Actor,
                "已找到根因", "达标", f.Today, 1, 1, 0, [], [], f.Clock.GetUtcNow().AddDays(-2), Fixture.Actor,
                f.Clock.GetUtcNow().AddDays(-3), [], Fixture.Reviewer, f.Clock.GetUtcNow());
            var catalog = await f.Store.ReadAsync(f.Company, default);
            await f.Store.SaveAsync(catalog with { Improvements = [improvement] }, 0, default);
            var experienceId = Guid.NewGuid();
            catalog = await f.Service.PublishExperienceAsync(experienceId, new(1, improvement.Id, "防松检查", "检查扭矩", "同类装配项目") { ProjectTags = ["质量闭环"] }, Fixture.Actor, UserRole.Engineer, default);
            var workspace = await f.Service.WorkspaceAsync(Fixture.Actor, UserRole.Engineer, default);
            Assert.Contains(f.Project.Id, Assert.Single(workspace.Catalog.Experiences).RecommendedProjectIds);
            catalog = await f.Service.AssignAdoptionAsync(new(catalog.RowVersion, experienceId, [f.Project.Id], Fixture.Actor, f.Today.AddDays(2)), Fixture.Actor, UserRole.Engineer, default);
            var adoption = Assert.Single(catalog.Adoptions);
            var list = await f.Repository.ListUserNotificationsAsync(Fixture.Actor, 100, default);
            var notification = Assert.Single(list, n => n.Category == QualityReviewService.NotificationCategory);
            Assert.Single(await f.Service.FilterNotificationsAsync([notification], Fixture.Actor, default));
            Assert.Empty(await f.Service.FilterNotificationsAsync([notification with { SourceKey = "quality-review:" + Guid.NewGuid().ToString("N") + ":adoption:" + adoption.Id.ToString("N") }], Fixture.Actor, default));
            catalog = await f.Service.ConfirmAdoptionAsync(adoption.Id, new(catalog.RowVersion, "不适用", "本项目采用全焊接结构，无螺栓连接"), Fixture.Actor, UserRole.Engineer, default);
            Assert.Equal("不适用", Assert.Single(catalog.Adoptions).State);
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ConfirmAdoptionAsync(adoption.Id, new(catalog.RowVersion, "已采用", "覆盖旧证据"), Fixture.Actor, UserRole.Engineer, default));
            await f.Grant(Fixture.Actor, QualityReviewPermissions.View, AuthorizationActions.BusinessNotifications);
            Assert.Empty(await f.Service.FilterNotificationsAsync([notification], Fixture.Actor, default));
        }
        finally { TenantContext.Clear(); }
    }
    private static QualityIssueProfile Profile(Guid issue, Guid? theme, DateOnly? day) => new(issue, "质量", "装配", "普通", day, theme, null, null, "", "", "", "未验证", "", [], "staff", DateTimeOffset.UtcNow);
    private static QualityIssueRow Row(Guid root, Guid theme, DateOnly? day)
    {
        var issue = new OplIssue(Guid.NewGuid(), root, 1, "紧固件松动", "普通", "处理中", "staff", null, "", "staff", DateTimeOffset.UtcNow, null, "staff", DateTimeOffset.UtcNow, 1, []);
        return new(issue, Profile(issue.Id, theme, day), root, "P001", "测试项目", "张三", "李四", true);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan span) => now += span;
    }
    private sealed class Fixture
    {
        public const string Actor = "quality-author";
        public const string Reviewer = "quality-reviewer";
        public TestClock Clock { get; } = new();
        public InMemoryPdmRepository Repository { get; }
        public InMemoryProjectOplRepository Issues { get; } = new();
        public InMemoryQualityReviewStore Store { get; } = new();
        public Project Project { get; private set; } = null!;
        public Guid Company => Project.OrganizationId!.Value;
        public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).Date);
        public QualityReviewService Service => new(Repository, Issues, Store, Clock);
        private Fixture() => Repository = new(Clock);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Project = await f.NewProject();
            foreach (var name in new[] { Actor, Reviewer }) await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, name == Actor ? "质量负责人" : "独立验证人", "unused", UserRole.Engineer, true, RoleCode: "quality-test", CompanyId: f.Company), default);
            return f;
        }
        public void Act(string actor = Actor, Guid? company = null) => TenantContext.Set(new(Guid.NewGuid(), company ?? Company, Company, actor, "quality-test", false, new HashSet<string>()));
        public async Task<Project> NewProject()
        {
            var company = (await Repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
            var customer = (await Repository.ListCustomersAsync(true, default))[0];
            return await Repository.CreateNumberedProjectAsync(new(company, "P", 0, customer.Id, "质量闭环验证项目", null, null, 1, "admin", "D:/test", "D:/release"), default);
        }
        public Task GrantAll(string actor) => Grant(actor, QualityReviewPermissions.Definitions().Select(p => p.Code).ToArray());
        public async Task Grant(string actor, params string[] permissions)
        {
            var codes = permissions.ToHashSet();
            var queue = new Queue<string>(codes);
            while (queue.TryDequeue(out var code)) foreach (var dep in RolePermissionCatalog.Permissions.Single(p => p.Code == code).Dependencies) if (codes.Add(dep)) queue.Enqueue(dep);
            var settings = await Repository.GetAuthorizationSettingsAsync(default);
            var template = new AuthorizationTemplate("quality-test-" + actor, "测试明确授权", "", codes.Select(c => new AuthorizationGrant(c, ["company"])).ToArray());
            await Repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Where(t => t.Key != template.Key).Append(template).ToArray(),
                Assignments = settings.Assignments.Where(a => a.Username != actor).Append(new(Guid.NewGuid(), actor, template.Key, Company, "company")).ToArray() }, settings.Revision, default);
        }
        public Task<OplIssue> Issue() => Issues.SaveAsync(new(Guid.NewGuid(), Project.Id, 0, "紧固失效", "高", "处理中", Actor, null, "补充扭矩检查", Actor, Clock.GetUtcNow(), null, Actor, Clock.GetUtcNow(), 0, []), 0, default);
        public Task<QualityReviewCatalog> Transition(Guid id, QualityReviewCatalog c, string action, string note = "") => Service.TransitionImprovementAsync(id, new(c.RowVersion, action, note), Actor, UserRole.Engineer, default);
    }
}
