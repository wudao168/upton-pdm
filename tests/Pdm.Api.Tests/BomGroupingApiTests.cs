using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomGroupingApiTests
{
    [Fact]
    public async Task DuplicateAdditionSaveKeepsAccessoryOwnersAndPublishedSnapshot()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var materialService = scope.ServiceProvider.GetRequiredService<MaterialService>();
        await scope.ServiceProvider.GetRequiredService<IMaterialBrandRepository>().SaveAsync(new(Guid.NewGuid(), "测试", true, 0, []), null, default);
        var material = await materialService.CreateAsync(new(null, "汇总测试物料", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "QA-GROUP", null, null, "测试", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
        var sample = new BomItem(Guid.NewGuid(), Guid.Empty, BomKind.Standard, 1, material.MaterialCode, material.Name, 1, "001", null, material.Specification, "W1", true) { Brand = material.Brand };
        var project = await repo.CreateProjectAsync(new("GROUP-API-" + Guid.NewGuid().ToString("N"), "BOM汇总接口测试", "admin", "D:/qa", "D:/qa-release"), "admin", default);
        var main = sample with { Id = Guid.NewGuid(), ProjectId = project.Id, Sequence = 1, Quantity = 1, Source = "Manual", SourceDocumentId = null, Revision = "W1" };
        var ownerA = Guid.NewGuid(); var ownerB = Guid.NewGuid();
        var first = main with { Id = Guid.NewGuid(), Sequence = 2, Quantity = 2, Source = "MaterialRelation", RelationMainBomItemId = ownerA };
        var second = first with { Id = Guid.NewGuid(), Sequence = 3, Quantity = 3, RelationMainBomItemId = ownerB };
        await repo.ReplaceBomAsync(project.Id, BomKind.Standard, [main, first, second], default);
        var published = await repo.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-GROUP-FROZEN", ReleasePackageState.Published,
            null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "") { Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = [main, first, second] }, default);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); options.Converters.Add(new JsonStringEnumConverter());
        var url = $"/api/projects/{project.Id}/boms/Standard?summary=true";
        var groups = (await client.GetFromJsonAsync<BomItem[]>(url, options))!;
        Assert.Equal(3, groups.Length);
        var group = groups.Single(row => row.Id == main.Id);
        var values = new BomItemInput(1, main.DrawingNumber, main.Name, 2, main.Unit, main.Material, main.Specification, main.Revision, true,
            Remark: main.Remark, Brand: main.Brand, SurfaceTreatment: main.SurfaceTreatment, Weight: main.Weight);
        var response = await client.PutAsJsonAsync(url, new { items = Array.Empty<BomItemInput>(), summaryOrder = groups.Select(row => row.Id),
            summaryRows = new[] { new BomSummaryEdit(group.SummaryItemIds!, group.SummaryFingerprint!, ["quantity", "engineeringKitReference"], values) } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var saved = await repo.GetBomAsync(project.Id, BomKind.Standard, default);
        Assert.Equal(2, saved.Single(row => row.Id == main.Id).Quantity);
        Assert.Equal(ownerA, saved.Single(row => row.Id == first.Id).RelationMainBomItemId);
        Assert.Equal(ownerB, saved.Single(row => row.Id == second.Id).RelationMainBomItemId);
        var frozen = (await repo.FindReleasePackageAsync(published.Id, default))!.StandardBomSnapshot;
        Assert.Equal(1, frozen.Single(row => row.Id == main.Id).Quantity);
        Assert.Equal(6, frozen.Sum(row => row.Quantity));
        groups = (await client.GetFromJsonAsync<BomItem[]>(url, options))!;
        group = groups.Single(row => row.Id == main.Id);
        response = await client.PutAsJsonAsync(url, new { items = Array.Empty<BomItemInput>(), summaryRows = new[] {
            new BomSummaryEdit(group.SummaryItemIds!, group.SummaryFingerprint!, ["engineeringKitReference"], values with { EngineeringKitReferenceId = Guid.NewGuid() }) } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, (await repo.GetBomAsync(project.Id, BomKind.Standard, default)).Single(row => row.Id == main.Id).Quantity);
    }
}
