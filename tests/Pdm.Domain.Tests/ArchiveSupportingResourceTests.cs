using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveSupportingResourceTests
{
    [Theory]
    [InlineData("H620_外观.STEP")]
    [InlineData("pplastic2.jpg")]
    [InlineData("纹理.PNG")]
    [InlineData("贴图.jpeg")]
    [InlineData("凹凸.bmp")]
    [InlineData("外观.tif")]
    [InlineData("图像.tiff")]
    [InlineData("参数.txt")]
    [InlineData("设计表.xls")]
    [InlineData("设计表.xlsx")]
    [InlineData("设计表.xlsm")]
    [InlineData("来源.stp")]
    [InlineData("来源.iges")]
    [InlineData("来源.igs")]
    [InlineData("来源.x_t")]
    [InlineData("来源.x_b")]
    [InlineData("来源.sat")]
    [InlineData("来源.stl")]
    [InlineData("图纸格式.slddrt")]
    public async Task AuxiliaryResourceReservesAndCommitsWithItsParent(string fileName)
    {
        var (repository, workflow, project) = await Fixture();
        var session = Guid.NewGuid(); var batch = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch, session, "QA");
        var root = await workflow.RegisterDocumentAsync(new(project.Id, "ROOT", "主图档", "ROOT.SLDASM", DocumentKind.Assembly,
            SourceSha256: new string('A', 64)), "admin", UserRole.Administrator, default, context);
        var resource = await workflow.RegisterDocumentAsync(new(project.Id, Path.GetFileNameWithoutExtension(fileName), "随附资源", fileName,
            DocumentKind.Other, SourceSha256: new string('B', 64)), "admin", UserRole.Administrator, default, context);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
        Assert.Empty(await repository.ListDocumentVersionsAsync(resource.Id, default));
        var source = ArchiveBatchTests.Node(resource) with { Role = ReferenceRole.ExternalFileDependency };
        var parent = ArchiveBatchTests.Node(root) with { Children = [source], IncludesDesignDependencies = true };
        var main = ArchiveBatchTests.Commit(project, root, parent, 'A', true);
        var auxiliary = ArchiveBatchTests.Commit(project, resource, source, 'B');
        var receipt = await workflow.CheckInBatchAsync(batch, "resource", "admin", UserRole.Administrator, session,
            [new(root.Id, main.File, main.Properties, main.ReferenceSnapshot, "主图与资源", true, null),
             new(resource.Id, auxiliary.File, auxiliary.Properties, auxiliary.ReferenceSnapshot, "随主图保存", false, null)], default);
        Assert.Equal(2, receipt.Results.Count);
        Assert.All(receipt.Results, result => Assert.True(result.VersionCreated));
        var stored = Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.Equal(resource.Id, Assert.Single(stored.ReferenceSnapshot.Children).DocumentId);
        Assert.Single(await repository.ListDocumentVersionsAsync(resource.Id, default));
        await workflow.CompleteArchiveBatchFollowupAsync(new(receipt, [root, resource]), default);
        foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified })
            Assert.DoesNotContain(await repository.GetBomAsync(project.Id, kind, default), row => row.SourceDocumentId == resource.Id);
        var replay = await workflow.CheckInBatchAsync(batch, "resource", "admin", UserRole.Administrator, session,
            [new(root.Id, main.File, main.Properties, main.ReferenceSnapshot, "主图与资源", true, null),
             new(resource.Id, auxiliary.File, auxiliary.Properties, auxiliary.ReferenceSnapshot, "随主图保存", false, null)], default);
        Assert.Equal(receipt with { FollowupPending = false }, replay);
        Assert.Single(await repository.ListDocumentVersionsAsync(resource.Id, default));
    }

    [Theory]
    [InlineData("pplastic2.jpg")]
    [InlineData("来源.step")]
    [InlineData("格式.slddrt")]
    [InlineData("设计表.xlsx")]
    public async Task OrdinaryStandaloneRegistrationIsStillRejected(string fileName)
    {
        var (repository, workflow, project) = await Fixture();
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterDocumentAsync(
            new(project.Id, "RESOURCE", "资源", fileName, DocumentKind.Other, SourceSha256: new string('A', 64)),
            "admin", UserRole.Administrator, default));
        Assert.Contains("须随主图档整批提交", error.Message);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }

    [Theory]
    [InlineData("脚本.exe")]
    [InlineData("脚本.ps1")]
    [InlineData("未知.bin")]
    public async Task UnknownBatchResourceTypeDoesNotCreateARegistration(string fileName)
    {
        var (repository, workflow, project) = await Fixture();
        var context = new ArchiveRegistrationContext(Guid.NewGuid(), Guid.NewGuid(), "QA");
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterDocumentAsync(
            new(project.Id, "RESOURCE", "资源", fileName, DocumentKind.Other, SourceSha256: new string('A', 64)),
            "admin", UserRole.Administrator, default, context));
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }

    [Fact]
    public async Task InvalidArchiveSessionCannotReserveAnImage()
    {
        var (repository, workflow, project) = await Fixture();
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterDocumentAsync(
            new(project.Id, "RESOURCE", "资源", "pplastic2.jpg", DocumentKind.Other, SourceSha256: new string('A', 64)),
            "admin", UserRole.Administrator, default, new(Guid.Empty, Guid.Empty, "")));
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }

    private static async Task<(InMemoryPdmRepository Repository, PdmWorkflowService Workflow, Project Project)> Fixture()
    {
        var repository = BusinessTestRepository.Create(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("AUX-" + Guid.NewGuid().ToString("N"), "随附资源", "admin", "vault", "release"), "admin", default);
        return (repository, new(repository, DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>(), null!, TimeProvider.System), project);
    }
}
