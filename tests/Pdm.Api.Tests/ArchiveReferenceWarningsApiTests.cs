using System.IdentityModel.Tokens.Jwt;
using System.Net;
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

public sealed class ArchiveReferenceWarningsApiTests
{
    [Fact]
    public async Task MissingArchiveBatchReturnsNotFoundForRecovery()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var response = await client.GetAsync($"/api/archive-batches/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("该批次尚未完成提交。", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ArchivePreservesNoticesAcrossHttpVersionReadAndReplayButRejectsBadBytes()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/archive-capabilities")).StatusCode);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var capabilities = await client.GetFromJsonAsync<Dictionary<string, bool>>("/api/archive-capabilities");
        Assert.True(capabilities!["referenceWarnings"]);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var directory = Path.Combine(Path.GetTempPath(), "pdm-reference-warning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".versions"));
        try
        {
            var project = await repository.CreateProjectAsync(new("WARNING-" + Guid.NewGuid().ToString("N"), "引用提示接口", "admin", directory, directory), "admin", default);
            var part = await repository.RegisterDocumentAsync(new(project.Id, "PART", "PART", "PART.SLDPRT", DocumentKind.Part), "admin", default);
            var session = Guid.NewGuid();
            part = await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
            string[] warnings = [@"来源：Z:\下载\SOURCE.STEP；未找到原始来源；原有关联保留", @"来源：Z:\设计库\滑块.SLDPRT；智能关联源缺失；原有关联保留"];
            var root = new DocumentReferenceNode(Guid.NewGuid(), part.Id, "PART", part.FileName, part.Name, part.Kind,
                "默认", 1, ReferenceNodeStatus.Normal, part.Revision, "admin", [])
                { ReferenceWarnings = warnings, UsesNativeFileInventory = true, IncludesDesignDependencies = true };
            var bytes = Encoding.UTF8.GetBytes("isolated HTTP fixture; no real CAD opened");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            object Payload(Guid batch, bool invalid)
            {
                var relative = ".versions/" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(Path.Combine(directory, relative), bytes);
                File.SetAttributes(Path.Combine(directory, relative), FileAttributes.ReadOnly);
                return new { batchId = batch, checkoutSessionId = session, files = new[] { new { documentId = part.Id,
                    request = new { projectId = project.Id, root, comment = "存档并保留来源提示", storageRelativePath = relative,
                        fileLength = bytes.Length, sha256 = invalid ? new string('F', 64) : hash, checkoutSessionId = session,
                        isProjectRoot = false, properties = new Dictionary<string, string?>() } } } };
            }
            var invalid = await client.PostAsJsonAsync("/api/archive-batches", Payload(Guid.NewGuid(), true));
            Assert.False(invalid.IsSuccessStatusCode);
            Assert.Empty(await repository.ListDocumentVersionsAsync(part.Id, default));
            var batch = Guid.NewGuid(); var payload = Payload(batch, false);
            var response = await client.PostAsJsonAsync("/api/archive-batches", payload);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            (await client.PostAsJsonAsync("/api/archive-batches", payload)).EnsureSuccessStatusCode();
            using var reopened = factory.CreateClient();
            reopened.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
            var versions = await reopened.GetFromJsonAsync<JsonElement[]>($"/api/documents/{part.Id}/versions");
            var stored = Assert.Single(versions!);
            Assert.Equal(warnings, stored.GetProperty("referenceSnapshot").GetProperty("referenceWarnings").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(hash, stored.GetProperty("sha256").GetString());
            Assert.Empty(stored.GetProperty("referenceSnapshot").GetProperty("children").EnumerateArray());
            Assert.Single(await repository.ListDocumentsAsync(project.Id, default));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
}
