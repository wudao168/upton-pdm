using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class StockedMaterialApiTests
{
    [Fact]
    public async Task RemovalDoesNotReturnSuccessOrDeleteMembershipWhenU9CannotBeReadBack()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "cancel-admin", "取消常备管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("cancel-admin", company, false, [], "admin", default);
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var config = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(config with { BaseUrl = "http://u9.test", ClientSecretCiphertext = "protected:test", WriteEnabled = true }, default);
        var now = DateTimeOffset.UtcNow;
        var material = await materials.CreateMaterialAsync(new(Guid.NewGuid(), "01020009211", "取消常备测试", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "M1", null, null, "品牌", null, null, null, null, MaterialApprovalStatus.Approved, "admin", now,
            "0102", "u9-item", "01020009211", MaterialSyncStatus.Succeeded, "admin", now, "admin", now, 1, "0102", U9SyncConfirmed: true),
            (await materials.FindCategoryAsync("0102", default))!, default);
        var scope = new StockedMaterialScope(company, config.EnterpriseCode, config.OrganizationCode);
        var store = factory.Services.GetRequiredService<IStockedMaterialStore>();
        await store.SaveAsync(new(scope, 0, new(), [new(StandardLibraryDefaults.UncategorizedId, material.Id, "admin", now)],
            [new(material.Id, new(config.EnterpriseCode, config.OrganizationCode, material.MaterialCode, 15, 50, true, "001", "个", now))]), 0, default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString()); Authenticate(client, "cancel-admin", "Administrator");
        var response = await client.DeleteAsync($"/api/stocked-materials/{material.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("U9C未返回唯一", await response.Content.ReadAsStringAsync());
        var retained = await store.ReadAsync(scope, default);
        Assert.Single(retained.Memberships); Assert.Equal(15, Assert.Single(retained.Snapshots).Plan!.SafetyStockQuantity);
        Assert.Equal(0, factory.Services.GetRequiredService<TestU9OpenApiClient>().PostCallCount);
    }

    [Fact]
    public async Task EndpointsEnforceAuthenticationAndManagementAndReturnDefaultSettings()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stocked-materials/")).StatusCode);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        foreach (var (username, role) in new[] { ("stock-admin", UserRole.Administrator), ("stock-engineer", UserRole.Engineer) })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, username, "unused", role, true, CompanyId: company), default);
            await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        }
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Authenticate(client, "stock-engineer", "Engineer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stocked-materials/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/stocked-materials/memberships", new { categoryIds = new[] { Guid.NewGuid() }, materialIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/stocked-materials/settings")).StatusCode);
        Authenticate(client, "stock-admin", "Administrator");
        var view = await client.GetFromJsonAsync<JsonElement>("/api/stocked-materials/settings");
        Assert.Equal(240, view.GetProperty("settings").GetProperty("syncIntervalMinutes").GetInt32());
        Assert.True(view.GetProperty("settings").GetProperty("useStandardAssistant").GetBoolean());
        var invalid = await client.PutAsJsonAsync("/api/stocked-materials/settings", new { settings = new { syncIntervalMinutes = 0, notifyLowStock = false, useStandardAssistant = true, recipients = Array.Empty<string>() }, expectedRowVersion = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("回读间隔", await invalid.Content.ReadAsStringAsync());
    }
    private static void Authenticate(HttpClient client, string actor, string role)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    [Fact]
    public async Task ImmediateSynchronizationRequiresAssistantRoleOrSettingsAdministrationAndExposesListCapability()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        var roles = await repository.CreateRoleAsync("普通标准库维护", "同步权限测试", "StandardizationAssistant", default);
        var managerRole = roles.Roles.Single(item => item.Name == "普通标准库维护").Role;
        foreach (var (name, role, code) in new[] { ("sync-manager", UserRole.Engineer, managerRole), ("sync-assistant", UserRole.Engineer, "StandardizationAssistant"), ("sync-admin", UserRole.Administrator, "Administrator") })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", role, true, RoleCode: code, CompanyId: company), default);
            await repository.SetUserCompanyScopeAsync(name, company, false, [], "admin", default);
        }
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Authenticate(client, "sync-manager", "Engineer");
        Assert.True(await repository.HasUserPermissionAsync("sync-manager", UserRole.Engineer, PermissionCodes.StandardLibraryManage, default));
        var manager = await client.GetFromJsonAsync<JsonElement>("/api/stocked-materials/");
        Assert.False(manager.GetProperty("canSynchronize").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/stocked-materials/refresh", null)).StatusCode);
        Authenticate(client, "sync-assistant", "Engineer");
        var assistant = await client.GetFromJsonAsync<JsonElement>("/api/stocked-materials/");
        Assert.True(assistant.GetProperty("canSynchronize").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/stocked-materials/refresh", null)).StatusCode); // 授权通过，测试环境尚未配置U9C密钥。
        Authenticate(client, "sync-admin", "Administrator");
        var admin = await client.GetFromJsonAsync<JsonElement>("/api/stocked-materials/");
        Assert.False(admin.GetProperty("canSynchronize").GetBoolean());
        Assert.True(admin.GetProperty("canManageSettings").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/stocked-materials/refresh", null)).StatusCode);
    }
}
