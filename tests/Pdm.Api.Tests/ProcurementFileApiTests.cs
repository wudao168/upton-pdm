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

    private static string Token() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new Claim(ClaimTypes.Name,"production"), new Claim(ClaimTypes.Role,"ProductionViewer")], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
