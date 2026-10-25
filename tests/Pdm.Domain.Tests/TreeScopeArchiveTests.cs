using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class TreeScopeArchiveTests
{
    [Fact]
    public void WorkingTreePreservesManagedDesignDependenciesAndSuppressedEmbeddedState()
    {
        var source = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "ROOT/依赖/参数.txt", "参数.txt", "方程",
            DocumentKind.Other, "", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, [])
            { Role = ReferenceRole.ExternalFileDependency };
        var embedded = source with { DocumentId = null, InstancePath = "ROOT/EMBEDDED", FileName = "EMBEDDED.SLDASM",
            Kind = DocumentKind.Assembly, Role = ReferenceRole.Component, Status = ReferenceNodeStatus.Virtual,
            IsSuppressed = true, Revision = null };
        var root = source with { DocumentId = Guid.NewGuid(), InstancePath = "ROOT", FileName = "ROOT.SLDASM",
            Kind = DocumentKind.Assembly, Role = ReferenceRole.Component, IncludesDesignDependencies = true,
            Children = [embedded, source] };
        var resolved = WorkingReferenceTree.Resolve(root, new Dictionary<Guid, DocumentReferenceNode> { [root.DocumentId!.Value] = root });
        Assert.Equal(source, Assert.Single(resolved.Children, child => child.Role == ReferenceRole.ExternalFileDependency));
        Assert.True(Assert.Single(resolved.Children, child => child.Status == ReferenceNodeStatus.Virtual).IsSuppressed);
        Assert.False(ArchiveSafety.SameReferences(root, root with { Children = [embedded with { IsSuppressed = false }, source] }));
        Assert.Contains(ReferenceTreeDiff.Compare(root, root with { Children = [embedded with { IsSuppressed = false }, source] }),
            change => change.Kind == ReferenceChangeKind.StatusChanged && change.PreviousValue == "嵌入，压缩" && change.CurrentValue == "嵌入，未压缩");
    }

    [Fact]
    public void ParentOwnChangeComparisonIgnoresChildVersionsAndOpeningConfigurationButRetainsDirectEdits()
    {
        var child = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "ROOT/CHILD", "CHILD.SLDPRT", "子件",
            DocumentKind.Part, "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var parent = child with { DocumentId = Guid.NewGuid(), InstancePath = "ROOT", FileName = "ROOT.SLDASM",
            Kind = DocumentKind.Assembly, Children = [child] };
        Assert.True(ArchiveSafety.SameOwnDesignReferences(parent, parent with { Children = [child with { Revision = new(null, 2, false) }] }));
        Assert.True(ArchiveSafety.SameOwnDesignReferences(parent, parent with { Configuration = "另一已保存配置", Children = [] }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(parent, parent with { Children = [child with { Quantity = 2 }] }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(parent, parent with { Children = [child with { Configuration = "另一子件配置" }] }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(parent, parent with { Children = [] }));
    }

    [Fact]
    public void ScopeMigrationIgnoresOnlyAuxiliaryRecordsWhileRealDesignChangesRemainSignificant()
    {
        var part = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "ROOT/PART", "PART.SLDPRT", "零件",
            DocumentKind.Part, "A", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var extra = part with { DocumentId = Guid.NewGuid(), InstancePath = "ROOT/native-file/CONTEXT", FileName = "CONTEXT.SLDASM",
            Kind = DocumentKind.Assembly, Role = ReferenceRole.ExternalFileDependency };
        var legacy = part with { DocumentId = Guid.NewGuid(), InstancePath = "ROOT", FileName = "ROOT.SLDASM", Kind = DocumentKind.Assembly, Children = [part, extra] };
        var current = legacy with { Children = [part], TreeScopeOnly = true };
        Assert.True(ArchiveSafety.SameReferences(legacy, current));
        Assert.Empty(ReferenceTreeDiff.Compare(legacy, current));
        Assert.False(ArchiveSafety.SameReferences(legacy, current with { TreeScopeOnly = false }));
        foreach (var changed in new[] { part with { Quantity = 2 }, part with { Configuration = "B" }, part with { Revision = new(null, 2, false) }, part with { Status = ReferenceNodeStatus.Suppressed } })
            Assert.False(ArchiveSafety.SameReferences(legacy, current with { Children = [changed] }));
        Assert.Equal(2, legacy.Children.Count);
        var mixedDrawing = legacy with { Kind = DocumentKind.Drawing, FileName = "DRAWING.SLDDRW", Children = [part, extra with { Role = ReferenceRole.Component }] };
        var scopedDrawing = mixedDrawing with { TreeScopeOnly = true, Children = [part] };
        Assert.True(ArchiveSafety.SameReferences(mixedDrawing, scopedDrawing));
        Assert.False(ArchiveSafety.SameReferences(mixedDrawing, scopedDrawing with { Children = [part with { Revision = new(null, 2, false) }] }));
    }

    [Fact]
    public async Task RemovingAuxiliaryManagementDoesNotCreateAVersionOrRewriteHistory()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("TREE-SCOPE", "树内存档", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var extra = await ArchiveBatchTests.Register(repository, project, "CONTEXT", DocumentKind.Assembly, session);
        foreach (var document in new[] { part, extra })
            await repository.CheckInVersionAsync(document.Id, "admin", session,
                ArchiveBatchTests.Commit(project, document, ArchiveBatchTests.Node(document), 'B'), default);
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var legacy = ArchiveBatchTests.Node(root) with { Children = [ArchiveBatchTests.Node(part), ArchiveBatchTests.Node(extra) with { Role = ReferenceRole.ExternalFileDependency }] };
        await repository.CheckInVersionAsync(root.Id, "admin", session, ArchiveBatchTests.Commit(project, root, legacy, 'A', true), default);
        var before = Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        root = await repository.CheckoutAsync(root.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(10), null, default);
        var scoped = legacy with { TreeScopeOnly = true, Children = [ArchiveBatchTests.Node(part)] };
        var commit = ArchiveBatchTests.Commit(project, root, scoped, 'A', true) with { ExpectedVersionId = before.Id };
        var result = await repository.CheckInVersionAsync(root.Id, "admin", session, commit, default);
        Assert.False(result.VersionCreated);
        Assert.Equal(before.Id, Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default)).Id);
        Assert.Equal(2, before.ReferenceSnapshot.Children.Count);
        Assert.Single(await repository.ListDocumentVersionsAsync(extra.Id, default));
    }

    [Fact]
    public async Task ScopedDrawingReusesUnchangedVersionButPreservesChangedFileContents()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("DRAWING-SCOPE", "工程图范围", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var outside = await ArchiveBatchTests.Register(repository, project, "OUTSIDE", DocumentKind.Part, session);
        foreach (var document in new[] { part, outside })
            await repository.CheckInVersionAsync(document.Id, "admin", session,
                ArchiveBatchTests.Commit(project, document, ArchiveBatchTests.Node(document), 'B'), default);
        var drawing = await ArchiveBatchTests.Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        var legacy = ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(part), ArchiveBatchTests.Node(outside)] };
        await repository.CheckInVersionAsync(drawing.Id, "admin", session, ArchiveBatchTests.Commit(project, drawing, legacy, 'A', true), default);
        var before = Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default));
        var scoped = legacy with { TreeScopeOnly = true, Children = [ArchiveBatchTests.Node(part)] };
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(10), null, default);
        var unchanged = ArchiveBatchTests.Commit(project, drawing, scoped, 'A', true) with { ExpectedVersionId = before.Id };
        var reused = await repository.CheckInVersionAsync(drawing.Id, "admin", session, unchanged, default);
        Assert.False(reused.VersionCreated);
        Assert.Equal(before.Id, Assert.Single(await repository.ListDocumentVersionsAsync(drawing.Id, default)).Id);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(10), null, default);
        var changed = ArchiveBatchTests.Commit(project, drawing, scoped, 'C', true) with { ExpectedVersionId = before.Id };
        var saved = await repository.CheckInVersionAsync(drawing.Id, "admin", session, changed, default);
        Assert.True(saved.VersionCreated);
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(drawing.Id, default)).Count);
        Assert.Equal(2, before.ReferenceSnapshot.Children.Count);
        Assert.Single(await repository.ListDocumentVersionsAsync(outside.Id, default));
    }
}
