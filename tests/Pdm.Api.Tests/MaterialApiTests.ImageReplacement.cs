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
    private async Task<(PdmMaterial Material, MaterialAttachment Old, MaterialAttachment[] Images, MaterialAttachment Document)> SeedImageReplacementAsync()
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.CoverImage);
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var images = new[] { fixture.Attachment with { Id = Guid.NewGuid(), OriginalFileName = "正面.png" }, fixture.Attachment with { Id = Guid.NewGuid(), OriginalFileName = "侧面.png" } };
        foreach (var image in images) await materials.CreateMaterialAttachmentAsync(image, default);
        var document = fixture.Attachment with { Id = Guid.NewGuid(), Kind = MaterialAttachmentKind.Document, OriginalFileName = "资料.pdf" };
        await materials.CreateMaterialAttachmentAsync(document, default);
        var material = await scope.ServiceProvider.GetRequiredService<MaterialAttachmentService>().SetCoverAsync(fixture.Material.Id, fixture.Attachment.Id, fixture.Material.RowVersion, "admin", UserRole.Administrator, default);
        return (material, fixture.Attachment, images, document);
    }

    [Fact]
    public async Task ReplaceImages_ReplacesExistingGalleryAndCover_PreservesOtherPropertiesAndIsIdempotent()
    {
        var fixture = await SeedImageReplacementAsync();
        var request = new { attachmentIds = fixture.Images.Select(image => image.Id).ToArray(), coverAttachmentId = fixture.Images[1].Id, expectedRowVersion = fixture.Material.RowVersion };
        using var first = await client.PutAsJsonAsync($"/api/materials/{fixture.Material.Id}/images", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(fixture.Images[1].Id, result.GetProperty("coverImageAttachmentId").GetGuid());
        using var retry = await client.PutAsJsonAsync($"/api/materials/{fixture.Material.Id}/images", request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(result.GetProperty("rowVersion").GetInt64(), (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt64());
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var remaining = await materials.ListMaterialAttachmentsAsync(fixture.Material.Id, null, default);
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, image => image.Id == fixture.Old.Id);
        Assert.Contains(remaining, image => image.Id == fixture.Document.Id);
        var saved = (await materials.FindMaterialAsync(fixture.Material.Id, default))!;
        Assert.Equal(fixture.Material.Name, saved.Name);
        Assert.Equal(fixture.Material.Specification, saved.Specification);
        Assert.Equal(fixture.Material.SelectionAdvice, saved.SelectionAdvice);
        var audits = await scope.ServiceProvider.GetRequiredService<IPdmRepository>().ListAuditAsync("admin", UserRole.Administrator, 500, default);
        Assert.Contains(audits, audit => audit.Action == "material.images.replace" && audit.EntityId == saved.Id.ToString());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("document")]
    [InlineData("duplicate")]
    [InlineData("six")]
    [InlineData("cover")]
    public async Task ReplaceImages_RejectsInvalidImageSetsWithoutChangingExistingPictures(string scenario)
    {
        var fixture = await SeedImageReplacementAsync();
        Guid[] ids = [fixture.Images[0].Id];
        var cover = fixture.Images[0].Id;
        if (scenario == "foreign") ids = [(await SeedImageReplacementAsync()).Images[0].Id];
        if (scenario == "document") ids = [fixture.Document.Id];
        if (scenario == "duplicate") ids = [cover, cover];
        if (scenario == "six") ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        if (scenario == "cover") cover = fixture.Images[1].Id;
        if (scenario is "foreign" or "document" or "six") cover = ids[0];
        using var response = await client.PutAsJsonAsync($"/api/materials/{fixture.Material.Id}/images", new { attachmentIds = ids, coverAttachmentId = cover, expectedRowVersion = fixture.Material.RowVersion });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        Assert.Equal(3, (await materials.ListMaterialAttachmentsAsync(fixture.Material.Id, MaterialAttachmentKind.CoverImage, default)).Count);
        Assert.Equal(fixture.Old.Id, (await materials.FindMaterialAsync(fixture.Material.Id, default))!.CoverImageAttachmentId);
    }

    [Fact]
    public async Task ReplaceImages_RejectsStaleVersionAndReadOnlyUser_AndAllowsExplicitClear()
    {
        var fixture = await SeedImageReplacementAsync();
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var current = await materials.UpdatePlmMetadataAsync(fixture.Material with { SelectionAdvice = "其他用户的新建议" }, fixture.Material.RowVersion, default);
        var path = $"/api/materials/{current.Id}/images";
        var request = new { attachmentIds = new[] { fixture.Images[0].Id }, coverAttachmentId = fixture.Images[0].Id, expectedRowVersion = fixture.Material.RowVersion };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, request)).StatusCode);
        Assert.Equal(3, (await materials.ListMaterialAttachmentsAsync(current.Id, MaterialAttachmentKind.CoverImage, default)).Count);
        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("production", "ProductionViewer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(path, request)).StatusCode);
        using var cleared = await client.PutAsJsonAsync(path, new { attachmentIds = Array.Empty<Guid>(), coverAttachmentId = (Guid?)null, expectedRowVersion = current.RowVersion });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty(await materials.ListMaterialAttachmentsAsync(current.Id, MaterialAttachmentKind.CoverImage, default));
        Assert.NotNull(await materials.FindMaterialAttachmentAsync(fixture.Document.Id, default));
        Assert.Null((await materials.FindMaterialAsync(current.Id, default))!.CoverImageAttachmentId);
        Assert.Equal("其他用户的新建议", (await materials.FindMaterialAsync(current.Id, default))!.SelectionAdvice);
    }
}
