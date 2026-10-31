using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class MaterialApiTests
{
    private async Task<(PdmMaterial Material, MaterialAttachment Attachment)> SeedDeletionAttachmentAsync(MaterialAttachmentKind kind)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MaterialService>();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var marker = Guid.NewGuid().ToString("N");
        var material = await service.CreateAsync(new(null, $"附件删除-{marker}", MaterialKind.Electrical,
            MaterialSupplyMode.Purchase, "001", $"DELETE-{marker}", null, null, "TEST", null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var attachment = new MaterialAttachment(Guid.NewGuid(), material.Id, kind, kind == MaterialAttachmentKind.Model3D ? "模型.step" : "资料.pdf",
            Path.GetTempPath(), $"test-{Guid.NewGuid():N}", 1, new string('A', 64), "admin", DateTimeOffset.UtcNow);
        await materials.CreateMaterialAttachmentAsync(attachment, default);
        return (material, attachment);
    }

    [Theory]
    [InlineData(MaterialAttachmentKind.Document)]
    public async Task DeleteAttachment_RemovesSelectedRecordAndCountAndAudits(MaterialAttachmentKind kind)
    {
        var (material, attachment) = await SeedDeletionAttachmentAsync(kind);
        var response = await client.DeleteAsync($"/api/materials/{material.Id}/attachments/{attachment.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<MaterialAttachment[]>($"/api/materials/{material.Id}/attachments"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/materials/{material.Id}/attachments/{attachment.Id}/file")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var saved = await materials.FindMaterialAsync(material.Id, default);
        Assert.NotNull(saved);
        Assert.Equal(material.RowVersion, saved.RowVersion);
        var listed = Assert.Single(await materials.ListMaterialsAsync(material.Name, null, false, 10, default), item => item.Id == material.Id);
        Assert.Equal(0, listed.Model3DAttachmentCount);
        Assert.Equal(0, listed.DocumentAttachmentCount);
        var audits = await scope.ServiceProvider.GetRequiredService<IPdmRepository>().ListAuditAsync("admin", UserRole.Administrator, 500, default);
        Assert.Contains(audits, entry => entry.Action == "material.attachment.delete" && entry.EntityId == attachment.Id.ToString());
    }

    [Fact]
    public async Task DeleteAttachment_RejectsAnotherMaterialAndPreservesBothRecords()
    {
        var first = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Model3D);
        var second = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Document);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/materials/{first.Material.Id}/attachments/{second.Attachment.Id}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        Assert.NotNull(await materials.FindMaterialAttachmentAsync(first.Attachment.Id, default));
        Assert.NotNull(await materials.FindMaterialAttachmentAsync(second.Attachment.Id, default));
    }

    [Fact]
    public async Task DeleteAttachment_RejectsReadOnlyUser()
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Document);
        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("production", "ProductionViewer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/materials/{fixture.Material.Id}/attachments/{fixture.Attachment.Id}")).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>($"/api/materials/{fixture.Material.Id}/attachments"))!);
    }

    [Fact]
    public async Task DeleteAttachment_RejectsArchivedMaterialAndCoverImage()
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Document);
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        await materials.UpdateMaterialAsync(fixture.Material with { IsArchived = true }, fixture.Material.RowVersion, default);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/materials/{fixture.Material.Id}/attachments/{fixture.Attachment.Id}")).StatusCode);
        var image = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.CoverImage);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/materials/{image.Material.Id}/attachments/{image.Attachment.Id}")).StatusCode);
        Assert.NotNull(await materials.FindMaterialAttachmentAsync(image.Attachment.Id, default));
    }

    [Fact]
    public async Task DeleteAttachment_ConcurrentRequestsDeleteOnlyOnce()
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Document);
        var path = $"/api/materials/{fixture.Material.Id}/attachments/{fixture.Attachment.Id}";
        var results = await Task.WhenAll(client.DeleteAsync(path), client.DeleteAsync(path));
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, result => result.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteAttachment_PreservesSharedStoredFileAndOtherAttachmentDownload()
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Model3D);
        var root = Path.Combine(Path.GetTempPath(), $"PdmAttachmentDelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "shared.bin");
        var payload = "shared-attachment-content"u8.ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, payload);
            using var scope = factory.Services.CreateScope();
            var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
            var shared = fixture.Attachment with { Id = Guid.NewGuid(), Kind = MaterialAttachmentKind.Document, StorageRoot = root,
                StorageRelativePath = "shared.bin", FileLength = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)) };
            await materials.CreateMaterialAttachmentAsync(shared, default);
            var selected = shared with { Id = Guid.NewGuid(), Kind = MaterialAttachmentKind.Document };
            await materials.CreateMaterialAttachmentAsync(selected, default);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/materials/{fixture.Material.Id}/attachments/{selected.Id}")).StatusCode);
            using var download = await client.GetAsync($"/api/materials/{fixture.Material.Id}/attachments/{shared.Id}/file");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }
}
