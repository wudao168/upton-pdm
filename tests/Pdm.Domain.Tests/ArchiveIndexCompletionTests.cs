using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveIndexCompletionTests
{
    [Theory]
    [InlineData(DocumentKind.Part)]
    [InlineData(DocumentKind.Assembly)]
    [InlineData(DocumentKind.Drawing)]
    public async Task SameNativeBytesCanCompletePendingIndexOnce(DocumentKind kind)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("INDEX-" + Guid.NewGuid().ToString("N"), "补全索引", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var suffix = kind == DocumentKind.Part ? ".SLDPRT" : kind == DocumentKind.Drawing ? ".SLDDRW" : ".SLDASM";
        var document = await repository.RegisterDocumentAsync(new(project.Id, "ROOT", "根图档", "ROOT" + suffix, kind), "admin", default);
        document = await repository.CheckoutAsync(document.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var pending = ArchiveBatchTests.Node(document) with { StructureIndexPending = true, UsesNativeFileInventory = true };
        var initial = ArchiveBatchTests.Commit(project, document, pending, 'A', true);
        var stored = await repository.CheckInVersionAsync(document.Id, "admin", session, initial, default);
        var workflow = new PdmWorkflowService(repository, DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>(), null!, TimeProvider.System);
        var completed = pending with { StructureIndexPending = false };
        var baseline = stored.Version!;
        foreach (var repeat in new[] { false, true })
        {
            await repository.CheckoutAsync(document.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
            var snapshot = initial.ReferenceSnapshot with { SnapshotId = Guid.NewGuid(), Root = completed };
            var input = new ArchiveBatchInput(document.Id, initial.File, new Dictionary<string, string?>(), snapshot,
                "重新读取完整索引", true, baseline.Id);
            var receipt = await workflow.CheckInBatchAsync(Guid.NewGuid(), "index-" + repeat, "admin", UserRole.Administrator, session, [input], default);
            var result = Assert.Single(receipt.Results);
            Assert.Equal(!repeat, result.VersionCreated);
            baseline = (await repository.ListDocumentVersionsAsync(document.Id, default)).First();
            Assert.False(baseline.ReferenceSnapshot.HasPendingStructureIndex);
            Assert.Equal(stored.Version!.Sha256, baseline.Sha256);
            Assert.Equal(2, (await repository.ListDocumentVersionsAsync(document.Id, default)).Count);
        }
    }

    [Fact]
    public async Task EmptyClientPropertyFallbackPreservesExistingVersionAndBomValues()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("PROPS-" + Guid.NewGuid().ToString("N"), "属性读失败保护", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var settings = await repository.GetSystemSettingsAsync(default);
        var properties = new Dictionary<string, string?>
        {
            ["全局/" + BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类")] = "标准件",
            ["全局/" + settings.BomDrawingNumberProperty] = "PART",
            ["全局/" + settings.BomNameProperty] = "原名称",
            ["全局/" + settings.BomMaterialProperty] = "原材质",
            ["全局/" + settings.BomSpecificationProperty] = "原型号"
        };
        var first = await repository.CheckInVersionAsync(part.Id, "admin", session,
            ArchiveBatchTests.Commit(project, part, ArchiveBatchTests.Node(part), 'A') with { Properties = properties }, default);
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(root.Id, "admin", session,
            ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root) with
            { Children = [ArchiveBatchTests.Node(part) with { InstancePath = "ROOT/PART" }] }, 'B', true), default);
        var workflow = new PdmWorkflowService(repository, DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>(), null!, TimeProvider.System);
        await workflow.GenerateMechanicalBomAsync(project.Id, true, "admin", UserRole.Administrator, default);
        var before = Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
        await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var changed = ArchiveBatchTests.Commit(project, part, ArchiveBatchTests.Node(part), 'C');
        var input = new ArchiveBatchInput(part.Id, changed.File, new Dictionary<string, string?>(), changed.ReferenceSnapshot,
            "客户端属性读取失败，仅存原生内容", false, first.Version!.Id);
        var receipt = await workflow.CheckInBatchAsync(Guid.NewGuid(), "empty-fallback", "admin", UserRole.Administrator, session, [input], default);
        await workflow.CompleteArchiveBatchFollowupAsync(new(receipt, [part]), default);
        var latest = (await repository.ListDocumentVersionsAsync(part.Id, default)).First();
        foreach (var property in properties) Assert.Equal(property.Value, latest.PropertySnapshot[property.Key]);
        var after = Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Material, after.Material);
        Assert.Equal(before.Specification, after.Specification);
        Assert.False(after.IsPendingClassification);
    }
}
