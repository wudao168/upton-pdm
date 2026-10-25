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
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectRecordReadAccessTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task VisibleProjectRecordsAreReadableWithoutContentAuditOrMaintenancePermission()
    {
        using var client = factory.CreateClient();
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var url = $"/api/projects/{projectId}/audit?take=200";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        Authorize(client, "admin", "Administrator");
        var roleName = "记录只读测试-" + Guid.NewGuid().ToString("N");
        var created = await client.PostAsJsonAsync("/api/role-permissions", new { name = roleName, description = "只有项目可见权限", sourceRoleCode = "Engineer" });
        created.EnsureSuccessStatusCode();
        using var directory = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var roleCode = directory.RootElement.GetProperty("roles").EnumerateArray().Single(item => item.GetProperty("name").GetString() == roleName).GetProperty("role").GetString()!;
        (await client.PutAsJsonAsync($"/api/role-permissions/{roleCode}", new { permissions = new[] { PermissionCodes.ProjectView } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        Assert.IsType<InMemoryPdmRepository>(repository);
        var username = "record-reader-" + Guid.NewGuid().ToString("N");
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "记录查看人员", "unused", UserRole.Engineer, true, RoleCode: roleCode, CompanyId: Guid.Parse("70000000-0000-0000-0000-000000000001")), default);
        var note = new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "admin", "project.manager-note", "Project", projectId.ToString(), "其他人员维护的记录");
        await repository.AppendAuditAsync(note, default);
        Authorize(client, username, "Engineer");
        var response = await client.GetAsync(url); response.EnsureSuccessStatusCode();
        var records = await response.Content.ReadFromJsonAsync<List<AuditEntry>>();
        Assert.Contains(records!, item => item.Id == note.Id && item.Detail == note.Detail);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{projectId}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/projects/{projectId}/manager-notes", new { content = "不允许新增的记录" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/projects/{projectId}/todos", new { content = "不允许推送的待办", recipientUsernames = new[] { username } })).StatusCode);
        Authorize(client, "admin", "Administrator");
        (await client.PutAsJsonAsync($"/api/role-permissions/{roleCode}", new { permissions = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Authorize(client, username, "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    private static void Authorize(HttpClient client, string username, string role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }
}
