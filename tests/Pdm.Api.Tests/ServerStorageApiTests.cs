using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ServerStorageApiTests
{
    [Fact]
    public async Task ChangingPhysicalUploadRoot_PreservesProjectFolderTreeAndFileOwnership()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/folders");
        var folder = before.EnumerateArray().First(f => f.GetProperty("purpose").ValueKind == JsonValueKind.Number
            ? f.GetProperty("purpose").GetInt32() == (int)ProjectFolderPurpose.Standard : f.GetProperty("purpose").GetString() == "Standard");
        var folderId = folder.GetProperty("id").GetGuid();
        var settings = (await client.GetFromJsonAsync<View>("/api/system-settings/storage"))!.Settings;
        var target = Path.Combine(Path.GetTempPath(), "pdm-project-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            (await client.PutAsJsonAsync("/api/system-settings/storage", settings with { ProjectFileRoot = target })).EnsureSuccessStatusCode();
            var after = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/folders");
            Assert.Equal(before.GetRawText(), after.GetRawText());
            var bytes = "project-file-root-test"u8.ToArray();
            var upload = await client.PostAsJsonAsync($"/api/projects/{projectId}/folders/{folderId}/file-uploads",
                new { fileName = "客户资料.pdf", totalLength = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            upload.EnsureSuccessStatusCode();
            var session = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await client.PutAsync($"/api/project-file-uploads/{session}/chunks/0", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
            var complete = await client.PostAsJsonAsync($"/api/project-file-uploads/{session}/complete", new { comment = "目录与物理根路径分离验证" });
            complete.EnsureSuccessStatusCode();
            var file = await complete.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(folderId, file.GetProperty("folderId").GetGuid());
            Assert.Single(Directory.GetFiles(target, "客户资料.pdf", SearchOption.AllDirectories));
            var downloaded = await client.GetByteArrayAsync($"/api/projects/{projectId}/files/{file.GetProperty("id").GetGuid()}/content");
            Assert.Equal(bytes, downloaded);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(target, true);
            }
        }
    }

    [Fact]
    public async Task Settings_PreserveHistoricalRootsAndLeaveBackupUnconfigured()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var original = (await client.GetFromJsonAsync<View>("/api/system-settings/storage"))!.Settings;
        var target = Path.Combine(Path.GetTempPath(), "pdm-storage-api-" + Guid.NewGuid().ToString("N"));
        var response = await client.PutAsJsonAsync("/api/system-settings/storage", original with
        {
            ProgramTemplateRoot = Path.Combine(target, "templates"), UploadTempRoot = Path.Combine(target, "uploads"),
            OplAttachmentRoot = Path.Combine(target, "opl"), HistoricalRoots = new Dictionary<string, string[]>(),
            DatabaseDataTarget = Path.Combine(target, "database"), SecretsTarget = Path.Combine(target, "keys")
        });
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<View>())!.Settings;
        Assert.Equal("", saved.BackupRoot);
        Assert.Equal(original.LegacyOplAttachmentRoot, saved.LegacyOplAttachmentRoot);
        Assert.Contains(original.ProgramTemplateRoot, saved.HistoricalRoots["programTemplates"]);
        Assert.Contains(original.UploadTempRoot, saved.HistoricalRoots["uploads"]);
        Assert.False(Directory.Exists(target)); // Saving is configuration, never a migration or a backup.
        var stale = await client.PutAsJsonAsync("/api/system-settings/storage", original);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var plan = await client.GetFromJsonAsync<Plan>("/api/system-settings/storage/backup-plan");
        Assert.Equal("", plan!.BackupRoot);
        Assert.Contains("待确认", plan.Message);
        Assert.Contains(plan.Sources, s => s.Path.Equals(original.ProgramTemplateRoot, StringComparison.OrdinalIgnoreCase));
        var active = factory.Services.GetRequiredService<IOptions<PdmStorageOptions>>().Value;
        Assert.Equal(saved.ProgramTemplateRoot, active.ProgramTemplateRoot);
    }

    [Fact]
    public async Task Settings_RejectBackupInsideArchiveAndForbidUnprivilegedAccess()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authorize(client, "admin", "Administrator");
        var settings = (await client.GetFromJsonAsync<View>("/api/system-settings/storage"))!.Settings;
        var response = await client.PutAsJsonAsync("/api/system-settings/storage", settings with { BackupRoot = Path.Combine(settings.ProgramTemplateRoot, "backup") });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("备份目标", await response.Content.ReadAsStringAsync());
        Authorize(client, "engineer", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/system-settings/storage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/system-settings/storage/backup-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/system-settings/storage", settings)).StatusCode);
    }

    private static void Authorize(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private sealed record View(ServerStorageSettings Settings);
    private sealed record Plan(string BackupRoot, string Message, StorageBackupSource[] Sources);
}
