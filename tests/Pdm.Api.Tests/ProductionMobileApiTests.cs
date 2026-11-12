using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class ProductionMobileApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task DirectApiEnforcesGrantRevocationCompanyAndPackagingIdempotency()
    {
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var store = factory.Services.GetRequiredService<IProductionTrackingStore>();
        var company = (await repository.FindUserAsync("admin", default))!.CompanyId!.Value;
        var directory = await ApiTestAuthorization.CreateRoleAsync(repository, "移动包装回归", "接口测试", "ProductionViewer", default);
        var role = directory.Roles.Single(item => item.Name == "移动包装回归").Role;
        var permissions = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProductionExecute, ProductionMobilePermissions.Use };
        await repository.SetRolePermissionsAsync(role, permissions, default);
        const string actor = "mobile-api-pack";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "包装测试人员", "unused", UserRole.ProductionViewer, true, CompanyId: company, RoleCode: role), default);
        TenantContext.Set(new(Guid.NewGuid(), company, company, actor, role, false, permissions.ToHashSet()));
        Project project;
        try { project = await ApiTestAuthorization.CreateProjectAsync(repository, new("MOBILE-" + Guid.NewGuid().ToString("N"), "移动包装项目", actor, "vault", "release"), actor, default); }
        finally { TenantContext.Clear(); }
        var line = new ProductionLine(Guid.NewGuid(), "material", [], "70006036-10", "连接块", "A", "件", 10, "Normal", null, null, null, null, null);
        var lot = new ProductionLot(Guid.NewGuid(), line.Id, "Self", 10, null, null, null, null, null, 0);
        var release = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-MOBILE", ReleasePackageState.Published, null, "A", "A", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing };
        await repository.CreateReleasePackageAsync(release, default);
        var state = new ProductionTrackingState(release.Id, project.Id, company, project.Code, project.Name, release.Number,
            release.Scope, DateTimeOffset.UtcNow, [line], [lot], [new(Guid.NewGuid(), Guid.NewGuid(), lot.Id, "ProductionReceived", 10, actor, DateTimeOffset.UtcNow),
                new(Guid.NewGuid(), Guid.NewGuid(), lot.Id, "MachiningReceived", 10, actor, DateTimeOffset.UtcNow)])
            { FlowMode = "DepartmentHandover", ReceivedAt = DateTimeOffset.UtcNow, ReceivedBy = actor };
        await store.CreateAsync(state, default);
        var station = new ProductionWorkstation(Guid.NewGuid(), "包装台", "", true);
        var printer = new ProductionPrinter(Guid.NewGuid(), "标签打印机", "TEST", actor, true);
        var catalog = await store.ScanCatalogAsync(company, default);
        Assert.True(await store.SaveScanCatalogAsync(new(company, [station], [new(Guid.NewGuid(), "User", actor, "Packing", [station.Id], true)], catalog.RowVersion + 1)
            { FlowMode = "DepartmentHandover", Printers = [printer] }, catalog.RowVersion, default));
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, "ProductionViewer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var path = $"/api/production-mobile/releases/{release.Id}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "?lineId=" + line.Id)).StatusCode);
        var input = new RecordProductionMovement(lot.Id, "PackingStarted", 5, Guid.NewGuid(), WorkstationId: station.Id, LabelPrinterId: printer.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/movements", input)).StatusCode);
        Assert.Empty((await store.FindAsync(release.Id, default))!.State.PrintJobs);
        await repository.SetRolePermissionsAsync(role, permissions.Append(ProductionMobilePermissions.Print).ToArray(), default);
        var before = await client.GetStringAsync(path + "?lineId=" + line.Id);
        Assert.True(before.Contains("PackingStarted", StringComparison.Ordinal), before);
        var response = await client.PostAsJsonAsync(path + "/movements", input);
        Assert.True(response.IsSuccessStatusCode, before + "\n" + await response.Content.ReadAsStringAsync());
        response = await client.PostAsJsonAsync(path + "/movements", input);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var packed = (await store.FindAsync(release.Id, default))!;
        Assert.Single(packed.State.PrintJobs); Assert.Equal(5, Assert.Single(packed.State.Movements, m => m.Label is not null).Quantity);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/production-mobile/printers/{printer.Id}/claim", new { })).StatusCode);
        await repository.SetRolePermissionsAsync(role, permissions, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/movements", input)).StatusCode);
        await repository.SetRolePermissionsAsync(role, [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "?lineId=" + line.Id)).StatusCode);
        await repository.SetRolePermissionsAsync(role, permissions.Append(ProductionMobilePermissions.Print).ToArray(), default);
        var other = state with { ReleasePackageId = Guid.NewGuid(), CompanyId = Guid.NewGuid() };
        await repository.CreateReleasePackageAsync(release with { Id = other.ReleasePackageId }, default);
        await store.CreateAsync(other, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/production-mobile/releases/{other.ReleasePackageId}?lineId={line.Id}")).StatusCode);

        var document = await repository.RegisterDocumentAsync(new(project.Id, "MOBILE-OBSOLETE", "作废权限验证", "test.SLDDRW", DocumentKind.Drawing), actor, default);
        var full = RolePermissionCatalog.Permissions.Where(p => !p.Hidden && p.Code != ProductionMobilePermissions.Obsolete).Select(p => p.Code).ToArray();
        await repository.SetRolePermissionsAsync(role, full, default);
        var obsoletePath = $"/api/documents/{document.Id}/obsolete";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(obsoletePath, new { comment = "图纸更换" })).StatusCode);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var updated = settings with
        {
            Templates = settings.Templates.Select(template => template.RoleCode == role
                ? template with { Grants = [.. template.Grants, new(ProductionMobilePermissions.Obsolete, ["company"])] }
                : template).ToArray()
        };
        var withInternalGrant = updated with
        {
            Templates = updated.Templates.Select(template => template.RoleCode == role
                ? template with { Grants = [.. template.Grants, new(PermissionCodes.ReleaseManage, ["company"])] }
                : template).ToArray()
        };
        var rejected = await client.PutAsJsonAsync("/api/authorization-center", withInternalGrant);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("内部权限", (await rejected.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        var saved = await client.PutAsJsonAsync("/api/authorization-center", updated);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var obsolete = await client.PostAsJsonAsync(obsoletePath, new { comment = "图纸更换，停止生产使用" });
        Assert.True(obsolete.IsSuccessStatusCode, await obsolete.Content.ReadAsStringAsync());
        Assert.Equal(DocumentLifecycleState.Obsolete, (await repository.FindDocumentAsync(document.Id, default))!.State);
        await repository.SetRolePermissionsAsync(role, full, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(obsoletePath, new { comment = "重试作废" })).StatusCode);
    }
}
