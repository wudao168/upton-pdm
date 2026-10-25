using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class DocumentOperationRuleTests
{
    [Fact]
    public void ApprovedLowRiskChecksCannotBeReenabledByOldSettings()
    {
        string[] ids = ["A08", "A10", "B02", "B03", "B14", "B17", "B18", "B20", "C03", "C10", "E06",
            "F03", "F14", "F21", "F23", "F25", "F27", "G10", "H09", "H10", "H11", "H12", "I10"];
        Assert.Equal(ids, DocumentOperationRuleCatalog.NonBlockingIds);
        var old = new DocumentOperationRuleSettings(19, []);
        Assert.Equal(ids, old.Normalize().DisabledRuleIds);
        var messages = new List<string>();
        using (DocumentRuleRuntime.Use([], messages.Add))
        {
            foreach (var id in ids) Assert.False(DocumentRuleRuntime.ShouldBlock(id, "旧客户端请求的前置拦截"));
            Assert.True(DocumentRuleRuntime.ShouldBlock("B10", "实际空文件"));
            Assert.True(DocumentRuleRuntime.ShouldBlock("D15", "实际并发版本冲突"));
            Assert.Equal(ids.Length, messages.Count);
        }
    }
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task NativeInventoryCompanionsTravelButOldFeatureDiscoveryDoesNotExpandTreeScope(bool nativeInventory, int fileCount)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-SCOPE-" + nativeInventory, "文件范围", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var companion = await ArchiveBatchTests.Register(repository, project, "CONTEXT", DocumentKind.Part, session);
        await repository.CheckInVersionAsync(companion.Id, "admin", session, ArchiveBatchTests.Commit(project, companion, ArchiveBatchTests.Node(companion), 'B'), default);
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var node = ArchiveBatchTests.Node(root) with { IncludesDesignDependencies = true, UsesNativeFileInventory = nativeInventory,
            Children = [ArchiveBatchTests.Node(companion) with { Role = ReferenceRole.ExternalFileDependency }] };
        await repository.CheckInVersionAsync(root.Id, "admin", session, ArchiveBatchTests.Commit(project, root, node, 'A', true), default);
        var workflow = new PdmWorkflowService(repository, new MetadataStorage(), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(root.Id, null, false, false, "admin", UserRole.Administrator, default, treeScopeOnly: true);
        Assert.Equal(fileCount, manifest.Files.Count);
    }

    [Fact]
    public async Task PartialIndexArchivesFileAndFinishesFollowupWithoutRemovingExistingBom()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-PARTIAL", "结构待补全", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, "KEEP", "既有物料", 3, "个", null, null, "W1", true);
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [item], default);
        var baseline = Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
        var session = Guid.NewGuid();
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var node = ArchiveBatchTests.Node(root) with { StructureIndexPending = true, UsesNativeFileInventory = true };
        var receipt = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "partial", "admin", session,
            [new(root.Id, ArchiveBatchTests.Commit(project, root, node, 'A', true))], default);
        var workflow = new PdmWorkflowService(repository, new MetadataStorage(), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        await workflow.CompleteArchiveBatchFollowupAsync(new(receipt, [root]), default);
        Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(baseline),
            System.Text.Json.JsonSerializer.Serialize(Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default))));
        Assert.False((await repository.FindArchiveBatchAsync(receipt.BatchId, default))!.FollowupPending);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateReleasePackageAsync(project.Id, null,
            "PENDING-RELEASE", "admin", "admin", "admin", UserRole.Administrator, default));
        Assert.Contains("待补全", error.Message);
        Assert.Empty(await repository.ListReleasePackagesAsync(project.Id, default));
    }

    [Fact]
    public async Task Pure2dDrawingCanArchiveAndReopenWithoutInventingAModelRelation()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-2D", "纯二维规则验收", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "2D", "纯二维", "2D.SLDDRW", DocumentKind.Drawing), "admin", default);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var commit = ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing), 'A', true);
        var receipt = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "pure2d", "admin", session, [new(drawing.Id, commit)], default);
        Assert.Single(receipt.Results);
        Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id, default));
        var stored = Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default));
        Assert.Empty(stored.ReferenceSnapshot.Children);
        var workflow = new PdmWorkflowService(repository, new MetadataStorage(), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(drawing.Id, null, false, false, "admin", UserRole.Administrator, default);
        Assert.Equal(drawing.Id, Assert.Single(manifest.Files).DocumentId);
    }

    [Fact]
    public async Task AuxiliaryDrawingFailureDoesNotBlockModelButExplicitDrawingStillFails()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-AUX", "辅助工程图", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        await repository.CheckInVersionAsync(part.Id, "admin", session, ArchiveBatchTests.Commit(project, part, ArchiveBatchTests.Node(part), 'A'), default);
        var drawing = await ArchiveBatchTests.Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        var commit = ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(part)] }, 'B');
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "aux-drawing", "admin", session, [new(drawing.Id, commit)], default);
        Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default));
        var workflow = new PdmWorkflowService(repository, new MetadataStorage(commit.File.RelativePath), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        var modelManifest = await workflow.CreateControlledOpenManifestAsync(part.Id, null, false, false, "admin", UserRole.Administrator, default);
        Assert.Equal(part.Id, Assert.Single(modelManifest.Files).DocumentId);
        Assert.Contains(modelManifest.Warnings, message => message.Contains("辅助工程图") && message.Contains("待处理"));
        await Assert.ThrowsAsync<IOException>(() => workflow.CreateControlledOpenManifestAsync(drawing.Id, null, false, false, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task RemovingOccurrenceDoesNotDeleteIndependentPartOrDrawing()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-REMOVE", "删除装配引用", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        await repository.CheckInVersionAsync(part.Id, "admin", session, ArchiveBatchTests.Commit(project, part, ArchiveBatchTests.Node(part), 'A'), default);
        var drawing = await ArchiveBatchTests.Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "independent-drawing", "admin", session,
            [new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(part)] }, 'B'))], default);
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(root.Id, "admin", session,
            ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root) with { Children = [ArchiveBatchTests.Node(part)] }, 'C', true), default);
        var previous = Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        root = await repository.CheckoutAsync(root.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        await repository.CheckInVersionAsync(root.Id, "admin", session,
            ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root), 'D', true) with { ExpectedVersionId = previous.Id }, default);
        Assert.Empty((await repository.ListDocumentVersionsAsync(root.Id, default)).First().ReferenceSnapshot.Children);
        Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default));
        Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default));
        Assert.Equal(part.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
    }

    private sealed class MetadataStorage(string? missingPath = null) : IFileStorage
    {
        public Task ValidateStoredFileMetadataAsync(Project project, StoredFile file, CancellationToken token) => file.RelativePath == missingPath
            ? Task.FromException(new IOException("验收模拟：工程图存档文件暂不可读取")) : Task.CompletedTask;
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken token) => Task.CompletedTask;
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long length, string hash, CancellationToken token, string? owner = null) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid id, int index, Stream content, CancellationToken token) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken token) => Task.FromResult(true);
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string path, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public void PendingStructureSurvivesWorkingTreeRefreshAndChangesArchiveComparison()
    {
        var id = Guid.NewGuid();
        var complete = new DocumentReferenceNode(id, id, "ROOT", "ROOT.SLDASM", "总装", DocumentKind.Assembly,
            "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var pending = complete with { StructureIndexPending = true };
        var refreshed = WorkingReferenceTree.Resolve(complete, new Dictionary<Guid, DocumentReferenceNode> { [id] = pending });
        Assert.True(refreshed.HasPendingStructureIndex);
        Assert.False(ArchiveSafety.SameReferences(complete, refreshed));
        Assert.False(WorkingReferenceTree.Resolve(pending, new Dictionary<Guid, DocumentReferenceNode> { [id] = complete }).HasPendingStructureIndex);
    }
}
