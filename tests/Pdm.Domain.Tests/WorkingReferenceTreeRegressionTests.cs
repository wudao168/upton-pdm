using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class WorkingReferenceTreeRegressionTests
{
    [Fact]
    public void SameRevisionKeepsCapturedOccurrencesInsteadOfReintroducingLegacyParentDependency()
    {
        var root = Node("ROOT", RevisionLabel.Released('A'));
        var part = Node("PART", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var child = Node("CHILD", RevisionLabel.Released('A')) with { Children = [part] };
        root = root with { Children = [child] };
        var legacy = child with { Children = [part, root with { Configuration = "", Children = [] }] };

        var result = WorkingReferenceTree.Resolve(root, new Dictionary<Guid, DocumentReferenceNode>
        {
            [root.DocumentId!.Value] = root, [child.DocumentId!.Value] = legacy,
            [part.DocumentId!.Value] = part with { Revision = part.Revision!.Value.NextWork() }
        });

        var resolvedPart = Assert.Single(Assert.Single(result.Children).Children);
        Assert.Equal(part.DocumentId, resolvedPart.DocumentId);
        Assert.Equal("W2", resolvedPart.Revision!.Value.Display);
        Assert.Equal(2, legacy.Children.Count);
        Assert.Single(child.Children);
    }

    [Fact]
    public void NewRevisionUsesNewChildrenAndKeepsBothSiblingOccurrences()
    {
        var child = Node("CHILD", RevisionLabel.InitialWork());
        var added = Node("NEW", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var latest = child with { Revision = child.Revision!.Value.NextWork(), Children = [added] };
        var root = Node("ROOT", RevisionLabel.InitialWork()) with
        {
            Children = [child with { InstancePath = "ROOT/CHILD-1" }, child with { InstancePath = "ROOT/CHILD-2" }]
        };

        var result = WorkingReferenceTree.Resolve(root, new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = latest });

        Assert.Equal(2, result.Children.Count);
        Assert.All(result.Children, item => { Assert.Equal("W2", item.Revision!.Value.Display); Assert.Equal(added.DocumentId, Assert.Single(item.Children).DocumentId); });
        Assert.NotEqual(result.Children[0].Children[0].InstancePath, result.Children[1].Children[0].InstancePath);
    }

    [Fact]
    public void ActualCycleInNewRevisionIsStillRejected()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var child = Node("CHILD", RevisionLabel.InitialWork());
        root = root with { Children = [child] };
        var latest = child with { Revision = child.Revision!.Value.NextWork(), Children = [root with { Children = [] }] };
        Assert.Throws<InvalidOperationException>(() => WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = latest }));
    }

    [Fact]
    public void SameRevisionPendingMetadataStillRefreshesWithoutReplacingStructure()
    {
        var child = Node("CHILD", RevisionLabel.InitialWork());
        var root = Node("ROOT", RevisionLabel.InitialWork()) with { Children = [child] };
        var pending = root with { StructureIndexPending = true };
        Assert.True(WorkingReferenceTree.Resolve(root, new Dictionary<Guid, DocumentReferenceNode> { [root.DocumentId!.Value] = pending }).StructureIndexPending);
        Assert.False(WorkingReferenceTree.Resolve(pending, new Dictionary<Guid, DocumentReferenceNode> { [root.DocumentId!.Value] = root }).StructureIndexPending);
    }

    [Fact]
    public void CompletedIndexAtSameRevisionSuppliesItsNewlyVerifiedChildren()
    {
        var pending = Node("ROOT", RevisionLabel.InitialWork()) with { StructureIndexPending = true };
        var child = Node("CHILD", RevisionLabel.InitialWork());
        var complete = pending with { StructureIndexPending = false, Children = [child] };
        var result = WorkingReferenceTree.Resolve(pending, new Dictionary<Guid, DocumentReferenceNode> { [pending.DocumentId!.Value] = complete });
        Assert.False(result.StructureIndexPending);
        Assert.Equal(child.DocumentId, Assert.Single(result.Children).DocumentId);
    }

    [Fact]
    public void ActualCycleAlreadyPresentInCapturedOccurrencesIsStillRejected()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var child = Node("CHILD", RevisionLabel.InitialWork()) with { Children = [root] };
        root = root with { Children = [child] };
        Assert.Throws<InvalidOperationException>(() => WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [root.DocumentId!.Value] = root }));
    }

    [Fact]
    public void SameRevisionValidIndexEnrichmentIsPreserved()
    {
        var child = Node("CHILD", RevisionLabel.InitialWork());
        var part = Node("PART", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var root = Node("ROOT", RevisionLabel.InitialWork()) with { Children = [child] };
        var completeChild = child with { Children = [part] };
        var result = WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = completeChild });
        Assert.Equal(part.DocumentId, Assert.Single(Assert.Single(result.Children).Children).DocumentId);
    }

    [Fact]
    public void VerifiedNativeCycleAtSameRevisionIsStillRejected()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var child = Node("CHILD", RevisionLabel.InitialWork());
        root = root with { Children = [child] };
        var invalid = child with { UsesNativeFileInventory = true, Children = [root with { Children = [] }] };
        Assert.Throws<InvalidOperationException>(() => WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = invalid }));
    }

    [Fact]
    public void LegacyParentContextDoesNotDiscardOtherValidIndexEnrichment()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var oldPart = Node("OLD", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var addedPart = Node("FOUND", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var child = Node("CHILD", RevisionLabel.InitialWork()) with { Children = [oldPart] };
        root = root with { Children = [child] };
        var legacy = child with { Children = [oldPart, root with { Configuration = "", Children = [] }, addedPart] };
        var result = WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = legacy });
        Assert.Equal([oldPart.DocumentId, addedPart.DocumentId], Assert.Single(result.Children).Children.Select(node => node.DocumentId));
    }

    [Fact]
    public void LegacySelfReferenceIsNeverTreatedAsParentContext()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var invalid = root with { Children = [root with { Configuration = "" }] };
        Assert.Throws<InvalidOperationException>(() => WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [root.DocumentId!.Value] = invalid }));
    }

    [Fact]
    public void ParentOverlayCannotReintroduceBareAncestorContextThroughAnOlderOccurrence()
    {
        var root = Node("ROOT", RevisionLabel.Released('A').NextWork());
        var part = Node("PART", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var added = Node("FOUND", RevisionLabel.InitialWork()) with { Kind = DocumentKind.Part };
        var child = Node("CHILD", RevisionLabel.Released('A')) with { Children = [part] };
        var parent = Node("PARENT", RevisionLabel.Released('A')) with { Children = [child] };
        root = root with { Children = [parent] };
        var context = root with { Configuration = "", InstancePath = "PARENT/CHILD/ROOT", Children = [] };
        var oldOccurrence = child with { Revision = RevisionLabel.InitialWork(), Children = [part, context] };
        var result = WorkingReferenceTree.Resolve(root, new Dictionary<Guid, DocumentReferenceNode>
        {
            [parent.DocumentId!.Value] = parent with { Children = [oldOccurrence] },
            [child.DocumentId!.Value] = child with { Children = [part, context, added] }
        });

        var resolved = Assert.Single(Assert.Single(result.Children).Children);
        Assert.Equal(child.Revision, resolved.Revision);
        Assert.Equal([part.DocumentId, added.DocumentId], resolved.Children.Select(node => node.DocumentId));
        Assert.Equal(2, oldOccurrence.Children.Count);
    }

    [Theory]
    [InlineData(false, "Default", "CHILD/ROOT")]
    [InlineData(false, "", "CHILD/ROOT-1")]
    [InlineData(true, "", "CHILD/ROOT")]
    public void RealAncestorCyclesDoNotMatchLegacyBareUnconfiguredContext(bool native, string configuration, string instancePath)
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var child = Node("CHILD", RevisionLabel.InitialWork());
        root = root with { Children = [child] };
        var edge = root with { Configuration = configuration, InstancePath = instancePath, Children = [] };
        var invalid = child with { UsesNativeFileInventory = native, Children = [edge] };
        Assert.Throws<InvalidOperationException>(() => WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = invalid }));
    }

    [Fact]
    public void NonComponentAncestorDependenciesArePreserved()
    {
        var root = Node("ROOT", RevisionLabel.InitialWork());
        var child = Node("CHILD", RevisionLabel.InitialWork());
        root = root with { Children = [child] };
        var dependency = root with { Configuration = "", Role = ReferenceRole.ExternalFileDependency, Children = [] };
        var result = WorkingReferenceTree.Resolve(root,
            new Dictionary<Guid, DocumentReferenceNode> { [child.DocumentId!.Value] = child with { Children = [dependency] } });
        var resolved = Assert.Single(Assert.Single(result.Children).Children);
        Assert.Equal(dependency.DocumentId, resolved.DocumentId);
        Assert.Equal(ReferenceRole.ExternalFileDependency, resolved.Role);
        Assert.Equal("ROOT", dependency.InstancePath);
    }

    private static DocumentReferenceNode Node(string name, RevisionLabel revision) => new(Guid.NewGuid(), Guid.NewGuid(),
        name, name + ".SLDASM", name, DocumentKind.Assembly, "Default", 1, ReferenceNodeStatus.Normal, revision, null, []);
}
