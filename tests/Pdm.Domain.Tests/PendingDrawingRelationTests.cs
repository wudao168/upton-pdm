using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PendingDrawingRelationTests
{
    [Fact]
    public async Task ExistingPendingVersionWithoutRelationRowsIsRecoveredReadOnlyForListAndOpen()
    {
        var (repository, project, session) = await Setup();
        var model = await Model(repository, project, session, "KNOWN");
        var drawing = await Drawing(repository, project, session);
        var pending = ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [ArchiveBatchTests.Node(model)] };
        // The legacy single-version entry point reproduces an immutable version without relation rows.
        var saved = (await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending, 'C'), default)).Version!;
        var rows = StoredRelations(repository);
        var rowCount = rows.Count;
        Assert.DoesNotContain(rows.Values, relation => relation.DrawingDocumentId == drawing.Id);
        Assert.Equal(model.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);

        var workflow = new PdmWorkflowService(repository, new MetadataStorage(), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(model.Id, null, false, false,
            "admin", UserRole.Administrator, default, treeScopeOnly: true);
        Assert.Equal(saved.Id, Assert.Single(manifest.Files, file => file.DocumentId == drawing.Id).VersionId);
        Assert.Equal(rowCount, rows.Count);
        Assert.DoesNotContain(rows.Values, relation => relation.DrawingDocumentId == drawing.Id);
        Assert.Equal(saved, Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default)));
        Assert.True(saved.ReferenceSnapshot.StructureIndexPending);

        // Discovering a drawing must not bypass its pending state when formal versions are captured.
        model = await repository.CheckoutAsync(model.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var modelVersion = Assert.Single(await repository.ListDocumentVersionsAsync(model.Id, default));
        await repository.CheckInVersionAsync(model.Id, "admin", session,
            ArchiveBatchTests.Commit(project, model, modelVersion.ReferenceSnapshot, 'A', true) with { ExpectedVersionId = modelVersion.Id }, default);
        var snapshot = (await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, model.DrawingNumber, model.Name, 1, "件", null, null, "W1", true)
        { SourceDocumentId = model.Id, SourceConfiguration = "Default" };
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "QA-PENDING-REL", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null)
        { Scope = ReleaseScope.NonStandardWithDrawing, RootDocumentId = model.Id, SelectedBomItemIds = [item.Id], NonStandardBomSnapshot = [item] };
        var capture = typeof(PdmWorkflowService).GetMethod("CaptureReleaseVersionsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => (Task<ReleasePackage>)capture.Invoke(workflow, [package, CancellationToken.None])!);
        Assert.Contains("待补全", error.Message);
        Assert.Contains(drawing.FileName, error.Message);
    }

    [Fact]
    public async Task CompatibilityReadUsesOnlyLatestSnapshotAndDoesNotRestoreRemovedHistoricalRelations()
    {
        var (repository, project, session) = await Setup();
        var first = await Model(repository, project, session, "FIRST");
        var second = await Model(repository, project, session, "SECOND");
        var drawing = await Drawing(repository, project, session);
        var pending = ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [ArchiveBatchTests.Node(first)] };
        var before = (await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending, 'C'), default)).Version!;
        Assert.Equal(first.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var after = (await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending with { Children = [ArchiveBatchTests.Node(second)] }, 'D')
                with { ExpectedVersionId = before.Id }, default)).Version!;
        Assert.Equal(second.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending with { StructureIndexPending = false, Children = [] }, 'E')
                with { ExpectedVersionId = after.Id }, default);
        Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id, default));
        Assert.DoesNotContain(StoredRelations(repository).Values, relation => relation.DrawingDocumentId == drawing.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibilityReadUsesCurrentRevisionWhenStoredVersionsHaveTheSameTimestamp(bool currentComplete)
    {
        var (repository, project, session) = await Setup();
        var first = await Model(repository, project, session, "FIRST");
        var second = await Model(repository, project, session, "SECOND");
        var drawing = await Drawing(repository, project, session);
        var pending = ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [ArchiveBatchTests.Node(first)] };
        var oldVersion = (await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending, 'C'), default)).Version!;
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var currentVersion = (await repository.CheckInVersionAsync(drawing.Id, "admin", session,
            ArchiveBatchTests.Commit(project, drawing, pending with
            { StructureIndexPending = !currentComplete, Children = currentComplete ? [] : [ArchiveBatchTests.Node(second)] }, 'D')
                with { ExpectedVersionId = oldVersion.Id }, default)).Version!;
        var stored = (IDictionary<Guid, DocumentVersion>)typeof(InMemoryPdmRepository)
            .GetField("versions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(repository)!;
        var keys = stored.Values.Where(version => version.DocumentId == drawing.Id).Select(version => version.Id).ToArray();
        var sameTime = DateTimeOffset.Parse("2026-10-23T01:00:00Z");
        // Import-like tie: enumeration presents the old snapshot first, but the document points at the new revision.
        stored[keys[0]] = oldVersion with { Id = keys[0], CreatedAt = sameTime };
        stored[keys[1]] = currentVersion with { Id = keys[1], CreatedAt = sameTime };
        Assert.Equal(oldVersion.Revision, stored.Values.First(version => version.DocumentId == drawing.Id).Revision);
        Assert.Equal(currentVersion.Revision, (await repository.FindDocumentAsync(drawing.Id, default))!.Revision);
        var relations = await repository.ListDocumentRelationsAsync(project.Id, default);
        if (currentComplete) Assert.Empty(relations);
        else Assert.Equal(second.Id, Assert.Single(relations).ModelDocumentId);
        Assert.DoesNotContain(StoredRelations(repository).Values, relation => relation.DrawingDocumentId == drawing.Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("virtual")]
    [InlineData("no-id")]
    [InlineData("unknown-id")]
    [InlineData("no-revision")]
    [InlineData("wrong-revision")]
    [InlineData("unarchived-model")]
    [InlineData("wrong-model-file")]
    [InlineData("wrong-model-kind")]
    [InlineData("other-project")]
    [InlineData("deleted-model")]
    [InlineData("deleted-drawing")]
    [InlineData("wrong-drawing-id")]
    [InlineData("wrong-drawing-file")]
    public async Task CompatibilityReadDoesNotInventAnUnverifiedRelation(string invalid)
    {
        var (repository, project, session) = await Setup();
        var modelProject = invalid == "other-project"
            ? await repository.CreateProjectAsync(new("QA-OTHER-" + Guid.NewGuid().ToString("N"), "其他项目", "admin", "vault", "release"), "admin", default)
            : project;
        var model = invalid == "unarchived-model"
            ? await ArchiveBatchTests.Register(repository, modelProject, "MODEL", DocumentKind.Part, session)
            : await Model(repository, modelProject, session, "MODEL");
        var drawing = await Drawing(repository, project, session);
        var child = ArchiveBatchTests.Node(model);
        child = invalid switch
        {
            "missing" => child with { Status = ReferenceNodeStatus.Missing },
            "virtual" => child with { Status = ReferenceNodeStatus.Virtual },
            "no-id" => child with { DocumentId = null },
            "unknown-id" => child with { DocumentId = Guid.NewGuid() },
            "no-revision" => child with { Revision = null },
            "wrong-revision" => child with { Revision = RevisionLabel.InitialWork().NextWork() },
            "wrong-model-file" => child with { FileName = "OTHER.SLDPRT" },
            "wrong-model-kind" => child with { Kind = DocumentKind.Assembly },
            _ => child
        };
        var pending = ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [child] };
        if (invalid == "wrong-drawing-id") pending = pending with { DocumentId = Guid.NewGuid() };
        if (invalid == "wrong-drawing-file") pending = pending with { FileName = "OTHER.SLDDRW" };
        await repository.CheckInVersionAsync(drawing.Id, "admin", session, ArchiveBatchTests.Commit(project, drawing, pending, 'C'), default);
        if (invalid is "deleted-model" or "deleted-drawing")
        {
            var current = (await repository.FindDocumentAsync(invalid == "deleted-model" ? model.Id : drawing.Id, default))!;
            await repository.SetDocumentDeletedAsync(current.Id, true, current.RowVersion, "admin", "兼容边界测试", DateTimeOffset.UtcNow, default);
        }
        Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id, default));
        Assert.DoesNotContain(StoredRelations(repository).Values, relation => relation.DrawingDocumentId == drawing.Id);
    }

    [Fact]
    public async Task FirstPartialDrawingRetainsKnownModelForRetrievalButRemainsPendingForRelease()
    {
        var (repository, project, session) = await Setup();
        var model = await Model(repository, project, session, "KNOWN");
        var unresolved = await Model(repository, project, session, "MISSING-VIEW");
        var drawing = await Drawing(repository, project, session);
        var tree = ArchiveBatchTests.Node(drawing) with
        {
            StructureIndexPending = true,
            Children = [ArchiveBatchTests.Node(model), ArchiveBatchTests.Node(unresolved) with { Status = ReferenceNodeStatus.Missing }]
        };
        await Archive(repository, project, session, drawing, tree, 'C');
        var relation = Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default));
        Assert.Equal(model.Id, relation.ModelDocumentId);
        Assert.Equal(drawing.Id, relation.DrawingDocumentId);
        Assert.True(Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default)).ReferenceSnapshot.StructureIndexPending);

        var workflow = new PdmWorkflowService(repository, new MetadataStorage(), new AtomicReleasePackagePublisher(TimeProvider.System), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(model.Id, null, false, false,
            "admin", UserRole.Administrator, default, treeScopeOnly: true);
        Assert.Equal(new[] { model.Id, drawing.Id }.Order(), manifest.Files.Select(file => file.DocumentId).Order());
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateReleasePackageAsync(project.Id, null,
            "PENDING-DRAWING", "admin", "admin", "admin", UserRole.Administrator, default));
        Assert.Contains("待补全", error.Message);
        Assert.Empty(await repository.ListReleasePackagesAsync(project.Id, default));
    }

    [Fact]
    public async Task PartialDrawingAddsKnownModelsAndOnlyCompleteSnapshotCanRemoveOldRelations()
    {
        var (repository, project, session) = await Setup();
        var first = await Model(repository, project, session, "FIRST");
        var second = await Model(repository, project, session, "SECOND");
        var drawing = await Drawing(repository, project, session);
        await Archive(repository, project, session, drawing,
            ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(first)] }, 'C');

        var partial = ArchiveBatchTests.Node(drawing) with
        {
            StructureIndexPending = true,
            Children = [ArchiveBatchTests.Node(second), ArchiveBatchTests.Node(second) with { Configuration = "OTHER" }]
        };
        await Archive(repository, project, session, drawing, partial, 'D');
        Assert.Equal(new[] { first.Id, second.Id }.Order(), (await repository.ListDocumentRelationsAsync(project.Id, default))
            .Select(relation => relation.ModelDocumentId).Order());
        await Archive(repository, project, session, drawing, partial with { Children = [] }, 'E');
        Assert.Equal(2, (await repository.ListDocumentRelationsAsync(project.Id, default)).Count);

        await Archive(repository, project, session, drawing, partial with { StructureIndexPending = false }, 'F');
        Assert.Equal(second.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
        await Archive(repository, project, session, drawing, partial with { StructureIndexPending = false, Children = [] }, 'G');
        Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id, default));
    }

    [Fact]
    public async Task FailedBatchRollsBackPartialDrawingRelationAdditions()
    {
        var (repository, project, session) = await Setup();
        var first = await Model(repository, project, session, "FIRST");
        var second = await Model(repository, project, session, "SECOND");
        var drawing = await Drawing(repository, project, session);
        await Archive(repository, project, session, drawing,
            ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(first)] }, 'C');
        var before = Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default));
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var other = await repository.RegisterDocumentAsync(new(project.Id, "LAST", "失败的图纸", "LAST.SLDDRW", DocumentKind.Drawing), "admin", default);
        other = await repository.CheckoutAsync(other.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var pending = ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [ArchiveBatchTests.Node(second)] };
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "rollback", "admin", session,
            [new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, pending, 'D') with { ExpectedVersionId = before.Id }),
             new(other.Id, ArchiveBatchTests.Commit(project, other, ArchiveBatchTests.Node(other), 'E') with { ExpectedVersionId = Guid.NewGuid() })], default));
        Assert.Equal(first.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
        Assert.Equal(before.Id, Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default)).Id);
    }

    private static async Task<(InMemoryPdmRepository Repository, Project Project, Guid Session)> Setup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-PENDING-REL-" + Guid.NewGuid().ToString("N"),
            "部分工程图关联", "admin", "vault", "release"), "admin", default);
        return (repository, project, Guid.NewGuid());
    }

    private static IDictionary<(Guid DrawingId, Guid ModelId), DocumentModelDrawingRelation> StoredRelations(InMemoryPdmRepository repository) =>
        (IDictionary<(Guid DrawingId, Guid ModelId), DocumentModelDrawingRelation>)typeof(InMemoryPdmRepository)
            .GetField("documentRelations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(repository)!;

    private static async Task<PdmDocument> Model(InMemoryPdmRepository repository, Project project, Guid session, string name)
    {
        var model = await ArchiveBatchTests.Register(repository, project, name, DocumentKind.Part, session);
        return (await repository.CheckInVersionAsync(model.Id, "admin", session,
            ArchiveBatchTests.Commit(project, model, ArchiveBatchTests.Node(model), 'A'), default)).Document;
    }

    private static async Task<PdmDocument> Drawing(InMemoryPdmRepository repository, Project project, Guid session)
    {
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAWING", "多页工程图", "DRAWING.SLDDRW", DocumentKind.Drawing), "admin", default);
        return await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
    }

    private static async Task Archive(InMemoryPdmRepository repository, Project project, Guid session,
        PdmDocument drawing, DocumentReferenceNode tree, char hash)
    {
        var previous = (await repository.ListDocumentVersionsAsync(drawing.Id, default)).FirstOrDefault();
        if (previous is not null)
            drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "pending-" + hash, "admin", session,
            [new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, tree, hash, true) with { ExpectedVersionId = previous?.Id })], default);
    }

    private sealed class MetadataStorage : IFileStorage
    {
        public Task ValidateStoredFileMetadataAsync(Project project, StoredFile file, CancellationToken token) => Task.CompletedTask;
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken token) => Task.CompletedTask;
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long length, string hash, CancellationToken token, string? owner = null) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid id, int index, Stream content, CancellationToken token) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken token) => Task.FromResult(true);
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string path, CancellationToken token) => throw new NotSupportedException();
    }
}
