using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class EngineeringKitAssetsApiTests
{
    [Theory]
    [InlineData(MaterialAttachmentKind.Model3D)]
    [InlineData(MaterialAttachmentKind.Document)]
    public async Task DeleteAttachment_KitRemovesOnlyLibraryFileAndKeepsReleasedComposition(MaterialAttachmentKind kind)
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var store = factory.Services.GetRequiredService<IMaterialRepository>();
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var kit = await CreateKit(client, store, "删除套件附件");
        var attachment = new MaterialAttachment(Guid.NewGuid(), kit.Id, kind, "套件附件.step", Path.GetTempPath(), "test.bin", 1, new string('A', 64), "admin", DateTimeOffset.UtcNow);
        await kits.CreateLibraryAttachmentAsync(attachment, default);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/materials/{kit.Id}/attachments/{attachment.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>($"/api/materials/{kit.Id}/attachments"))!);
        var saved = (await kits.FindAsync(kit.Id, default))!;
        Assert.Equal(kit.RowVersion, saved.RowVersion);
        Assert.Equal(kit.CurrentReleasedRevisionId, saved.CurrentReleasedRevisionId);
        Assert.Equal(JsonSerializer.Serialize(kit.Revisions), JsonSerializer.Serialize(saved.Revisions));
        Assert.Null(await store.FindMaterialAsync(kit.Id, default));
    }

    [Fact]
    public async Task DeleteAttachment_KitRejectsReadOnlyAndInactiveOwners()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var kit = await CreateKit(client, factory.Services.GetRequiredService<IMaterialRepository>(), "附件删除权限");
        var attachment = new MaterialAttachment(Guid.NewGuid(), kit.Id, MaterialAttachmentKind.Document, "套件资料.pdf", Path.GetTempPath(), "test.bin", 1, new string('A', 64), "admin", DateTimeOffset.UtcNow);
        await kits.CreateLibraryAttachmentAsync(attachment, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/materials/{kit.Id}/attachments/{attachment.Id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        await kits.SetActiveAsync(kit.Id, false, kit.RowVersion, "admin", DateTimeOffset.UtcNow, default);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/materials/{kit.Id}/attachments/{attachment.Id}")).StatusCode);
        Assert.NotNull(await kits.FindLibraryAttachmentAsync(attachment.Id, default));
    }

    [Fact]
    public async Task DeleteAttachment_KitRejectsOtherKitAndCoverImages()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var store = factory.Services.GetRequiredService<IMaterialRepository>();
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var first = await CreateKit(client, store, "附件所属套件");
        var second = await CreateKit(client, store, "另一套件", first.Revisions[0].Components[0].MaterialId);
        var file = new MaterialAttachment(Guid.NewGuid(), first.Id, MaterialAttachmentKind.Document, "套件资料.pdf", Path.GetTempPath(), "test.bin", 1, new string('A', 64), "admin", DateTimeOffset.UtcNow);
        var image = file with { Id = Guid.NewGuid(), Kind = MaterialAttachmentKind.CoverImage, OriginalFileName = "封面.png" };
        await kits.CreateLibraryAttachmentAsync(file, default); await kits.CreateLibraryAttachmentAsync(image, default);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/materials/{second.Id}/attachments/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/materials/{first.Id}/attachments/{image.Id}")).StatusCode);
        Assert.Equal(2, (await kits.ListLibraryAttachmentsAsync(first.Id, null, default)).Count);
    }
}
