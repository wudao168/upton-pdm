using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class MaterialAttachmentStorageTests
{
    [Fact]
    public async Task ChunkUpload_PublishesReadOnlyFileUnderCapturedRootAndVerifiesHash()
    {
        var testRoot = NewTestRoot();
        var uploadRoot = Path.Combine(testRoot, "upload");
        var attachmentRoot = Path.Combine(testRoot, "material-attachments");
        var payload = "controlled-step-data"u8.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var storage = CreateStorage(uploadRoot, 5);

        try
        {
            var session = await storage.StartUploadAsync(
                Guid.NewGuid(), "01010000001", MaterialAttachmentKind.Model3D, "sensor.step",
                payload.Length, sha256, attachmentRoot, "engineer", default);

            for (var index = 0; index * session.ChunkSize < payload.Length; index++)
            {
                var offset = index * session.ChunkSize;
                var length = Math.Min(session.ChunkSize, payload.Length - offset);
                await using var chunk = new MemoryStream(payload, offset, length, writable: false);
                await storage.WriteChunkAsync(session.Id, index, chunk, "engineer", default);
            }

            var stored = await storage.CompleteUploadAsync(session.Id, "engineer", default);
            var publishedPath = Path.Combine(stored.StorageRoot, stored.RelativePath);
            Assert.Equal(Path.GetFullPath(attachmentRoot).TrimEnd(Path.DirectorySeparatorChar), stored.StorageRoot);
            Assert.True(File.Exists(publishedPath));
            Assert.True(File.GetAttributes(publishedPath).HasFlag(FileAttributes.ReadOnly));

            var attachment = new MaterialAttachment(
                Guid.NewGuid(), stored.MaterialId, stored.Kind, stored.OriginalFileName, stored.StorageRoot,
                stored.RelativePath, stored.Length, stored.Sha256, "engineer", stored.StoredAt);
            await storage.VerifyAsync(attachment, default);
            await using var downloaded = await storage.OpenReadAsync(attachment, default);
            using var copy = new MemoryStream();
            await downloaded.CopyToAsync(copy);
            Assert.Equal(payload, copy.ToArray());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Upload_RejectsWrongKindAndPathFileNamesBeforeCreatingSession()
    {
        var testRoot = NewTestRoot();
        var storage = CreateStorage(Path.Combine(testRoot, "upload"), 8);

        try
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.StartUploadAsync(
                Guid.NewGuid(), "01010000001", MaterialAttachmentKind.Model3D, "manual.pdf",
                1, new string('A', 64), Path.Combine(testRoot, "files"), "engineer", default));
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.StartUploadAsync(
                Guid.NewGuid(), "01010000001", MaterialAttachmentKind.Document, $"folder{Path.DirectorySeparatorChar}manual.pdf",
                1, new string('A', 64), Path.Combine(testRoot, "files"), "engineer", default));
            Assert.False(Directory.Exists(Path.Combine(testRoot, "upload")));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task CompleteUpload_WithWrongHashDoesNotPublishAttachment()
    {
        var testRoot = NewTestRoot();
        var attachmentRoot = Path.Combine(testRoot, "material-attachments");
        var payload = "tampered"u8.ToArray();
        var storage = CreateStorage(Path.Combine(testRoot, "upload"), payload.Length);

        try
        {
            var session = await storage.StartUploadAsync(
                Guid.NewGuid(), "01010000001", MaterialAttachmentKind.Document, "manual.pdf",
                payload.Length, new string('A', 64), attachmentRoot, "engineer", default);
            await using var chunk = new MemoryStream(payload, writable: false);
            await storage.WriteChunkAsync(session.Id, 0, chunk, "engineer", default);

            await Assert.ThrowsAsync<PdmConflictException>(() => storage.CompleteUploadAsync(session.Id, "engineer", default));
            Assert.False(Directory.Exists(attachmentRoot) && Directory.EnumerateFiles(attachmentRoot, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static LocalMaterialAttachmentStorage CreateStorage(string uploadRoot, int chunkSize) =>
        new(Options.Create(new PdmStorageOptions { UploadTempRoot = uploadRoot, ChunkSizeBytes = chunkSize }), TimeProvider.System);

    private static string NewTestRoot() => Path.Combine(Path.GetTempPath(), "pdm-material-attachment-test", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
