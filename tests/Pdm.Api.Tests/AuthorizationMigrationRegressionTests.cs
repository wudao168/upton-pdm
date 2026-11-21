using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task UserManagementDefaultsGrantOnlySystemAdministratorOnce()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var initial = await repository.GetAuthorizationSettingsAsync(default);
        var permissions = new[] { PermissionCodes.UserDeactivate, PermissionCodes.UserDelete };
        foreach (var code in permissions)
        {
            var definition = Assert.Single(RolePermissionCatalog.Permissions, item => item.Code == code);
            Assert.True(definition.Sensitive);
            Assert.Equal("system", definition.Scope);
            var role = Assert.Single(initial.Templates, item => item.Grants.Any(grant => grant.Permission == code));
            Assert.Equal(nameof(UserRole.Administrator), role.RoleCode);
        }

        var existing = initial with
        {
            UserManagementDefaultsApplied = false,
            Templates = initial.Templates.Select(role => role with
            {
                Grants = role.Grants.Where(grant => !permissions.Contains(grant.Permission)).ToArray()
            }).ToArray()
        };
        var upgraded = AuthorizationMigration.EnableUserManagementDefaults(existing);
        Assert.True(upgraded.UserManagementDefaultsApplied);
        foreach (var role in existing.Templates)
        {
            var migrated = upgraded.Templates.Single(item => item.Key == role.Key);
            Assert.Equal(role.Grants, migrated.Grants.Where(grant => !permissions.Contains(grant.Permission)));
            Assert.Equal(role.RoleCode == nameof(UserRole.Administrator) ? 2 : 0,
                migrated.Grants.Count(grant => permissions.Contains(grant.Permission)));
        }

        var saved = await repository.SaveAuthorizationSettingsAsync(existing, initial.Revision, default);
        Assert.True(saved.UserManagementDefaultsApplied);
        Assert.DoesNotContain(AuthorizationMigration.EnableUserManagementDefaults(saved).Templates.SelectMany(role => role.Grants),
            grant => permissions.Contains(grant.Permission));
    }

    [Fact]
    public async Task DocumentReleaseDefaultsAddOnlyConfirmedScopesAndPreserveRevocations()
    {
        const string permission = PermissionCodes.DocumentLockEmergencyRelease;
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var initial = await repository.GetAuthorizationSettingsAsync(default);
        var prior = initial with
        {
            DocumentReleaseDefaultsApplied = false,
            Templates = initial.Templates.Select(role => role.RoleCode switch
            {
                nameof(UserRole.BusinessUnitManager) => role with
                {
                    Grants = role.Grants.Where(grant => grant.Permission != permission)
                        .Append(new AuthorizationGrant(permission, ["project-manager"])).ToArray()
                },
                "MechanicalManager" => role with { Grants = role.Grants.Where(grant => grant.Permission != permission).ToArray() },
                _ => role
            }).ToArray()
        };

        Assert.True(initial.DocumentReleaseDefaultsApplied);
        Assert.Contains(initial.Templates.Single(role => role.RoleCode == nameof(UserRole.Administrator)).Grants,
            grant => grant.Permission == permission && grant.Scopes.Contains("company"));
        var upgraded = AuthorizationMigration.EnableDocumentReleaseDefaults(prior);
        Assert.True(upgraded.DocumentReleaseDefaultsApplied);
        Assert.Same(prior.Assignments, upgraded.Assignments);
        foreach (var role in prior.Templates)
        {
            var result = upgraded.Templates.Single(item => item.Key == role.Key);
            switch (role.RoleCode)
            {
                case nameof(UserRole.BusinessUnitManager):
                    Assert.Equal(new[] { "project-manager", "division-manager" }, result.Grants.Single(grant => grant.Permission == permission).Scopes);
                    break;
                case "MechanicalManager":
                    Assert.Equal(new[] { "division-member" }, result.Grants.Single(grant => grant.Permission == permission).Scopes);
                    break;
                default:
                    Assert.Same(role, result);
                    break;
            }
            Assert.Equal(role.Grants.Where(grant => grant.Permission != permission), result.Grants.Where(grant => grant.Permission != permission));
        }

        var saved = await repository.SaveAuthorizationSettingsAsync(prior, initial.Revision, default);
        Assert.True(saved.DocumentReleaseDefaultsApplied);
        Assert.Same(saved, AuthorizationMigration.EnableDocumentReleaseDefaults(saved));
        Assert.DoesNotContain((await repository.GetAuthorizationSettingsAsync(default)).Templates
            .Single(role => role.RoleCode == "MechanicalManager").Grants, grant => grant.Permission == permission);
    }

    [Theory]
    [InlineData("BusinessUnitManager", UserRole.BusinessUnitManager)]
    [InlineData("MechanicalManager", UserRole.Engineer)]
    public async Task DefaultDocumentReleaseRolesAccessOnlyTheirDivisionAndHonorRevocation(string roleCode, UserRole baseRole)
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var directory = await repository.GetOrganizationDirectoryAsync(default);
        var company = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var division = await repository.SaveOrganizationUnitAsync(new(null, company, null, "RELEASE-A", "图档释放测试一部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        var otherDivision = await repository.SaveOrganizationUnitAsync(new(null, company, null, "RELEASE-B", "图档释放测试二部", OrganizationUnitKind.BusinessDivision, true, 2, true), default);
        var user = new UserAccount(Guid.NewGuid(), "drawing-release-operator", "图档释放测试人员", "unused", baseRole, true, RoleCode: roleCode, CompanyId: company);
        await repository.CreateUserAsync(user, default);
        if (baseRole == UserRole.BusinessUnitManager)
            await repository.SetOrganizationUnitManagersAsync(division.Id, user.Username, [], default);
        else
            await repository.SetOrganizationMembershipsAsync(user.Username, [division.Id], division.Id, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(user, TimeSpan.FromHours(1)));

        async Task<PdmDocument> CheckoutInAsync(OrganizationUnit unit)
        {
            var project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer, unit.Name, null, null, 1, "admin", "D:/test", "D:/release"), default);
            await repository.SetProjectExecutionUnitAsync(project.Id, unit.Id, "admin", default);
            var document = await repository.RegisterDocumentAsync(new(project.Id, "RELEASE-1", "图档释放测试", "RELEASE-1.SLDPRT", DocumentKind.Part), "engineer", default);
            return await repository.CheckoutAsync(document.Id, "engineer", Guid.NewGuid(), "验证电脑", DateTimeOffset.UtcNow.AddMinutes(5), default);
        }

        static string Path(PdmDocument document) => $"/api/documents/{document.Id}/emergency-release";
        static object Payload(PdmDocument document) => new { sessionId = document.CheckoutSessionId, document.CheckedOutAt, reason = "确认图档交接" };
        var own = await CheckoutInAsync(division);
        var outside = await CheckoutInAsync(otherDivision);
        var eligible = Assert.Single((await client.GetFromJsonAsync<EditLockSummary[]>($"/api/edit-locks?documentId={own.Id}"))!);
        Assert.True(eligible.CanEmergencyRelease);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path(outside), Payload(outside))).StatusCode);
        using var crossCompany = new HttpRequestMessage(HttpMethod.Post, Path(own)) { Content = JsonContent.Create(Payload(own)) };
        crossCompany.Headers.Add("X-Company-Id", directory.Organizations.First(item => item.Id != company).Id.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(crossCompany)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path(own), Payload(own))).StatusCode);

        var authorization = await repository.GetAuthorizationSettingsAsync(default);
        var revoked = authorization with { Templates = authorization.Templates.Select(role => role.RoleCode == roleCode ? role with
        {
            Grants = role.Grants.Where(grant => grant.Permission != PermissionCodes.DocumentLockEmergencyRelease).ToArray()
        } : role).ToArray() };
        await repository.SaveAuthorizationSettingsAsync(revoked, authorization.Revision, default);
        var pending = await repository.CheckoutAsync(own.Id, "engineer", Guid.NewGuid(), "验证电脑", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var visible = (await client.GetFromJsonAsync<EditLockSummary[]>($"/api/edit-locks?documentId={pending.Id}"))!;
        Assert.DoesNotContain(visible, row => row.CanEmergencyRelease);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path(pending), Payload(pending))).StatusCode);
        Assert.Equal(pending.CheckoutSessionId, (await repository.FindDocumentAsync(pending.Id, default))!.CheckoutSessionId);
    }
}
