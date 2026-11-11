using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionMobilePermissionTests
{
    [Fact]
    public async Task ExistingConfigurationShowsNewPermissionsWithoutGrantingThem()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        foreach (var permission in new[] { ProductionMobilePermissions.Use, ProductionMobilePermissions.Print,
            ProductionMobilePermissions.Reprint, ProductionMobilePermissions.Agent, ProductionMobilePermissions.Printers, ProductionMobilePermissions.Obsolete })
        {
            var definition = Assert.Single(RolePermissionCatalog.Permissions, p => p.Code == permission);
            Assert.False(definition.Hidden); Assert.NotEmpty(definition.Name);
            Assert.True(FunctionPermissionCatalog.RequiresExplicitGrant(permission));
            Assert.DoesNotContain(settings.Templates.SelectMany(t => t.Grants), grant => grant.Permission == permission);
        }
    }

    [Fact]
    public void ProjectScopedGrantAndRevocationCannotLeakToAnotherProjectOrCompany()
    {
        var company = Guid.NewGuid();
        var user = new UserAccount(Guid.NewGuid(), "pack", "包装人员", "unused", UserRole.ProductionViewer, true, CompanyId: company);
        var a = new Project(Guid.NewGuid(), "A", "项目甲", "owner", "vault", "release", true) { OrganizationId = company };
        var b = a with { Id = Guid.NewGuid(), Code = "B" };
        var grants = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProductionMobilePermissions.Use,
            ProductionMobilePermissions.Print, ProductionMobilePermissions.Reprint }.Select(code => new AuthorizationGrant(code, ["company"])).ToArray();
        var template = new AuthorizationTemplate("packing", "包装授权", "", grants);
        var settings = new AuthorizationSettings(1, [template], [new(Guid.NewGuid(), user.Username, template.Key, company, "project", a.Id)], ProjectPermissionPositions.BuiltIn, DateTimeOffset.UtcNow);
        var directory = new OrganizationDirectory([], [], [], [], []);
        bool Allowed(Project p, AuthorizationSettings s) => AuthorizationPolicy.Decide(s, new(user, p, p, directory), ProductionMobilePermissions.Print).Allowed;
        Assert.True(Allowed(a, settings)); Assert.False(Allowed(b, settings));
        Assert.False(Allowed(a with { OrganizationId = Guid.NewGuid() }, settings));
        Assert.False(Allowed(a, settings with { Assignments = [] }));
    }
}
