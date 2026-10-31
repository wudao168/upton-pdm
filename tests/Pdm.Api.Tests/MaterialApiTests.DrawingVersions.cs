using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class MaterialApiTests
{
    [Theory]
    [InlineData("Drawing2D", "内部图纸.pdf")]
    [InlineData("Model3D", "内部模型.step")]
    public async Task DrawingVersions_UploadAutomaticallyIncrementsAndOnlyStandardizationCanEnable(string kind, string name)
    {
        var fixture = await SeedDeletionAttachmentAsync(MaterialAttachmentKind.Document);
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var settings = await repository.GetSystemSettingsAsync(default);
        var root = Path.Combine(Path.GetTempPath(), "pdm-drawing-api", Guid.NewGuid().ToString("N"));
        await repository.UpdateSystemSettingsAsync(settings with { MaterialAttachmentRoot = root }, default);
        try
        {
        async Task<JsonElement> Upload()
        {
            var data = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
            var started = await client.PostAsJsonAsync($"/api/materials/{fixture.Material.Id}/attachments/uploads", new { kind, fileName = name, totalLength = data.Length, sha256 = Convert.ToHexString(SHA256.HashData(data)) });
            started.EnsureSuccessStatusCode();
            var session = await started.Content.ReadFromJsonAsync<JsonElement>();
            var id = session.GetProperty("id").GetGuid();
            (await client.PutAsync($"/api/material-attachment-uploads/{id}/chunks/0", new ByteArrayContent(data))).EnsureSuccessStatusCode();
            var finished = await client.PostAsync($"/api/material-attachment-uploads/{id}/complete", null);
            Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync());
            return await finished.Content.ReadFromJsonAsync<JsonElement>();
        }
        var first = await Upload(); var second = await Upload();
        Assert.Equal(1, first.GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, second.GetProperty("versionNumber").GetInt32());
        Assert.False(second.GetProperty("isEnabled").GetBoolean());
        var firstId = first.GetProperty("id").GetGuid(); var secondId = second.GetProperty("id").GetGuid();
        var firstPath = $"/api/materials/{fixture.Material.Id}/attachments/{firstId}/enable";
        (await client.PostAsJsonAsync(firstPath, new { expectedEnabledId = (Guid?)null })).EnsureSuccessStatusCode();
        using var engineer = factory.CreateClient();
        engineer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        var secondPath = $"/api/materials/{fixture.Material.Id}/attachments/{secondId}/enable";
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsJsonAsync(secondPath, new { expectedEnabledId = firstId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(secondPath, new { expectedEnabledId = (Guid?)null })).StatusCode);
        (await client.PostAsJsonAsync(secondPath, new { expectedEnabledId = firstId })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/materials/{fixture.Material.Id}/attachments/{firstId}")).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/materials/{fixture.Material.Id}/attachments?kind={kind}");
        Assert.Equal(2, history!.Length);
        Assert.Equal(secondId, Assert.Single(history, file => file.GetProperty("isEnabled").GetBoolean()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/materials/{fixture.Material.Id}/attachments/{firstId}/file")).StatusCode);
        }
        finally
        {
            await repository.UpdateSystemSettingsAsync(settings, default);
            if (Directory.Exists(root)) { foreach (var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(root,true); }
        }
    }
}
