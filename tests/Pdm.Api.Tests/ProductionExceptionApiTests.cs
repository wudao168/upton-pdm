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

namespace Upton.Pdm.Api.Tests;

public sealed class ProductionExceptionApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    private HttpClient Client(string actor, UserRole role)
    {
        var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role.ToString())],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("External")]
    public async Task HttpActionsPersistStageQuantitiesEnforcePermissionsAndFinishReplacement(string route)
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var store = factory.Services.GetRequiredService<IProductionTrackingStore>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(o => o.IsActive).Id;
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        var project = await repository.CreateProjectAsync(new("EX-API-" + Guid.NewGuid().ToString("N"), "制造异常接口模拟", "admin", "vault", "release"), "admin", default);
        var bom = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "EX-API", "接口模拟件", 10, "件", "钢", "EX-API-MODEL", "B", true);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "EX-API-RP", ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [bom], SelectedBomItemIds = [bom.Id] };
        await repository.CreateReleasePackageAsync(package, default);
        var seeder = new ProductionTrackingService(repository, store, TimeProvider.System, new ProductionDrawingService(repository));
        using var client = Client("admin", UserRole.Administrator);
        var received = await client.PostAsJsonAsync($"/api/production-tracking/releases/{package.Id}/receive", new { requestId = Guid.NewGuid() });
        Assert.True(received.IsSuccessStatusCode, await received.Content.ReadAsStringAsync());
        var initial = (await received.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        initial = await seeder.AssignAsync(package.Id, new(initial.State.Lines[0].Id, route, 10, route == "External" ? "模拟外协厂" : null, null, DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        var lotId = initial.State.Lots[0].Id;
        var endpoint = $"/api/production-tracking/releases/{package.Id}";
        var catalog = (await client.GetFromJsonAsync<ProductionScanCatalog>("/api/production-tracking/scan-settings"))!;
        var station = new ProductionWorkstation(Guid.NewGuid(), "接口模拟工位", "CNC组", true);
        var config = new SaveProductionScanCatalog([station], ProductionFlowRules.Posts.Keys.Select(post =>
            new ProductionPostBinding(Guid.NewGuid(), "Role", "Administrator", post, [station.Id], true)).ToArray(), catalog.RowVersion)
            { FlowMode = "DepartmentHandover", ControlRoleCodes = ["Administrator"] };
        var saved = await client.PutAsJsonAsync("/api/production-tracking/scan-settings", config);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        async Task<ProductionScanContext> Actions() => (await client.GetFromJsonAsync<ProductionScanContext>(endpoint + "/actions"))!;
        async Task<ProductionTrackingRecord> Move(string kind, decimal quantity, string? source = null, Guid? request = null)
        {
            var response = await client.PostAsJsonAsync(endpoint + "/movements", new RecordProductionMovement(lotId, kind, quantity, request ?? Guid.NewGuid(),
                "接口模拟，实物已隔离", kind == "Issue" ? "模拟领用人" : null, ProductionDepartmentFlow.IsControl(kind) ? null : station.Id, source));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!;
        }
        Assert.Contains((await Actions()).Actions, a => a.Kind == "StageScrap" && a.SourceKind == "Assigned");
        await Move("Pause", 3, "Assigned");
        var key = Guid.NewGuid();
        var cancelled = await Move("TaskCancelled", 2, "Assigned", key);
        Assert.Equal(cancelled.RowVersion, (await Move("TaskCancelled", 2, "Assigned", key)).RowVersion);
        var oversized = await client.PostAsJsonAsync(endpoint + "/movements", new RecordProductionMovement(lotId, "StageScrap", 6, Guid.NewGuid(), "超量模拟", SourceKind: "Assigned"));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        await Move("StageScrap", 2, "Assigned");
        await Move("Resume", 3, "Assigned");
        async Task Finish()
        {
            for (var step = 0; step < 12; step++)
            {
                var action = (await Actions()).Actions.FirstOrDefault(a => a.LotId == lotId && a.Post != "Control" && a.Kind is not ("Trace" or "Rework" or "Scrap"));
                if (action is null) return;
                await Move(action.Kind, action.RemainingQuantity);
            }
            Assert.Fail("接口流转未结束。");
        }
        await Finish();
        var shortage = (await client.GetFromJsonAsync<ProductionTrackingRecord>(endpoint))!;
        Assert.Equal("数量不足，待补齐", ProductionTrackingRules.Status(shortage.State));
        Assert.Equal(6, ProductionTrackingRules.Issued(shortage.State, initial.State.Lines[0].Id));
        var demandInput = new CancelUnassignedProductionDemand(initial.State.Lines[0].Id, 1, "取消一件补制需求", Guid.NewGuid());
        var demandResponse = await client.PostAsJsonAsync(endpoint + "/cancel-unassigned-demand", demandInput);
        Assert.Equal(HttpStatusCode.OK, demandResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(endpoint + "/cancel-unassigned-demand", demandInput)).StatusCode);
        var replacementResponse = await client.PostAsJsonAsync(endpoint + "/lots", new AssignProductionLot(initial.State.Lines[0].Id, "Self", 3, null, null, null, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, replacementResponse.StatusCode);
        lotId = (await replacementResponse.Content.ReadFromJsonAsync<ProductionTrackingRecord>())!.State.Lots.Last().Id;
        await Finish();
        var final = (await client.GetFromJsonAsync<ProductionTrackingRecord>(endpoint))!;
        Assert.Equal(9, ProductionTrackingRules.Issued(final.State, initial.State.Lines[0].Id));
        Assert.Equal("变更已结案", ProductionTrackingRules.Status(final.State));
        Assert.Empty((await Actions()).Actions);

        // 有页面查看权限也不能自行取得异常处理授权。
        var username = "ex-view-" + Guid.NewGuid().ToString("N");
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "只读模拟员", "unused", UserRole.ProductionViewer, true), default);
        await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        using var viewer = Client(username, UserRole.ProductionViewer);
        var forbidden = await viewer.PostAsJsonAsync(endpoint + "/movements", new RecordProductionMovement(lotId, "StageScrap", 1, Guid.NewGuid(), "无权限模拟", SourceKind: "Assigned"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
