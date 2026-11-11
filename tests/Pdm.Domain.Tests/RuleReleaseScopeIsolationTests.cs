using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class RuleReleaseScopeIsolationTests
{
    [Theory]
    [InlineData(ReleaseScope.NonStandardLongLead)]
    [InlineData(ReleaseScope.NonStandardWithDrawing)]
    public async Task PendingIndexDoesNotAddFormalPromotionRulesToLongLeadBindings(ReleaseScope scope)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-INDEX-LONGLEAD-" + scope, "长交期范围", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var model = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var modelNode = ArchiveBatchTests.Node(model) with { StructureIndexPending = true };
        await repository.CheckInVersionAsync(model.Id, "admin", session,
            ArchiveBatchTests.Commit(project, model, modelNode, 'A', true), default);
        var drawing = await ArchiveBatchTests.Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "longlead-drawing", "admin", session,
            [new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing) with { Children = [modelNode] }, 'B'))], default);
        Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default));
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "PART", "零件", 1, "个", "钢", "测试", "W1", true)
        { SourceDocumentId = model.Id, SourceConfiguration = modelNode.Configuration, SourceInstancePath = modelNode.InstancePath };
        await repository.ReplaceBomAsync(project.Id, BomKind.NonStandard, [item], default);
        var snapshot = (await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "QA-LONGLEAD", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null)
        { Scope = scope, RootDocumentId = model.Id, SelectedBomItemIds = [item.Id], NonStandardBomSnapshot = [item] };
        var workflow = new PdmWorkflowService(repository, new UnusedStorage(), new NoOpPublisher(), TimeProvider.System);
        var method = typeof(PdmWorkflowService).GetMethod("CaptureReleaseVersionsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<ReleasePackage> Capture() => (Task<ReleasePackage>)method.Invoke(workflow, [package, CancellationToken.None])!;
        if (scope == ReleaseScope.NonStandardWithDrawing)
        {
            var captured = await Capture();
            Assert.Equal(drawing.Id, Assert.Single(captured.VersionBindings!, binding => binding.Publish).DocumentId);
            Assert.Equal(1, captured.DrawingDocumentCount);
        }
        else
        {
            var captured = await Capture();
            Assert.Equal(2, captured.VersionBindings!.Count);
            Assert.All(captured.VersionBindings, binding => { Assert.False(binding.Publish); Assert.False(binding.RequiresPromotion); });
            Assert.False(captured.LocksDocuments);
            Assert.Equal(0, captured.DrawingDocumentCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmedRemovalKeepsItsFrozenSourceRootProtection(bool removedRootPending, bool unknownRemovedId)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var project = (await repository.FindProjectAsync(projectId, default))!;
        var session = Guid.NewGuid();
        var complete = await ArchiveCompleteRoot(repository, project, session);
        var pending = await ArchiveBatchTests.Register(repository, project, "PENDING-REMOVAL-ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(pending.Id, "admin", session,
            ArchiveBatchTests.Commit(project, pending, ArchiveBatchTests.Node(pending) with { StructureIndexPending = true }, 'A', true), default);
        var removedRootId = removedRootPending ? pending.Id : complete.RootDocumentId;
        var removed = new BomItem(Guid.NewGuid(), projectId, BomKind.NonStandard, 1, "REMOVED", "已删除组件", 1, "个", null, null, "W1", true)
        {
            SourceDocumentId = Guid.NewGuid(), SourceConfiguration = "Default",
            SourceInstancePath = $"roots/{removedRootId:N}/deleted-occurrence", Source = "Auto"
        };
        var baseline = await repository.SaveBomDraftAsync(projectId, BomKind.NonStandard, [removed], "admin", default);
        await repository.SetBomVersionStateAsync([baseline.Id], BomVersionState.Released, "admin", null, default);
        var next = await repository.SaveBomDraftAsync(projectId, BomKind.NonStandard, [], "admin", default);
        Assert.Equal(baseline.Id, next.BaseVersionId);
        // A later current-BOM row cannot replace the frozen deletion source.
        await repository.ReplaceBomAsync(projectId, BomKind.NonStandard,
            [removed with { SourceDocumentId = null, SourceInstancePath = null, Source = "Manual" }], default);
        var snapshot = (await repository.GetLatestReferenceSnapshotAsync(projectId, default))!;
        var package = new ReleasePackage(Guid.NewGuid(), projectId, "QA-REMOVE", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null)
        {
            Scope = ReleaseScope.NonStandardSupplement, NonStandardBomVersionId = next.Id,
            SelectedBomItemIds = [], NonStandardBomSnapshot = [],
            ConfirmedRemovedBomItemIds = [unknownRemovedId ? Guid.NewGuid() : removed.Id]
        };
        var workflow = new PdmWorkflowService(repository, new UnusedStorage(), new NoOpPublisher(), TimeProvider.System);
        var method = typeof(PdmWorkflowService).GetMethod("RequireCompletePackageReleaseIndexAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task Validate() => (Task)method.Invoke(workflow, [package, snapshot, CancellationToken.None])!;
        if (unknownRemovedId)
        {
            var error = await Assert.ThrowsAsync<PdmRuleException>(Validate);
            Assert.Contains(unknownRemovedId ? "来源" : "待补全", error.Message);
        }
        else await Validate();
    }

    private static async Task<CadReferenceSnapshot> ArchiveCompleteRoot(InMemoryPdmRepository repository, Project project, Guid session)
    {
        var root = await ArchiveBatchTests.Register(repository, project, "COMPLETE-ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(root.Id, "admin", session,
            ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root), 'C', true), default);
        return (await repository.GetProjectReferenceRootAsync(project.Id, root.Id, default))!;
    }

    private sealed class NoOpPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken token) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken token) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedStorage : IFileStorage
    {
        public Task<UploadSession> StartUploadAsync(Guid id, string name, long length, string hash, CancellationToken token, string? owner = null) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid id, int index, Stream content, CancellationToken token) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken token) => Task.FromResult(true);
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string path, CancellationToken token) => throw new NotSupportedException();
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken token) => throw new NotSupportedException();
    }
}
