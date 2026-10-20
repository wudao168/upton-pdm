using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class WorkflowNotificationApiTests
{
    private static string Token(string username, UserRole role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task CompanyAndDepartmentRolesSaveSeparatelyFromApprovalTemplates()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "workflow-notice-admin", "通知配置管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("workflow-notice-admin", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("workflow-notice-admin", UserRole.Administrator));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var unit = await repository.SaveOrganizationUnitAsync(new(null, company, null, "NOTICE-SUPPLY", "供应链", OrganizationUnitKind.Department, true, 0), default);
        var before = JsonSerializer.Serialize((await repository.GetSystemSettingsAsync(default)).ApprovalWorkflows);
        var initial = await client.GetFromJsonAsync<WorkflowNotificationCatalog>("/api/workflow-notifications");
        Assert.NotNull(initial); Assert.Empty(initial.Positions);
        var payload = new SaveWorkflowNotification(true, [new("SubmitterDepartment", null, RoleCode: "TechnicalAssistant"), new("Fixed", unit.Id, RoleCode: "SupplyChain"), new("CompanyRole", null, RoleCode: "TechnicalAssistant")], initial.RowVersion, company);
        var response = await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release", payload);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<WorkflowNotificationCatalog>())!;
        Assert.Equal(3, Assert.Single(saved.Flows).Recipients.Count);
        Assert.Equal(before, JsonSerializer.Serialize((await repository.GetSystemSettingsAsync(default)).ApprovalWorkflows));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release", payload)).StatusCode);
        var wrongCompany = await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release", payload with { CompanyId = Guid.NewGuid(), ExpectedRowVersion = saved.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, wrongCompany.StatusCode);
        Assert.Contains("公司已变化", await wrongCompany.Content.ReadAsStringAsync());
        var foreignDepartment = await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release", payload with { Recipients = [new("Fixed", Guid.NewGuid(), RoleCode: "SupplyChain")], ExpectedRowVersion = saved.RowVersion });
        Assert.Equal(HttpStatusCode.BadRequest, foreignDepartment.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<WorkflowNoticeJob[]>("/api/workflow-notifications/flows/standard-release/records"))!);
        var reread = (await client.GetFromJsonAsync<WorkflowNotificationCatalog>("/api/workflow-notifications"))!;
        Assert.Equal(saved.RowVersion, reread.RowVersion); Assert.Equal("CompanyRole", reread.Flows.Single().Recipients.Last().DepartmentSource);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "workflow-notice-viewer", "普通工程师", "unused", UserRole.Engineer, true, CompanyId: company), default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("workflow-notice-viewer", UserRole.Engineer));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workflow-notifications")).StatusCode);
    }
}
