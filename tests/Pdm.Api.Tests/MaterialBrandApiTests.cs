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
public sealed class MaterialBrandApiTests
{
    [Fact]
    public async Task BrandCatalogRequiresMaintenancePermissionAndProtectsConcurrentEdits()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var response = await client.PostAsJsonAsync("/api/material-brands", new { name = "QA-BRAND", isActive = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var brand = (await response.Content.ReadFromJsonAsync<MaterialBrand>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/material-brands", new { name = "qa-brand", isActive = true })).StatusCode);
        response = await client.PutAsJsonAsync($"/api/material-brands/{brand.Id}", new { name = "阿普顿", isActive = true, rowVersion = brand.RowVersion });
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/material-brands/{brand.Id}", new { name = "过期更新", isActive = true, rowVersion = brand.RowVersion })).StatusCode);
        var catalog = (await client.GetFromJsonAsync<MaterialBrand[]>("/api/material-brands"))!;
        Assert.Contains("QA-BRAND", Assert.Single(catalog, row => row.Id == brand.Id).PreviousNames);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MaterialBrandService>();
        await Assert.ThrowsAsync<PdmRuleException>(() => service.NormalizeAsync("库外品牌", null, default));
        var invalidMaterial = await client.PostAsJsonAsync("/api/materials", new { name = "库外品牌校验", kind = "Standard", supplyMode = "Purchase", unitCode = "001", specification = "QA", brand = "库外品牌", categoryCode = "0102" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMaterial.StatusCode);
        Assert.Contains("品牌库", await invalidMaterial.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.True((await client.GetAsync("/api/material-brands")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/material-brands", new { name = "无权新增", isActive = true })).StatusCode);
    }
    private static string Token(string actor, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
