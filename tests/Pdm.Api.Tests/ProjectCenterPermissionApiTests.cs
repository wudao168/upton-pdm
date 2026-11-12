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

public sealed class ProjectCenterPermissionApiTests
{
    [Fact]
    public async Task OverviewRecordsMatchTheExistingRecentAuditWindow()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var projects = await repository.ListProjectsAsync(default);
        var root = projects.First(project => project.ParentProjectId is null);
        var family = projects.Where(project => project.Id == root.Id || project.ParentProjectId == root.Id || project.RootProjectId == root.Id).ToArray();
        var now = DateTimeOffset.UtcNow;
        // An old note outside the recent 200 entries must remain outside the overview.
        await repository.AppendAuditAsync(new(Guid.NewGuid(), now.AddDays(-1), "admin", "project.manager-note", "Project", root.Id.ToString(), "旧记录"), default);
        for (var index = 0; index < 200; index++)
            await repository.AppendAuditAsync(new(Guid.NewGuid(), now.AddSeconds(index), "admin", index == 199 ? "project.manager-note" : "project.update", "Project", root.Id.ToString(), "近期记录"), default);
        var expected = await repository.ListProjectOverviewRecordsAsync(family.Select(project => project.Id).ToArray(), 200, default);
        var actual = await client.GetFromJsonAsync<AuditEntry[]>($"/api/projects/{root.Id}/overview-records");
        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual!, entry => entry.Detail == "旧记录");
        Assert.Contains(actual!, entry => entry.Detail == "近期记录");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/overview-records")).StatusCode);
    }

    [Fact]
    public async Task ProjectDetailUsesTheSameVisibilityAndCapabilitiesAsProjectList()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        using var projects = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/projects"));
        foreach (var item in projects.RootElement.EnumerateArray().Take(3))
        {
            var id = item.GetProperty("id").GetGuid();
            var detail = await client.GetStringAsync($"/api/projects/{id}");
            Assert.Equal(item.GetRawText(), detail);
        }
    }
    [Theory]
    [InlineData("AssemblySupervisor")]
    [InlineData("AssemblyFitter")]
    [InlineData("ElectricalSupervisor")]
    [InlineData("AssemblyElectrician")]
    public async Task AssemblyRolesCanReadButCannotWriteDownloadOrReadHiddenTabs(string roleCode)
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        const string actor = "assembly-permission-test";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "装配权限测试", "unused", UserRole.ProductionViewer, true, RoleCode: roleCode), default);
        if (project.OrganizationId is Guid company)
            await repository.SetUserCompanyScopeAsync(actor, company, true, [company], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(actor));
        var permissions = await repository.GetUserPermissionsAsync(actor, UserRole.ProductionViewer, default);
        Assert.Equal(8, permissions.Count(code => code.StartsWith("project.tab.")));
        Assert.DoesNotContain(ProjectCenterPermissions.Write, permissions);
        Assert.DoesNotContain(PermissionCodes.ProductionIssue, permissions);
        foreach (var suffix in new[] { "documents", "plan", "opl", "boms/Standard", "bom-headers/overview", "release-packages", "quality-upload-access" })
        {
            var response = await client.GetAsync($"/api/projects/{project.Id}/{suffix}");
            Assert.True(response.IsSuccessStatusCode, suffix + ": " + await response.Content.ReadAsStringAsync());
        }
        foreach (var suffix in new[] { "contact-information", "nameplate", "files", "budget", "audit", "overview-records", "boms/Standard/export", "export-metadata" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{project.Id}/{suffix}")).StatusCode);
        foreach (var suffix in new[] { "opl", "manager-notes", "todos", "plan/submit", "procurement-tracking/refresh", "release-preview-items/archive" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/projects/{project.Id}/{suffix}", new { })).StatusCode);
        foreach (var suffix in new[] { "plan", $"plan/tasks/{Guid.NewGuid()}/progress", "details" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/projects/{project.Id}/{suffix}", new { })).StatusCode);
        var document = Guid.NewGuid(); var version = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/documents/{document}/versions/{version}/file?download=false&preview=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/documents/{document}/versions/{version}/preview?download=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/release-packages/{Guid.NewGuid()}/department-acknowledgements", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/quality-inspections/{Guid.NewGuid()}/download")).StatusCode);
        // 在线预览走真实业务读取：不存在的文件返回404，而非被下载权限误拦截。
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/quality-inspections/{Guid.NewGuid()}/download?preview=true")).StatusCode);
    }

    [Fact]
    public async Task SavingRoleCanDisableOneTabWithoutLegacyExpansionRestoringIt()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var response = await client.PutAsJsonAsync("/api/authorization-center", AuthorizationPolicy.UpdateRole(settings, "AssemblyFitter",
            [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("documents"), "document.view"]));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var permissions = await repository.GetRolePermissionDirectoryAsync(default);
        var role = permissions.Roles.Single(item => item.Role == "AssemblyFitter");
        Assert.Contains(ProjectCenterPermissions.Tab("documents"), role.Permissions);
        Assert.DoesNotContain(ProjectCenterPermissions.Tab("budget"), role.Permissions);
        Assert.DoesNotContain(ProjectCenterPermissions.Write, role.Permissions);
    }

    private static string Token(string actor, string role = "ProductionViewer") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
