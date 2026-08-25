using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProgramTemplateStorageTests
{
    [Fact]
    public async Task ZipUpload_IsHashedValidatedAndStoredReadOnly()
    {
        var testRoot = NewTestRoot();
        var storageRoot = Path.Combine(testRoot, "program-templates");
        var payload = CreateZipPayload();
        var storage = CreateStorage(Path.Combine(testRoot, "uploads"), storageRoot, 7);

        try
        {
            var session = await storage.StartUploadAsync(
                Guid.NewGuid(), "PT-FB-0001", ProgramTemplateAttachmentKind.Package, "motor.zip",
                payload.Length, Convert.ToHexString(SHA256.HashData(payload)), "engineer", default);
            for (var index = 0; index * session.ChunkSize < payload.Length; index++)
            {
                var offset = index * session.ChunkSize;
                var length = Math.Min(session.ChunkSize, payload.Length - offset);
                await using var chunk = new MemoryStream(payload, offset, length, writable: false);
                await storage.WriteChunkAsync(session.Id, index, chunk, "engineer", default);
            }

            var stored = await storage.CompleteUploadAsync(session.Id, "engineer", default);
            var path = Path.Combine(storageRoot, stored.RelativePath);
            Assert.True(File.Exists(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            await storage.VerifyAsync(stored.RelativePath, stored.Length, stored.Sha256, default);
            await using var opened = await storage.OpenReadAsync(stored.RelativePath, default);
            using var copy = new MemoryStream();
            await opened.CopyToAsync(copy);
            Assert.Equal(payload, copy.ToArray());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Upload_RejectsWrongExtensionAndInvalidZip()
    {
        var testRoot = NewTestRoot();
        var storageRoot = Path.Combine(testRoot, "program-templates");
        var storage = CreateStorage(Path.Combine(testRoot, "uploads"), storageRoot, 32);
        var invalidZip = "not-a-zip"u8.ToArray();

        try
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.StartUploadAsync(
                Guid.NewGuid(), "PT-FB-0001", ProgramTemplateAttachmentKind.Package, "motor.pdf",
                1, new string('A', 64), "engineer", default));
            var session = await storage.StartUploadAsync(
                Guid.NewGuid(), "PT-FB-0001", ProgramTemplateAttachmentKind.Package, "motor.zip",
                invalidZip.Length, Convert.ToHexString(SHA256.HashData(invalidZip)), "engineer", default);
            await using var chunk = new MemoryStream(invalidZip, writable: false);
            await storage.WriteChunkAsync(session.Id, 0, chunk, "engineer", default);

            await Assert.ThrowsAsync<PdmRuleException>(() => storage.CompleteUploadAsync(session.Id, "engineer", default));
            Assert.False(Directory.Exists(storageRoot) && Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static LocalProgramTemplateStorage CreateStorage(string uploadRoot, string storageRoot, int chunkSize) =>
        new(Options.Create(new PdmStorageOptions
        {
            UploadTempRoot = uploadRoot,
            ProgramTemplateRoot = storageRoot,
            ChunkSizeBytes = chunkSize
        }), TimeProvider.System);

    private static byte[] CreateZipPayload()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("motor.scl").Open())
        {
            entry.Write("FUNCTION_BLOCK Motor"u8);
        }
        return output.ToArray();
    }

    private static string NewTestRoot() => Path.Combine(Path.GetTempPath(), "pdm-program-template-test", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
