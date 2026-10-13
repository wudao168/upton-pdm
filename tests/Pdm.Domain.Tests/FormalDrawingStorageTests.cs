using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class FormalDrawingStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-formal-storage", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage storage;
    private readonly Project project;

    public FormalDrawingStorageTests()
    {
        Directory.CreateDirectory(root);
        project = new Project(Guid.NewGuid(), "TEST", "测试", "admin", root, Path.Combine(root, "release"), true);
        storage = new LocalFileStorage(Options.Create(new PdmStorageOptions()), new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System);
    }

    private StoredFile CreateFile(bool readOnly = true, string extension = ".slddrw")
    {
        var relative = Path.Combine(".release-formal", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") + extension);
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = "formal drawing"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return new StoredFile(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ControlledOpenAcceptsProtectedFormalDrawing()
    {
        await storage.ValidateStoredFileMetadataAsync(project, CreateFile(), default);
    }

    [Fact]
    public async Task VersionDownloadAcceptsProtectedFormalDrawing()
    {
        await storage.VerifyVersionReadAsync(project, CreateFile(), default);
    }

    [Fact]
    public async Task VersionDownloadRejectsTamperedContent()
    {
        var file = CreateFile();
        await Assert.ThrowsAsync<PdmConflictException>(() => storage.VerifyVersionReadAsync(project,
            file with { Sha256 = new string('0', 64) }, default));
    }

    [Fact]
    public async Task VersionDownloadRepairsMatchingLegacyFormalDrawing()
    {
        var file = CreateFile(false);
        await storage.VerifyVersionReadAsync(project, file, default);
        Assert.True(File.GetAttributes(Path.Combine(root, file.RelativePath)).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task CheckInStillRejectsFormalDirectory()
    {
        await Assert.ThrowsAsync<PdmRuleException>(() => storage.VerifyStoredFileAsync(project, CreateFile(), default));
    }

    [Fact]
    public async Task ControlledOpenRepairsMatchingLegacyFormalDrawing()
    {
        var file = CreateFile(false);
        await storage.ValidateStoredFileMetadataAsync(project, file, default);
        Assert.True(File.GetAttributes(Path.Combine(root, file.RelativePath)).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task ControlledOpenRejectsIncorrectLength()
    {
        var file = CreateFile();
        await Assert.ThrowsAsync<PdmConflictException>(() => storage.ValidateStoredFileMetadataAsync(project, file with { Length = file.Length + 1 }, default));
    }

    [Theory]
    [InlineData(".sldasm")]
    [InlineData(".pdf")]
    public async Task ControlledOpenRejectsOtherFormatsInFormalDirectory(string extension)
    {
        await Assert.ThrowsAsync<PdmRuleException>(() => storage.ValidateStoredFileMetadataAsync(project, CreateFile(extension: extension), default));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
