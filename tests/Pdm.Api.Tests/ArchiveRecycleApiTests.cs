using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Xunit;

namespace Upton.Pdm.Api.Tests;

public sealed class ArchiveRecycleApiTests
{
    [Fact]
    public async Task PreviewRecycleAndRestoreUseAtomicProjectScopedRoutes()
    {
        using var factory = new RecycleFactory(); using var client = factory.CreateClient();
        void Authenticate(string role)
        {
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        Authenticate("Administrator");
        var repository = factory.Services.GetRequiredService<IPdmRepository>(); Assert.Contains("InMemory", repository.GetType().Name);
        var project = await repository.CreateProjectAsync(new("REC-HTTP-" + Guid.NewGuid().ToString("N"), "批次回收HTTP测试", "admin", "vault", "release"), "admin", default);
        var batch = Guid.NewGuid(); var session = Guid.NewGuid(); var registration = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind) => await repository.ReserveArchiveDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "admin", registration, default);
        var part = await Reserve("PART", DocumentKind.Part); var root = await Reserve("ROOT", DocumentKind.Assembly);
        DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name, doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "admin", []);
        DocumentVersionCommit Commit(PdmDocument doc, bool isRoot) => new(new StoredFile(".versions/" + doc.Id, 20, new string('A', 64), DateTimeOffset.UtcNow), "首次存档", new Dictionary<string, string?>(),
            new(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "admin", isRoot ? Node(doc) with { Children = [Node(part)] } : Node(doc), new string('B', 64)), [], [], IsProjectRoot: isRoot);
        await repository.CheckInBatchVersionsAsync(batch, "fixture", "admin", session, [new(part.Id, Commit(part, false)), new(root.Id, Commit(root, true))], default);
        await repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var route = $"/api/projects/{project.Id}/documents";
        var batches = await client.GetFromJsonAsync<JsonElement>(route + "/archive-batches"); Assert.Equal(batch, batches[0].GetProperty("batchId").GetGuid());
        var preview = await client.GetFromJsonAsync<ArchiveRecyclePreview>(route + $"/archive-batches/{batch}/recycle-preview"); Assert.True(preview!.CanRecycle); Assert.Equal(2, preview.Members.Count);
        var wrong = await client.PostAsJsonAsync(route + $"/archive-batches/{batch}/recycle", new { scopeHash = "stale", reason = "误操作" }); Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(2, (await repository.ListDocumentsAsync(project.Id, default)).Count);
        Authenticate("Engineer"); var denied = await client.GetAsync(route + "/archive-batches"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Authenticate("Administrator");
        var recycle = await client.PostAsJsonAsync(route + $"/archive-batches/{batch}/recycle", new { scopeHash = preview.ScopeHash, reason = "存错项目" }); Assert.True(recycle.IsSuccessStatusCode, await recycle.Content.ReadAsStringAsync());
        var set = (await recycle.Content.ReadFromJsonAsync<ArchiveRecycleSet>())!; Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
        Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        var single = await client.PostAsJsonAsync(route + $"/{root.Id}/restore", new { expectedRowVersion = (await repository.FindDocumentIncludingDeletedAsync(root.Id, default))!.RowVersion }); Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        var restore = await client.PostAsync(route + $"/recycle-sets/{set.Id}/restore", null); Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.Equal(2, (await repository.ListDocumentsAsync(project.Id, default)).Count); Assert.NotNull(await repository.GetLatestReferenceSnapshotAsync(project.Id, default));
    }
    [Fact]
    public async Task BatchRecycleKeepsReferencedDocumentsAndCanRestoreSuccessfulItems()
    {
        using var factory = new RecycleFactory(); using var client = factory.CreateClient();
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        var repository = factory.Services.GetRequiredService<IPdmRepository>(); Assert.Contains("InMemory", repository.GetType().Name);
        var project = await repository.CreateProjectAsync(new("REC-BATCH-" + Guid.NewGuid().ToString("N"), "单项回收隔离测试", "admin", "vault", "release"), "admin", default);
        var batch = Guid.NewGuid(); var session = Guid.NewGuid(); var registration = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind) => await repository.ReserveArchiveDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "admin", registration, default);
        var free = await Reserve("FREE", DocumentKind.Part); var used = await Reserve("USED", DocumentKind.Part); var root = await Reserve("ROOT", DocumentKind.Assembly);
        DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name, doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "admin", []);
        DocumentVersionCommit Commit(PdmDocument doc, bool isRoot) => new(new StoredFile(".versions/" + doc.Id, 20, new string('A', 64), DateTimeOffset.UtcNow), "测试存档", new Dictionary<string, string?>(),
            new(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "admin", isRoot ? Node(doc) with { Children = [Node(used)] } : Node(doc), new string('B', 64)), [], [], IsProjectRoot: isRoot);
        await repository.CheckInBatchVersionsAsync(batch, "fixture", "admin", session, [new(free.Id, Commit(free, false)), new(used.Id, Commit(used, false)), new(root.Id, Commit(root, true))], default);
        await repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var freeBefore = (await repository.FindDocumentAsync(free.Id, default))!;
        var usedBefore = (await repository.FindDocumentAsync(used.Id, default))!;
        var route = $"/api/projects/{project.Id}/documents";
        var response = await client.PostAsJsonAsync(route + "/recycle-batch", new
        {
            items = new[] { new { documentId = free.Id, expectedRowVersion = freeBefore.RowVersion }, new { documentId = used.Id, expectedRowVersion = usedBefore.RowVersion } },
            reason = "隔离验证"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<ControlledDocumentRecycleBatchResult>())!;
        Assert.Equal(free.Id, Assert.Single(result.RecycledDocumentIds));
        Assert.Equal(used.Id, Assert.Single(result.Failures).DocumentId); Assert.Contains("引用", result.Failures[0].Reason);
        var deleted = (await repository.FindDocumentIncludingDeletedAsync(free.Id, default))!; Assert.NotNull(deleted.DeletedAt);
        Assert.Null((await repository.FindDocumentIncludingDeletedAsync(used.Id, default))!.DeletedAt);
        Assert.Single(await repository.ListDocumentVersionsAsync(free.Id, default));
        var restore = await client.PostAsJsonAsync(route + $"/{free.Id}/restore", new { expectedRowVersion = deleted.RowVersion });
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.Null((await repository.FindDocumentIncludingDeletedAsync(free.Id, default))!.DeletedAt);
        Assert.Single(await repository.ListDocumentVersionsAsync(free.Id, default));
    }

    private sealed class RecycleFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory); builder.UseEnvironment("Development"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Pdm:Database:Provider"] = "InMemory", ["Pdm:Database:RunMigrations"] = "false" }));
            builder.ConfigureServices(services => { foreach (var service in services.Where(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && item.ImplementationType?.Namespace == "Upton.Pdm.Api").ToArray()) services.Remove(service); services.AddDataProtection().UseEphemeralDataProtectionProvider(); });
        }
    }
}
