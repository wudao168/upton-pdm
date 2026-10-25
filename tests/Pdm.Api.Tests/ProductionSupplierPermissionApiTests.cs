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

public sealed class ProductionSupplierPermissionApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    private HttpClient Client(string username, UserRole role = UserRole.Engineer)
    {
        var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task SupplierViewManageAndAllocationChoicesHaveSeparatePermissions()
    {
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        async Task<HttpClient> Account(string name, params string[] permissions)
        {
            var label = name + Guid.NewGuid().ToString("N");
            var directory = await repository.CreateRoleAsync(label, "供应商权限回归", "Engineer", default);
            var definition = directory.Roles.Single(item => item.Name == label);
            await repository.SetRolePermissionsAsync(definition.Role, permissions, default);
            await repository.CreateUserAsync(new(Guid.NewGuid(), label, name, "unused", UserRole.Engineer, true, CompanyId: company, RoleCode: definition.Role), default);
            await repository.SetUserCompanyScopeAsync(label, company, false, [], "admin", default);
            return Client(label);
        }
        using var admin = Client("admin", UserRole.Administrator);
        using var viewer = await Account("供应商只读", PermissionCodes.ProductionSupplierView);
        using var manager = await Account("供应商维护", PermissionCodes.ProductionSupplierView, PermissionCodes.ProductionSupplierManage);
        using var allocator = await Account("仅分配", PermissionCodes.ProductionAssign);
        using var denied = await Account("无供应商权限");
        var categoryId = Guid.NewGuid();
        var saved = await admin.PutAsJsonAsync($"/api/production-suppliers/categories/{categoryId}", new SaveProductionSupplierCategory("机加", true, 0));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var supplierId = Guid.NewGuid();
        var input = new SaveProductionSupplier("S-PRIVATE", "可选供应商", "完整公司名称", [categoryId], true, 1, Contact: "受限联系人", ContactPhone: "私有联系电话", Remarks: "内部备注");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/production-suppliers/{supplierId}", input)).StatusCode);
        var catalog = (await viewer.GetFromJsonAsync<ProductionSupplierCatalog>("/api/production-suppliers"))!;
        Assert.Equal("受限联系人", Assert.Single(catalog.Suppliers).Contact);
        input = input with { ExpectedRowVersion = catalog.RowVersion };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/production-suppliers/{supplierId}", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/production-suppliers/categories/{categoryId}", new SaveProductionSupplierCategory("不能修改", true, catalog.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await allocator.GetAsync("/api/production-suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await allocator.PutAsJsonAsync($"/api/production-suppliers/{supplierId}", input)).StatusCode);
        var choicesResponse = await allocator.GetAsync("/api/production-suppliers/choices");
        Assert.Equal(HttpStatusCode.OK, choicesResponse.StatusCode);
        using var choices = JsonDocument.Parse(await choicesResponse.Content.ReadAsStringAsync());
        var supplier = choices.RootElement.GetProperty("suppliers")[0];
        Assert.Equal("可选供应商", supplier.GetProperty("name").GetString());
        Assert.False(supplier.TryGetProperty("fullName", out _)); Assert.False(supplier.TryGetProperty("contact", out _)); Assert.False(supplier.TryGetProperty("remarks", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/production-suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/production-suppliers/choices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/production-suppliers/{supplierId}", input with { Name = "已维护供应商" })).StatusCode);
        var permission = RolePermissionCatalog.Permissions.Single(item => item.Code == PermissionCodes.ProductionSupplierManage);
        Assert.Equal("system", permission.Scope); Assert.Contains(PermissionCodes.ProductionSupplierView, permission.Dependencies);
    }
}
