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

public sealed class EngineeringKitCategoryApiTests
{
    private static string Token(string name, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task StandardMaterialArchiveAndRestoreUseExistingMasterAndRespectInactiveFilter()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var categoryResponse = await client.PostAsJsonAsync("/api/standard-library/categories", new { name = "普通物料启停分类", isActive = true, sortOrder = 1 });
        categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<StandardLibraryCategory>())!;
        var now = DateTimeOffset.UtcNow;
        var material = new PdmMaterial(Guid.NewGuid(), "01021111222", "普通标准物料", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", "TEST-01", null, null, "UPTON", null,
            null, null, null, MaterialApprovalStatus.Approved, "admin", now, "0102", "u9-id", "01021111222", MaterialSyncStatus.Succeeded,
            "admin", now, "admin", now, 1, "0102", U9SyncConfirmed: true, SourceSystem: MaterialDataSource.Pdm, MasterOwner: MaterialMasterOwner.Pdm);
        var materialRepository = factory.Services.GetRequiredService<IMaterialRepository>();
        var materialCategory = (await materialRepository.FindCategoryAsync("0102", default))!;
        material = await materialRepository.CreateMaterialAsync(material, materialCategory, default);
        (await client.PostAsJsonAsync("/api/standard-library/memberships", new { categoryIds = new[] { category.Id }, materialIds = new[] { material.Id } })).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/materials/{material.Id}/archive?expectedRowVersion=1", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var archiveResponse = await client.PostAsync($"/api/materials/{material.Id}/archive?expectedRowVersion=1", null);
        archiveResponse.EnsureSuccessStatusCode();
        var archived = await archiveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(archived.GetProperty("isArchived").GetBoolean());
        var hidden = await client.GetFromJsonAsync<JsonElement>("/api/standard-library/materials");
        Assert.Equal(0, hidden.GetProperty("total").GetInt32());
        var shown = await client.GetFromJsonAsync<JsonElement>("/api/standard-library/materials?includeInactive=true");
        var row = Assert.Single(shown.GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("material").GetProperty("isArchived").GetBoolean());
        Assert.Equal(category.Id, row.GetProperty("categories")[0].GetProperty("id").GetGuid());
        var reactivateResponse = await client.PostAsync($"/api/materials/{material.Id}/reactivate?expectedRowVersion={archived.GetProperty("rowVersion").GetInt64()}", null);
        reactivateResponse.EnsureSuccessStatusCode();
        var restored = await reactivateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(restored.GetProperty("isArchived").GetBoolean());
        Assert.Equal(material.MaterialCode, restored.GetProperty("materialCode").GetString());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/standard-library/materials")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PublishPlacesKitInChosenCategoryAndEngineerReadsOnlyReleasedDetails()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var categoryResponse = await client.PostAsJsonAsync("/api/standard-library/categories", new { name = "套件测试分类", code = "kt", isActive = true, sortOrder = 1 });
        categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<StandardLibraryCategory>())!;
        Assert.Equal("UTJ-01", category.Code);
        var now = DateTimeOffset.UtcNow;
        var material = new PdmMaterial(Guid.NewGuid(), "01029999999", "测试子料", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", "PIN-01", null, "备注", "UPTON", null,
            null, null, null, MaterialApprovalStatus.Approved, "tester", now, "0102", "u9-test", "01029999999", MaterialSyncStatus.Succeeded,
            "tester", now, "tester", now, 1, "0102", false, null, null, true, MaterialDataSource.U9C, MaterialMasterOwner.U9C, now);
        await factory.Services.GetRequiredService<IMaterialRepository>().UpsertU9MaterialAsync(material, default);
        var input = new { name = "标准套件", brand = "UPTON", modelMode = "Auto", standardLibraryCategoryId = category.Id,
            components = new[] { new { materialId = material.Id, quantity = 1, sortOrder = 1, isOptional = false } } };
        var created = await client.PostAsJsonAsync("/api/engineering-kits", input);
        created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.Empty((await client.GetFromJsonAsync<EngineeringKit[]>($"/api/engineering-kits/library?categoryId={category.Id}"))!);
        var publishedResponse = await client.PostAsJsonAsync($"/api/engineering-kits/{draft.Id}/publish", new { expectedRowVersion = draft.RowVersion });
        publishedResponse.EnsureSuccessStatusCode();
        var released = (await publishedResponse.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.Equal("UTJ-01-0001", released.Model);
        Assert.Equal(category.Id, released.StandardLibraryCategoryId);
        Assert.Single((await client.GetFromJsonAsync<EngineeringKit[]>($"/api/engineering-kits/library?categoryId={category.Id}"))!);
        Assert.Empty((await client.GetFromJsonAsync<EngineeringKit[]>($"/api/engineering-kits/library?categoryId={StandardLibraryDefaults.UncategorizedId}"))!);
        var edited = await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}", new { input.name, input.brand, input.modelMode, input.standardLibraryCategoryId, expectedRowVersion = released.RowVersion,
            components = new[] { new { materialId = material.Id, quantity = 2, sortOrder = 1, isOptional = false } } });
        edited.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        var view = (await client.GetFromJsonAsync<EngineeringKit>($"/api/engineering-kits/{draft.Id}"))!;
        var revision = Assert.Single(view.Revisions);
        Assert.Equal(EngineeringKitRevisionState.Released, revision.State);
        Assert.Equal(1, revision.Components[0].Quantity);
        Assert.Equal("PIN-01", revision.Components[0].Specification);
        Assert.Equal("UPTON", revision.Components[0].Brand);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/engineering-kits", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}/category", new { categoryId = category.Id, expectedRowVersion = released.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}/active", new { isActive = false, expectedRowVersion = released.RowVersion })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var current = (await client.GetFromJsonAsync<EngineeringKit>($"/api/engineering-kits/{draft.Id}"))!;
        var deactivatedResponse = await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}/active", new { isActive = false, expectedRowVersion = current.RowVersion });
        deactivatedResponse.EnsureSuccessStatusCode();
        var deactivated = (await deactivatedResponse.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.False(deactivated.IsActive);
        Assert.Equal(released.Model, deactivated.Model);
        Assert.Equal(current.Revisions.Count, deactivated.Revisions.Count);
        var defaultLibraryPage = await client.GetFromJsonAsync<JsonElement>($"/api/standard-library/materials?categoryId={category.Id}");
        Assert.Empty(defaultLibraryPage.GetProperty("items").EnumerateArray());
        var libraryPage = await client.GetFromJsonAsync<JsonElement>($"/api/standard-library/materials?categoryId={category.Id}&includeInactive=true");
        Assert.False(libraryPage.GetProperty("items")[0].GetProperty("kit").GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}/active", new { isActive = true, expectedRowVersion = current.RowVersion })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        var retained = (await client.GetFromJsonAsync<EngineeringKit>($"/api/engineering-kits/{draft.Id}"))!;
        Assert.False(retained.IsActive);
        Assert.Single(retained.Revisions);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/engineering-kits/{draft.Id}/expand", new { quantity = 1 })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var reactivated = await client.PutAsJsonAsync($"/api/engineering-kits/{draft.Id}/active", new { isActive = true, expectedRowVersion = deactivated.RowVersion });
        reactivated.EnsureSuccessStatusCode();
        Assert.True((await reactivated.Content.ReadFromJsonAsync<EngineeringKit>())!.IsActive);
    }
}
