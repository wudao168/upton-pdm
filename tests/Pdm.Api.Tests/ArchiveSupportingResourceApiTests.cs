using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
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

public sealed class ArchiveSupportingResourceApiTests
{
    [Fact]
    public async Task ImageAndStepFollowTheirNativeParentAcrossRegistrationCommitReplayAndOpenManifest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdm-aux-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, ".versions"));
        try
        {
            using var factory = new AuxiliaryFactory(folder);
            using var client = factory.CreateClient();
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
                [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            var repository = factory.Services.GetRequiredService<IPdmRepository>();
            Assert.Contains("InMemory", repository.GetType().Name);
            var project = await repository.CreateProjectAsync(new("AUX-HTTP-" + Guid.NewGuid().ToString("N"), "HTTP随附资源", "admin", folder, folder), "admin", default);
            var batch = Guid.NewGuid(); var session = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes("isolated resource fixture bytes"); var hash = Convert.ToHexString(SHA256.HashData(bytes));
            async Task<PdmDocument> Reserve(string name, DocumentKind kind)
            {
                var response = await client.PostAsJsonAsync($"/api/archive-batches/{batch}/projects/{project.Id}/registrations",
                    new { document = new { drawingNumber = Path.GetFileNameWithoutExtension(name), name, fileName = name, kind, sourceSha256 = hash }, sessionId = session, machineName = "QA" });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                return (await response.Content.ReadFromJsonAsync<PdmDocument>())!;
            }
            var root = await Reserve("ROOT.SLDASM", DocumentKind.Assembly);
            var image = await Reserve("pplastic2.jpg", DocumentKind.Other);
            var step = await Reserve("H620_外观.STEP", DocumentKind.Other);
            Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
            DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name,
                doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "admin", [])
                { Role = doc.Kind == DocumentKind.Other ? ReferenceRole.ExternalFileDependency : ReferenceRole.Component };
            // Match the plugin's fixed native inventory, rather than the legacy
            // tree-only projection which intentionally omits unverified old sources.
            var tree = Node(root) with { Children = [Node(image), Node(step)], IncludesDesignDependencies = true, UsesNativeFileInventory = true };
            object Input(PdmDocument doc)
            {
                var relative = ".versions/" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(Path.Combine(folder, relative), bytes);
                File.SetAttributes(Path.Combine(folder, relative), FileAttributes.ReadOnly);
                return new { documentId = doc.Id, request = new { projectId = project.Id, root = doc.Id == root.Id ? tree : Node(doc),
                    comment = "主图档与随附资源", storageRelativePath = relative, fileLength = bytes.Length, sha256 = hash,
                    checkoutSessionId = session, isProjectRoot = doc.Id == root.Id, properties = new Dictionary<string, string?>() } };
            }
            var payload = new { batchId = batch, checkoutSessionId = session, files = new[] { Input(root), Input(image), Input(step) } };
            var committed = await client.PostAsJsonAsync("/api/archive-batches", payload);
            Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
            var receipt = (await committed.Content.ReadFromJsonAsync<ArchiveBatchReceipt>())!;
            Assert.Equal(3, receipt.Results.Count);
            (await client.PostAsJsonAsync("/api/archive-batches", payload)).EnsureSuccessStatusCode();
            Assert.Single(await repository.ListDocumentVersionsAsync(image.Id, default));
            Assert.Single(await repository.ListDocumentVersionsAsync(step.Id, default));
            var manifest = await client.PostAsJsonAsync($"/api/documents/{root.Id}/open-manifest",
                new { versionId = (Guid?)null, releasedOnly = false, forEdit = false, resolveLegacyReferences = false, treeScopeOnly = true });
            Assert.True(manifest.IsSuccessStatusCode, await manifest.Content.ReadAsStringAsync());
            var data = await manifest.Content.ReadFromJsonAsync<JsonElement>();
            var names = data.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("fileName").GetString()).ToArray();
            Assert.Contains("pplastic2.jpg", names); Assert.Contains("H620_外观.STEP", names);
            foreach (var file in data.GetProperty("files").EnumerateArray().Where(file => file.GetProperty("fileName").GetString() != root.FileName))
            {
                var downloaded = await client.GetByteArrayAsync($"/api/documents/{file.GetProperty("documentId").GetGuid()}/versions/{file.GetProperty("versionId").GetGuid()}/file?download=false");
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(downloaded)));
            }
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PdmWorkflowService>().CompleteArchiveBatchFollowupAsync(new(receipt, [root, image, step]), default);
            foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified })
                Assert.DoesNotContain(await repository.GetBomAsync(project.Id, kind, default), item => item.SourceDocumentId == image.Id || item.SourceDocumentId == step.Id);
        }
        finally
        {
            var resolved = Path.GetFullPath(folder);
            if (Directory.Exists(resolved) && resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(resolved, true);
            }
        }
    }

    private sealed class AuxiliaryFactory(string folder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pdm:Database:Provider"] = "InMemory", ["Pdm:Database:RunMigrations"] = "false",
                ["Pdm:Storage:UploadTempRoot"] = Path.Combine(folder, "uploads")
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                    && item.ImplementationType?.Namespace == "Upton.Pdm.Api").ToArray()) services.Remove(service);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
        }
    }
}
