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
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class WorkspaceDocumentApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNameCandidatesRemainAmbiguousAndDoNotExpandUnrelatedDrawingFamilies(bool explicitId)
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "同名引用验收", null, null, 1, "admin", "D:/test", "D:/release"), default);
        var first = await repository.CreateSubprojectAsync(new(root.Id, "子项目一", null, 1), default);
        var second = await repository.CreateSubprojectAsync(new(root.Id, "子项目二", null, 1), default);
        var assembly = await repository.RegisterDocumentAsync(new(root.Id, "ROOT", "总装", "ROOT.SLDASM", DocumentKind.Assembly), "admin", default);
        var models = new List<PdmDocument>();
        var drawings = new List<PdmDocument>();
        foreach (var child in new[] { first, second })
        {
            var model = await repository.RegisterDocumentAsync(new(child.Id, "PART", "同名候选", "PART.SLDPRT", DocumentKind.Part), "admin", default);
            models.Add(model);
            drawings.Add(await repository.RegisterDocumentAsync(new(child.Id, "DRAWING", "子项目工程图", child.Code + ".SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: model.Id), "admin", default));
        }
        var node = new DocumentReferenceNode(Guid.NewGuid(), explicitId ? models[0].Id : null, "ROOT/PART", "PART.SLDPRT", "零件", DocumentKind.Part, "Default", 1, ReferenceNodeStatus.Normal, models[0].Revision, "admin", []);
        var tree = new DocumentReferenceNode(Guid.NewGuid(), assembly.Id, "ROOT", assembly.FileName, assembly.Name, assembly.Kind, "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "admin", [node]);
        await repository.CheckoutAsync(assembly.Id, "admin", default);
        await repository.CheckInAsync(assembly.Id, "admin", assembly.Revision, new(Guid.NewGuid(), root.Id, assembly.Id, DateTimeOffset.UtcNow, "admin", tree, new string('A', 64)), default);
        Authorize(client);
        client.DefaultRequestHeaders.Add("X-PDM-Document-View", "workspace");
        using var result = JsonDocument.Parse(await client.GetStringAsync($"/api/projects/{root.Id}/folder-documents"));
        var indexed = result.RootElement.GetProperty("libraryDocuments").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.All(models, item => Assert.Contains(item.Id, indexed));
        Assert.DoesNotContain(drawings[1].Id, indexed);
        Assert.Equal(explicitId, indexed.Contains(drawings[0].Id));
        Assert.Equal(explicitId ? 1 : 0, result.RootElement.GetProperty("documentRelations").GetArrayLength());
    }

    [Fact]
    public async Task WorkspaceIndexFolderCountsAndLazyFolderReadsPreserveDocumentsAndAuthorization()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(new("INDEX-" + Guid.NewGuid().ToString("N"), "导航读取测试", "admin", "D:/test", "D:/release"), "admin", default);
        var document = await repository.RegisterDocumentAsync(new(project.Id, "INDEX", "索引零件", "INDEX.SLDPRT", DocumentKind.Part), "admin", default);
        Authorize(client);
        var url = $"/api/projects/{project.Id}/folder-documents";
        var original = await client.GetFromJsonAsync<PdmDocument[]>(url);
        Assert.Contains(original!, item => item.Id == document.Id);
        client.DefaultRequestHeaders.Add("X-PDM-Document-View", "workspace");
        using var index = JsonDocument.Parse(await client.GetStringAsync(url));
        Assert.Contains(index.RootElement.GetProperty("currentDocuments").EnumerateArray(), item => item.GetProperty("id").GetGuid() == document.Id);
        Assert.Contains(index.RootElement.GetProperty("libraryDocuments").EnumerateArray(), item => item.GetProperty("id").GetGuid() == document.Id);
        client.DefaultRequestHeaders.Remove("X-PDM-Document-View");
        client.DefaultRequestHeaders.Add("X-PDM-Document-View", "counts");
        using var counts = JsonDocument.Parse(await client.GetStringAsync(url));
        var folder = original!.Single(item => item.Id == document.Id).FolderId!.Value;
        Assert.Equal(original!.Count(item => item.FolderId == folder), counts.RootElement.GetProperty("documentCounts").GetProperty(folder.ToString()).GetInt32());
        client.DefaultRequestHeaders.Remove("X-PDM-Document-View");
        client.DefaultRequestHeaders.Add("X-PDM-Folder-ID", folder.ToString());
        var selected = await client.GetFromJsonAsync<PdmDocument[]>(url);
        Assert.All(selected!, item => Assert.Equal(folder, item.FolderId));
        Assert.Contains(selected!, item => item.Id == document.Id);
        client.DefaultRequestHeaders.Remove("X-PDM-Folder-ID");
        client.DefaultRequestHeaders.Add("X-PDM-Folder-ID", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-PDM-Folder-ID");
        client.DefaultRequestHeaders.Add("X-PDM-Folder-ID", "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-PDM-Folder-ID");
        foreach (var view in new[] { "workspace", "counts" })
        {
            client.DefaultRequestHeaders.Remove("X-PDM-Document-View");
            client.DefaultRequestHeaders.Add("X-PDM-Document-View", view);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/folder-documents")).StatusCode);
        }
    }

    [Fact]
    public async Task FamilyNameplateSummaryMatchesEachAuthorizedDetail()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        Authorize(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{project.Id}/nameplate");
        request.Headers.Add("X-PDM-Nameplate-View", "family-access");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var summaries = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(summaries.RootElement.EnumerateArray());
        foreach (var item in summaries.RootElement.EnumerateArray())
        {
            using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/projects/{item.GetProperty("projectId").GetGuid()}/nameplate"));
            Assert.Equal(detail.RootElement.GetProperty("nameplate").GetRawText(), item.GetProperty("nameplate").GetRawText());
            Assert.Equal(detail.RootElement.GetProperty("canEdit").GetBoolean(), item.GetProperty("canEdit").GetBoolean());
        }
    }

    private static void Authorize(HttpClient client)
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
