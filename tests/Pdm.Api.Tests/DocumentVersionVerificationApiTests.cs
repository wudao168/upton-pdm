using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class DocumentVersionVerificationApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task ReturnsOnlyLatestAndRequestedBoundChecksumsWithoutSnapshots()
    {
        using var fixture = await CreateAsync();
        var first = fixture.Seed(fixture.Document, "W1", 'A', DateTimeOffset.UtcNow.AddMinutes(-1));
        var latest = fixture.Seed(fixture.Document, "W2", 'B', DateTimeOffset.UtcNow);
        using var response = await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications",
            new { documents = new[] { new { documentId = fixture.Document.Id, boundVersionId = first.Id } } });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var row = Assert.Single((await response.Content.ReadFromJsonAsync<DocumentVersionVerificationResult[]>())!);
        Assert.True(row.Accessible);
        Assert.Equal(latest.Id, row.Latest!.Id); Assert.Equal(first.Id, row.Bound!.Id);
        Assert.Equal(latest.Sha256, row.Latest.Sha256);
        Assert.Equal(new string('C', 64), row.Latest.SourceFileSha256);
        Assert.Equal(128, row.Latest.FileLength);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("referenceSnapshot", json); Assert.DoesNotContain("bomSnapshot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("propertySnapshot", json); Assert.DoesNotContain("STRUCTURE-AND-BOM-MARKER", json);
        var full = await fixture.Client.GetStringAsync($"/api/documents/{fixture.Document.Id}/versions?take=1");
        Assert.Contains("STRUCTURE-AND-BOM-MARKER", full);
        Assert.True(Encoding.UTF8.GetByteCount(json) * 100 < Encoding.UTF8.GetByteCount(full));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("empty-document")]
    [InlineData("empty-version")]
    public async Task RejectsInvalidBatches(string kind)
    {
        using var fixture = await CreateAsync();
        var documents = kind switch {
            "empty" => Array.Empty<DocumentVersionVerificationRequest>(),
            "too-many" => Enumerable.Range(0, 101).Select(_ => new DocumentVersionVerificationRequest(Guid.NewGuid(), null)).ToArray(),
            "duplicate" => new[] { new DocumentVersionVerificationRequest(fixture.Document.Id, null), new(fixture.Document.Id, Guid.NewGuid()) },
            "empty-document" => [new DocumentVersionVerificationRequest(Guid.Empty, null)],
            _ => [new DocumentVersionVerificationRequest(fixture.Document.Id, Guid.Empty)]
        };
        using var response = await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications", new { documents });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("每批须包含", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForeignAndMissingBoundVersionsNeverAttachToAnotherDocument()
    {
        using var fixture = await CreateAsync();
        var other = await fixture.Repository.RegisterDocumentAsync(new(fixture.Project.Id, "OTHER", "其他图档", "OTHER.SLDPRT", DocumentKind.Part), "admin", default);
        var own = fixture.Seed(fixture.Document, "W1", 'A', DateTimeOffset.UtcNow);
        var foreign = fixture.Seed(other, "W1", 'B', DateTimeOffset.UtcNow);
        foreach (var bound in new[] { foreign.Id, Guid.NewGuid(), own.Id })
        {
            var rows = await (await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications",
                new { documents = new[] { new { documentId = fixture.Document.Id, boundVersionId = bound } } })).Content.ReadFromJsonAsync<DocumentVersionVerificationResult[]>();
            var row = Assert.Single(rows!); Assert.Equal(own.Id, row.Latest!.Id); Assert.Null(row.Bound);
        }
    }

    [Fact]
    public async Task UnarchivedDocumentHasNoInventedVersion()
    {
        using var fixture = await CreateAsync();
        var rows = await (await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications",
            new { documents = new[] { new { documentId = fixture.Document.Id, boundVersionId = (Guid?)null } } })).Content.ReadFromJsonAsync<DocumentVersionVerificationResult[]>();
        var row = Assert.Single(rows!); Assert.True(row.Accessible); Assert.Null(row.Latest); Assert.Null(row.Bound);
    }

    [Fact]
    public async Task MixedBatchChecksEachProjectsPermissionWithoutLeakingDeniedMetadata()
    {
        using var fixture = await CreateAsync("engineer");
        var inaccessibleProject = await fixture.Repository.CreateProjectAsync(new("DENIED-" + Guid.NewGuid().ToString("N"), "无权项目", "unrelated-owner", "vault", "release"), "admin", default);
        var denied = await fixture.Repository.RegisterDocumentAsync(new(inaccessibleProject.Id, "DENIED", "无权图档", "DENIED.SLDPRT", DocumentKind.Part), "admin", default);
        var folders = await fixture.Repository.ListProjectFoldersAsync(inaccessibleProject.Id, "admin", UserRole.Administrator, default);
        var folder = folders.Single(item => item.Id == denied.FolderId);
        await fixture.Repository.SetProjectFolderPermissionsAsync(inaccessibleProject.Id, folder.Id,
            [new(FolderPrincipalType.Role, nameof(UserRole.Engineer), FolderAccess.None)], "admin", UserRole.Administrator, default);
        var allowed = fixture.Seed(fixture.Document, "W1", 'A', DateTimeOffset.UtcNow);
        var secret = fixture.Seed(denied, "W1", 'B', DateTimeOffset.UtcNow);
        fixture.Authenticate("engineer", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync($"/api/documents/{denied.Id}/versions?take=1")).StatusCode);
        using var response = await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications",
            new { documents = new[] { new { documentId = fixture.Document.Id, boundVersionId = (Guid?)null }, new { documentId = denied.Id, boundVersionId = (Guid?)secret.Id } } });
        response.EnsureSuccessStatusCode();
        var rows = (await response.Content.ReadFromJsonAsync<DocumentVersionVerificationResult[]>())!;
        Assert.Equal(allowed.Id, rows.Single(row => row.DocumentId == fixture.Document.Id).Latest!.Id);
        var blocked = rows.Single(row => row.DocumentId == denied.Id);
        Assert.False(blocked.Accessible); Assert.Null(blocked.Latest); Assert.Null(blocked.Bound);
        Assert.DoesNotContain(secret.Sha256, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequiresAuthentication()
    {
        using var fixture = await CreateAsync(); fixture.Client.DefaultRequestHeaders.Authorization = null;
        using var response = await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications", new { documents = new[] { new { documentId = fixture.Document.Id } } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BatchEndpointRequiresTheSameGranularViewPermissionAsVersionHistory()
    {
        using var fixture = await CreateAsync("engineer");
        await fixture.Repository.SetRolePermissionsAsync("Engineer",
            [FunctionPermissionCatalog.SchemaMarker, PermissionCodes.ProjectContentView], default);
        fixture.Authenticate("engineer", "Engineer");
        using var response = await fixture.Client.PostAsJsonAsync("/api/documents/version-verifications",
            new { documents = new[] { new { documentId = fixture.Document.Id } } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<Fixture> CreateAsync(string owner = "admin")
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPdmRepository>(); services.AddSingleton<IPdmRepository>(repository);
        }));
        var client = host.CreateClient();
        var project = await repository.CreateProjectAsync(new("VERIFY-" + Guid.NewGuid().ToString("N"), "批量校验", owner, "vault", "release"), owner, default);
        var document = await repository.RegisterDocumentAsync(new(project.Id, "ROOT", "根图档", "ROOT.SLDASM", DocumentKind.Assembly), owner, default);
        var fixture = new Fixture(host, client, repository, project, document); fixture.Authenticate("admin", "Administrator"); return fixture;
    }

    private sealed record Fixture(WebApplicationFactory<Program> Host, HttpClient Client, InMemoryPdmRepository Repository, Project Project, PdmDocument Document) : IDisposable
    {
        public void Authenticate(string actor, string role)
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
                [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials)));
        }
        public DocumentVersion Seed(PdmDocument document, string revision, char hash, DateTimeOffset at)
        {
            var snapshot = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.FileName, document.FileName,
                "STRUCTURE-AND-BOM-MARKER" + new string('X', 100_000), document.Kind, "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.Parse(revision), null, []);
            var version = new DocumentVersion(Guid.NewGuid(), document.Id, RevisionLabel.Parse(revision), DocumentVersionStatus.Work,
                "internal-path", 128, new string(hash, 64), "admin", at, "不可传输的完整属性", new Dictionary<string, string?> { ["SourceFileSha256"] = new string('C', 64) }, snapshot, [], [], null, null, null, null, null);
            ((ConcurrentDictionary<Guid, DocumentVersion>)typeof(InMemoryPdmRepository).GetField("versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Repository)!)[version.Id] = version;
            return version;
        }
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
}
