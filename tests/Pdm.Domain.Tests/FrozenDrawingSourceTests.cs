using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class FrozenDrawingSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-frozen-drawing", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApprovedDrawingIsReusedUnchangedWithoutStampingAndSupportsRepeatedValidation()
    {
        var (converter, source, project, package, _) = await PrepareAsync();
        var original = await File.ReadAllBytesAsync(Path.Combine(root, source.StorageRelativePath));

        var first = (await converter.FinalizeDrawingsAsync(package, project, [source], root, default))[source.DocumentId];
        var second = (await converter.FinalizeDrawingsAsync(package, project, [source], root, default))[source.DocumentId];

        Assert.Equal(first, second);
        Assert.Equal(source.SourceVersionId, first.SourceVersionId);
        Assert.Equal(source.StorageRelativePath, first.StorageRelativePath);
        Assert.Equal(source.Sha256, first.Sha256);
        Assert.Equal(source.FileLength, first.FileLength);
        Assert.Equal("B", first.Revision);
        Assert.Empty(first.QrContent);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, source.StorageRelativePath)));
        Assert.False(Directory.Exists(Path.Combine(root, ".release-formal")));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("length")]
    public async Task FrozenSourceChecksStillRejectChanges(string change)
    {
        var (converter, source, project, package, repository) = await PrepareAsync();
        switch (change)
        {
            case "version": source = source with { SourceVersionId = Guid.NewGuid() }; break;
            case "hash": source = source with { Sha256 = new string('F', 64) }; break;
            case "revision": source = source with { ExpectedFormalRevision = "C" }; break;
            case "content":
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, source.StorageRelativePath));
                bytes[0] ^= 1;
                await File.WriteAllBytesAsync(Path.Combine(root, source.StorageRelativePath), bytes);
                break;
            case "length": await File.AppendAllTextAsync(Path.Combine(root, source.StorageRelativePath), "changed"); break;
        }

        await Assert.ThrowsAsync<PdmConflictException>(() => converter.FinalizeDrawingsAsync(package, project, [source], root, default));
        Assert.Equal("A-W1", repository.Version!.Revision.Display);
    }

    private async Task<(SolidWorksServerPreviewConverter Converter, ReleasePreviewSource Source, Project Project,
        ReleasePackage Package, FrozenVersionRepository Repository)> PrepareAsync()
    {
        Directory.CreateDirectory(root);
        var relative = "frozen.SLDDRW";
        // 已有的A版文字不属于转图输入校验条件，不得为生成B版PDF改写源文件。
        await File.WriteAllTextAsync(Path.Combine(root, relative), "approved drawing with UPLM_RELEASE_REV_1: A");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, relative));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var id = Guid.NewGuid();
        var version = new DocumentVersion(Guid.NewGuid(), id, new RevisionLabel('A', 1, false), DocumentVersionStatus.Work,
            relative, bytes.Length, hash, "admin", DateTimeOffset.UtcNow, "", new Dictionary<string, string?>(), null!, [], [], null, null, null, null);
        var source = new ReleasePreviewSource(id, version.Id, "P7000XX.30.01-09", relative, DocumentKind.Drawing,
            relative, bytes.Length, hash, hash, "B");
        var repository = DispatchProxy.Create<IPdmRepository, FrozenVersionRepository>();
        var proxy = (FrozenVersionRepository)(object)repository;
        proxy.Version = version;
        var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository);
        var project = new Project(Guid.NewGuid(), "P-TEST", "测试", "admin", root, root, true);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-FROZEN", ReleasePackageState.Publishing,
            null, "", "", [], DateTimeOffset.UtcNow, null, null);
        return (converter, source, project, package, proxy);
    }

    public class FrozenVersionRepository : DispatchProxy
    {
        public DocumentVersion? Version;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IPdmRepository.FindDocumentVersionAsync) => Task.FromResult(
                (Guid)args![0]! == Version!.DocumentId && (Guid)args[1]! == Version.Id ? Version : null),
            _ => throw new InvalidOperationException("冻结来源校验不应启动转图或读取转图服务器配置：" + method.Name)
        };
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
