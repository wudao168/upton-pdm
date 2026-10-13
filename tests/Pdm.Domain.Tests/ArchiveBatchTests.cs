using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveBatchTests
{
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
