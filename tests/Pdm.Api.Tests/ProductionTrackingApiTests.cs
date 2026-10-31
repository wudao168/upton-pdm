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

public sealed class ProductionTrackingApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    private static void Authorize(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),
                SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Fact]
    public async Task BatchScanKeepsItsIdentityAndActionsExposePublishedSameRevisionIncrement()
    {
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        var project = await repository.CreateProjectAsync(new("SCAN-INCREMENT-" + Guid.NewGuid().ToString("N"), "扫码增量接口", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "NS-INCREMENT", "接口模拟件", 10, "件", "钢", "MODEL-INCREMENT", "B", true);
        var original = new ReleasePackage(Guid.NewGuid(), project.Id, "ORIGINAL-10", ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        await repository.CreateReleasePackageAsync(original, default);
        var initial = (await (await client.PostAsJsonAsync($"/api/production-tracking/releases/{original.Id}/receive", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        var allocation = await client.PostAsJsonAsync($"/api/production-tracking/releases/{original.Id}/lots",
            new AssignProductionLot(initial.State.Lines[0].Id, "Self", 10, null, null, null, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, allocation.StatusCode);
        var lot = (await allocation.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!.State.Lots.Single();
        var supplement = original with { Id = Guid.NewGuid(), Number = "ADD-2", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = original.PublishedAt!.Value.AddMinutes(1), NonStandardBomSnapshot = [item with { Quantity = 12 }] };
        await repository.CreateReleasePackageAsync(supplement, default);
        var code = Uri.EscapeDataString($"UPLM-PRODUCTION|{original.Id}|{lot.Id}");
        var scan = Assert.Single((await client.GetFromJsonAsync<ProductionScanResult[]>($"/api/production-tracking/scan?code={code}"))!);
        Assert.Equal(lot.Id, scan.LotId);
        Assert.Equal(10, scan.LotQuantity);
        var context = (await client.GetFromJsonAsync<ProductionScanContext>($"/api/production-tracking/releases/{original.Id}/actions"))!;
        var extra = Assert.Single(context.RelatedDemands);
        Assert.Equal(supplement.Id, extra.ReleasePackageId);
        Assert.Equal(initial.State.Lines[0].Id, extra.RelatedToLineId);
        Assert.True(extra.IsSupplement);
        Assert.Equal(2, extra.RequiredQuantity);
        Assert.Equal(2, extra.UnassignedQuantity);
        var target = (await client.GetFromJsonAsync<ProductionTrackingRecord>($"/api/production-tracking/releases/{extra.ReleasePackageId}"))!;
        Assert.Equal(extra.LineId, target.State.Lines.Single().Id);
        Assert.Empty(target.State.Lots);
    }

    [Fact]
    public async Task PublishedReleaseCanBeReceivedAndAssignedButEngineerCannotAllocate()
    {
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        foreach (var (username, role) in new[] { ("admin", UserRole.Administrator), ("engineer", UserRole.Engineer) })
        {
            if (await repository.FindUserAsync(username, default) is null)
                await repository.CreateUserAsync(new(Guid.NewGuid(), username, username, "unused", role, true), default);
            await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        }
        var project = await repository.CreateProjectAsync(new("PROD-API-" + Guid.NewGuid().ToString("N"),
            "生产接口", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1,
            "NS-API-1", "夹持座", 2, "件", "45钢", "7002541.00-02", "B", true);
        var release = new ReleasePackage(Guid.NewGuid(), project.Id, "FB-API",
            ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "release")
        { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        await repository.CreateReleasePackageAsync(release, default);

        var received = await client.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/receive",
            new { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var initial = await received.Content.ReadFromJsonAsync<ProductionTrackingRecord>();
        Assert.NotNull(initial?.State.ReceivedAt);
        var line = Assert.Single(initial.State.Lines);
        Assert.Equal("7002541.00-02", line.DrawingNumber);
        Assert.Equal("NS-API-1", line.MaterialCode);
        Assert.Equal("夹持座", line.Name);
        var projectSummaries = (await client.GetFromJsonAsync<ProductionTrackingProject[]>("/api/production-tracking/projects"))!;
        Assert.Equal(2, projectSummaries.SelectMany(entry => entry.Packages).Single(entry => entry.ReleasePackageId == release.Id).TotalQuantity);
        var supplierCatalog = (await client.GetFromJsonAsync<ProductionSupplierCatalog>("/api/production-suppliers"))!;
        var categoryId = Guid.NewGuid();
        var categorySaved = await client.PutAsJsonAsync($"/api/production-suppliers/categories/{categoryId}",
            new SaveProductionSupplierCategory("接口外协分类", true, supplierCatalog.RowVersion));
        Assert.Equal(HttpStatusCode.OK, categorySaved.StatusCode);
        supplierCatalog = (await categorySaved.Content.ReadFromJsonAsync<ProductionSupplierCatalog>())!;
        var supplierId = Guid.NewGuid();
        var supplierSaved = await client.PutAsJsonAsync($"/api/production-suppliers/{supplierId}",
            new SaveProductionSupplier("API-SUP", "供应商A", "接口供应商有限公司", [categoryId], true, supplierCatalog.RowVersion, Contact: "私有联系人"));
        Assert.Equal(HttpStatusCode.OK, supplierSaved.StatusCode);
        var allocated = await client.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lots",
            new { lineId = line.Id, route = "External", quantity = 2, supplier = "供应商A",
                supplierId,
                assignee = "供应链", promisedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
                requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, allocated.StatusCode);
        var allocationRecord = (await allocated.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        var lot = Assert.Single(allocationRecord.State.Lots);
        var procurement = (await client.GetFromJsonAsync<ProjectProcurementTrackingResult>($"/api/projects/{project.Id}/procurement-tracking"))!;
        var tracked = Assert.Single(procurement.Items);
        Assert.Empty(tracked.PurchaseOrderNumbers);
        Assert.Equal("B", tracked.Manufacturing!.Revision);
        Assert.Equal(2, tracked.Manufacturing.AssignedQuantity);
        Assert.Equal(0, tracked.Manufacturing.QualifiedQuantity);
        Assert.Equal("供应商A", Assert.Single(tracked.Manufacturing.Batches).Supplier);
        Assert.NotNull(procurement.ManufacturingReadAt);
        supplierCatalog = (await supplierSaved.Content.ReadFromJsonAsync<ProductionSupplierCatalog>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/production-suppliers/{Guid.NewGuid()}",
            new SaveProductionSupplier("API-UNUSED", "未使用供应商", "未使用供应商有限公司", [categoryId], true, supplierCatalog.RowVersion))).StatusCode);
        Authorize(client, "engineer", "Engineer");
        var traceOptions = await client.GetAsync($"/api/production-tracking/releases/{release.Id}/external-suppliers");
        Assert.Equal(HttpStatusCode.OK, traceOptions.StatusCode);
        var traceChoices = (await traceOptions.Content.ReadFromJsonAsync<ProductionSupplierChoices>())!;
        Assert.Equal(supplierId, Assert.Single(traceChoices.Suppliers).Id);
        Assert.Equal(categoryId, Assert.Single(traceChoices.Categories).Id);
        Assert.DoesNotContain("私有联系人", await traceOptions.Content.ReadAsStringAsync());
        Assert.DoesNotContain("contact", await traceOptions.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/production-suppliers")).StatusCode);
        var blockedRoleName = "无项目内容" + Guid.NewGuid().ToString("N");
        var blockedRole = (await repository.CreateRoleAsync(blockedRoleName, "追溯权限验证", "Engineer", default)).Roles.Single(item => item.Name == blockedRoleName);
        await repository.SetRolePermissionsAsync(blockedRole.Role, [], default);
        var blockedUser = "blocked-" + Guid.NewGuid().ToString("N");
        await repository.CreateUserAsync(new(Guid.NewGuid(), blockedUser, "无项目内容", "unused", UserRole.Engineer, true, CompanyId: company, RoleCode: blockedRole.Role), default);
        await repository.SetUserCompanyScopeAsync(blockedUser, company, false, [], "admin", default);
        Authorize(client, blockedUser, "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/production-tracking/releases/{release.Id}/external-suppliers")).StatusCode);
        Authorize(client, "admin", "Administrator");
        var update = new UpdateProductionLotAllocation(lot.Id, "Self", 2, null, "机加", null, "改为内部加工", Guid.NewGuid(), allocationRecord.RowVersion);
        var edited = await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocation", update);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editedRecord = (await edited.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        var editedLot = Assert.Single(editedRecord.State.Lots);
        Assert.Equal(lot.Id, editedLot.Id);
        Assert.Equal("Self", editedLot.Route);
        Assert.Null(editedLot.SupplierId);
        var selfOnly = (await client.GetFromJsonAsync<ProductionTrackingProject[]>("/api/production-tracking/projects?selfOnly=true"))!;
        Assert.Equal(2, selfOnly.SelectMany(project => project.Packages).Single(package => package.ReleasePackageId == release.Id).TotalQuantity);
        var outsource = update with { Route = "External", Supplier = "客户端不可信名称", SupplierId = supplierId,
            PromisedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), RequestId = Guid.NewGuid(), ExpectedRowVersion = editedRecord.RowVersion };
        var outsourced = await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocation", outsource);
        Assert.Equal(HttpStatusCode.OK, outsourced.StatusCode);
        var outsourcedRecord = (await outsourced.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        Assert.Equal("供应商A", Assert.Single(outsourcedRecord.State.Lots).Supplier);
        Assert.Equal(lot.Id, Assert.Single(outsourcedRecord.State.Lots).Id);
        selfOnly = (await client.GetFromJsonAsync<ProductionTrackingProject[]>("/api/production-tracking/projects?selfOnly=true"))!;
        Assert.DoesNotContain(selfOnly.SelectMany(project => project.Packages), package => package.ReleasePackageId == release.Id);
        var externalOnly = (await client.GetFromJsonAsync<ProductionTrackingProject[]>("/api/production-tracking/projects?externalOnly=true"))!;
        Assert.Equal(2, externalOnly.SelectMany(project => project.Packages).Single(package => package.ReleasePackageId == release.Id).TotalQuantity);
        var replay = await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocation", outsource);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(outsourcedRecord.RowVersion, (await replay.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!.RowVersion);
        var bulk = new { lineIds = new[] { line.Id }, route = "Self", quantity = 999, requestId = Guid.NewGuid(), expectedRowVersion = outsourcedRecord.RowVersion };
        var batched = await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocations", bulk);
        Assert.Equal(HttpStatusCode.OK, batched.StatusCode);
        var batchRecord = (await batched.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        Assert.Equal(lot.Id, Assert.Single(batchRecord.State.Lots).Id);
        Assert.Equal(2, Assert.Single(batchRecord.State.Lots).Quantity);
        Assert.Equal("Self", Assert.Single(batchRecord.State.Lots).Route);
        var batchReplay = await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocations", bulk);
        Assert.Equal(batchRecord.RowVersion, (await batchReplay.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!.RowVersion);
        using var qr = await client.GetAsync($"/api/production-tracking/releases/{release.Id}/lots/{lot.Id}/card-qr");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await qr.Content.ReadAsStringAsync());

        Authorize(client, "engineer", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocations", bulk)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lot-allocation", update)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/production-suppliers")).StatusCode);
        var forbidden = await client.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lots",
            new { lineId = line.Id, route = "Self", quantity = 1, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
