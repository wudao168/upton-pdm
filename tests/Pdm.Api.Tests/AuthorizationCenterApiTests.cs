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
        var company = (await repo.FindUserAsync("admin", default))!.CompanyId!.Value;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var a = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "授权测试项目", null, null, 1, "permission-admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var b = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "未授权项目", null, null, 1, "permission-admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-admin", "权限管理员", "unused", UserRole.Engineer, true, RoleCode: "permission-admin", CompanyId: company), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-worker", "项目人员", "unused", UserRole.Engineer, true, RoleCode: "permission-worker", CompanyId: company), default);
        var existing = await repo.GetAuthorizationSettingsAsync(default);
        var manager = Template("manager", PermissionCodes.RoleSettingsView, PermissionCodes.RoleSettingsEdit, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView) with { RoleCode = "permission-admin" };
        var viewer = Template("viewer", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, "project.tab.information.view", "project.templates.assign",
            "project.risk.manage", "project.record.manage", PermissionCodes.DingTalkSelfBind) with { ProjectAssignable = true };
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
        var workbench = await client.GetFromJsonAsync<ProjectWorkbench>("/api/workbench?scope=company&summaryOnly=true");
        var authorizedProject = Assert.Single(workbench!.Projects);
        Assert.Equal(a.Id, authorizedProject.Project.Id);
        Assert.True(authorizedProject.CanRegisterRisk);
        Assert.True(authorizedProject.CanMaintainRecord);
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
        var company = (await repo.FindUserAsync("admin", default))!.CompanyId!.Value;
        await repo.CreateUserAsync(new(Guid.NewGuid(), "permission-owner", "权限负责人", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var settings = await repo.GetAuthorizationSettingsAsync(default); Login(client, "permission-owner", "Administrator");
        var empty = settings with { Templates = settings.Templates.Select(t => t with { Grants = [] }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/authorization-center", empty)).StatusCode);
        var invalid = settings with { Templates = settings.Templates.Append(Template("invalid", PermissionCodes.RoleSettingsEdit) with { ProjectAssignable = true }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/authorization-center", invalid)).StatusCode);
        Assert.Equal(settings.Revision, (await repo.GetAuthorizationSettingsAsync(default)).Revision);
    }

    [Fact]
    public async Task BudgetAssessmentFollowsExplicitAuthorizationWithoutProjectStaffing()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.FindUserAsync("admin", default))!.CompanyId!.Value;
        const string username = "budget-admin";
        await repo.CreateUserAsync(new(Guid.NewGuid(), username, "预算管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var customer = (await repo.ListCustomersAsync(false, default)).First();
        var first = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "可授权预算项目", null, null, 1, username, @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var second = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "范围外预算项目", null, null, 1, username, @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        Login(client, username, "Administrator");
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var adminRole = Assert.Single(settings.Templates, role => role.RoleCode == "Administrator");
        Assert.DoesNotContain(adminRole.Grants, grant => grant.Permission == PermissionCodes.ProjectBudgetAssessmentEdit);
        var financeGrants = adminRole.Grants.Select(grant => grant.Permission is AuthorizationActions.FinancialBudgetRead or AuthorizationActions.FinancialBudgetMaintain
            ? grant with { Scopes = ["company"] } : grant).ToArray();
        settings = await repo.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Select(role => role.Key == adminRole.Key ? role with { Grants = financeGrants } : role).ToArray()
        }, settings.Revision, default);

        var center = await client.GetFromJsonAsync<JsonElement>("/api/authorization-center");
        Assert.Contains(center.GetProperty("permissions").EnumerateArray(), permission =>
            permission.GetProperty("code").GetString() == PermissionCodes.ProjectBudgetAssessmentEdit);
        var path = $"/api/projects/{first.Id}/budget/assessments";
        var command = new SaveBudgetAssessmentCommand([new(Guid.NewGuid(), "外购设备", "Equipment", null,
            [new(Guid.NewGuid(), "测试设备", Quantity: 1, UnitPrice: 25)])], 0);
        Assert.False((await client.GetFromJsonAsync<BudgetAssessmentDirectory>(path))!.Sheets.Single().CanEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, command)).StatusCode);

        var scopedRole = Template("budget-assessment-scope", PermissionCodes.ProjectBudgetAssessmentEdit) with { ProjectAssignable = true };
        var assignment = new AuthorizationAssignment(Guid.NewGuid(), username, scopedRole.Key, company, "project", first.Id);
        var grantedResponse = await client.PutAsJsonAsync("/api/authorization-center", settings with
        {
            Templates = [.. settings.Templates, scopedRole],
            Assignments = [assignment]
        });
        Assert.True(grantedResponse.IsSuccessStatusCode, await grantedResponse.Content.ReadAsStringAsync());
        var granted = (await grantedResponse.Content.ReadFromJsonAsync<AuthorizationSettings>())!;
        var preview = (await client.GetFromJsonAsync<AuthorizationDecision[]>($"/api/authorization-center/preview?username={username}&projectId={first.Id}"))!;
        Assert.True(preview.Single(decision => decision.Permission == PermissionCodes.ProjectBudgetAssessmentEdit).Allowed);
        Assert.True((await client.GetFromJsonAsync<BudgetAssessmentDirectory>(path))!.Sheets.Single().CanEdit);
        var saved = await client.PutAsJsonAsync(path, command);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(25m, (await saved.Content.ReadFromJsonAsync<BudgetAssessmentDirectory>())!.Sheets.Single().Total);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/projects/{second.Id}/budget/assessments", command)).StatusCode);

        var revoked = await client.PutAsJsonAsync("/api/authorization-center", granted with { Assignments = [] });
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());
        Assert.False((await client.GetFromJsonAsync<BudgetAssessmentDirectory>(path))!.Sheets.Single().CanEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, command with { ExpectedRowVersion = 1 })).StatusCode);
    }

    [Fact]
    public async Task ExpenseBonusAndStockEndpointsHonorProjectAndCompanyGrants()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.Where(company => company.IsActive).ToArray();
        var company = companies[0].Id;
        var otherCompany = companies[1];
        const string owner = "module-permission-admin";
        const string worker = "module-permission-worker";
        await repository.CreateUserAsync(new(Guid.NewGuid(), owner, "权限维护测试", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), worker, "项目授权测试", "unused", UserRole.ProductionViewer, true, CompanyId: company), default);
        var customer = (await repository.ListCustomersAsync(false, default)).First();
        Task<Project> CreateProject(Guid companyId, string name) => repository.CreateNumberedProjectAsync(new(companyId, "P", 2,
            customer.Id, name, null, null, 1, owner, @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var project = await CreateProject(company, "已授权项目");
        var second = await CreateProject(company, "本公司范围外项目");
        var foreign = await CreateProject(otherCompany.Id, "其他公司项目");
        var expenses = $"/api/projects/{project.Id}/expense-requests";
        var bonus = $"/api/projects/{project.Id}/bonus";
        const string stock = "/api/stocked-materials/";
        var draft = new SaveExpenseRequest("SitePurchase", "Procurement", null, "权限测试", [], 0, false);

        Login(client, worker, "ProductionViewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(expenses)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(bonus)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(stock)).StatusCode);

        Login(client, owner, "Administrator");
        var original = await repository.GetAuthorizationSettingsAsync(default);
        var projectRole = Template("module-project-scope", PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
            ProjectCenterPermissions.Tab("budget"), ProjectCenterPermissions.Write, PermissionCodes.ProjectExpenseView,
            PermissionCodes.ProjectExpenseSubmit, PermissionCodes.ProjectBonusView, PermissionCodes.ProjectBonusManage) with { ProjectAssignable = true };
        var companyRole = Template("module-company-scope", PermissionCodes.StandardLibraryView, PermissionCodes.StockView, PermissionCodes.StockSettingsManage);
        var response = await client.PutAsJsonAsync("/api/authorization-center", original with
        {
            Templates = [.. original.Templates, projectRole, companyRole],
            Assignments = [new(Guid.NewGuid(), worker, projectRole.Key, company, "project", project.Id), new(Guid.NewGuid(), worker, companyRole.Key, company, "company")]
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var granted = (await response.Content.ReadFromJsonAsync<AuthorizationSettings>())!;
        Login(client, worker, "ProductionViewer");
        Assert.True((await client.GetFromJsonAsync<ExpenseDirectory>(expenses))!.CanCreate);
        var savedDraft = await client.PutAsJsonAsync($"{expenses}/{Guid.NewGuid()}", draft);
        Assert.True(savedDraft.IsSuccessStatusCode, await savedDraft.Content.ReadAsStringAsync());
        Assert.True((await client.GetFromJsonAsync<ProjectBonusView>(bonus))!.CanCreate);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{bonus}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(stock + "settings")).StatusCode);
        foreach (var denied in new[] { second, foreign })
        {
            Assert.False((await client.GetAsync($"/api/projects/{denied.Id}/bonus")).IsSuccessStatusCode);
            Assert.False((await client.PutAsJsonAsync($"/api/projects/{denied.Id}/expense-requests/{Guid.NewGuid()}", draft)).IsSuccessStatusCode);
        }
        using var foreignStockRequest = new HttpRequestMessage(HttpMethod.Get, stock + "settings");
        foreignStockRequest.Headers.Add("X-Company-Id", otherCompany.Id.ToString());
        Assert.False((await client.SendAsync(foreignStockRequest)).IsSuccessStatusCode);

        Login(client, owner, "Administrator");
        var revoked = await client.PutAsJsonAsync("/api/authorization-center", granted with { Assignments = [] });
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());
        Login(client, worker, "ProductionViewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{expenses}/{Guid.NewGuid()}", draft)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(bonus)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(stock + "settings")).StatusCode);
    }

    [Fact]
    public async Task RoleCreatedInCenterCanBeAssignedRenamedAndDeletedAfterUnassignment()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.FindUserAsync("admin", default))!.CompanyId!.Value;
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
