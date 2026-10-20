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

public sealed class ReleaseNotificationSettingsTests
{
    private static string Token(string username, UserRole role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task DepartmentsAreAdministratorSelectedCompanyScopedAndSecretsNeverReturned()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "notice-admin", "通知管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("notice-admin", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("notice-admin", UserRole.Administrator));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var department = await repository.SaveOrganizationUnitAsync(new(null, company, null, "CUSTOM-NOTICE", "管理员自定义接收部门", OrganizationUnitKind.Department, true, 0), default);
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/settings/release-notifications");
        Assert.False(initial.GetProperty("enabled").GetBoolean()); Assert.Equal(0, initial.GetProperty("rules").GetArrayLength());
        Assert.Contains(initial.GetProperty("departments").EnumerateArray(), value => value.GetProperty("id").GetGuid() == department.Id);
        var rule = new ReleaseNotificationRule(Guid.NewGuid(), null, "NonStandard", "Changed", [department.Id], true, ["Email"], null, true);
        var input = new SaveReleaseNotificationSettings(true, "http://192.168.2.8:5173", [rule],
            new("smtp.example.invalid", 587, "plm@example.invalid", "notice-user", "private-smtp-test-secret"), 0);
        var response = await client.PutAsJsonAsync("/api/settings/release-notifications", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("private-smtp-test-secret", json);
        var saved = JsonDocument.Parse(json).RootElement;
        Assert.True(saved.GetProperty("hasMailPassword").GetBoolean()); Assert.Equal("", saved.GetProperty("mail").GetProperty("password").GetString());
        Assert.Equal(department.Id, saved.GetProperty("rules")[0].GetProperty("departmentIds")[0].GetGuid());
        Assert.Contains(saved.GetProperty("rules")[0].GetProperty("channels").EnumerateArray(), value => value.GetString() == "InApp");
        var stored = (await factory.Services.GetRequiredService<IReleaseDeliveryStore>().FindConfigurationAsync(company, default))!;
        Assert.DoesNotContain("private-smtp-test-secret", stored.ProtectedPayload); Assert.True(stored.Enabled); Assert.NotNull(stored.EnabledAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/settings/release-notifications", input)).StatusCode);
        var foreign = input with { ExpectedRowVersion = 1, Rules = [rule with { DepartmentIds = [Guid.NewGuid()] }], Mail = input.Mail with { Password = "" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/release-notifications", foreign)).StatusCode);
        Assert.Equal(1, (await factory.Services.GetRequiredService<IReleaseDeliveryStore>().FindConfigurationAsync(company, default))!.RowVersion);
        var keepSecret = await client.PutAsJsonAsync("/api/settings/release-notifications", input with { ExpectedRowVersion = 1, Mail = input.Mail with { Password = "" } });
        Assert.Equal(HttpStatusCode.OK, keepSecret.StatusCode);
        Assert.True((await keepSecret.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasMailPassword").GetBoolean());
    }

    [Fact]
    public async Task OrdinaryUsersCannotReadOrChangeAdministratorNotificationConfiguration()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "notice-engineer", "工程师", "unused", UserRole.Engineer, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("notice-engineer", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("notice-engineer", UserRole.Engineer));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings/release-notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/settings/release-notifications", new SaveReleaseNotificationSettings(false, "", [], new(), 0))).StatusCode);
        Assert.Null(await factory.Services.GetRequiredService<IReleaseDeliveryStore>().FindConfigurationAsync(company, default));
    }

    [Theory]
    [InlineData("http://account:password@192.168.2.8:5173")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://192.168.2.8:5173/?token=secret")]
    public async Task NotificationLinksCannotContainCredentialsScriptsOrTokens(string url)
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "notice-admin", "管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("notice-admin", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("notice-admin", UserRole.Administrator)); client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var department = await repository.SaveOrganizationUnitAsync(new(null, company, null, "SAFE-NOTICE", "通知部门", OrganizationUnitKind.Department, true, 0), default);
        var input = new SaveReleaseNotificationSettings(true, url, [new(Guid.NewGuid(), null, "All", "Published", [department.Id], false, ["InApp"], null, false)], new(), 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/release-notifications", input)).StatusCode);
        Assert.Null(await factory.Services.GetRequiredService<IReleaseDeliveryStore>().FindConfigurationAsync(company, default));
    }
}
