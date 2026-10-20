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

public sealed class ProductionScanApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    private HttpClient Client(string username, UserRole role = UserRole.ProductionViewer)
    {
        var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task ConfiguredAccountsOnlySeeTheirCurrentStepsAndCannotForgeActorStationOrQuantity()
    {
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        var roleCodes = new Dictionary<string, string>();
        foreach (var (username, permission) in new[] { ("scan-receive", PermissionCodes.ProductionExecute), ("scan-machine", PermissionCodes.ProductionExecute),
                     ("scan-pack", PermissionCodes.ProductionExecute), ("scan-quality", PermissionCodes.ProductionInspect), ("scan-issue", PermissionCodes.ProductionIssue) })
        {
            var directory = await repository.CreateRoleAsync("扫码测试-" + username, "接口回归", "ProductionViewer", default);
            var definition = directory.Roles.Single(item => item.Name == "扫码测试-" + username);
            await repository.SetRolePermissionsAsync(definition.Role, [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, permission], default);
            roleCodes[username] = definition.Role;
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, "人员-" + username, "unused", UserRole.ProductionViewer, true, RoleCode: definition.Role), default);
            await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        }
        var project = await repository.CreateProjectAsync(new("SCAN-API-" + Guid.NewGuid().ToString("N"), "扫码接口回归", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "02041000013", "垫片", 2, "件", "钢", "7002541.00-02", "W1", true);
        var release = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-SCAN-API", ReleasePackageState.Published, null, "W1", "W1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
        { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        await repository.CreateReleasePackageAsync(release, default);
        using var admin = Client("admin", UserRole.Administrator);
        var catalog = (await admin.GetFromJsonAsync<ProductionScanCatalog>("/api/production-tracking/scan-settings"))!;
        Assert.Empty(catalog.Bindings);
        Assert.Empty(catalog.Workstations);
        var initial = (await (await admin.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/receive", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        var assigned = (await (await admin.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/lots", new { lineId = initial.State.Lines[0].Id, route = "Self", quantity = 2, requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        var lot = Assert.Single(assigned.State.Lots);
        var stations = new[] { new ProductionWorkstation(Guid.NewGuid(), "接收台", "", true), new ProductionWorkstation(Guid.NewGuid(), "机加工位", "CNC组", true),
            new ProductionWorkstation(Guid.NewGuid(), "包装台", "", true), new ProductionWorkstation(Guid.NewGuid(), "质检台", "", true), new ProductionWorkstation(Guid.NewGuid(), "领料台", "", true) };
        var bindings = new[] { new ProductionPostBinding(Guid.NewGuid(), "Role", roleCodes["scan-receive"], "Reception", [stations[0].Id], true),
            new ProductionPostBinding(Guid.NewGuid(), "User", "scan-machine", "Machining", [stations[1].Id], true),
            new ProductionPostBinding(Guid.NewGuid(), "User", "scan-pack", "Packing", [stations[2].Id], true),
            new ProductionPostBinding(Guid.NewGuid(), "User", "scan-quality", "QualityReceiving", [stations[3].Id], true),
            new ProductionPostBinding(Guid.NewGuid(), "User", "scan-quality", "Inspection", [stations[3].Id], true),
            new ProductionPostBinding(Guid.NewGuid(), "User", "scan-issue", "Issue", [stations[4].Id], true) };
        var config = new SaveProductionScanCatalog(stations, bindings, catalog.RowVersion);
        var saved = await admin.PutAsJsonAsync("/api/production-tracking/scan-settings", config);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/production-tracking/scan-settings", config)).StatusCode);
        using var receive = Client("scan-receive"); using var machine = Client("scan-machine"); using var pack = Client("scan-pack"); using var quality = Client("scan-quality"); using var issue = Client("scan-issue");
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.GetAsync("/api/production-tracking/scan-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.GetAsync("/api/production-tracking/scan-settings/options")).StatusCode);
        async Task<ProductionScanContext> Actions(HttpClient client) => (await client.GetFromJsonAsync<ProductionScanContext>($"/api/production-tracking/releases/{release.Id}/actions"))!;
        Assert.Equal("ProductionReceived", Assert.Single((await Actions(receive)).Actions).Kind);
        Assert.Empty((await Actions(machine)).Actions);
        var qr = Uri.EscapeDataString($"UPLM-PRODUCTION|{release.Id}|{lot.Id}");
        Assert.Equal(lot.Id, Assert.Single((await receive.GetFromJsonAsync<ProductionScanResult[]>($"/api/production-tracking/scan?code={qr}"))!).LotId);
        async Task<ProductionTrackingRecord> Move(HttpClient client, string kind, int station, Guid? key = null)
        {
            var response = await client.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/movements", new
            {
                lotId = lot.Id, kind, quantity = 2, requestId = key ?? Guid.NewGuid(), workstationId = stations[station].Id,
                recipient = kind == "Issue" ? "真实领用人" : null, actor = "伪造操作人", actorName = "伪造姓名", occurredAt = "2000-01-01T00:00:00Z"
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        }
        var receiptKey = Guid.NewGuid();
        var record = await Move(receive, "ProductionReceived", 0, receiptKey);
        Assert.Equal(record.RowVersion, (await Move(receive, "ProductionReceived", 0, receiptKey)).RowVersion);
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/production-tracking/capacity", new { resourceGroupId = "CNC组", date, availableHours = 8, expectedRowVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/schedule", new { lotId = lot.Id, slots = new[] { new { resourceGroupId = "CNC组", date, hours = 1 } }, promisedOn = date, requestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal("Start", Assert.Single((await Actions(machine)).Actions).Kind);
        Assert.Empty((await Actions(pack)).Actions);
        var wrongStation = await machine.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/movements", new { lotId = lot.Id, kind = "Start", quantity = 2, requestId = Guid.NewGuid(), workstationId = stations[2].Id });
        Assert.Equal(HttpStatusCode.BadRequest, wrongStation.StatusCode);
        record = await Move(machine, "Start", 1);
        record = await Move(machine, "MachiningCompleted", 1);
        Assert.Equal("PackingStarted", Assert.Single((await Actions(pack)).Actions).Kind);
        record = await Move(pack, "PackingStarted", 2);
        record = await Move(pack, "Packed", 2);
        record = await Move(quality, "QualityReceived", 3);
        Assert.Contains((await Actions(quality)).Actions, action => action.Kind == "Pass");
        var over = await quality.PostAsJsonAsync($"/api/production-tracking/releases/{release.Id}/movements", new { lotId = lot.Id, kind = "Pass", quantity = 3, requestId = Guid.NewGuid(), workstationId = stations[3].Id });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        record = await Move(quality, "Pass", 3);
        record = await Move(issue, "Issue", 4);
        Assert.Empty((await Actions(machine)).Actions);
        Assert.Empty((await Actions(issue)).Actions);
        var events = record.State.Movements.Where(movement => ProductionFlowRules.Labels.ContainsKey(movement.Kind)).ToArray();
        Assert.Equal(8, events.Length);
        Assert.All(events, movement => { Assert.StartsWith("scan-", movement.Actor); Assert.Equal("人员-" + movement.Actor, movement.ActorName); Assert.NotNull(movement.BindingId); Assert.NotNull(movement.WorkstationName); Assert.True(movement.OccurredAt.Year > 2000); Assert.NotNull(movement.FromStage); Assert.NotNull(movement.ToStage); });
    }
}
