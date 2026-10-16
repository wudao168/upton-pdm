using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class ProductionDrawingAccessTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidPublishedPdfAndStepCanBeReadAndArchived(bool step)
    {
        using var fixture = await CreateFixtureAsync();
        using var response = await fixture.Client.GetAsync(fixture.PreviewUrl(step));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(step ? "model/step" : "application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(step ? "正式STEP" : "正式PDF", await response.Content.ReadAsStringAsync());

        using var download = await fixture.DownloadArchiveAsync(step);
        Assert.True(download.IsSuccessStatusCode, await download.Content.ReadAsStringAsync());
        await using var bytes = await download.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(bytes);
        Assert.Contains(archive.Entries, item => item.FullName.EndsWith("-BOM.xlsx", StringComparison.Ordinal));
        var preview = Assert.Single(archive.Entries, item => item.FullName.EndsWith(step ? ".step" : ".pdf", StringComparison.Ordinal));
        using var reader = new StreamReader(preview.Open());
        Assert.Equal(step ? "正式STEP" : "正式PDF", await reader.ReadToEndAsync());
        using var workbookBytes = new MemoryStream();
        await Assert.Single(archive.Entries, item => item.FullName.EndsWith("-BOM.xlsx", StringComparison.Ordinal)).Open().CopyToAsync(workbookBytes);
        workbookBytes.Position = 0;
        using var workbook = new ZipArchive(workbookBytes);
        var sheet = System.Xml.Linq.XDocument.Load(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = sheet.Descendants(ns + "row").ToArray();
        var header = rows.Single(row => row.Elements(ns + "c").Any(cell => cell.Value == "BOM总量"));
        var data = rows.SkipWhile(row => row != header).Skip(1).First();
        foreach (var expected in new[] { ("BOM总量", "6"), ("已提前发布", "0"), ("本次新增下发", "6"), ("单套数量", "2"), ("整套数量", "3") })
        {
            var column = new string(header.Elements(ns + "c").Single(cell => cell.Value == expected.Item1).Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray());
            Assert.Equal(expected.Item2, data.Elements(ns + "c").Single(cell => new string(cell.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray()) == column).Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteDrawingBlocksCachedProductionSelectionButPreservesEngineeringHistory(bool step)
    {
        using var fixture = await CreateFixtureAsync();
        var list = await fixture.Client.GetFromJsonAsync<ProductionDrawingItem[]>(
            $"/api/production-drawings?includeHistory=true&projectId={fixture.Project.Id}");
        Assert.Single(list!);
        (await fixture.Client.GetAsync(fixture.PreviewUrl(step))).EnsureSuccessStatusCode();

        await fixture.Repository.ObsoleteDocumentAsync(fixture.Drawing.Id, "admin", default);

        using var response = await fixture.Client.GetAsync(fixture.PreviewUrl(step));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("已作废", await response.Content.ReadAsStringAsync());
        using var archive = await fixture.DownloadArchiveAsync(step);
        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
        Assert.Contains("已作废", await archive.Content.ReadAsStringAsync());
        var after = await fixture.Client.GetFromJsonAsync<ProductionDrawingItem[]>(
            $"/api/production-drawings?includeHistory=true&projectId={fixture.Project.Id}");
        Assert.Empty(after!);
        using var engineering = await fixture.Client.GetAsync(
            $"/api/documents/{fixture.Drawing.Id}/versions/{fixture.DrawingVersion.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, engineering.StatusCode);
        Assert.Equal("正式PDF", await engineering.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ObsoleteModelBlocksStepAndStepArchiveWhileDrawingPdfRemainsAvailable()
    {
        using var fixture = await CreateFixtureAsync();
        await fixture.Repository.ObsoleteDocumentAsync(fixture.Model.Id, "admin", default);

        using var step = await fixture.Client.GetAsync(fixture.PreviewUrl(true));
        Assert.Equal(HttpStatusCode.Conflict, step.StatusCode);
        Assert.Contains("已作废", await step.Content.ReadAsStringAsync());
        using var archive = await fixture.DownloadArchiveAsync(true);
        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
        using var pdf = await fixture.Client.GetAsync(fixture.PreviewUrl(false));
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
    }

    [Fact]
    public async Task ModelFromAnotherReleasePackageCannotBeSubstitutedIntoDrawingDownload()
    {
        using var fixture = await CreateFixtureAsync();
        var otherPackage = fixture.Package with { Id = Guid.NewGuid(), Number = "OTHER-PACKAGE" };
        await fixture.Repository.CreateReleasePackageAsync(otherPackage, default);
        var otherVersion = fixture.ModelVersion with { Id = Guid.NewGuid(), ReleasePackageId = otherPackage.Id };
        Store<DocumentVersion>(fixture.Repository, "versions")[otherVersion.Id] = otherVersion;

        using var response = await fixture.Client.GetAsync(fixture.PreviewUrl(false)
            + $"?modelDocumentId={fixture.Model.Id}&modelVersionId={otherVersion.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("同一发布包", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ViewPermissionAloneCannotDownloadProductionFiles()
    {
        using var fixture = await CreateFixtureAsync();
        var folders = await fixture.Repository.ListProjectFoldersAsync(fixture.Project.Id, "admin", UserRole.Administrator, default);
        var folder = folders.Single(item => item.Id == fixture.Drawing.FolderId);
        await fixture.Repository.SetProjectFolderPermissionsAsync(fixture.Project.Id, folder.Id,
            [new(FolderPrincipalType.Role, nameof(UserRole.Engineer), FolderAccess.View)], "admin", UserRole.Administrator, default);
        Authorize(fixture.Client, "engineer", "Engineer");

        using var response = await fixture.Client.GetAsync(fixture.PreviewUrl(false));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var archive = await fixture.DownloadArchiveAsync(false);
        Assert.Equal(HttpStatusCode.Forbidden, archive.StatusCode);
        using var engineering = await fixture.Client.GetAsync(
            $"/api/documents/{fixture.Drawing.Id}/versions/{fixture.DrawingVersion.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, engineering.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteDuringArchiveCreationDiscardsThePreparedZip(bool step)
    {
        using var fixture = await CreateFixtureAsync();
        using var scope = factory.Services.CreateScope();
        var storage = DispatchProxy.Create<IFileStorage, RevokeOnReadStorage>();
        var hook = (RevokeOnReadStorage)storage;
        hook.Inner = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        hook.Revoke = () => fixture.Repository.ObsoleteDocumentAsync(
            step ? fixture.Model.Id : fixture.Drawing.Id, "admin", default).GetAwaiter().GetResult();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPdmRepository>();
            services.AddSingleton<IPdmRepository>(fixture.Repository);
            services.RemoveAll<IFileStorage>();
            services.AddSingleton(storage);
        }));
        using var client = app.CreateClient();
        Authorize(client, "admin", "Administrator");

        using var response = await client.PostAsJsonAsync($"/api/projects/{fixture.Project.Id}/production-drawings/archive",
            new { versionIds = new[] { fixture.DrawingVersion.Id }, format = step ? "Step" : "Pdf" });

        Assert.True(hook.Triggered);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("已作废", await response.Content.ReadAsStringAsync());
        Assert.NotEqual("application/zip", response.Content.Headers.ContentType!.MediaType);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var repository = (InMemoryPdmRepository)factory.Services.GetRequiredService<IPdmRepository>();
        var root = Path.Combine(Path.GetTempPath(), "pdm-production-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = await repository.CreateProjectAsync(new("PROD-ACCESS-" + Guid.NewGuid().ToString("N"),
            "生产取用验收", "admin", root, root), "admin", default);
        var model = await repository.RegisterDocumentAsync(new(project.Id, "PART", "模型", "PART.SLDPRT", DocumentKind.Part), "admin", default);
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAWING", "图纸", "DRAWING.SLDDRW",
            DocumentKind.Drawing, RelatedModelDocumentId: model.Id), "admin", default);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-ACCESS", ReleasePackageState.Published,
            null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, root)
        {
            Scope = ReleaseScope.NonStandardWithDrawing, PreviewState = ReleasePreviewState.Succeeded, WholeSetMultiplier = 3,
            NonStandardBomSnapshot = [new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1,
                model.DrawingNumber, model.Name, 2, "件", null, null, "B", true) { SourceDocumentId = model.Id }]
        };
        await repository.CreateReleasePackageAsync(package, default);
        var drawingVersion = await ReleasedVersionAsync(drawing, package.Id, root, DocumentPreviewFormat.Pdf, "正式PDF");
        var modelVersion = await ReleasedVersionAsync(model, package.Id, root, DocumentPreviewFormat.Step, "正式STEP");
        var versions = Store<DocumentVersion>(repository, "versions");
        versions[drawingVersion.Id] = drawingVersion;
        versions[modelVersion.Id] = modelVersion;
        return new(repository, client, project, drawing, model, drawingVersion, modelVersion, package, root);
    }

    private static async Task<DocumentVersion> ReleasedVersionAsync(
        PdmDocument document, Guid packageId, string root, DocumentPreviewFormat format, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var relative = Path.Combine(".release-previews", packageId.ToString("N"),
            document.Id.ToString("N") + (format == DocumentPreviewFormat.Pdf ? ".pdf" : ".step"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
        await File.WriteAllBytesAsync(Path.Combine(root, relative), bytes);
        return new(Guid.NewGuid(), document.Id, RevisionLabel.Released('B'), DocumentVersionStatus.Released,
            document.FileName, 12, hash, "admin", DateTimeOffset.UtcNow, "正式发布", new Dictionary<string, string?>(),
            new(Guid.NewGuid(), document.Id, document.FileName, document.FileName, document.Name, document.Kind,
                "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.Released('B'), null, []),
            [], [], null, null, null, packageId, new(format, relative, bytes.Length, hash, hash));
    }

    private static ConcurrentDictionary<Guid, T> Store<T>(InMemoryPdmRepository repository, string field)
        => (ConcurrentDictionary<Guid, T>)typeof(InMemoryPdmRepository)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    private static void Authorize(HttpClient client, string username, string role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    private sealed record Fixture(InMemoryPdmRepository Repository, HttpClient Client, Project Project, PdmDocument Drawing,
        PdmDocument Model, DocumentVersion DrawingVersion, DocumentVersion ModelVersion, ReleasePackage Package, string Root) : IDisposable
    {
        public string PreviewUrl(bool step) => $"/api/production-drawings/{Drawing.Id}/versions/{DrawingVersion.Id}/preview"
            + (step ? $"?modelDocumentId={Model.Id}&modelVersionId={ModelVersion.Id}" : "");

        public Task<HttpResponseMessage> DownloadArchiveAsync(bool step) => Client.PostAsJsonAsync(
            $"/api/projects/{Project.Id}/production-drawings/archive", new { versionIds = new[] { DrawingVersion.Id }, format = step ? "Step" : "Pdf" });

        public void Dispose()
        {
            Client.Dispose();
            Directory.Delete(Root, true);
        }
    }

    public class RevokeOnReadStorage : DispatchProxy
    {
        public IFileStorage Inner { get; set; } = null!;
        public Action Revoke { get; set; } = null!;
        public bool Triggered { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFileStorage.OpenReadAsync) && !Triggered)
            {
                Triggered = true;
                Revoke();
            }
            return targetMethod.Invoke(Inner, args);
        }
    }
}
