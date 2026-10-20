using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class BrowserCadArchiveTests
{
    [Fact]
    public async Task ProjectArchivePreservesStructureRelationsAndParentVersionsAndReplaysWithoutDuplicateVersions()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id);
        var files = new Dictionary<string, string> { ["ROOT.SLDASM"] = "root", ["PART.SLDPRT"] = "part", ["PART.SLDDRW"] = "drawing" };
        var first = await fixture.Stage(target, files); await fixture.Process(first.Id, "ROOT.SLDASM");
        var docs = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        Assert.Equal(3, docs.Count); Assert.All(docs, doc => Assert.Null(doc.CheckedOutBy));
        Assert.Equal(3, fixture.Queue.Read(first.Id)!.NewVersions);
        Assert.Single(await fixture.Repository.ListDocumentRelationsAsync(fixture.Project.Id, default));
        var savedDrawing = docs.Single(d => d.Kind == DocumentKind.Drawing);
        var drawingVersion = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(savedDrawing.Id, default));
        Assert.Equal(drawingVersion.Sha256, drawingVersion.Preview!.SourceSha256);
        Assert.Equal(DocumentPreviewFormat.Pdf, drawingVersion.Preview.Format);
        await fixture.Process(first.Id, "ROOT.SLDASM");
        foreach (var doc in docs) Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
        var repeated = await fixture.Stage(target, files); await fixture.Process(repeated.Id, "ROOT.SLDASM");
        Assert.Equal(0, fixture.Queue.Read(repeated.Id)!.NewVersions);
        var child = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part changed" }); await fixture.Process(child.Id, "PART.SLDPRT");
        var parent = docs.Single(d => d.Kind == DocumentKind.Assembly); var part = docs.Single(d => d.Kind == DocumentKind.Part);
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(parent.Id, default));
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        var forest = (await fixture.Repository.GetLatestReferenceSnapshotAsync(fixture.Project.Id, default))!;
        Assert.Single(ProjectReferenceForest.Roots(forest));
        Assert.Equal("W2", forest.Root.Children.Single(n => n.DocumentId == part.Id).Revision!.Value.Display);
        var drawing = await fixture.Stage(target, new() { ["EXTRA.SLDDRW"] = "extra drawing" }); await fixture.Process(drawing.Id, "EXTRA.SLDDRW");
        Assert.Equal(1, fixture.Queue.Read(drawing.Id)!.NewVersions);
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        Assert.Equal(2, (await fixture.Repository.ListDocumentRelationsAsync(fixture.Project.Id, default)).Count);
        Assert.True(File.Exists(fixture.Queue.Under(first.Id, "input/PART.SLDPRT")));
    }

    [Fact]
    public async Task DisabledEntryRejectsDirectUploadAndQueuedCommitWithoutCreatingDocuments()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id);
        var staged = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = false })).StatusCode);
        var capability = (await fixture.Client.GetFromJsonAsync<BrowserCadArchiveCapability>($"/api/browser-archives/capabilities?projectId={fixture.Project.Id}"))!;
        Assert.False(capability.Enabled); Assert.False(capability.CanArchive);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsJsonAsync($"/api/browser-archives/{staged.Id}/start", new { rootPath = "PART.SLDPRT" })).StatusCode);
        using var upload = Fixture.Content(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", upload)).StatusCode);
        fixture.Tenant();
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ProcessAsync(fixture.Queue.Read(staged.Id)!, "admin", UserRole.Administrator, default));
        Assert.Empty(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.True(File.Exists(fixture.Queue.Under(staged.Id, "input/PART.SLDPRT")));
    }

    [Fact]
    public async Task ViewerAndNonOwnerCannotUseOrBypassArchiveAndOnlyCurrentAdministratorChangesSwitch()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id); var staged = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part" });
        await fixture.Repository.CreateUserAsync(new(Guid.NewGuid(), "viewer", "只读用户", "unused", UserRole.ProductionViewer, true), default);
        Fixture.Auth(fixture.Client, "viewer", "ProductionViewer");
        Assert.False((await fixture.Client.GetFromJsonAsync<BrowserCadArchiveCapability>($"/api/browser-archives/capabilities?projectId={fixture.Project.Id}"))!.CanArchive);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync($"/api/browser-archives/{staged.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = false })).StatusCode);
        using var content = Fixture.Content(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", content)).StatusCode);
        TenantContext.Set(new(Guid.NewGuid(), fixture.Company, fixture.Company, "viewer", "ProductionViewer", false, new HashSet<string>()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetEnabledAsync(false, "viewer", UserRole.Administrator, default));
    }

    [Fact]
    public async Task ZipTraversalIsRejectedBeforeAnyPathCanEscape()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        using var bytes = new MemoryStream(); using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        { using var entry = new StreamWriter(zip.CreateEntry("../escaped.SLDPRT").Open()); entry.Write("unsafe"); }
        using var form = new MultipartFormDataContent(); form.Add(JsonContent.Create(new BrowserCadArchiveTarget(fixture.Project.Id)), "target");
        form.Add(new ByteArrayContent(bytes.ToArray()), "files", "test.zip");
        var response = await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("越界", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Queue.Root, "escaped.SLDPRT")));
    }

    [Fact]
    public async Task StandardArchiveRequiresDepartmentRightsAndKeepsUnclassifiedLinesAndAvoidsNoOpVersions()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        using (var denied = Fixture.Content(new(), new() { ["PART.SLDPRT"] = "part" }))
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files", denied)).StatusCode);
        await fixture.Repository.CreateUserAsync(new(Guid.NewGuid(), "std", "标准化工程师", "unused", UserRole.ProcessReviewer, true), default);
        var department = await fixture.Repository.SaveOrganizationUnitAsync(new(null, fixture.Company, null, "STD-WEB", "标准化", OrganizationUnitKind.BusinessDivision, true, 1, false, true), default);
        await fixture.Repository.SetOrganizationMembershipsAsync("std", [department.Id], department.Id, default);
        Fixture.Auth(fixture.Client, "std", "ProcessReviewer");
        var files = new Dictionary<string, string> { ["PART.SLDPRT"] = "unclassified" };
        var staged = await fixture.Stage(new(DrawingType: 2), files); await fixture.Process(staged.Id, "PART.SLDPRT", "std", UserRole.ProcessReviewer);
        var standard = fixture.Scope.ServiceProvider.GetRequiredService<StandardStructureService>(); fixture.Tenant("std", UserRole.ProcessReviewer);
        var item = Assert.Single((await standard.ReadAsync("std", UserRole.ProcessReviewer, default)).Catalog.Structures);
        var revision = Assert.Single(item.Revisions); Assert.Equal(BomKind.Unclassified, Assert.Single(revision.Bom).Kind);
        var update = await fixture.Stage(new(StructureId: item.Id, ExpectedStructureRevisionId: revision.Id, DrawingType: 2), files);
        await fixture.Process(update.Id, "PART.SLDPRT", "std", UserRole.ProcessReviewer);
        fixture.Tenant("std", UserRole.ProcessReviewer);
        Assert.Single(Assert.Single((await standard.ReadAsync("std", UserRole.ProcessReviewer, default)).Catalog.Structures).Revisions);
        Assert.Equal(0, fixture.Queue.Read(update.Id)!.NewVersions);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pdm-browser-archive-test", Guid.NewGuid().ToString("N"));
        private readonly PdmApiFactory basic = new();
        internal readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory;
        internal readonly IServiceScope Scope;
        internal readonly HttpClient Client;
        internal IPdmRepository Repository => Scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        internal BrowserCadArchiveService Service => Scope.ServiceProvider.GetRequiredService<BrowserCadArchiveService>();
        internal BrowserCadArchiveQueue Queue => Scope.ServiceProvider.GetRequiredService<BrowserCadArchiveQueue>();
        internal Project Project = null!;
        internal Guid Company = Guid.Parse("70000000-0000-0000-0000-000000000001");
        internal Fixture()
        {
            Factory = basic.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                foreach (var hosted in services.Where(s => s.ImplementationType == typeof(BrowserCadArchiveHostedService)).ToArray()) services.Remove(hosted);
                services.RemoveAll<IBrowserCadReader>(); services.AddSingleton<IBrowserCadReader, FakeReader>();
                services.AddSingleton<IOptions<PdmStorageOptions>>(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(Root, "uploads"), StandardStructureRoot = Path.Combine(Root, "standard"), ChunkSizeBytes = 4 }));
            }));
            Client = Factory.CreateClient(); Scope = Factory.Services.CreateScope(); Auth(Client, "admin", "Administrator");
        }
        internal async Task Setup()
        {
            if (await Repository.FindUserAsync("admin", default) == null) await Repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
            Tenant(); Project = await Repository.CreateProjectAsync(new("WEB-" + Guid.NewGuid().ToString("N"), "网页存档验证", "admin", Path.Combine(Root, "vault"), Path.Combine(Root, "release")), "admin", default);
            TenantContext.Clear();
        }
        internal void Tenant(string actor = "admin", UserRole role = UserRole.Administrator) => TenantContext.Set(new(Guid.NewGuid(), Company, Company, actor, role.ToString(), false, new HashSet<string>()));
        internal static void Auth(HttpClient client, string user, string role)
        {
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, user), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        internal static MultipartFormDataContent Content(BrowserCadArchiveTarget target, Dictionary<string, string> files)
        {
            var form = new MultipartFormDataContent(); form.Add(JsonContent.Create(target), "target");
            foreach (var file in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file.Value)), "files", file.Key);
            return form;
        }
        internal async Task<BrowserCadArchiveView> Stage(BrowserCadArchiveTarget target, Dictionary<string, string> files)
        {
            TenantContext.Clear(); using var form = Content(target, files);
            var result = await Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files{(target.ProjectId is Guid project ? "?projectId=" + project : "")}", form);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<BrowserCadArchiveView>())!;
        }
        internal async Task Process(Guid id, string root, string actor = "admin", UserRole role = UserRole.Administrator)
        {
            TenantContext.Clear(); var started = await Client.PostAsJsonAsync($"/api/browser-archives/{id}/start", new { rootPath = root });
            Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync()); Tenant(actor, role);
            await Service.ProcessAsync(Queue.Read(id)!, actor, role, default); TenantContext.Clear();
            Assert.Equal("Succeeded", Queue.Read(id)!.State);
        }
        public void Dispose() { TenantContext.Clear(); Scope.Dispose(); Client.Dispose(); Factory.Dispose(); basic.Dispose(); }
    }

    private sealed class FakeReader : IBrowserCadReader
    {
        public async Task<BrowserCadNativeResult> ReadAsync(string directory, BrowserCadNativeRequest request, CancellationToken ct)
        {
            var input = Directory.GetFiles(request.Directory, "*", SearchOption.AllDirectories).Where(p => new[] { ".sldasm", ".sldprt", ".slddrw" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
            var paths = input.ToDictionary(p => Path.GetFileName(p)!, StringComparer.OrdinalIgnoreCase); var rootName = Path.GetFileName(request.RootPath);
            Guid Key(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).Take(16).ToArray());
            DocumentKind Kind(string name) => name.EndsWith("SLDASM") ? DocumentKind.Assembly : name.EndsWith("SLDPRT") ? DocumentKind.Part : DocumentKind.Drawing;
            DocumentReferenceNode Node(string name) => new(Key("node-" + name), Key(name), name, name, Path.GetFileNameWithoutExtension(name), Kind(name), "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []) { TreeScopeOnly = true };
            var partNames = paths.Keys.Where(n => Kind(n) == DocumentKind.Part).ToArray();
            var used = paths.Keys.Where(n => n == rootName || Kind(n) == DocumentKind.Part || Kind(n) == DocumentKind.Drawing).ToArray();
            var result = new List<BrowserCadNativeFile>();
            foreach (var name in used)
            {
                var source = paths[name]; var relative = Path.GetRelativePath(request.Directory, source).Replace('\\', '/'); var output = Path.Combine(directory, "work", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(source, output, true); var text = await File.ReadAllTextAsync(source, ct);
                var sha = await BrowserCadArchiveQueue.HashAsync(output, ct); var node = Node(name);
                if (Kind(name) == DocumentKind.Assembly) node = node with { Children = partNames.Select(p => Node(p) with { InstancePath = name + "/" + p }).ToArray() };
                if (Kind(name) == DocumentKind.Drawing) node = node with { Children = partNames.Take(1).Select(Node).ToArray() };
                var properties = new Dictionary<string, string?> { ["SourceFileSha256"] = sha, ["物料名称"] = node.DisplayName, ["物料编码"] = node.DisplayName, ["单位"] = "件", ["材质"] = "钢" };
                if (text != "unclassified") properties["物料分类"] = "非标件";
                string? preview = null;
                if (Kind(name) == DocumentKind.Drawing)
                {
                    preview = "_previews/" + Key(name).ToString("N") + ".pdf";
                    var previewFile = Path.Combine(directory, "work", preview); Directory.CreateDirectory(Path.GetDirectoryName(previewFile)!);
                    await File.WriteAllTextAsync(previewFile, "%PDF-1.7 isolated work preview", ct);
                }
                result.Add(new(Key(name), relative, name, new FileInfo(output).Length, sha, node, properties, preview));
            }
            return new(true, Key(rootName), result);
        }
    }
}
