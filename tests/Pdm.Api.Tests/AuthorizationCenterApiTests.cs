using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class AuthorizationCenterApiTests
{
    private static void Login(HttpClient client, string name, string role = "Engineer")
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private static AuthorizationTemplate Template(string key, params string[] codes) => new(key, key, "", codes.Select(c => new AuthorizationGrant(c, ["company"])).ToArray());

    [Fact]
    public async Task SavedConfigurationControlsEndpointsPreviewAndRevocationWithoutRelogin()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var a = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "授权测试项目", null, null, 1, "permission-admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var b = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "未授权项目", null, null, 1, "permission-admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-admin", "权限管理员", "unused", UserRole.Engineer, true, RoleCode: "permission-admin", CompanyId: company), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-worker", "项目人员", "unused", UserRole.Engineer, true, RoleCode: "permission-worker", CompanyId: company), default);
        var existing = await repo.GetAuthorizationSettingsAsync(default);
        var manager = Template("manager", PermissionCodes.RoleSettingsView, PermissionCodes.RoleSettingsEdit, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView) with { RoleCode = "permission-admin" };
        var viewer = Template("viewer", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "project.tab.information.view", "project.templates.assign", PermissionCodes.DingTalkSelfBind) with { ProjectAssignable = true };
        var settings = await repo.SaveAuthorizationSettingsAsync(existing with { Templates = [manager, viewer], Assignments = [new(Guid.NewGuid(), "permission-worker", "viewer", company, "project", a.Id)] }, existing.Revision, default);
        Login(client, "permission-worker");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/authorization-center")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/authorization-center/projects/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me/dingtalk-binding")).StatusCode);
        var assignments = settings.Assignments.Where(assignment => assignment.TargetId == a.Id).ToArray();
        var projectUpdate = await client.PutAsJsonAsync($"/api/authorization-center/projects/{a.Id}", new ProjectAuthorizationRequest(settings.Revision, assignments));
        Assert.True(projectUpdate.IsSuccessStatusCode, await projectUpdate.Content.ReadAsStringAsync());
        settings = await repo.GetAuthorizationSettingsAsync(default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/authorization-center/projects/{b.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{a.Id}/nameplate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{b.Id}/nameplate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{a.Id}/plan")).StatusCode);
        Login(client, "permission-admin");
        Assert.True(await repo.HasUserPermissionAsync("permission-admin", UserRole.Engineer, PermissionCodes.RoleSettingsView, default));
        var centerResponse = await client.GetAsync("/api/authorization-center");
        Assert.True(centerResponse.IsSuccessStatusCode, await centerResponse.Content.ReadAsStringAsync());
        var preview = await client.GetFromJsonAsync<AuthorizationDecision[]>($"/api/authorization-center/preview?username=permission-worker&projectId={a.Id}");
        Assert.True(preview!.Single(d => d.Permission == "project.templates.assign").Allowed);
        Assert.False(preview!.Single(d => d.Permission == PermissionCodes.DingTalkSelfBind).Allowed);
        var savedResponse = await client.PutAsJsonAsync("/api/authorization-center", settings with { Assignments = [] });
        Assert.True(savedResponse.IsSuccessStatusCode, await savedResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/authorization-center", settings)).StatusCode);
        Login(client, "permission-worker");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/authorization-center/projects/{a.Id}")).StatusCode);
        Assert.False((await client.PutAsJsonAsync("/api/project-permissions", new { rules = new { } })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task CannotRemoveLastPermissionManagerOrCreateProjectTemplateWithSystemActions()
    {
        await using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-owner", "权限负责人", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var settings = await repo.GetAuthorizationSettingsAsync(default); Login(client, "permission-owner", "Administrator");
        var empty = settings with { Templates = settings.Templates.Select(t => t with { Grants = [] }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/authorization-center", empty)).StatusCode);
        var invalid = settings with { Templates = settings.Templates.Append(Template("invalid", PermissionCodes.RoleSettingsEdit) with { ProjectAssignable = true }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/authorization-center", invalid)).StatusCode);
        Assert.Equal(settings.Revision, (await repo.GetAuthorizationSettingsAsync(default)).Revision);
    }

    [Fact]
    public async Task RoleCreatedInCenterCanBeAssignedRenamedAndDeletedAfterUnassignment()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        await repo.CreateUserAsync(new(Guid.NewGuid(), "role-owner", "角色维护人", "unused", UserRole.Administrator, true, CompanyId: company), default);
        Login(client, "role-owner", "Administrator");

        var original = await repo.GetAuthorizationSettingsAsync(default);
        Assert.All(original.Templates, role =>
        {
            Assert.NotNull(role.RoleCode);
            Assert.Contains(role.Grants, grant => grant.Permission == PermissionCodes.DingTalkSelfBind && grant.Scopes.Contains("company"));
        });
        var custom = Template("drawing-reader", PermissionCodes.RoleSettingsView) with { Name = "图档查阅员" };
        var optedOut = Template("opted-out") with { Grants = [new(PermissionCodes.DingTalkSelfBind, [])], ProjectAssignable = true };
        async Task<AuthorizationSettings> Save(AuthorizationSettings settings)
        {
            using var response = await client.PutAsJsonAsync("/api/authorization-center", settings);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<AuthorizationSettings>())!;
        }

        var saved = await Save(original with { Templates = [.. original.Templates, custom, optedOut] });
        var created = Assert.Single(saved.Templates, role => role.Key == custom.Key);
        Assert.Contains(created.Grants, grant => grant.Permission == PermissionCodes.DingTalkSelfBind && grant.Scopes.Contains("company"));
        Assert.Empty(Assert.Single(saved.Templates, role => role.Key == optedOut.Key).Grants);
        var code = created.RoleCode;
        Assert.NotNull(code);
        var directory = await client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        Assert.Contains(directory.GetProperty("roles").EnumerateArray(), role =>
            role.GetProperty("role").GetString() == code && role.GetProperty("name").GetString() == custom.Name);

        var assigned = await client.PostAsJsonAsync("/api/users", new
        {
            username = "new-role-reader", displayName = "查阅测试", password = "test1234",
            companyId = company, role = code, roles = new[] { code }
        });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        Login(client, "new-role-reader", "ProductionViewer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/authorization-center")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/authorization-center", saved)).StatusCode);
        Login(client, "role-owner", "Administrator");

        var renamed = await Save(saved with { Templates = saved.Templates.Select(role => role.Key == custom.Key ? role with { Name = "工程资料查阅" } : role).ToArray() });
        Assert.Equal("工程资料查阅", (await repo.GetRolePermissionDirectoryAsync(default)).Roles.Single(role => role.Role == code).Name);
        var removed = renamed with { Templates = renamed.Templates.Where(role => role.Key != custom.Key).ToArray() };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/authorization-center", removed)).StatusCode);
        var unassigned = await client.PutAsJsonAsync("/api/users/new-role-reader", new
        {
            displayName = "查阅测试", companyId = company, role = "ProductionViewer", roles = new[] { "ProductionViewer" }
        });
        Assert.True(unassigned.IsSuccessStatusCode, await unassigned.Content.ReadAsStringAsync());
        await Save(removed);
        Assert.DoesNotContain((await repo.GetRolePermissionDirectoryAsync(default)).Roles, role => role.Role == code);
    }
}
