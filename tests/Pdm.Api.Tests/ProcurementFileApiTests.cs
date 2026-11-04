using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProcurementFileApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task BuyerReadsPublishedFilesWithoutBomMaintenanceAndCannotDownloadUnrelatedFiles()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var materialService = scope.ServiceProvider.GetRequiredService<MaterialService>();
        var brands = scope.ServiceProvider.GetRequiredService<IMaterialBrandRepository>();
        await brands.SaveAsync(new(Guid.NewGuid(), "测试", true, 0, []), null, default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var material = await materialService.CreateAsync(new(null, "采购附图样例", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "QA-PURCHASE-FILE", null, null, "测试", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
        var root = Path.Combine(Path.GetTempPath(), "pdm-procurement-files-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = "%PDF-published-purchase-file"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root,"purchase.pdf"), bytes);
            var attachment = await materials.CreateMaterialAttachmentAsync(new(Guid.NewGuid(), material.Id, MaterialAttachmentKind.Document,
                "采购技术协议.pdf", root, "purchase.pdf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "admin", DateTimeOffset.UtcNow), default);
            var reference = BomFileReference.From(attachment, "标准物料", "技术协议");
            var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, material.MaterialCode, material.Name, 2, "001", null, "QA", "V01", true) { Files = [reference] };
            await repository.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-PROCUREMENT-FILES", ReleasePackageState.Published,
                null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, root)
            { Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = [item] }, default);
            await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [item with { Files = [reference with { Id = Guid.NewGuid(), FileName = "未发布新版.pdf" }] }], default);
            // 旧资料从物料当前列表移除，已发布采购引用及原文件仍保留。
            Assert.True(await materials.DeleteMaterialAttachmentAsync(material.Id, attachment.Id, default));
            await repository.SetRolePermissionsAsync("ProductionViewer", [FunctionPermissionCatalog.SchemaMarker,
                ProjectCenterPermissions.SchemaMarker, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
                ProjectCenterPermissions.Tab("procurement"), ProjectCenterPermissions.Download], default);
            Assert.False(await repository.HasUserPermissionAsync("production", UserRole.ProductionViewer, PermissionCodes.BomMechanicalEdit, default));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            var url = $"/api/projects/{project.Id}/procurement-files?materialCode={material.MaterialCode}";
            var listing = await client.GetAsync(url);
            listing.EnsureSuccessStatusCode();
            var files = await listing.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.Equal(attachment.Id, Assert.Single(files!).GetProperty("id").GetGuid());
            var tracking = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/procurement-tracking");
            var row = Assert.Single(tracking.GetProperty("items").EnumerateArray(), value => value.GetProperty("materialCode").GetString() == material.MaterialCode);
            Assert.Equal("采购技术协议.pdf", Assert.Single(row.GetProperty("files").EnumerateArray()).GetProperty("fileName").GetString());
            var downloadUrl = $"/api/projects/{project.Id}/procurement-files/{attachment.Id}/file?materialCode={material.MaterialCode}";
            using var download = await client.GetAsync(downloadUrl);
            download.EnsureSuccessStatusCode();
            Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(downloadUrl.Replace(material.MaterialCode,"OTHER-MATERIAL"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(downloadUrl.Replace(attachment.Id.ToString(),Guid.NewGuid().ToString()))).StatusCode);
            using var anonymous = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            await repository.SetRolePermissionsAsync("ProductionViewer", [], default);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task MissingPublishedFilesShowCurrentBomSupplementsWithoutRewritingReleaseOrExposingUnpublishedMaterials()
    {
        using var isolated = new PdmApiFactory();
        using var scope = isolated.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var store = scope.ServiceProvider.GetRequiredService<IBomFileStore>();
        var materialService = scope.ServiceProvider.GetRequiredService<MaterialService>();
        await scope.ServiceProvider.GetRequiredService<IMaterialBrandRepository>().SaveAsync(new(Guid.NewGuid(), "补充测试", true, 0, []), null, default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var material = await materialService.CreateAsync(new(null, "补充附图", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "QA-PROCUREMENT-SUPPLEMENT", null, null, "补充测试", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
        var root = Path.Combine(Path.GetTempPath(), "pdm-procurement-files-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = "%PDF-current-bom-supplement"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "supplement.pdf"), bytes);
            var attachment = await materials.CreateMaterialAttachmentAsync(new(Guid.NewGuid(), material.Id, MaterialAttachmentKind.Document,
                "补充图纸.pdf", root, "supplement.pdf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "admin", DateTimeOffset.UtcNow), default);
            var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, material.MaterialCode, material.Name, 2, "001", null, "QA", "V01", true) { Files = [] };
            var package = await repository.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-PROCUREMENT-SUPPLEMENT", ReleasePackageState.Published,
                null, "", "", [], DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), root)
            { Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = [item] }, default);
            await repository.ReplaceBomAsync(project.Id, BomKind.Standard,
                [item, item with { Id = Guid.NewGuid(), DrawingNumber = "UNPUBLISHED-ONLY", Files = [BomFileReference.From(attachment, "标准物料", "图纸")] }], default);
            await repository.SetRolePermissionsAsync("ProductionViewer", [FunctionPermissionCatalog.SchemaMarker,
                ProjectCenterPermissions.SchemaMarker, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
                ProjectCenterPermissions.Tab("procurement"), ProjectCenterPermissions.Download], default);
            using var client = isolated.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            var url = $"/api/projects/{project.Id}/procurement-files?materialCode={material.MaterialCode}";
            var listing = await client.GetFromJsonAsync<JsonElement[]>(url);
            Assert.Equal(attachment.Id, Assert.Single(listing!).GetProperty("id").GetGuid());
            var tracking = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/procurement-tracking");
            var row = Assert.Single(tracking.GetProperty("items").EnumerateArray(), value => value.GetProperty("materialCode").GetString() == material.MaterialCode);
            Assert.True(row.GetProperty("filesFromCurrentBom").GetBoolean());
            Assert.Equal(attachment.Id, Assert.Single(row.GetProperty("files").EnumerateArray()).GetProperty("id").GetGuid());
            var downloadUrl = $"/api/projects/{project.Id}/procurement-files/{attachment.Id}/file?materialCode={material.MaterialCode}";
            Assert.Equal(bytes, await client.GetByteArrayAsync(downloadUrl));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(downloadUrl.Replace(material.MaterialCode, "UNPUBLISHED-ONLY"))).StatusCode);
            Assert.Empty(Assert.Single((await repository.FindReleasePackageAsync(package.Id, default))!.StandardBomSnapshot).Files!);

            // 人工采用空引用后，备料不得绕过当前BOM的附件选择。
            var catalog = await store.ReadAsync(BomFileService.Scope(project.Id, material.MaterialCode), default);
            Assert.True(await store.SaveAsync(catalog with { LibraryFiles = [] }, catalog.RowVersion, default));
            Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(url))!);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(downloadUrl)).StatusCode);

            // 被正式移除的物料保留历史状态，不从当前工作区补充附件。
            catalog = await store.ReadAsync(catalog.ScopeId, default);
            Assert.True(await store.SaveAsync(catalog with { LibraryFiles = [BomFileReference.From(attachment, "标准物料", "图纸")] }, catalog.RowVersion, default));
            await repository.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-PROCUREMENT-SUPPLEMENT-REMOVED", ReleasePackageState.Published,
                null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, root)
            { Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = [] }, default);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(url))!);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Token() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new Claim(ClaimTypes.Name,"production"), new Claim(ClaimTypes.Role,"ProductionViewer")], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
