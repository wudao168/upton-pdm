using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class StorageHistoryTests
{
    [Fact]
    public async Task OplAttachments_KeepOriginalLocationAfterChangingGlobalAndAttachmentRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-opl-history-" + Guid.NewGuid().ToString("N"));
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var issues = new InMemoryProjectOplRepository();
        var storage = new LocalProjectOplStorage();
        var project = (await projects.ListProjectsAsync(default))[0];
        var paths = new PdmStorageOptions { OplAttachmentRoot = Path.Combine(root, "old"), LegacyOplAttachmentRoot = Path.Combine(root, "old") };
        var service = new ProjectOplService(projects, issues, storage, TimeProvider.System, paths);
        try
        {
            var input = new OplInput("历史附件", "普通", "待处理", "", null, "", 0, []);
            var issue = await service.SaveAsync(project.Id, null, input, [new("proof.pdf", [1, 2, 3])], "admin", UserRole.Administrator, default);
            var file = Assert.Single(issue.Attachments);
            Assert.Equal(paths.OplAttachmentRoot, file.StorageRoot);
            // Simulate an attachment written by the previous version without a captured root.
            await issues.SaveAsync(issue with { Attachments = [file with { StorageRoot = null }] }, issue.RowVersion, default);
            paths.OplAttachmentRoot = Path.Combine(root, "new");
            var system = await projects.GetSystemSettingsAsync(default);
            await projects.UpdateSystemSettingsAsync(system with { VaultRoot = Path.Combine(root, "new-global") }, default);
            var opened = await service.OpenAsync(project.Id, issue.Id, file.Id, "admin", UserRole.Administrator, default);
            await using (opened.Content) { Assert.Equal(3, opened.Content.Length); }
            var newer = await service.SaveAsync(project.Id, null, input, [new("new.pdf", [4])], "admin", UserRole.Administrator, default);
            Assert.Equal(paths.OplAttachmentRoot, Assert.Single(newer.Attachments).StorageRoot);
            paths.OplAttachmentRoot = Path.Combine(root, "newest");
            var reopened = await service.OpenAsync(project.Id, newer.Id, newer.Attachments[0].Id, "admin", UserRole.Administrator, default);
            await using (reopened.Content) { Assert.Equal(1, reopened.Content.Length); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ChangingRoots_KeepsExistingUploadSessionAndProgramFileReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-storage-history-" + Guid.NewGuid().ToString("N"));
        var settings = new PdmStorageOptions { UploadTempRoot = Path.Combine(root, "old-uploads"), ProgramTemplateRoot = Path.Combine(root, "old-templates") };
        var bytes = "history-file"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var fileStorage = new LocalProjectFileStorage(Options.Create(settings), TimeProvider.System);
        try
        {
            var session = await fileStorage.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "proof.pdf", bytes.Length, hash, Path.Combine(root, "vault"), "admin", default);
            var relative = Path.Combine("PT-001", "r1", "proof.pdf");
            var oldFile = Path.Combine(settings.ProgramTemplateRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            await File.WriteAllBytesAsync(oldFile, bytes);
            settings.HistoricalRoots = new Dictionary<string, string[]> { ["uploads"] = [settings.UploadTempRoot], ["programTemplates"] = [settings.ProgramTemplateRoot] };
            settings.UploadTempRoot = Path.Combine(root, "new-uploads");
            settings.ProgramTemplateRoot = Path.Combine(root, "new-templates");
            await fileStorage.WriteChunkAsync(session.Id, 0, new MemoryStream(bytes), "admin", default);
            var stored = await fileStorage.CompleteAsync(session.Id, "admin", default);
            Assert.True(File.Exists(Path.Combine(stored.StorageRoot, stored.RelativePath)));
            var templates = new LocalProgramTemplateStorage(Options.Create(settings), TimeProvider.System);
            await templates.VerifyAsync(relative, bytes.Length, hash, default);
            await using var opened = await templates.OpenReadAsync(relative, default);
            using var output = new MemoryStream();
            await opened.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
            Assert.True(File.Exists(oldFile));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("D:\\PDM\\Vault\\backup")]
    [InlineData("D:\\PDM")]
    [InlineData("D:\\PDM\\Vault")]
    public void BackupDestination_MustNotOverlapSource(string destination) => Assert.Throws<PdmRuleException>(() =>
        StorageBackupPlanner.ValidateDestination(destination, [new(@"D:\PDM\Vault", ["图档"], true, true)]));
}
