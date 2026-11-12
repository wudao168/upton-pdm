using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class OplQualityPermissionTests
{
    [Fact]
    public async Task NewCatalogEntriesStayVisibleButAreNeverGrantedByLegacyContentOrAdministratorDefaults()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await PermissionTestUsers.Ensure(repository, ("admin", UserRole.Administrator));
        var definitions = QualityReviewPermissions.Definitions().ToArray();
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var expanded = FunctionPermissionCatalog.Expand([PermissionCodes.ProjectView, PermissionCodes.ProjectContentView]);
        foreach (var definition in definitions)
        {
            Assert.Contains(RolePermissionCatalog.Permissions, p => p.Code == definition.Code && !p.Hidden);
            Assert.True(FunctionPermissionCatalog.RequiresExplicitGrant(definition.Code));
            Assert.Null(definition.LegacyPermission);
            Assert.DoesNotContain(definition.Code, expanded);
            Assert.DoesNotContain(settings.Templates.SelectMany(t => t.Grants), grant => grant.Permission == definition.Code);
            Assert.False(await AuthorizationPolicy.CanAsync(repository, project.Id, "admin", definition.Code, default));
        }
    }

    [Fact]
    public async Task AdoptionGrantRequiresSummaryViewAndRespectsRevocationAndProjectScope()
    {
        var f = await Fixture.Create();
        var child = await f.Repository.CreateSubprojectAsync(new(f.Project.Id, "经验采纳未授权子项目", null, 1), default);
        await f.Grant(f.Project.Id, QualityReviewPermissions.AdoptionConfirm);
        Assert.True(await AuthorizationPolicy.CanAsync(f.Repository, f.Project.Id, Fixture.Actor, QualityReviewPermissions.AdoptionConfirm, default));
        Assert.True(await AuthorizationPolicy.CanAsync(f.Repository, f.Project.Id, Fixture.Actor, QualityReviewPermissions.SummaryView, default));
        Assert.False(await AuthorizationPolicy.CanAsync(f.Repository, child.Id, Fixture.Actor, QualityReviewPermissions.AdoptionConfirm, default));
        await f.Grant(f.Project.Id, "project.opl.view");
        Assert.False(await AuthorizationPolicy.CanAsync(f.Repository, f.Project.Id, Fixture.Actor, QualityReviewPermissions.AdoptionConfirm, default));
        Assert.False(await AuthorizationPolicy.CanAsync(f.Repository, f.Project.Id, Fixture.Actor, QualityReviewPermissions.SummaryView, default));
    }

    [Fact]
    public async Task UnrelatedAuthorizedUserCanEditAndRevocationTakesEffectWithoutCreatorOrManagerBypass()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit");
        var issue = await f.Seed();
        Assert.NotEqual(Fixture.Actor, issue.CreatedBy);
        Assert.NotEqual(Fixture.Actor, issue.Owner);
        Assert.True(Assert.Single((await f.Service.GetAsync(f.Project.Id, Fixture.Actor, UserRole.Engineer, default, false)).Rows).CanEdit);
        var updated = await f.Service.SaveAsync(f.Project.Id, issue.Id, Input(issue, "待处理") with { Description = "已授权人员修改" }, [], Fixture.Actor, UserRole.Engineer, default);
        Assert.Equal("已授权人员修改", updated.Description);
        await f.Grant(f.Project.Id, "project.opl.view");
        Assert.False(Assert.Single((await f.Service.GetAsync(f.Project.Id, Fixture.Actor, UserRole.Administrator, default, false)).Rows).CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id, Input(updated, "待处理"), [], Fixture.Actor, UserRole.Administrator, default));
    }

    [Fact]
    public async Task CreatorCannotCompleteThroughEitherEndpointWithoutCompletePermission()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit");
        var issue = await f.Seed(creator: Fixture.Actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id, Input(issue, "已完成"), [], Fixture.Actor, UserRole.Administrator, default));
        Assert.False(Assert.Single((await f.Service.GetAsync(f.Project.Id, Fixture.Actor, UserRole.Administrator, default, false)).Rows).CanComplete);
        Assert.Empty(f.Gate.Calls);
        Assert.Equal("待处理", Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)).Status);
    }

    [Theory]
    [InlineData("待处理", "已完成")]
    [InlineData("待处理", "已作废")]
    [InlineData("已完成", "处理中")]
    [InlineData("已作废", "待处理")]
    public async Task SaveCannotBypassQualityGateAndDoesNotPersistRejectedState(string current, string next)
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed(status: current);
        f.Gate.Reject = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id, Input(issue, next), [], Fixture.Actor, UserRole.Engineer, default));
        var call = Assert.Single(f.Gate.Calls);
        Assert.Equal(current, call.Issue.Status);
        Assert.Equal(next, call.Next);
        Assert.Equal(current, Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)).Status);
        Assert.Equal(issue.RowVersion, Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)).RowVersion);
    }

    [Fact]
    public async Task EditingAClosedIssueStillInvokesSaveGateWithoutChangingItsState()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit");
        var issue = await f.Seed(status: "已完成");
        f.Gate.RejectSave = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id,
            Input(issue, "已完成") with { Description = "关闭后修改问题" }, [], Fixture.Actor, UserRole.Engineer, default));
        Assert.Single(f.Gate.SaveCalls);
        Assert.Empty(f.Gate.Calls);
        Assert.Equal(issue, Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)));
        var noGate = new ProjectOplService(f.Repository, f.Issues, new EmptyStorage(), TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => noGate.SaveAsync(f.Project.Id, issue.Id,
            Input(issue, "已完成"), [], Fixture.Actor, UserRole.Engineer, default));
    }

    [Fact]
    public async Task SaveCannotSkipReopeningAndCompleteAnAlreadyVoidedIssue()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed(status: "已作废");
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id, Input(issue, "已完成"), [], Fixture.Actor, UserRole.Engineer, default));
        Assert.Empty(f.Gate.Calls);
        Assert.Equal("已作废", Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)).Status);
    }

    [Fact]
    public async Task ClosingWhileEditingContentCannotReuseThePreviouslyVerifiedRevision()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed();
        f.Gate.VerifiedRevision = issue.RowVersion;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id,
            Input(issue, "已完成") with { Description = "验证后改动的问题内容" }, [], Fixture.Actor, UserRole.Engineer, default));
        Assert.Equal(issue.RowVersion + 1, Assert.Single(f.Gate.Calls).Issue.RowVersion);
        Assert.Equal("待处理", Assert.Single(await f.Issues.ListAsync(f.Project.Id, default)).Status);
        var done = await f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Engineer, default);
        Assert.Equal("已完成", done.Status);
    }

    [Fact]
    public async Task CompleteAndVisitorTerminalStateUseGateAndMissingDependencyFailsClosed()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed();
        f.Gate.Reject = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Engineer, default));
        var noGate = new ProjectOplService(f.Repository, f.Issues, new EmptyStorage(), TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => noGate.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => noGate.SaveAsync(f.Project.Id, issue.Id, Input(issue, "已完成"), [], Fixture.Actor, UserRole.Engineer, default));
        Assert.Single(f.Gate.Calls);
        TenantContext.Set(new(Guid.NewGuid(), f.Project.OrganizationId!.Value, f.Project.OrganizationId.Value, Fixture.Actor, "opl-test", false, new HashSet<string>()));
        try
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SubmitVisitorAsync(f.Project,
                new("访客不能绕过完成门禁", Status: "已完成"), [], Fixture.Actor, "测试人员", default));
            Assert.Equal(2, f.Gate.Calls.Count);
        }
        finally { TenantContext.Clear(); }
        // 普通问题的正式门禁允许完成时，旧 complete 授权继续有效，不额外授予质量权限。
        f.Gate.Reject = false;
        var done = await f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Engineer, default);
        Assert.Equal("已完成", done.Status);
        Assert.False(await AuthorizationPolicy.CanAsync(f.Repository, f.Project.Id, Fixture.Actor, QualityReviewPermissions.IssueClose, default));
    }

    [Fact]
    public async Task RootRouteCannotReadEditOrCompleteUnassignedChildIssue()
    {
        var f = await Fixture.Create();
        var child = await f.Repository.CreateSubprojectAsync(new(f.Project.Id, "仅子项目授权的质量问题", null, 1), default);
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed(related: child.Id);
        Assert.Empty((await f.Service.GetAsync(f.Project.Id, Fixture.Actor, UserRole.Engineer, default, false)).Rows);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Project.Id, issue.Id, Input(issue, "待处理"), [], Fixture.Actor, UserRole.Engineer, default));
        Assert.Empty(f.Gate.Calls);
    }

    [Fact]
    public async Task ProjectGrantDoesNotAuthorizeOtherCompanyContext()
    {
        var f = await Fixture.Create();
        await f.Grant(f.Project.Id, "project.opl.edit", "project.opl.complete");
        var issue = await f.Seed();
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), f.Project.OrganizationId!.Value, Fixture.Actor, "opl-test", false, new HashSet<string>()));
        try
        {
            var readError = await Record.ExceptionAsync(() => f.Service.GetAsync(f.Project.Id, Fixture.Actor, UserRole.Administrator, default, false));
            var writeError = await Record.ExceptionAsync(() => f.Service.CompleteAsync(f.Project.Id, issue.Id, issue.RowVersion, Fixture.Actor, UserRole.Administrator, default));
            Assert.True(readError is UnauthorizedAccessException or PdmNotFoundException);
            Assert.True(writeError is UnauthorizedAccessException or PdmNotFoundException);
            Assert.Empty(f.Gate.Calls);
        }
        finally { TenantContext.Clear(); }
    }

    private static OplInput Input(OplIssue issue, string status) => new(issue.Description, issue.Priority, status, issue.Owner,
        issue.DueDate, "已调整并复测", issue.RowVersion, [], issue.RelatedProjectId);

    private sealed class Fixture
    {
        public const string Actor = "opl-explicit-user";
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public InMemoryProjectOplRepository Issues { get; } = new();
        public RecordingGate Gate { get; } = new();
        public Project Project { get; private set; } = null!;
        public ProjectOplService Service => new(Repository, Issues, new EmptyStorage(), TimeProvider.System, qualityGate: Gate);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            var organization = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
            var customer = (await f.Repository.ListCustomersAsync(true, default))[0];
            f.Project = await f.Repository.CreateNumberedProjectAsync(new(organization, "P", 0, customer.Id, "OPL质量权限测试", null, null, 1, "admin", "D:/test", "D:/release"), default);
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), Actor, "质量权限测试人员", "unused", UserRole.Engineer, true, RoleCode: "opl-test", CompanyId: f.Project.OrganizationId), default);
            return f;
        }
        public async Task Grant(Guid projectId, params string[] operations)
        {
            var settings = await Repository.GetAuthorizationSettingsAsync(default);
            var codes = operations.Concat(new[] { "project.opl.view", ProjectCenterPermissions.Tab("opl"), ProjectCenterPermissions.Write }).ToHashSet();
            var queue = new Queue<string>(codes);
            while (queue.TryDequeue(out var code))
                foreach (var dependency in RolePermissionCatalog.Permissions.Single(p => p.Code == code).Dependencies)
                    if (codes.Add(dependency)) queue.Enqueue(dependency);
            var template = new AuthorizationTemplate("opl-test-grant", "质量问题测试授权", "", codes.Select(code => new AuthorizationGrant(code, ["company"])).ToArray(), ProjectAssignable: true);
            await Repository.SaveAuthorizationSettingsAsync(settings with
            {
                Templates = settings.Templates.Where(t => t.Key != template.Key).Append(template).ToArray(),
                Assignments = settings.Assignments.Where(a => a.Username != Actor).Append(new(Guid.NewGuid(), Actor, template.Key, Project.OrganizationId!.Value, "project", projectId)).ToArray()
            }, settings.Revision, default);
        }
        public Task<OplIssue> Seed(string status = "待处理", string creator = "原提交人", Guid? related = null) => Issues.SaveAsync(
            new(Guid.NewGuid(), Project.Id, 0, "紧固件松动", "高", status, "", null, "", creator, DateTimeOffset.UtcNow,
                status == "已完成" ? DateTimeOffset.UtcNow : null, creator, DateTimeOffset.UtcNow, 0, [], RelatedProjectId: related), 0, default);
    }
    private sealed class RecordingGate : IQualityIssueGate
    {
        public bool Reject { get; set; }
        public bool RejectSave { get; set; }
        public long? VerifiedRevision { get; set; }
        public List<(OplIssue Issue, string Next)> Calls { get; } = [];
        public List<(OplIssue Original, OplIssue Candidate)> SaveCalls { get; } = [];
        public Task<bool> CanTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct) => Task.FromResult(!Reject);
        public Task RequireSaveAsync(OplIssue original, OplIssue candidate, string actor, CancellationToken ct)
        {
            SaveCalls.Add((original, candidate));
            if (RejectSave) throw new PdmRuleException("请先重开问题再修改已关闭内容。");
            return Task.CompletedTask;
        }
        public Task RequireTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct)
        {
            Calls.Add((issue, nextStatus));
            if (Reject) throw new PdmRuleException("改善措施尚未通过有效性验证。");
            if (VerifiedRevision.HasValue && issue.RowVersion != VerifiedRevision.Value) throw new PdmRuleException("问题内容已变化，请重新验证。");
            return Task.CompletedTask;
        }
    }
    private sealed class EmptyStorage : IProjectOplStorage
    {
        public Task WriteAsync(string root, Guid project, Guid id, byte[] content, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream> OpenAsync(string root, Guid project, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Delete(string root, Guid project, Guid id) { }
    }
}
