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
    public async Task RelationCategoryEndpointsUseStandardTaxonomyAndGatePublication()
    {
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var materialService = scope.ServiceProvider.GetRequiredService<MaterialService>();
        var library = scope.ServiceProvider.GetRequiredService<StandardLibraryService>();
        var marker = Guid.NewGuid().ToString("N");
        async Task<PdmMaterial> Create(string name)
        {
            var saved = await materialService.CreateAsync(new(null, name + marker, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", name + marker, null, null, "UPTON", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
            return await materials.UpdateMaterialAsync(saved with { ApprovalStatus = MaterialApprovalStatus.Approved, U9SyncConfirmed = true, SyncStatus = MaterialSyncStatus.Succeeded }, saved.RowVersion, default);
        }
        var main = await Create("主料");
        var part = await Create("配件");
        var category = await library.SaveCategoryAsync(null, new("关联分类" + marker, null, 1, true), "admin", UserRole.Administrator, default);
        var catalog = await client.GetFromJsonAsync<StandardLibraryCategory[]>("/api/material-relations/categories");
        Assert.Contains(catalog!, item => item.Id == category.Id);
        var created = await client.PostAsJsonAsync("/api/material-relations/templates", new { mainMaterialId = main.Id, name = "关联配置", groups = new[] { new { name = "附件", isRequired = true, selectionMode = "Single", minSelection = 1, maxSelection = 1, autoSelectUnique = false, sortOrder = 1, options = new[] { new { materialId = part.Id, quantityMode = "PerMainQuantity", quantityPerSet = 2, isDefault = true, sortOrder = 1 } } } } });
        created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<MaterialRelationTemplate>())!;
        var publishPath = $"/api/material-relations/templates/{draft.Id}/publish";
        var publishBody = new { revisionId = draft.DraftRevision!.Id, expectedRowVersion = draft.DraftRevision.RowVersion };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(publishPath, publishBody)).StatusCode);
        var categoryPath = $"/api/material-relations/materials/{main.Id}/categories";
        using var readOnly = factory.CreateClient();
        readOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsJsonAsync(categoryPath, new { categoryId = category.Id })).StatusCode);
        (await client.PutAsJsonAsync(categoryPath, new { categoryId = category.Id })).EnsureSuccessStatusCode();
        Assert.Single((await client.GetFromJsonAsync<StandardLibraryCategory[]>(categoryPath))!);
        (await client.PostAsJsonAsync(publishPath, publishBody)).EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await client.GetStringAsync($"/api/standard-library/materials?categoryId={category.Id}"));
        var row = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(main.Id, row.GetProperty("material").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Object, row.GetProperty("relation").GetProperty("publishedRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("relation").GetProperty("draftRevision").ValueKind);
    }
}
