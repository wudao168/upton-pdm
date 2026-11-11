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
public sealed class ProjectBonusApiTests
{
    [Fact]
    public async Task SettingsAreEditableVersionedAndNewBudgetEntryRequiresManager()
    {
        await using var factory = new PdmApiFactory(); var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings/project-bonus")).StatusCode);
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repo.ListProjectsAsync(default)).First();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First(o => o.IsActive).Id;
        if (await repo.FindUserAsync("bonus-admin", default) is null)
            await repo.CreateUserAsync(new(Guid.NewGuid(), "bonus-admin", "奖金管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        Login(client, "bonus-admin", "Administrator");
        var response = await client.GetAsync("/api/settings/project-bonus");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var settings = (await response.Content.ReadFromJsonAsync<BonusSettingsView>())!.Settings;
        Assert.Equal(16, settings.Roles.Count);
        var edited = settings with { Roles = settings.Roles.Select((r, i) => i == 0 ? r with { Name = "项目统筹" } : r).ToArray() };
        response = await client.PutAsJsonAsync("/api/settings/project-bonus", edited);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var saved = (await response.Content.ReadFromJsonAsync<BonusSettingsView>())!.Settings;
        Assert.Equal(1, saved.Version); Assert.Equal("项目统筹", saved.Roles[0].Name);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/settings/project-bonus", edited)).StatusCode);
        var view = await client.GetFromJsonAsync<ProjectBonusView>($"/api/projects/{project.Id}/bonus");
        Assert.False(view!.CanCreate); Assert.Null(view.Pool); Assert.Empty(view.Applications);
        response = await client.PutAsJsonAsync($"/api/projects/{project.Id}/bonus/Shipment", new SaveBonusDraft(0, [], [], "", false));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    private static void Login(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }
}
