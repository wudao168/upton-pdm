using System.Net;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class AssemblyPreviewApiTests
{
    [Fact]
    public async Task LatestAssemblyEndpointRequiresAuthenticationAndReturnsFrozenVersionAndVerifiedContent()
    {
        using var factory = new PdmApiFactory();
        var converter = new RecordingConverter();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAssemblyPreviewConverter>();
            services.AddSingleton<IAssemblyPreviewConverter>(converter);
        }));
        using var client = host.CreateClient();
        var repository = host.Services.GetRequiredService<IPdmRepository>();
        var vault = Path.Combine(Path.GetTempPath(), "pdm-assembly-api-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(vault, ".versions", "test", "PARENT.SLDASM");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, new byte[100]);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var project = await repository.CreateProjectAsync(new($"ASM-PREVIEW-{Guid.NewGuid():N}", "装配预览接口", "admin",
            vault, vault), "admin", default);
        var document = await repository.RegisterDocumentAsync(new(project.Id, "PARENT", "父装配", "PARENT.SLDASM", DocumentKind.Assembly), "admin", default);
        document = await repository.CheckoutAsync(document.Id, "admin", default);
        var tree = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.FileName, document.FileName, document.Name,
            DocumentKind.Assembly, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, null, []);
        var result = await repository.CheckInVersionAsync(document.Id, "admin", new(new(".versions/test/PARENT.SLDASM", 100, new string('A', 64), DateTimeOffset.UtcNow),
            "测试", new Dictionary<string, string?>(), new(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "admin", tree, new string('B', 64)), [], []), default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/documents/{document.Id}/assembly-preview", null)).StatusCode);
        Assert.Null(converter.Manifest);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        try
        {
            var response = await client.PostAsync($"/api/documents/{document.Id}/assembly-preview", null);
            response.EnsureSuccessStatusCode();
            Assert.Equal(result.Version!.Id.ToString("D"), response.Headers.GetValues("X-Pdm-Preview-Version").Single());
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(converter.Bytes, bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), response.Headers.GetValues("X-Pdm-Preview-Sha256").Single());
            Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(converter.Manifest!.ForEdit);
            Assert.Equal("admin", converter.Initiator);
            Assert.Equal(result.Version.Id, converter.Manifest.RootVersionId);
            Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, default));
        }
        finally
        {
            if (File.Exists(converter.Path)) File.Delete(converter.Path);
            File.SetAttributes(source, FileAttributes.Normal);
            Directory.Delete(vault, true);
        }
    }

    private sealed class RecordingConverter : IAssemblyPreviewConverter
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".easm");
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes("rebuilt assembly preview");
        public ControlledOpenManifest? Manifest { get; private set; }
        public string? Initiator { get; private set; }
        public async Task<AssemblyPreviewFile> GenerateAssemblyAsync(ControlledOpenManifest manifest, Project project, CancellationToken cancellationToken, string? initiator = null)
        {
            Manifest = manifest;
            Initiator = initiator;
            await File.WriteAllBytesAsync(Path, Bytes, cancellationToken);
            return new(Path, manifest.RootVersionId, Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)));
        }
    }
}
