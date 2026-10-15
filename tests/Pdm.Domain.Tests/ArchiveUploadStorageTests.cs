using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Domain;

namespace Pdm.Domain.Tests;

public sealed class ArchiveUploadStorageTests
{
    [Fact]
    public async Task ExpiredOwnedStagingIsCollectedButAdoptedAndLegacyFilesRemain()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-owned-upload", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new UploadClock();
            var repository = new InMemoryPdmRepository(clock);
            var project = await repository.CreateProjectAsync(new("QA-OWNED", "归属回收验收", "admin", Path.Combine(root,"vault"), Path.Combine(root,"release")), "admin", default);
            var storage = new LocalFileStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root,"uploads"), ChunkSizeBytes = 4 }), repository, clock);
            var bytes = "123456"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var editSession = Guid.NewGuid();
            async Task<(PdmDocument Document, UploadSession Upload, StoredFile File)> Upload(string name)
            {
                var document = await Upton.Pdm.Domain.Tests.ArchiveBatchTests.Register(repository, project, name, DocumentKind.Part, editSession);
                var upload = await storage.StartArchiveUploadAsync(project.Id, document.FileName, bytes.Length, hash, "admin", new(Guid.NewGuid(),document.Id,editSession), default);
                await storage.WriteChunkAsync(upload.Id,0,new MemoryStream(bytes[..4]),default);
                await storage.WriteChunkAsync(upload.Id,1,new MemoryStream(bytes[4..]),default);
                await Assert.ThrowsAsync<PdmConflictException>(() => storage.CompleteUploadAsync(upload.Id,".versions/wrong",default));
                return (document,upload,await storage.CompleteUploadAsync(upload.Id,upload.TargetRelativePath!,default));
            }
            var adopted = await Upload("ADOPTED");
            var unused = await Upload("UNUSED");
            var adoptedCommit = Upton.Pdm.Domain.Tests.ArchiveBatchTests.Commit(project,adopted.Document,Upton.Pdm.Domain.Tests.ArchiveBatchTests.Node(adopted.Document),'A') with { File=adopted.File };
            await repository.CheckInBatchVersionsAsync(adopted.Upload.ArchiveContext!.BatchId,"accepted","admin",editSession,[new(adopted.Document.Id,adoptedCommit)],default);
            var legacy = await storage.StartUploadAsync(project.Id,"legacy.SLDPRT",bytes.Length,hash,default,"admin");
            await storage.WriteChunkAsync(legacy.Id,0,new MemoryStream(bytes[..4]),default);
            await storage.WriteChunkAsync(legacy.Id,1,new MemoryStream(bytes[4..]),default);
            var legacyFile = await storage.CompleteUploadAsync(legacy.Id,".versions/legacy/file.SLDPRT",default);
            clock.Now = clock.Now.AddDays(2);
            await storage.CleanupExpiredArchiveUploadsAsync(default);
            Assert.False(File.Exists(Path.Combine(project.VaultLocation,unused.File.RelativePath)));
            Assert.True(File.Exists(Path.Combine(project.VaultLocation,adopted.File.RelativePath)));
            Assert.True(File.Exists(Path.Combine(project.VaultLocation,legacyFile.RelativePath)));
            var staleCommit = Upton.Pdm.Domain.Tests.ArchiveBatchTests.Commit(project,unused.Document,Upton.Pdm.Domain.Tests.ArchiveBatchTests.Node(unused.Document),'A') with { File=unused.File };
            await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(unused.Upload.ArchiveContext!.BatchId,"stale","admin",editSession,[new(unused.Document.Id,staleCommit)],default));
            await storage.CleanupExpiredArchiveUploadsAsync(default);
            Assert.Empty(await repository.ClaimExpiredArchiveUploadsAsync(50,default));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
                Directory.Delete(root,true);
            }
        }
    }

    private sealed class UploadClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
