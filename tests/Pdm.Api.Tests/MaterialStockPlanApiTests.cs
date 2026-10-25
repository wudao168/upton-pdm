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
    public async Task List_RequiresAuthentication_AndReturnsOnlyCurrentU9OrganizationStockedPlans()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/material-stock-plans")).StatusCode);
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var config = await materials.GetIntegrationConfigurationAsync(default);
        var plan = new MaterialStockPlan(config.EnterpriseCode, config.OrganizationCode, "01020014079", 15, 50, true, "001", "个", DateTimeOffset.UtcNow);
        await materials.SaveStockPlansAsync([plan, plan with { MaterialCode = "zero", SafetyStockQuantity = 0 }, plan with { OrganizationCode = "another" }, plan with { EnterpriseCode = "another" }], default);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var result = await client.GetFromJsonAsync<JsonElement>("/api/material-stock-plans");
        Assert.Equal(config.OrganizationCode, result.GetProperty("organizationCode").GetString());
        Assert.Equal("01020014079", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("materialCode").GetString());
    }
}
