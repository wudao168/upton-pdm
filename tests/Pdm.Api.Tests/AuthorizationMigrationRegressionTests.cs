using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class AuthorizationMigrationRegressionTests
{
    // Both repository directory formats have already expanded legacy input before migration.
    private static AuthorizationSettings Migrate(IEnumerable<string> stored, bool includeCompatibilityAliases)
    {
        var resolved = includeCompatibilityAliases
            ? FunctionPermissionCatalog.Effective(stored)
            : FunctionPermissionCatalog.Expand(stored).Where(code => !RolePermissionCatalog.Permissions.Single(p => p.Code == code).Hidden).ToHashSet();
        var role = new RolePermissionSettings("migration-test", "迁移测试", "", UserRole.Engineer, false, false, resolved.ToArray(), 0);
        return AuthorizationMigration.Create(new(RolePermissionCatalog.Permissions, [role]), ProjectPermissionSettings.Default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvedReadOnlyDirectoryDoesNotRegainHiddenTabsOrWriteOperations(bool aliases)
    {
        var role = Migrate(ProjectCenterPermissions.AssemblyReadOnly(), aliases).Templates.Single(t => t.RoleCode == "migration-test");
        var permissions = role.Grants.Select(g => g.Permission).ToHashSet();
        Assert.Equal(8, permissions.Count(code => code.StartsWith("project.tab.")));
        Assert.Contains("document.view", permissions);
        foreach (var code in new[] { ProjectCenterPermissions.Write, ProjectCenterPermissions.Download, ProjectCenterPermissions.Export, ProjectCenterPermissions.Print,
            ProjectCenterPermissions.Tab("information"), ProjectCenterPermissions.Tab("files"), ProjectCenterPermissions.Tab("budget"), ProjectCenterPermissions.Tab("records") })
            Assert.DoesNotContain(code, permissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GranularCheckoutDoesNotRestoreRevokedArchiveRegisterOrRestore(bool aliases)
    {
        var settings = Migrate([FunctionPermissionCatalog.SchemaMarker, ProjectCenterPermissions.SchemaMarker,
            PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("documents"),
            ProjectCenterPermissions.Write, "document.view", "document.checkout"], aliases);
        var role = settings.Templates.Single(t => t.RoleCode == "migration-test");
        Assert.Contains(role.Grants, g => g.Permission == "document.checkout");
        foreach (var code in new[] { "document.register", "document.archive", "document.restore" })
            Assert.DoesNotContain(role.Grants, g => g.Permission == code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyCatalogStillMigratesPreviouslyGrantedOperations(bool aliases)
    {
        var settings = Migrate([PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit], aliases);
        var role = settings.Templates.Single(t => t.RoleCode == "migration-test");
        Assert.Equal(12, role.Grants.Count(g => g.Permission.StartsWith("project.tab.")));
        foreach (var code in new[] { ProjectCenterPermissions.Write, "document.register", "document.checkout", "document.archive", "document.restore" })
            Assert.Contains(role.Grants, g => g.Permission == code);
    }

    [Fact]
    public void MigratedReadGrantHonorsProjectCompanyAndRevocation()
    {
        var company = Guid.NewGuid();
        var user = new UserAccount(Guid.NewGuid(), "migration-reader", "迁移验证人员", "unused", UserRole.ProductionViewer, true, CompanyId: company);
        Project Project(Guid organization) => new(Guid.NewGuid(), "MIGRATION", "迁移权限项目", "owner", "vault", "release", true) { OrganizationId = organization };
        var authorized = Project(company); var other = Project(company); var foreign = Project(Guid.NewGuid());
        var migrated = Migrate(ProjectCenterPermissions.AssemblyReadOnly(), true);
        var template = migrated.Templates.Single(t => t.RoleCode == "migration-test") with { RoleCode = null, ProjectAssignable = true };
        var settings = migrated with { Templates = [template], Assignments = [new(Guid.NewGuid(), user.Username, template.Key, company, "project", authorized.Id)] };
        var directory = new OrganizationDirectory([], [], [], [], []);
        bool Allowed(AuthorizationSettings value, Project project, string permission) => AuthorizationPolicy.Decide(value, new(user, project, project, directory), permission).Allowed;
        Assert.True(Allowed(settings, authorized, "document.view"));
        Assert.False(Allowed(settings, authorized, "document.checkout"));
        Assert.False(Allowed(settings, other, "document.view"));
        Assert.False(Allowed(settings, foreign, "document.view"));
        Assert.False(Allowed(settings with { Assignments = [] }, authorized, "document.view"));
    }

    [Fact]
    public async Task ExistingUnifiedConfigurationIsNotMigratedAgain()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var original = await repository.GetAuthorizationSettingsAsync(default);
        var updated = original with { Templates = original.Templates.Select(t => t with
        {
            Grants = t.Grants.Where(g => g.Permission != "document.checkout" && g.Permission != ProjectCenterPermissions.Tab("documents")).ToArray()
        }).ToArray() };
        var saved = await repository.SaveAuthorizationSettingsAsync(updated, original.Revision, default);
        var read = await repository.GetAuthorizationSettingsAsync(default);
        Assert.Same(saved, read);
        Assert.DoesNotContain(read.Templates.SelectMany(t => t.Grants), g => g.Permission == "document.checkout" || g.Permission == ProjectCenterPermissions.Tab("documents"));
    }
}
