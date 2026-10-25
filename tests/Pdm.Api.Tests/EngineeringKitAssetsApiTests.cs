using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class EngineeringKitAssetsApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static Task<StandardLibraryMaterialPage?> Page(HttpClient client, string path) => client.GetFromJsonAsync<StandardLibraryMaterialPage>(path, Json);
    private static string Token(string actor, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Theory]
    [InlineData("Model3D", "kit.step")]
    [InlineData("Document", "kit.pdf")]
    public async Task KitSharesListPagingButKeepsIndependentAttachmentsAndMaterialIdentity(string kind, string fileName)
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var materialStore = factory.Services.GetRequiredService<IMaterialRepository>();
        var now = DateTimeOffset.UtcNow;
        var categoryResponse = await client.PostAsJsonAsync("/api/standard-library/categories", new { name = "合并列表测试", isActive = true, sortOrder = 1 });
        categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<StandardLibraryCategory>())!;
        var material = new PdmMaterial(Guid.NewGuid(), "01029999999", "真实子料", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", "PIN-01", null, "子料备注", "UPTON", null,
            null, null, null, MaterialApprovalStatus.Approved, "admin", now, "0102", "u9-test", "01029999999", MaterialSyncStatus.Succeeded,
            "admin", now, "admin", now, 1, "0102", U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C);
        await materialStore.UpsertU9MaterialAsync(material, default);
        (await client.PostAsJsonAsync("/api/standard-library/memberships", new { categoryIds = new[] { category.Id }, materialIds = new[] { material.Id } })).EnsureSuccessStatusCode();
        var created = await client.PostAsJsonAsync("/api/engineering-kits", new { name = "套件名称", brand = "UPTON", description = "套件备注", modelMode = "Manual", model = "KIT-MODEL", standardLibraryCategoryId = category.Id,
            components = new[] { new { materialId = material.Id, quantity = 2, sortOrder = 1, isOptional = false } } });
        created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<EngineeringKit>())!;
        var published = await client.PostAsJsonAsync($"/api/engineering-kits/{draft.Id}/publish", new { expectedRowVersion = draft.RowVersion });
        published.EnsureSuccessStatusCode();
        var kit = (await published.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.Null(await materialStore.FindMaterialAsync(kit.Id, default));

        var prefix = $"/api/standard-library/materials?categoryId={category.Id}&pageSize=1";
        var first = (await Page(client, prefix + "&page=1"))!;
        var second = (await Page(client, prefix + "&page=2"))!;
        Assert.Equal(2, first.Total); Assert.Null(Assert.Single(first.Items).Kit);
        var row = Assert.Single(second.Items); Assert.Equal(kit.Id, row.Kit!.Id);
        Assert.Equal((kit.Code, kit.Name, kit.Model, kit.Brand, kit.Description),
            (row.Material.MaterialCode, row.Material.Name, row.Material.Specification, row.Material.Brand, row.Material.Remark));
        Assert.Empty((await Page(client, prefix + "&recommendedOnly=true"))!.Items);
        Assert.Single((await Page(client, prefix + "&query=KIT-MODEL&brand=UPTON"))!.Items);

        var root = Path.Combine(Path.GetTempPath(), "pdm-kit-assets-" + Guid.NewGuid().ToString("N"));
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { MaterialAttachmentRoot = root }, default);
        var payload = Encoding.UTF8.GetBytes("controlled kit attachment");
        var uploadRequest = new { kind, fileName, totalLength = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)) };
        var start = await client.PostAsJsonAsync($"/api/materials/{kit.Id}/attachments/uploads", uploadRequest);
        start.EnsureSuccessStatusCode();
        var session = (await start.Content.ReadFromJsonAsync<MaterialAttachmentUploadSession>(Json))!;
        (await client.PutAsync($"/api/material-attachment-uploads/{session.Id}/chunks/0", new ByteArrayContent(payload))).EnsureSuccessStatusCode();
        var complete = await client.PostAsync($"/api/material-attachment-uploads/{session.Id}/complete", null);
        complete.EnsureSuccessStatusCode();
        var attachment = (await complete.Content.ReadFromJsonAsync<MaterialAttachment>(Json))!;
        Assert.Equal(payload, await client.GetByteArrayAsync($"/api/materials/{kit.Id}/attachments/{attachment.Id}/file"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/materials/{material.Id}/attachments/{attachment.Id}/file")).StatusCode);
        Assert.Empty(await materialStore.ListMaterialAttachmentsAsync(material.Id, null, default));
        var refreshed = Assert.Single((await Page(client, prefix + "&query=KIT-MODEL"))!.Items);
        Assert.Equal(1, kind == "Model3D" ? refreshed.Material.Model3DAttachmentCount : refreshed.Material.DocumentAttachmentCount);
        Assert.Equal(kit.CurrentReleasedRevisionId, refreshed.Kit!.CurrentReleasedRevisionId);
        Assert.Equal(kit.RowVersion, refreshed.Kit.RowVersion);
        var project = (await repository.ListProjectsAsync(default)).First();
        var source = (await repository.GetBomAsync(project.Id, BomKind.Standard, default)).First();
        var reference = Guid.NewGuid();
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [
            source with { Id = Guid.NewGuid(), EngineeringKitId = kit.Id, EngineeringKitReferenceId = reference },
            source with { Id = Guid.NewGuid(), EngineeringKitId = kit.Id, EngineeringKitReferenceId = reference },
            source with { Id = Guid.NewGuid(), EngineeringKitId = kit.Id, EngineeringKitReferenceId = Guid.NewGuid() }
        ], default);
        var used = Assert.Single((await Page(client, prefix + "&query=KIT-MODEL"))!.Items);
        Assert.Equal(2, used.Material.ReferenceCount);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Single((await client.GetFromJsonAsync<MaterialAttachment[]>($"/api/materials/{kit.Id}/attachments", Json))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/materials/{kit.Id}/attachments/uploads", uploadRequest)).StatusCode);
        Assert.Null(await materialStore.FindMaterialAsync(kit.Id, default));
    }

    [Fact]
    public async Task LibraryMetadataKeepsIdentityAndReleasedCompositionAndRejectsStaleOrUnauthorizedUpdates()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var materialStore = factory.Services.GetRequiredService<IMaterialRepository>();
        var kit = await CreateKit(client, materialStore, "资料维护套件");
        var savedResponse = await client.PutAsJsonAsync($"/api/engineering-kits/{kit.Id}/library-metadata", new {
            remark = "新的套件备注", selectionAdvice = "适用于真空系统", referencePrice = 123.45m, isRecommended = true,
            coverImageAttachmentId = (Guid?)null, expectedRowVersion = kit.RowVersion, name = "不应改名", model = "不应改型号", brand = "不应改品牌", components = Array.Empty<object>() });
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.Equal((kit.Code, kit.Name, kit.Model, kit.Brand, kit.CurrentReleasedRevisionId), (saved.Code, saved.Name, saved.Model, saved.Brand, saved.CurrentReleasedRevisionId));
        Assert.Equal(JsonSerializer.Serialize(kit.Revisions), JsonSerializer.Serialize(saved.Revisions));
        Assert.Equal(("新的套件备注", "适用于真空系统", 123.45m, true), (saved.Description, saved.SelectionAdvice, saved.ReferencePrice, saved.IsRecommended));
        Assert.Equal(kit.RowVersion + 1, saved.RowVersion);
        var stale = await client.PutAsJsonAsync($"/api/engineering-kits/{kit.Id}/library-metadata", new SaveEngineeringKitLibraryMetadataCommand("旧页面", null, null, false, null, kit.RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var row = Assert.Single((await Page(client, "/api/standard-library/materials?recommendedOnly=true&query=" + kit.Code))!.Items);
        Assert.Equal(saved.SelectionAdvice, row.Material.SelectionAdvice); Assert.Equal(saved.ReferencePrice, row.Material.ReferencePrice);
        Assert.Null(await materialStore.FindMaterialAsync(kit.Id, default));
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/engineering-kits/{kit.Id}/library-metadata", new SaveEngineeringKitLibraryMetadataCommand("未授权", null, null, false, null, saved.RowVersion))).StatusCode);
        Assert.Equal(saved.Description, (await kits.FindAsync(kit.Id, default))!.Description);
    }

    [Fact]
    public async Task KitPicturesSupportCoverAndDownloadsAndEnforceThreePictureLimitAtCompletion()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var materialStore = factory.Services.GetRequiredService<IMaterialRepository>();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var kit = await CreateKit(client, materialStore, "图片套件");
        var other = await CreateKit(client, materialStore, "另一套件", kit.Revisions[0].Components[0].MaterialId);
        var root = Path.Combine(Path.GetTempPath(), "pdm-kit-pictures-" + Guid.NewGuid().ToString("N"));
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { MaterialAttachmentRoot = root }, default);
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR0kAAAAASUVORK5CYII=");
        var sessions = new List<MaterialAttachmentUploadSession>();
        for (var i = 0; i < 4; i++)
        {
            var start = await client.PostAsJsonAsync($"/api/materials/{kit.Id}/attachments/uploads", new { kind = "CoverImage", fileName = $"泵{i}.png", totalLength = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            start.EnsureSuccessStatusCode(); var session = (await start.Content.ReadFromJsonAsync<MaterialAttachmentUploadSession>(Json))!;
            (await client.PutAsync($"/api/material-attachment-uploads/{session.Id}/chunks/0", new ByteArrayContent(bytes))).EnsureSuccessStatusCode(); sessions.Add(session);
        }
        var responses = await Task.WhenAll(sessions.Select(session => client.PostAsync($"/api/material-attachment-uploads/{session.Id}/complete", null)));
        Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest));
        var pictures = await kits.ListLibraryAttachmentsAsync(kit.Id, MaterialAttachmentKind.CoverImage, default); Assert.Equal(3, pictures.Count);
        var cover = pictures[0];
        var savedResponse = await client.PutAsJsonAsync($"/api/engineering-kits/{kit.Id}/library-metadata", new SaveEngineeringKitLibraryMetadataCommand(kit.Description, "图片选型", null, false, cover.Id, kit.RowVersion));
        savedResponse.EnsureSuccessStatusCode(); var saved = (await savedResponse.Content.ReadFromJsonAsync<EngineeringKit>())!;
        Assert.Equal(cover.Id, Assert.Single((await Page(client, "/api/standard-library/materials?query=" + kit.Code))!.Items).CoverImage!.Id);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/materials/{kit.Id}/attachments/{cover.Id}/file"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/materials/{other.Id}/attachments/{cover.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/engineering-kits/{other.Id}/library-metadata", new SaveEngineeringKitLibraryMetadataCommand(null, null, null, false, cover.Id, other.RowVersion))).StatusCode);
        (await client.PutAsJsonAsync($"/api/engineering-kits/{kit.Id}/library-metadata", new SaveEngineeringKitLibraryMetadataCommand(saved.Description, saved.SelectionAdvice, null, false, null, saved.RowVersion))).EnsureSuccessStatusCode();
        Assert.Equal(3, (await kits.ListLibraryAttachmentsAsync(kit.Id, MaterialAttachmentKind.CoverImage, default)).Count);
        Assert.Equal(kit.CurrentReleasedRevisionId, (await kits.FindAsync(kit.Id, default))!.CurrentReleasedRevisionId);
        Assert.Null(await materialStore.FindMaterialAsync(kit.Id, default));
    }

    private static async Task<EngineeringKit> CreateKit(HttpClient client, IMaterialRepository materials, string name, Guid? materialId = null)
    {
        if (materialId is null)
        {
            var now = DateTimeOffset.UtcNow; materialId = Guid.NewGuid();
            await materials.UpsertU9MaterialAsync(new(materialId.Value, "01029999998", "套件子料", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", "PART", null, null, "UPTON", null,
                null, null, null, MaterialApprovalStatus.Approved, "admin", now, "0102", "kit-test", "01029999998", MaterialSyncStatus.Succeeded,
                "admin", now, "admin", now, 1, "0102", U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C), default);
        }
        var categoryResponse = await client.PostAsJsonAsync("/api/standard-library/categories", new { name, isActive = true, sortOrder = 1 }); categoryResponse.EnsureSuccessStatusCode();
        var category = (await categoryResponse.Content.ReadFromJsonAsync<StandardLibraryCategory>())!;
        var created = await client.PostAsJsonAsync("/api/engineering-kits", new { name, brand = "UPTON", description = "原备注", modelMode = "Manual", model = name + "-MODEL", standardLibraryCategoryId = category.Id,
            components = new[] { new { materialId = materialId.Value, quantity = 2, sortOrder = 1, isOptional = false } } }); created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<EngineeringKit>())!;
        var published = await client.PostAsJsonAsync($"/api/engineering-kits/{draft.Id}/publish", new { expectedRowVersion = draft.RowVersion }); published.EnsureSuccessStatusCode();
        return (await published.Content.ReadFromJsonAsync<EngineeringKit>())!;
    }
}
