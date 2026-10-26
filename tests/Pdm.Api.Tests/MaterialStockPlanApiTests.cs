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

public sealed class MaterialStockPlanApiTests
{
    [Fact]
    public async Task List_RequiresAuthentication_AndReturnsOnlyTheLocalFormalBusinessStockSnapshot()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/material-stock-plans")).StatusCode);
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var config = await materials.GetIntegrationConfigurationAsync(default);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "stock-api-admin", "常备查询管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync("stock-api-admin", company, false, [], "admin", default);
        var plan = new MaterialStockPlan(config.EnterpriseCode, config.OrganizationCode, "01020014079", 15, 50, true, "001", "个", DateTimeOffset.UtcNow);
        await materials.SaveStockPlansAsync([plan, plan with { MaterialCode = "raw-cache-only" }, plan with { OrganizationCode = "another" }], default);
        var material = await materials.CreateMaterialAsync(new(Guid.NewGuid(), plan.MaterialCode, "业务常备件", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "K400+", null, null, "品牌", null, null, null, null, MaterialApprovalStatus.Approved, "admin", plan.SyncedAt,
            "0102", "u9-item", plan.MaterialCode, MaterialSyncStatus.Succeeded, "admin", plan.SyncedAt, "admin", plan.SyncedAt, 1, "0102", U9SyncConfirmed: true),
            (await materials.FindCategoryAsync("0102", default))!, default);
        var draftId = Guid.NewGuid(); var managedId = Guid.NewGuid(); var category = StandardLibraryDefaults.UncategorizedId;
        var store = factory.Services.GetRequiredService<IStockedMaterialStore>();
        await store.SaveAsync(new(new(company, config.EnterpriseCode, config.OrganizationCode), 0, new(),
            [new(category, material.Id, "admin", plan.SyncedAt), new(category, draftId, "admin", plan.SyncedAt), new(category, managedId, "admin", plan.SyncedAt)],
            [new(material.Id, plan, 5, plan.SyncedAt), new(draftId, plan with { MaterialCode = "draft", SafetyStockQuantity = 0 }), new(managedId, plan with { MaterialCode = "0104020000001" })],
            ManagementMaterials: [new(managedId, "0104020000001", "工具刀具", null, null, "001", "010402", "工具刀具", "u9-tool")], BusinessSnapshotReady: true), 0, default);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "stock-api-admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var result = await client.GetFromJsonAsync<JsonElement>("/api/material-stock-plans");
        Assert.Equal(config.OrganizationCode, result.GetProperty("organizationCode").GetString());
        Assert.Equal("01020014079", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("materialCode").GetString());
        Assert.Equal(5, result.GetProperty("items")[0].GetProperty("stockQuantity").GetDecimal());
    }
}
