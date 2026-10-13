using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class FormalDrawingReadOnlyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegacyFormalReadRepairsOnlyMatchingContent(bool verifyRead, bool corrupted)
    {
        await WithFile(async (storage, project, file, path) =>
        {
            if (corrupted) await File.WriteAllBytesAsync(path, "wrong"u8.ToArray());
            Task Read() => verifyRead ? storage.VerifyVersionReadAsync(project, file, default)
                : storage.ValidateStoredFileMetadataAsync(project, file, default);
            if (corrupted)
            {
                await Assert.ThrowsAsync<PdmConflictException>(Read);
                Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
                Assert.Equal("wrong"u8.ToArray(), await File.ReadAllBytesAsync(path));
            }
            else
            {
                await Read();
                await Read();
                Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
                Assert.Equal("valid"u8.ToArray(), await File.ReadAllBytesAsync(path));
            }
        });
    }

    [Theory]
    [InlineData(".versions/part/file.sldprt")]
    [InlineData(".release-formal/not-a-package/file.slddrw")]
    public async Task OtherWritableFilesAreNotRepaired(string relative)
    {
        await WithFile(async (storage, project, file, path) =>
        {
            if (relative.StartsWith(".versions/", StringComparison.Ordinal))
                await Assert.ThrowsAsync<PdmConflictException>(() => storage.ValidateStoredFileMetadataAsync(project, file, default));
            else
                await Assert.ThrowsAsync<PdmRuleException>(() => storage.ValidateStoredFileMetadataAsync(project, file, default));
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }, relative);
    }

    [Fact]
    public async Task FormalFileStillCannotBeUsedAsNewCheckIn()
    {
        await WithFile(async (storage, project, file, path) =>
        {
            await Assert.ThrowsAnyAsync<PdmRuleException>(() => storage.VerifyStoredFileAsync(project, file, default));
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        });
    }

    private static async Task WithFile(Func<LocalFileStorage, Upton.Pdm.Domain.Project, StoredFile, string, Task> test, string? relative = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-formal-readonly", Guid.NewGuid().ToString("N"));
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new CreateProjectCommand("QA-FORMAL", "只读修复测试", "admin", root, Path.Combine(root, "release")), "admin", default);
        var storage = new LocalFileStorage(Options.Create(new PdmStorageOptions()), repository, TimeProvider.System);
        relative ??= $".release-formal/{Guid.NewGuid():N}/{Guid.NewGuid():N}.slddrw";
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = "valid"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            await test(storage, project, new StoredFile(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow), path);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
