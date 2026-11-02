using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class MaterialApiTests
{
    [Fact]
    public async Task SharedMainScopeIsPublishedTogetherAndLegacyEditsPreserveTheRange()
    {
        using var services = factory.Services.CreateScope();
        var materials = services.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var materialService = services.ServiceProvider.GetRequiredService<MaterialService>();
        var library = services.ServiceProvider.GetRequiredService<StandardLibraryService>();
        var marker = Guid.NewGuid().ToString("N");
        async Task<PdmMaterial> Create(string name)
        {
            var created = await materialService.CreateAsync(new(null, name + marker, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", name + marker, null, null, "UPTON", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
            return await materials.UpdateMaterialAsync(created with { ApprovalStatus = MaterialApprovalStatus.Approved, U9SyncConfirmed = true, SyncStatus = MaterialSyncStatus.Succeeded }, created.RowVersion, default);
        }
        var a = await Create("M6x18"); var b = await Create("M6x20"); var c = await Create("M6x25"); var part = await Create("M6垫圈");
        var category = await library.SaveCategoryAsync(null, new("共享配置验证" + marker, null, 1, true), "admin", UserRole.Administrator, default);
        async Task Classify(PdmMaterial material) => (await client.PutAsJsonAsync($"/api/material-relations/materials/{material.Id}/categories", new { categoryId = category.Id })).EnsureSuccessStatusCode();
        await Classify(a); await Classify(b);
        var groups = new[] { new { name = "垫圈", isRequired = true, selectionMode = "Single", minSelection = 1, maxSelection = 1, autoSelectUnique = false, sortOrder = 1,
            options = new[] { new { materialId = part.Id, quantityMode = "PerMainQuantity", quantityPerSet = 2, isDefault = false, sortOrder = 1 } } } };
        object Body(params PdmMaterial[] mains) => new { mainMaterialId = mains[0].Id, mainMaterialIds = mains.Select(item => item.Id).ToArray(), name = "M6系列共享配置", groups };
        async Task<MaterialRelationTemplate> Read(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<MaterialRelationTemplate>())!; }
        var draft = await Read(await client.PostAsJsonAsync("/api/material-relations/templates", Body(a, b)));
        Assert.Equal(2, draft.MainMaterials.Count);
        async Task<HttpResponseMessage> Publish(MaterialRelationTemplate template) => await client.PostAsJsonAsync($"/api/material-relations/templates/{template.Id}/publish", new { revisionId = template.DraftRevision!.Id, expectedRowVersion = template.DraftRevision.RowVersion });
        var first = await Read(await Publish(draft));
        Assert.Equal(new[] { a.Id, b.Id }, first.PublishedMainMaterials.Select(item => item.MaterialId));
        var next = await Read(await client.PutAsJsonAsync($"/api/material-relations/templates/{draft.Id}/draft", Body(b, c)));
        var published = Assert.Single((await client.GetFromJsonAsync<MaterialRelationTemplate[]>("/api/material-relations/templates"))!, item => item.Id == draft.Id);
        Assert.Contains(published.MainMaterials, item => item.MaterialId == a.Id);
        Assert.DoesNotContain(published.MainMaterials, item => item.MaterialId == c.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/material-relations/templates", Body(c))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(next)).StatusCode);
        await Classify(c);
        var changed = await Read(await Publish(next));
        Assert.Equal(new[] { b.Id, c.Id }, changed.PublishedMainMaterials.Select(item => item.MaterialId));
        await Read(await client.PostAsJsonAsync("/api/material-relations/templates", Body(a)));
        using var page = JsonDocument.Parse(await client.GetStringAsync($"/api/standard-library/materials?categoryId={category.Id}"));
        var rows = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(JsonValueKind.Null, rows.Single(row => row.GetProperty("material").GetProperty("id").GetGuid() == a.Id).GetProperty("relation").ValueKind);
        Assert.All(rows.Where(row => row.GetProperty("material").GetProperty("id").GetGuid() != a.Id), row => Assert.Equal(draft.Id, row.GetProperty("relation").GetProperty("id").GetGuid()));
        var legacy = await Read(await client.PutAsJsonAsync($"/api/material-relations/templates/{draft.Id}/draft", new { mainMaterialId = b.Id, name = "兼容旧客户端保存", groups }));
        Assert.Equal(2, legacy.MainMaterials.Count);
        using var engineer = factory.CreateClient();
        engineer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        var readOnly = (await engineer.GetFromJsonAsync<MaterialRelationTemplate[]>("/api/material-relations/templates?includeDraft=true"))!;
        Assert.All(readOnly, item => Assert.Null(item.DraftRevision));
    }
}
