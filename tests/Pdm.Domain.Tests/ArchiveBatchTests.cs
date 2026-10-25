using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveBatchTests
{
    [Fact]
    public async Task ChangedPartAndItsDrawingBindTheSameNewVersionWithoutAdvancingParent()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("LINKED-" + Guid.NewGuid().ToString("N"), "图模联动", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await Register(repository, project, "PART", DocumentKind.Part, session);
        var parent = await Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAWING", "不同名工程图", "DRAWING.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: part.Id), "admin", default);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "initial", "admin", session,
            [new(part.Id, Commit(project, part, Node(part), 'A')),
             new(parent.Id, Commit(project, parent, Node(parent) with { Children = [Node(part)] }, 'B', true)),
             new(drawing.Id, Commit(project, drawing, Node(drawing) with { Children = [Node(part)] }, 'C'))], default);
        var originalParent = Assert.Single(await repository.ListDocumentVersionsAsync(parent.Id, default));
        var partBase = Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default));
        var drawingBase = Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default));
        part = await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var receipt = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "update", "admin", session,
            [new(part.Id, Commit(project, part, Node(part), 'D') with { ExpectedVersionId = partBase.Id }),
             new(drawing.Id, Commit(project, drawing, Node(drawing) with { Children = [Node(part)] }, 'E') with { ExpectedVersionId = drawingBase.Id })], default);
        Assert.All(receipt.Results, result => Assert.True(result.VersionCreated));
        var partVersion = (await repository.ListDocumentVersionsAsync(part.Id, default)).First();
        var drawingVersion = (await repository.ListDocumentVersionsAsync(drawing.Id, default)).First();
        Assert.Equal(partVersion.Revision, Assert.Single(drawingVersion.ReferenceSnapshot.Children).Revision);
        Assert.Equal("W2", partVersion.Revision.Display);
        Assert.Equal("W2", drawingVersion.Revision.Display);
        Assert.Equal(originalParent.Id, Assert.Single(await repository.ListDocumentVersionsAsync(parent.Id, default)).Id);
        Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryDrawingModelRelationIsAtomicWithTheBatch(bool failLastDrawing)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("MULTI-" + Guid.NewGuid().ToString("N"), "多模型工程图", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid(); var batch = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind) => await repository.ReserveArchiveDocumentAsync(
            new(project.Id, name, name, name + (kind == DocumentKind.Part ? ".SLDPRT" : ".SLDDRW"), kind, SourceSha256: new string('A',64)), "admin", context, default);
        var first = await Reserve("FIRST", DocumentKind.Part); var second = await Reserve("SECOND", DocumentKind.Part);
        var drawing = await Reserve("MULTI", DocumentKind.Drawing); var last = await Reserve("LAST", DocumentKind.Drawing);
        var drawingRoot = Node(drawing) with { Children = [Node(first), Node(second) with { Configuration = "D14" }, Node(second) with { Configuration = "D15" }] };
        var lastCommit = Commit(project, last, Node(last) with { Children = [Node(first)] }, 'E');
        if (failLastDrawing) lastCommit = lastCommit with { ExpectedVersionId = Guid.NewGuid() };
        ArchiveBatchFile[] files = [new(first.Id, Commit(project, first, Node(first), 'B')), new(second.Id, Commit(project, second, Node(second), 'C')),
            new(drawing.Id, Commit(project, drawing, drawingRoot, 'D')), new(last.Id, lastCommit)];
        if (failLastDrawing)
        {
            await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(batch, "multi", "admin", session, files, default));
            Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id, default));
            Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
            Assert.Null(await repository.FindArchiveBatchAsync(batch, default));
        }
        else
        {
            await repository.CheckInBatchVersionsAsync(batch, "multi", "admin", session, files, default);
            var relations = (await repository.ListDocumentRelationsAsync(project.Id, default)).Where(relation => relation.DrawingDocumentId == drawing.Id).ToArray();
            Assert.Equal(2, relations.Length);
            Assert.Contains(relations, relation => relation.ModelDocumentId == first.Id);
            Assert.Contains(relations, relation => relation.ModelDocumentId == second.Id);
            Assert.Equal(3, (await repository.ListDocumentVersionsAsync(drawing.Id, default)).Single().ReferenceSnapshot.Children.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRegistrationsAndDrawingRelationsBecomeVisibleOnlyWhenEveryFileCommits(bool failLastDrawing)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("RESERVE-" + Guid.NewGuid().ToString("N"), "新登记整体回滚", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid(); var batch = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch,session,"QA");
        var root = await repository.ReserveArchiveDocumentAsync(new(project.Id,"ROOT","总装","ROOT.SLDASM",DocumentKind.Assembly,SourceSha256:new string('A',64)),"admin",context,default);
        var drawing = await repository.ReserveArchiveDocumentAsync(new(project.Id,"DRW","工程图","DRW.SLDDRW",DocumentKind.Drawing,RelatedModelDocumentId:root.Id,SourceSha256:new string('B',64)),"admin",context,default);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id,default));
        Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id,default));
        var drawingCommit = Commit(project,drawing,Node(drawing) with { Children = [Node(root)] },'D');
        if(failLastDrawing) drawingCommit = drawingCommit with { ExpectedVersionId = Guid.NewGuid() };
        ArchiveBatchFile[] files = [new(root.Id,Commit(project,root,Node(root),'C',true)), new(drawing.Id,drawingCommit)];
        if(failLastDrawing)
        {
            await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(batch,"request","admin",session,files,default));
            Assert.Empty(await repository.ListDocumentsAsync(project.Id,default));
            Assert.Empty(await repository.ListDocumentRelationsAsync(project.Id,default));
            Assert.Empty(await repository.ListDocumentVersionsAsync(root.Id,default));
            Assert.Null(await repository.GetLatestReferenceSnapshotAsync(project.Id,default));
            await repository.CancelArchiveRegistrationAsync(batch,"admin",session,default);
            Assert.Null(await repository.FindDocumentAsync(drawing.Id,default));
        }
        else
        {
            var receipt = await repository.CheckInBatchVersionsAsync(batch,"request","admin",session,files,default);
            Assert.Equal(2,(await repository.ListDocumentsAsync(project.Id,default)).Count);
            Assert.Single(await repository.ListDocumentRelationsAsync(project.Id,default));
            Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id,default));
            Assert.Equal(receipt,await repository.CheckInBatchVersionsAsync(batch,"request","admin",session,files,default));
            await repository.CancelArchiveRegistrationAsync(batch,"admin",session,default);
            Assert.Equal(2,(await repository.ListDocumentsAsync(project.Id,default)).Count);
        }
    }

    [Theory]
    [InlineData("permission", false)]
    [InlineData("missing-reference", false)]
    [InlineData("base-version", false)]
    [InlineData("permission", true)]
    [InlineData("missing-reference", true)]
    [InlineData("base-version", true)]
    public async Task LateParentFailureRollsBackChildVersionLocksAndProjectStructure(string failure, bool existingVersion)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("ATOMIC-" + Guid.NewGuid().ToString("N"), "整批回滚", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await Register(repository, project, "PART", DocumentKind.Part, session);
        Guid? previousVersionId = null;
        if (existingVersion)
        {
            var previous = await repository.CheckInVersionAsync(part.Id, "admin", session, Commit(project, part, Node(part), 'C'), default);
            previousVersionId = previous.Version!.Id;
            part = await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        }
        var root = await Register(repository, project, "ROOT", DocumentKind.Assembly, failure == "permission" ? Guid.NewGuid() : session);
        var child = Node(part);
        if (failure == "missing-reference") child = child with { DocumentId = null };
        var rootCommit = Commit(project, root, Node(root) with { Children = [child] }, 'B', true);
        if (failure == "base-version") rootCommit = rootCommit with { ExpectedVersionId = Guid.NewGuid() };
        var batch = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => repository.CheckInBatchVersionsAsync(batch, "request", "admin", session,
            [new(part.Id, Commit(project, part, Node(part), 'A') with { ExpectedVersionId = previousVersionId }), new(root.Id, rootCommit)], default));
        var remaining = await repository.ListDocumentVersionsAsync(part.Id, default);
        if (existingVersion) Assert.Equal(previousVersionId, Assert.Single(remaining).Id);
        else Assert.Empty(remaining);
        Assert.Empty(await repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.Equal(session, (await repository.FindDocumentAsync(part.Id, default))!.CheckoutSessionId);
        Assert.Equal(root.CheckoutSessionId, (await repository.FindDocumentAsync(root.Id, default))!.CheckoutSessionId);
        Assert.Null(await repository.GetLatestReferenceSnapshotAsync(project.Id, default));
        Assert.Null(await repository.FindArchiveBatchAsync(batch, default));
    }

    [Fact]
    public async Task CompleteBatchBindsNewChildVersionAndReplaysWithoutDuplicateVersions()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("ATOMIC-" + Guid.NewGuid().ToString("N"), "整批成功", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await Register(repository, project, "PART", DocumentKind.Part, session);
        await repository.CheckInVersionAsync(part.Id, "admin", session, Commit(project, part, Node(part), 'A'), default);
        part = await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var root = await Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var rootTree = Node(root) with { Children = [Node(part) with { InstancePath = "ROOT/PART-1", Configuration = "CFG-A" },
            Node(part) with { InstancePath = "ROOT/PART-2", Configuration = "CFG-B", Quantity = 2 }] };
        var partCommit = Commit(project, part, Node(part), 'C') with { ExpectedVersionId = Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default)).Id };
        ArchiveBatchFile[] files = [new(root.Id, Commit(project, root, rootTree, 'B', true)), new(part.Id, partCommit)];
        var batch = Guid.NewGuid();
        var receipt = await repository.CheckInBatchVersionsAsync(batch, "request", "admin", session, files, default);
        var repeated = await repository.CheckInBatchVersionsAsync(batch, "request", "admin", session, files, default);
        Assert.Equal(receipt, repeated);
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        var rootVersion = Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.All(rootVersion.ReferenceSnapshot.Children, child => Assert.Equal("W2", child.Revision!.Value.Display));
        Assert.Equal(new[] { "CFG-A", "CFG-B" }, rootVersion.ReferenceSnapshot.Children.Select(child => child.Configuration));
        Assert.Null((await repository.FindDocumentAsync(part.Id, default))!.CheckoutSessionId);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(batch, "different", "admin", session, files, default));
    }

    internal static async Task<PdmDocument> Register(IPdmRepository repository, Project project, string name, DocumentKind kind, Guid session)
    {
        var doc = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "admin", default);
        return await repository.CheckoutAsync(doc.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
    }
    internal static DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name,
        doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "admin", []);
    internal static DocumentVersionCommit Commit(Project project, PdmDocument doc, DocumentReferenceNode tree, char hash, bool root = false) => new(
        new StoredFile(".versions/" + Guid.NewGuid().ToString("N"), 128, new string(hash, 64), DateTimeOffset.UtcNow), "整批测试",
        new Dictionary<string, string?>(), new CadReferenceSnapshot(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "admin", tree, new string('F', 64)), [], [], IsProjectRoot: root)
        { RequireBaseVersion = true };
}
