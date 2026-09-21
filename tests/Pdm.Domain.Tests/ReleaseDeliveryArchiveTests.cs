using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

/// <summary>发布成品归档：机械发布汇总到"机械发布"，电气发布汇总到"电气发布"，重复执行不产生重复版本。</summary>
public sealed class ReleaseDeliveryArchiveTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task PublishedDeliverablesAreArchivedIntoReleaseFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-release-archive-test", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = TimeProvider.System;
            var repository = new InMemoryPdmRepository(clock);
            var files = new InMemoryProjectFileRepository(clock);
            var service = new ReleaseDeliveryArchiveService(repository, files, clock);
            var project = await repository.FindProjectAsync(ProjectId, default) ?? throw new InvalidOperationException("测试项目不存在。");
            var publishedPath = Path.Combine(root, "RP-ARCHIVE-1");
            Directory.CreateDirectory(publishedPath);
            await File.WriteAllTextAsync(Path.Combine(publishedPath, "nonstandard-parts-bom.xlsx"), "bom");
            await File.WriteAllTextAsync(Path.Combine(publishedPath, "7080113.00-01.step"), "step");
            await File.WriteAllTextAsync(Path.Combine(publishedPath, "manifest.json"), "{}");
            var package = await CreatePublishedPackageAsync(repository, project.Id, publishedPath, "RP-ARCHIVE-1", ReleaseScope.NonStandardWithDrawing);

            Assert.Equal(2, await service.ArchiveAsync(package.Id, "admin", default));

            var mechanical = await repository.FindProjectFolderByKeyAsync(project.Id, "mechanical.release", default);
            Assert.NotNull(mechanical);
            var archived = await files.ListAsync(mechanical!.RootProjectId, mechanical.Id, false, default);
            Assert.Equal(["7080113.00-01.step", "nonstandard-parts-bom.xlsx"], archived.Select(item => item.FileName).OrderBy(name => name).ToArray());
            // 登记的是发布目录里已有的文件，不复制内容；相对路径带发布包号，避免后续发布重名撞唯一索引。
            Assert.All(archived, item => Assert.Equal(Path.GetDirectoryName(publishedPath), item.CurrentVersion!.StorageRoot));
            Assert.All(archived, item => Assert.Equal(Path.Combine(Path.GetFileName(publishedPath), item.FileName), item.CurrentVersion!.StorageRelativePath));
            Assert.All(archived, item => Assert.Equal("发布成品 · RP-ARCHIVE-1", item.CurrentVersion!.Comment));

            // 重复执行（例如重启补登记、转图补齐后再次归档）不生成重复版本。
            Assert.Equal(0, await service.ArchiveAsync(package.Id, "admin", default));
            foreach (var item in archived) Assert.Single(await files.ListVersionsAsync(item.Id, default));

            // 电气发布汇总到"电气发布"。
            var electricalPath = Path.Combine(root, "RP-ARCHIVE-2");
            Directory.CreateDirectory(electricalPath);
            await File.WriteAllTextAsync(Path.Combine(electricalPath, "electrical-bom.xlsx"), "bom");
            var electricalPackage = await CreatePublishedPackageAsync(repository, project.Id, electricalPath, "RP-ARCHIVE-2", ReleaseScope.ElectricalFormal);

            Assert.Equal(1, await service.ArchiveAsync(electricalPackage.Id, "admin", default));

            var electrical = await repository.FindProjectFolderByKeyAsync(project.Id, "electrical.release", default);
            Assert.NotNull(electrical);
            var electricalFiles = await files.ListAsync(electrical!.RootProjectId, electrical.Id, false, default);
            Assert.Equal("electrical-bom.xlsx", Assert.Single(electricalFiles).FileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnpublishedOrMissingDirectoryArchivesNothing()
    {
        var clock = TimeProvider.System;
        var repository = new InMemoryPdmRepository(clock);
        var files = new InMemoryProjectFileRepository(clock);
        var service = new ReleaseDeliveryArchiveService(repository, files, clock);
        var project = await repository.FindProjectAsync(ProjectId, default) ?? throw new InvalidOperationException("测试项目不存在。");
        var draft = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-ARCHIVE-DRAFT", ReleasePackageState.Draft,
            null, string.Empty, string.Empty, [], clock.GetUtcNow(), null, null) { Scope = ReleaseScope.NonStandardWithDrawing };
        await repository.CreateReleasePackageAsync(draft, default);

        Assert.Equal(0, await service.ArchiveAsync(draft.Id, "admin", default));
        Assert.Equal(0, await service.ArchiveAsync(Guid.NewGuid(), "admin", default));
    }

    private static async Task<ReleasePackage> CreatePublishedPackageAsync(
        InMemoryPdmRepository repository,
        Guid projectId,
        string publishedPath,
        string number,
        ReleaseScope scope)
    {
        var now = TimeProvider.System.GetUtcNow();
        var package = new ReleasePackage(Guid.NewGuid(), projectId, number, ReleasePackageState.Published,
            null, string.Empty, string.Empty, [], now, now, publishedPath) { Scope = scope };
        return await repository.CreateReleasePackageAsync(package, default);
    }
}
