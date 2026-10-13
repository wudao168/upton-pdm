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

public sealed class ProjectWorkbenchApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task ReviewScopesRespectContentPermissionsAndDepartmentAuthority()
    {
        using var client = factory.CreateClient();
        Authorize(client, "engineer", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workbench?scope=division")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workbench?scope=unknown")).StatusCode);
        var response = await client.GetAsync("/api/workbench?scope=company");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var services = factory.Services.CreateScope();
        var repository = services.ServiceProvider.GetRequiredService<IPdmRepository>();
        foreach (var item in data.RootElement.GetProperty("projects").EnumerateArray())
        {
            var id = item.GetProperty("project").GetProperty("id").GetGuid();
            Assert.True(await repository.HasProjectContentReadAccessAsync(id, "engineer", UserRole.Engineer, default));
        }
        Assert.Contains("company", data.RootElement.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()));
        (await client.GetAsync("/api/workbench?scope=personal")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task WorkbenchRequiresLoginAndRejectsUnauthorizedViews()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workbench")).StatusCode);
        Authorize(client, "engineer", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workbench?view=manager")).StatusCode);
        var response = await client.GetAsync("/api/workbench?view=engineer");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("engineer", data.RootElement.GetProperty("view").GetString());
        Assert.All(data.RootElement.GetProperty("projects").EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("budget").ValueKind));
    }

    [Fact]
    public async Task RiskLifecycleValidatesInputPersistsAndClosesWithAudit()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var directory = await repository.GetOrganizationDirectoryAsync(default);
        var companyId = directory.Organizations[0].Id;
        var unit = await repository.SaveOrganizationUnitAsync(new(null, companyId, null, "WBTEST", "工作台测试部门", OrganizationUnitKind.Department, true, 0), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "workbench-owner", "风险负责人", "unused", UserRole.Engineer, true, CompanyId: companyId), default);
        await repository.SetOrganizationMembershipsAsync("workbench-owner", [unit.Id], unit.Id, default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var project = await repository.CreateNumberedProjectAsync(new(companyId, "P", 0, customer.Id, "工作台风险接口测试", null,
            new DateOnly(2026, 10, 5), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        var owner = "workbench-owner";
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        client.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
        var command = new { title = "工作台接口验收风险", impact = "核对交付节点", owner, dueDate = "2026-10-10", severity = "warning" };
        var invalid = await client.PostAsJsonAsync($"/api/projects/{project.Id}/risks", command with { title = "" });
        Assert.False(invalid.IsSuccessStatusCode);
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/risks", command);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        var view = await client.GetFromJsonAsync<ProjectWorkbench>("/api/workbench?view=division");
        Assert.Contains(view!.Risks, risk => risk.Id == id && risk.CanClose && !risk.Closed);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/workbench/risks/{id}/close", null)).StatusCode);
        var risk = await scope.ServiceProvider.GetRequiredService<IProjectWorkbenchRiskRepository>().FindAsync(id, default);
        Assert.True(risk!.Closed);
        Assert.Equal("admin", risk.ClosedBy);
    }

    private static void Authorize(HttpClient client, string username, string role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }
}
