using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectReferenceDocumentCountsTests
{
    private static DocumentReferenceNode Node(string name, Guid? id = null, DocumentKind kind = DocumentKind.Part,
        params DocumentReferenceNode[] children) => new(Guid.NewGuid(), id, Guid.NewGuid().ToString(), name, name,
            kind, "默认", 1, ReferenceNodeStatus.Normal, null, null, children);

    [Fact]
    public void CountsEntireReferenceStructureInsteadOfOnlyFourRegisteredDocuments()
    {
        var project = Guid.NewGuid();
        var documents = Enumerable.Range(0, 4).Select(i => new ProjectReferenceDocumentCounts.Document(
            Guid.NewGuid(), project, $"part-{i}.SLDPRT", DocumentKind.Part)).ToArray();
        var nodes = Enumerable.Range(0, 40).Select(i => Node($"part-{i}.SLDPRT", i < 4 ? documents[i].Id : null)).ToArray();
        var root = Node("root.SLDASM", kind: DocumentKind.Assembly, children: nodes);
        Assert.Equal((41, 41, 0), ProjectReferenceDocumentCounts.Count(root, project, documents, []));
    }

    [Fact]
    public void RepeatedInstancesAndUniqueFilenameResolutionCountOnce()
    {
        var project = Guid.NewGuid();
        var document = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "part.SLDPRT", DocumentKind.Part);
        var child = Node("part.SLDPRT", document.Id);
        var duplicatePath = child with { Children = [Node("discarded.SLDPRT")] };
        var root = Node("root.SLDASM", kind: DocumentKind.Assembly,
            children: [child, duplicatePath, Node("PART.sldprt"), Node("unknown.SLDPRT"), Node("UNKNOWN.sldprt")]);
        Assert.Equal((3, 3, 0), ProjectReferenceDocumentCounts.Count(root, project, [document], []));
    }

    [Fact]
    public void AmbiguousFilenameDoesNotSelectAnArbitraryDocument()
    {
        var project = Guid.NewGuid();
        var documents = Enumerable.Range(0, 2).Select(_ => new ProjectReferenceDocumentCounts.Document(
            Guid.NewGuid(), project, "same.SLDPRT", DocumentKind.Part)).ToArray();
        var root = Node("root.SLDASM", kind: DocumentKind.Assembly, children:
            [Node("same.SLDPRT"), Node("same.SLDPRT", documents[0].Id), Node("same.SLDPRT", documents[1].Id)]);
        Assert.Equal((4, 4, 0), ProjectReferenceDocumentCounts.Count(root, project, documents, []));
    }

    [Fact]
    public void IncludesRelatedAndProjectDrawingsWithoutDoubleCountingOrUnrelatedFamilyDrawings()
    {
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();
        var model = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "root.SLDASM", DocumentKind.Assembly);
        var drawing = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "root.SLDDRW", DocumentKind.Drawing);
        var related = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), other, "related.SLDDRW", DocumentKind.Drawing);
        var unrelated = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), other, "other.SLDDRW", DocumentKind.Drawing);
        var root = Node(model.FileName, model.Id, model.Kind, Node(drawing.FileName, drawing.Id, drawing.Kind));
        Assert.Equal((3, 1, 2), ProjectReferenceDocumentCounts.Count(root, project, [model, drawing, related, unrelated],
            [new(model.Id, drawing.Id), new(model.Id, related.Id)]));
    }

    [Fact]
    public void NoReferenceTreeIncludesStandaloneModelsAndIndependentDrawings()
    {
        var project = Guid.NewGuid();
        var model = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "orphan.SLDPRT", DocumentKind.Part);
        var drawing = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "drawing.SLDDRW", DocumentKind.Drawing);
        Assert.Equal((1, 1, 0), ProjectReferenceDocumentCounts.Count(null, project, [model], []));
        Assert.Equal((2, 1, 1), ProjectReferenceDocumentCounts.Count(null, project, [model, drawing], []));
    }

    [Fact]
    public void IncludesStandaloneModelsBesideReferenceTreeWithoutCountingOtherProjectModels()
    {
        var project = Guid.NewGuid();
        var rootModel = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "root.SLDASM", DocumentKind.Assembly);
        var standalone = new ProjectReferenceDocumentCounts.Document(Guid.NewGuid(), project, "standalone.SLDPRT", DocumentKind.Part);
        var other = standalone with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), FileName = "other.SLDPRT" };
        Assert.Equal((2, 2, 0), ProjectReferenceDocumentCounts.Count(Node(rootModel.FileName, rootModel.Id, rootModel.Kind), project, [rootModel, standalone, other], []));
    }

    [Fact]
    public async Task ProjectSummaryExcludesRecycledAndPurgedDrawingsAndRestoresLiveCounts()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Upton.Pdm.Application.CreateProjectCommand("COUNT-TEST", "计数测试", "admin", "unused", "unused"), "admin", default);
        var drawing = await repository.RegisterDocumentAsync(new Upton.Pdm.Application.RegisterDocumentCommand(project.Id, "COUNT-001", "独立工程图", "COUNT-001.SLDDRW", DocumentKind.Drawing), "admin", default);
        async Task<int?> DrawingCount() => (await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default)).Single(item => item.Id == project.Id).DrawingDocumentCount;
        Assert.Equal(1, await DrawingCount());
        var now = DateTimeOffset.UtcNow;
        var deleted = await repository.SetDocumentDeletedAsync(drawing.Id, true, drawing.RowVersion, "admin", "清理重复图纸", now, default);
        Assert.Equal(0, await DrawingCount());
        var restored = await repository.SetDocumentDeletedAsync(drawing.Id, false, deleted.RowVersion, "admin", "恢复", now, default);
        Assert.Equal(1, await DrawingCount());
        await repository.SetDocumentDeletedAsync(drawing.Id, true, restored.RowVersion, "admin", "清理", now, default);
        await repository.PurgeExpiredDeletedDocumentsAsync(now.AddDays(1), now.AddDays(2), default);
        Assert.Equal(0, await DrawingCount());
    }
}
