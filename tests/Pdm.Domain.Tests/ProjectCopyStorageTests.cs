using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProjectCopyStorageTests
{
    [Fact]
    public async Task CopyVersionAsync_CopiesControlledDocumentBetweenProjectVaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-project-copy-storage-test", Guid.NewGuid().ToString("N"));
        var sourceVault = Path.Combine(root, "source");
        var targetVault = Path.Combine(root, "target");
        Directory.CreateDirectory(sourceVault);
        Directory.CreateDirectory(targetVault);
        try
        {
            var payload = "controlled-model"u8.ToArray();
            var relative = Path.Combine(".versions", Guid.NewGuid().ToString("N"), "W3", "模型.SLDPRT");
            var sourcePath = Path.Combine(sourceVault, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, payload);
            File.SetAttributes(sourcePath, FileAttributes.ReadOnly);
            var source = new Project(Guid.NewGuid(), "SOURCE", "源项目", "admin", sourceVault, Path.Combine(root, "release-source"), true);
            var target = new Project(Guid.NewGuid(), "TARGET", "目标项目", "admin", targetVault, Path.Combine(root, "release-target"), true);
            var stored = new StoredFile(relative, payload.Length, Convert.ToHexString(SHA256.HashData(payload)), DateTimeOffset.UtcNow);
            var repository = new InMemoryPdmRepository(TimeProvider.System);
            var storage = new LocalFileStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root, "uploads") }), repository, TimeProvider.System);

            var copied = await storage.CopyVersionAsync(source, target, stored,
                Path.Combine(".versions", Guid.NewGuid().ToString("N"), "W1", "模型.SLDPRT"), default);

            var copiedPath = Path.Combine(targetVault, copied.RelativePath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(copiedPath));
            Assert.Equal(stored.Sha256, copied.Sha256);
            Assert.True(File.GetAttributes(copiedPath).HasFlag(FileAttributes.ReadOnly));
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
