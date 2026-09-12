using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectFileStorageTests
{
    [Fact]
    public async Task ChunkUpload_PublishesImmutableVersionAndVerifiesHash()
    {
        var root = NewRoot();
        var payload = "project-file-content"u8.ToArray();
        var storage = CreateStorage(Path.Combine(root, "uploads"), 5);
        try
        {
            var projectId = Guid.NewGuid();
            var session = await storage.StartAsync(projectId, Guid.NewGuid(), "会议纪要.pdf", payload.Length, Convert.ToHexString(SHA256.HashData(payload)), Path.Combine(root, "vault"), "engineer", default);
            for (var index = 0; index * session.ChunkSize < payload.Length; index++)
            {
                var offset = index * session.ChunkSize;
                await using var chunk = new MemoryStream(payload, offset, Math.Min(session.ChunkSize, payload.Length - offset), writable: false);
                await storage.WriteChunkAsync(session.Id, index, chunk, "engineer", default);
            }
            var stored = await storage.CompleteAsync(session.Id, "engineer", default);
            Assert.Contains(Path.Combine(".project-files", projectId.ToString("N"), "versions"), stored.RelativePath);
            var version = new ProjectFileVersion(stored.VersionId, Guid.NewGuid(), 1, stored.FileName, stored.StorageRoot, stored.RelativePath, stored.Length, stored.Sha256, "engineer", stored.StoredAt, null);
            await storage.VerifyAsync(version, default);
            Assert.True(File.GetAttributes(Path.Combine(stored.StorageRoot, stored.RelativePath)).HasFlag(FileAttributes.ReadOnly));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Upload_RejectsProgramsAndProtectsSessionOwner()
    {
        var root = NewRoot();
        var storage = CreateStorage(Path.Combine(root, "uploads"), 8);
        try
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "run.exe", 1, new string('A', 64), Path.Combine(root, "vault"), "engineer", default));
            var session = await storage.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "manual.pdf", 1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })), Path.Combine(root, "vault"), "engineer", default);
            await using var chunk = new MemoryStream(new byte[] { 1 }, writable: false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.WriteChunkAsync(session.Id, 0, chunk, "other", default));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Cancel_RemovesOnlyOwnersTemporarySession()
    {
        var root = NewRoot();
        var storage = CreateStorage(Path.Combine(root, "uploads"), 8);
        try
        {
            var session = await storage.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "manual.pdf", 1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })), Path.Combine(root, "vault"), "engineer", default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.CancelAsync(session.Id, "other", default));
            await storage.CancelAsync(session.Id, "engineer", default);
            await Assert.ThrowsAsync<PdmNotFoundException>(() => storage.CancelAsync(session.Id, "engineer", default));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RetentionCleanup_RemovesPublishedVersionContent()
    {
        var root = NewRoot();
        var payload = new byte[] { 1, 2, 3 };
        var storage = CreateStorage(Path.Combine(root, "uploads"), payload.Length);
        try
        {
            var session = await storage.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "archive.pdf", payload.Length, Convert.ToHexString(SHA256.HashData(payload)), Path.Combine(root, "vault"), "engineer", default);
            await using var chunk = new MemoryStream(payload, writable: false);
            await storage.WriteChunkAsync(session.Id, 0, chunk, "engineer", default);
            var stored = await storage.CompleteAsync(session.Id, "engineer", default);
            var version = new ProjectFileVersion(stored.VersionId, Guid.NewGuid(), 1, stored.FileName, stored.StorageRoot, stored.RelativePath, stored.Length, stored.Sha256, "engineer", stored.StoredAt, null);
            await storage.DeleteVersionsAsync([version], default);
            Assert.False(File.Exists(Path.Combine(stored.StorageRoot, stored.RelativePath)));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CopyVersionAsync_CopiesLatestFileIntoDifferentProjectVault()
    {
        var root = NewRoot();
        var payload = "air-sequence"u8.ToArray();
        var storage = CreateStorage(Path.Combine(root, "uploads"), payload.Length);
        try
        {
            var sourceProjectId = Guid.NewGuid();
            var session = await storage.StartAsync(sourceProjectId, Guid.NewGuid(), "气路时序图.pdf", payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)), Path.Combine(root, "source-vault"), "engineer", default);
            await using var chunk = new MemoryStream(payload, writable: false);
            await storage.WriteChunkAsync(session.Id, 0, chunk, "engineer", default);
            var source = await storage.CompleteAsync(session.Id, "engineer", default);
            var sourceVersion = new ProjectFileVersion(source.VersionId, Guid.NewGuid(), 1, source.FileName, source.StorageRoot,
                source.RelativePath, source.Length, source.Sha256, "engineer", source.StoredAt, null);

            var targetProjectId = Guid.NewGuid();
            var copied = await storage.CopyVersionAsync(sourceVersion, targetProjectId, Guid.NewGuid(),
                Path.Combine(root, "target-vault"), "engineer", default);

            Assert.Equal(targetProjectId, copied.ProjectId);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(copied.StorageRoot, copied.RelativePath)));
            Assert.True(File.GetAttributes(Path.Combine(copied.StorageRoot, copied.RelativePath)).HasFlag(FileAttributes.ReadOnly));
        }
        finally { DeleteRoot(root); }
    }

    private static LocalProjectFileStorage CreateStorage(string uploadRoot, int chunkSize) => new(Options.Create(new PdmStorageOptions { UploadTempRoot = uploadRoot, ChunkSizeBytes = chunkSize }), TimeProvider.System);
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "pdm-project-file-test", Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string root) { if (!Directory.Exists(root)) return; foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
}
