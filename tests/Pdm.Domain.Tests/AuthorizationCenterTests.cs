using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class AuthorizationCenterTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static UserAccount User(string role = "custom-designer") => new(Guid.NewGuid(), "worker", "测试人员", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: Company);
    private static Project Project(Guid? parent = null) => new(Guid.NewGuid(), "TEST", "权限验证", "owner", "vault", "release", true) { OrganizationId = Company, ParentProjectId = parent, RootProjectId = parent };
    private static readonly OrganizationDirectory Directory = new([], [], [], [], []);
    private static AuthorizationTemplate Template(string key, params string[] actions) => new(key, key, "", actions.Select(a => new AuthorizationGrant(a, ["company"])).ToArray());
    private static AuthorizationSettings Settings(params AuthorizationTemplate[] templates) => new(1, templates, [], ProjectPermissionPositions.BuiltIn, DateTimeOffset.UtcNow);

    [Fact]
    public void CustomTemplateGrantsProjectOperationWithoutSecondPositionCheckbox()
    {
        var user = User(); var project = Project();
        var template = Template("design", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "bom.mechanical.row.view", "bom.mechanical.row.update");
        var settings = Settings(template) with { Assignments = [new(Guid.NewGuid(), user.Username, template.Key, Company, "project", project.Id)] };
        Assert.True(AuthorizationPolicy.Decide(settings, new(user, project, project, Directory), "bom.mechanical.row.update").Allowed);
    }

    [Fact]
    public void ProjectManagerGrantDoesNotLeakIntoAnotherProjectsReadOnlyAssignment()
    {
        var user = User(); var a = Project(); var b = Project();
        var edit = Template("edit", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "bom.mechanical.row.view", "bom.mechanical.row.update");
        var read = Template("read", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "bom.mechanical.row.view");
        var settings = Settings(edit, read) with { Assignments = [new(Guid.NewGuid(), user.Username, edit.Key, Company, "project", a.Id), new(Guid.NewGuid(), user.Username, read.Key, Company, "project", b.Id)] };
        Assert.True(AuthorizationPolicy.Decide(settings, new(user, a, a, Directory), "bom.mechanical.row.update").Allowed);
        Assert.False(AuthorizationPolicy.Decide(settings, new(user, b, b, Directory), "bom.mechanical.row.update").Allowed);
        Assert.True(AuthorizationPolicy.Decide(settings, new(user, b, b, Directory), "bom.mechanical.row.view").Allowed);
    }

    [Fact]
    public void AdministratorAndDeveloperDoNotBypassRevocation()
    {
        var project = Project();
        foreach (var role in new[] { "Administrator", "developer" })
        {
            var user = User(role) with { Role = UserRole.Administrator };
            Assert.False(AuthorizationPolicy.Decide(Settings(), new(user, project, project, Directory), PermissionCodes.DocumentRecycle).Allowed);
            Assert.False(AuthorizationPolicy.GlobalAllows(Settings(), user, Company, PermissionCodes.RoleSettingsEdit));
        }
    }

    [Fact]
    public void DisabledTemplateAndUserLoseAccessAndSourcesExplainRemainingGrant()
    {
        var project = Project(); var user = User();
        var first = Template("first", PermissionCodes.ProjectView) with { RoleCode = user.EffectiveRoleCode, IsActive = false };
        var second = Template("second", PermissionCodes.ProjectView) with { RoleCode = user.EffectiveRoleCode };
        var settings = Settings(first, second);
        var allowed = AuthorizationPolicy.Decide(settings, new(user, project, project, Directory), PermissionCodes.ProjectView);
        Assert.True(allowed.Allowed); Assert.Single(allowed.Sources); Assert.Contains("second", allowed.Sources[0]);
        Assert.False(AuthorizationPolicy.Decide(settings, new(user with { IsActive = false }, project, project, Directory), PermissionCodes.ProjectView).Allowed);
    }

    [Fact]
    public void SubtreeGrantCannotCrossCompanyOrReachSiblingOutsideItsRoot()
    {
        var user = User(); var root = Project(); var child = Project(root.Id); var other = Project();
        var template = Template("read", PermissionCodes.ProjectView);
        var settings = Settings(template) with { Assignments = [new(Guid.NewGuid(), user.Username, template.Key, Company, "subtree", root.Id)] };
        Assert.True(AuthorizationPolicy.Decide(settings, new(user, child, root, Directory), PermissionCodes.ProjectView).Allowed);
        Assert.False(AuthorizationPolicy.Decide(settings, new(user, other, other, Directory), PermissionCodes.ProjectView).Allowed);
        Assert.False(AuthorizationPolicy.Decide(settings, new(user, child with { OrganizationId = Guid.NewGuid() }, root, Directory), PermissionCodes.ProjectView).Allowed);
    }

    [Fact]
    public void MissingDependencyCannotBeBorrowedFromDifferentProject()
    {
        var user = User(); var a = Project(); var b = Project();
        var write = Template("write", "bom.mechanical.row.update");
        var read = Template("read", PermissionCodes.ProjectContentView, "bom.mechanical.row.view", PermissionCodes.ProjectView);
        var settings = Settings(write, read) with { Assignments = [new(Guid.NewGuid(), user.Username, write.Key, Company, "project", a.Id), new(Guid.NewGuid(), user.Username, read.Key, Company, "project", b.Id)] };
        var decision = AuthorizationPolicy.Decide(settings, new(user, a, a, Directory), "bom.mechanical.row.update");
        Assert.False(decision.Allowed); Assert.Contains("前置", decision.Reason);
    }

    [Fact]
    public async Task SavingConfigurationUsesRevisionAndDoesNotReapplyDefaultGrants()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var original = await repository.GetAuthorizationSettingsAsync(default);
        var removed = original with { Templates = original.Templates.Select(t => t with { Grants = t.Grants.Where(g => g.Permission != PermissionCodes.DocumentRecycle).ToArray() }).ToArray() };
        var saved = await repository.SaveAuthorizationSettingsAsync(removed, original.Revision, default);
        Assert.Equal(original.Revision + 1, saved.Revision);
        Assert.DoesNotContain((await repository.GetAuthorizationSettingsAsync(default)).Templates.SelectMany(t => t.Grants), g => g.Permission == PermissionCodes.DocumentRecycle);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.SaveAuthorizationSettingsAsync(original, original.Revision, default));
    }

    [Fact]
    public async Task MigrationCreatesOnlyRegisteredGrantsAndSeparatesProjectTemplates()
    {
        var repo = new InMemoryPdmRepository(TimeProvider.System);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        foreach (var grant in settings.Templates.SelectMany(t => t.Grants))
            Assert.Contains(RolePermissionCatalog.Permissions, p => p.Code == grant.Permission && !p.Hidden);
        var reader = Assert.Single(settings.Templates, t => t.Key == "project-reader");
        Assert.True(reader.ProjectAssignable);
        Assert.All(reader.Grants, g => Assert.Equal("project", RolePermissionCatalog.Permissions.Single(p => p.Code == g.Permission).Scope));
        Assert.DoesNotContain(reader.Grants, g => g.Permission == ProjectCenterPermissions.Download);
    }

    [Fact]
    public async Task AdministratorBusinessInboxFollowsVisibleConfiguration()
    {
        var repo = new InMemoryPdmRepository(TimeProvider.System);
        var user = User("Administrator") with { Username = "configured-admin", Role = UserRole.Administrator };
        await repo.CreateUserAsync(user, default);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        Assert.False(await NotificationRecipientPolicy.CanReceiveAsync(repo, user.Username, default));
        var receive = Template("notifications", AuthorizationActions.BusinessNotifications);
        await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Append(receive).ToArray(), Assignments = [new(Guid.NewGuid(), user.Username, receive.Key, Company, "company")] }, settings.Revision, default);
        Assert.True(await NotificationRecipientPolicy.CanReceiveAsync(repo, user.Username, default));
        Assert.False(await repo.HasUserPermissionAsync("nonexistent", UserRole.Administrator, PermissionCodes.RoleSettingsEdit, default));
    }

    [Fact]
    public void GlobalOperationRequiresDependenciesAndCompanyAssignment()
    {
        var user = User();
        var template = Template("settings", PermissionCodes.RoleSettingsEdit);
        var settings = Settings(template) with { Assignments = [new(Guid.NewGuid(), user.Username, template.Key, Company, "project", Guid.NewGuid())] };
        Assert.False(AuthorizationPolicy.GlobalAllows(settings, user, Company, PermissionCodes.RoleSettingsEdit));
        settings = settings with { Assignments = [settings.Assignments[0] with { Scope = "company", TargetId = null }] };
        Assert.False(AuthorizationPolicy.GlobalAllows(settings, user, Company, PermissionCodes.RoleSettingsEdit));
        settings = settings with { Templates = [Template("settings", PermissionCodes.RoleSettingsEdit, PermissionCodes.RoleSettingsView)] };
        Assert.True(AuthorizationPolicy.GlobalAllows(settings, user, Company, PermissionCodes.RoleSettingsEdit));
    }
}
