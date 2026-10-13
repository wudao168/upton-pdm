using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ArchiveUploadStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeAndCompletionReplayRetainOneImmutableFile(bool interrupted)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-archive-upload", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new InMemoryPdmRepository(TimeProvider.System);
            var project = await repository.CreateProjectAsync(new CreateProjectCommand("QA-UPLOAD", "续传验收", "admin", Path.Combine(root, "vault"), Path.Combine(root, "release")), "admin", default);
            var storage = new LocalFileStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root, "uploads"), ChunkSizeBytes = 4 }), repository, TimeProvider.System);
            var bytes = "1234567890"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var session = await storage.StartUploadAsync(project.Id, "QA.SLDPRT", bytes.Length, hash, default, "admin");
            await storage.WriteChunkAsync(session.Id, 0, new MemoryStream(bytes[..4]), default);
            if (interrupted)
            {
                Assert.Equal(4, (await storage.GetUploadSessionAsync(session.Id, default)).ReceivedLength);
                await Assert.ThrowsAsync<PdmConflictException>(() => storage.CompleteUploadAsync(session.Id, ".versions/qa/file.SLDPRT", default));
                storage = new LocalFileStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root, "uploads"), ChunkSizeBytes = 4 }), repository, TimeProvider.System);
            }
            await storage.WriteChunkAsync(session.Id, 1, new MemoryStream(bytes[4..8]), default);
            await storage.WriteChunkAsync(session.Id, 2, new MemoryStream(bytes[8..]), default);
            var first = await storage.CompleteUploadAsync(session.Id, ".versions/qa/file.SLDPRT", default);
            var replay = await storage.CompleteUploadAsync(session.Id, first.RelativePath, default);
            Assert.Equal(first, replay);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(project.VaultLocation, first.RelativePath)));
            Assert.True(File.GetAttributes(Path.Combine(project.VaultLocation, first.RelativePath)).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(bytes.Length, (await storage.GetUploadSessionAsync(session.Id, default)).ReceivedLength);
            await Assert.ThrowsAsync<PdmConflictException>(() => storage.CompleteUploadAsync(session.Id, ".versions/other/file.SLDPRT", default));
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
}
