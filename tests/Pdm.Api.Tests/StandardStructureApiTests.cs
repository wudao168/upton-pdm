using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class StandardStructureApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static void Authenticate(HttpClient client, string username, string role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    [Fact]
    public async Task HttpArchiveUsesIndependentCategoriesAuthorizesStandardizationAndServesReadOnlyPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-standard-structure-api", Guid.NewGuid().ToString("N"));
        try
        {
            using var baseFactory = new PdmApiFactory();
            using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStandardStructureFiles>();
                services.AddSingleton<IStandardStructureFiles>(new LocalStandardStructureFiles(Options.Create(new PdmStorageOptions { StandardStructureRoot = root })));
            }));
            using var client = factory.CreateClient();
            const string path = "/api/standard-structures";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            var repository = factory.Services.GetRequiredService<IPdmRepository>();
            await repository.CreateUserAsync(new(Guid.NewGuid(), "std", "标准化工程师", "unused", UserRole.ProcessReviewer, true), default);
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
            await repository.CreateUserAsync(new(Guid.NewGuid(), "engineer", "工程师", "unused", UserRole.Engineer, true), default);
            var company = Guid.Parse("70000000-0000-0000-0000-000000000001");
            var department = await repository.SaveOrganizationUnitAsync(new(null, company, null, "STD-HTTP", "标准化部", OrganizationUnitKind.BusinessDivision, true, 1, false, true), default);
            await repository.SetOrganizationMembershipsAsync("std", [department.Id], department.Id, default);
            Authenticate(client, "std", "ProcessReviewer");
            var view = (await client.GetFromJsonAsync<StandardStructureView>(path, Json))!;
            Assert.True(view.CanArchive);
            Assert.True(view.CanManage);
            var categoryId = Guid.NewGuid();
            var categoryResponse = await client.PutAsJsonAsync($"{path}/categories/{categoryId}", new SaveStandardStructureCategory("HTTP升降分类", null, 1, true, view.Catalog.RowVersion));
            Assert.Equal(HttpStatusCode.OK, categoryResponse.StatusCode);
            var catalog = (await categoryResponse.Content.ReadFromJsonAsync<StandardStructureCatalog>(Json))!;
            var materials = await client.GetStringAsync("/api/standard-library/categories");
            Assert.DoesNotContain("HTTP升降分类", materials);
            var node = new StandardStructureNode("root", "BZ-HTTP-001", "HTTP升降组件", "test.SLDASM", "Assembly", "native", "preview", false, 1,
                [new("embedded", "BZ-HTTP-PART", "嵌入零件", "embedded.SLDPRT", "Part", null, null, true, 1, [])]);
            var manifest = new ArchiveStandardStructure(Guid.NewGuid(), 1, "HTTP升降组件", node,
                [new("line", BomKind.NonStandard, "BZ-HTTP-PART", "底板", 2, "件", "W1", "钢")], Guid.NewGuid());
            MultipartFormDataContent Content()
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(manifest, Json)), "manifest");
                form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("native archive test bytes")), "native", "test.SLDASM");
                form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.7 preview test bytes")), "preview", "test.pdf");
                return form;
            }
            Authenticate(client, "admin", "Administrator");
            using (var content = Content()) Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path + "/archive", content)).StatusCode);
            Authenticate(client, "std", "ProcessReviewer");
            using var archive = Content();
            var archivedResponse = await client.PostAsync(path + "/archive", archive);
            Assert.Equal(HttpStatusCode.OK, archivedResponse.StatusCode);
            var structure = (await archivedResponse.Content.ReadFromJsonAsync<StandardStructure>(Json))!;
            Assert.Equal("UPST-1-0001", structure.Code);
            var archivedCatalog = (await client.GetFromJsonAsync<StandardStructureView>(path, Json))!.Catalog;
            var modelCategory = Assert.Single(archivedCatalog.Categories, c => c.Id == structure.CategoryId);
            Assert.Equal("UPST-1-0001-V01", modelCategory.Name);
            Assert.Equal(StandardStructureRules.UnclassifiedId, modelCategory.ParentId);
            Assert.Equal("test.SLDASM", structure.Revisions[0].Root.FileName);
            var revision = Assert.Single(structure.Revisions);
            Assert.Equal(2, revision.Files.Count);
            Assert.True(Assert.Single(revision.Root.Children).Embedded);
            var preview = await client.GetAsync($"{path}/revisions/{revision.Id}/preview/preview");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("application/pdf", preview.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("%PDF", await preview.Content.ReadAsStringAsync());
            var originalFile = await client.GetAsync($"{path}/revisions/{revision.Id}/files/native");
            Assert.Equal(HttpStatusCode.OK, originalFile.StatusCode);
            Assert.Equal("application/octet-stream", originalFile.Content.Headers.ContentType!.MediaType);
            Assert.Equal("native archive test bytes", await originalFile.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/revisions/{revision.Id}/files/missing")).StatusCode);
            await repository.SetRolePermissionsAsync("Engineer", [FunctionPermissionCatalog.SchemaMarker, PermissionCodes.ProjectContentView], default);
            Authenticate(client, "engineer", "Engineer");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{path}/revisions/{revision.Id}/files/native")).StatusCode);
            Authenticate(client, "std", "ProcessReviewer");
            var shared = (await client.GetFromJsonAsync<StandardStructureView>(path, Json))!;
            Assert.Equal(StandardStructureRules.GroupId, shared.Catalog.CompanyId);
            var movedResponse = await client.PostAsJsonAsync(path + "/classifications", new MoveStandardStructures([structure.Id], categoryId, shared.Catalog.RowVersion));
            Assert.Equal(HttpStatusCode.OK, movedResponse.StatusCode);
            var moved = (await movedResponse.Content.ReadFromJsonAsync<StandardStructureCatalog>(Json))!;
            Assert.Equal(categoryId, Assert.Single(moved.Structures).CategoryId);
            using (var retry = Content())
            {
                var repeated = (await (await client.PostAsync(path + "/archive", retry)).Content.ReadFromJsonAsync<StandardStructure>(Json))!;
                Assert.Single(repeated.Revisions);
                Assert.Equal(structure.Code, repeated.Code);
            }
            manifest = manifest with { RequestId = Guid.NewGuid(), ExpectedRevisionId = revision.Id, StructureId = structure.Id };
            using (var upgrade = Content())
            {
                var upgradedResponse = await client.PostAsync(path + "/archive", upgrade);
                Assert.Equal(HttpStatusCode.OK, upgradedResponse.StatusCode);
                var upgraded = (await upgradedResponse.Content.ReadFromJsonAsync<StandardStructure>(Json))!;
                Assert.Equal(2, upgraded.Revisions.Count);
                Assert.Equal(categoryId, upgraded.CategoryId);
                Assert.Equal("UPST-1-0001", upgraded.Code);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{path}/revisions/{revision.Id}/preview/native")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{path}/categories/{categoryId}", new SaveStandardStructureCategory("过期覆盖", null, 1, true, catalog.RowVersion))).StatusCode);
            Authenticate(client, "admin", "Administrator");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/storage")).StatusCode);
            var current = (await client.GetFromJsonAsync<StandardStructureView>(path, Json))!;
            Assert.False(current.CanArchive);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + "/storage", new { storageRoot = root + "-other", expectedRowVersion = current.Catalog.RowVersion })).StatusCode);
            var snapshots = JsonSerializer.Serialize(Assert.Single(current.Catalog.Structures).Revisions, Json);
            Authenticate(client, "engineer", "Engineer");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{path}/{structure.Id}/state", new ChangeStandardStructureState("Delete", current.Catalog.RowVersion))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{path}/{structure.Id}/references")).StatusCode);
            Authenticate(client, "admin", "Administrator");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{structure.Id}/state", new ChangeStandardStructureState("Disable", 0))).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<StandardStructureReference[]>($"{path}/{structure.Id}/references", Json))!);
            foreach (var action in new[] { "Disable", "Delete", "Restore", "Enable" })
            {
                var response = await client.PostAsJsonAsync($"{path}/{structure.Id}/state", new ChangeStandardStructureState(action, current.Catalog.RowVersion));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var changed = (await response.Content.ReadFromJsonAsync<StandardStructureCatalog>(Json))!;
                var saved = Assert.Single(changed.Structures);
                Assert.Equal(action == "Delete", saved.DeletedAt is not null);
                Assert.Equal(action == "Enable", saved.IsActive);
                Assert.Equal(snapshots, JsonSerializer.Serialize(saved.Revisions, Json));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/revisions/{revision.Id}/preview/preview")).StatusCode);
                Assert.Equal(action == "Enable" ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                    (await client.GetAsync($"{path}/revisions/{revision.Id}/files/native")).StatusCode);
                current = current with { Catalog = changed };
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
