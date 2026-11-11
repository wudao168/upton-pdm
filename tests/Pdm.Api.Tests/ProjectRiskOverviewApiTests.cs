using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectRiskOverviewApiTests
{
    [Fact]
    public async Task RiskOverviewUsesProjectScopeAndExistingTabPermissions()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var planning = factory.Services.GetRequiredService<IProjectPlanningRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First().Id;
        const string actor = "risk-reader";
        const string role = "risk-reader-test";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "风险查看人", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        var root = Path.Combine(AppContext.BaseDirectory, ".data", "project-risk-tests", Guid.NewGuid().ToString("N"));
        var storage = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(storage with { VaultRoot = root }, default);
        Task<Project> Create(string name) => repository.CreateNumberedProjectAsync(
            new(company, "P", 2, customer, name, null, null, 1, actor, root, Path.Combine(root, "release")), default);
        var project = await Create("风险概览测试");
        var child = await repository.CreateSubprojectAsync(new(project.Id, "风险子项目", null, 1), default);
        var outside = await Create("越范围项目");
        await repository.SetMainProjectStaffingAsync(project.Id, new(actor, [], []), actor, default);

        var now = DateTimeOffset.UtcNow;
        var day = new DateOnly(2026, 11, 1);
        var task = new ProjectPlanTask(Guid.NewGuid(), "待核对设计任务", "Design", null, 1, day, day, null, null,
            null, null, 0, ProjectPlanTaskStatus.NotStarted, [], 1, false, true, 1);
        await planning.SavePlanAsync(new(Guid.NewGuid(), project.Id, Guid.NewGuid(), "测试计划", "Design", null, null,
            day, day, day, 1, [task], actor, now, actor, now, 1) { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, null, null, default);
        var existing = await repository.GetAuthorizationSettingsAsync(default);
        var grants = new AuthorizationGrant[]
        {
            new(PermissionCodes.ProjectView, ["company"]),
            new(PermissionCodes.ProjectContentView, ["MainManager"]),
            new(ProjectCenterPermissions.Tab("overview"), ["MainManager"]),
            new(ProjectCenterPermissions.Tab("project-plan"), ["MainManager"]),
            new("project.plan.view", ["MainManager"])
        };
        var template = new AuthorizationTemplate(role, "风险权限测试", "", grants, role);
        var settings = await repository.SaveAuthorizationSettingsAsync(existing with { Templates = [template] }, existing.Revision, default);

        string PathFor(Project item) => $"/api/projects/{item.Id}/risk-overview";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PathFor(project))).StatusCode);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        var family = (await client.GetFromJsonAsync<ProjectRiskOverviewProject[]>(PathFor(project)))!;
        Assert.Equal(new[] { project.Id, child.Id }, family.Select(item => item.Project.Id));
        Assert.Equal(task.Name, Assert.Single(family[0].Plan!.Tasks).Name);
        Assert.Null(family[0].Procurement);
        Assert.Empty(family[0].Releases);
        Assert.Equal(child.Id, Assert.Single((await client.GetFromJsonAsync<ProjectRiskOverviewProject[]>(PathFor(child)))!).Project.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(outside))).StatusCode);

        var withoutPlan = grants.Where(grant => grant.Permission != "project.plan.view").ToArray();
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template with { Grants = withoutPlan }] }, settings.Revision, default);
        Assert.All((await client.GetFromJsonAsync<ProjectRiskOverviewProject[]>(PathFor(project)))!, item =>
        {
            Assert.Null(item.Plan);
            Assert.Null(item.Project.DeliveryDate);
        });
        await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = [template with { Grants = withoutPlan.Where(grant => grant.Permission != ProjectCenterPermissions.Tab("overview")).ToArray() }]
        }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(project))).StatusCode);
    }
}
