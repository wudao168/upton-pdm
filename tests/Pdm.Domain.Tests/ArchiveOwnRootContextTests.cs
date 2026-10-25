using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveOwnRootContextTests
{
    [Theory]
    [InlineData(DocumentKind.Part, ReferenceNodeStatus.Suppressed)]
    [InlineData(DocumentKind.Assembly, ReferenceNodeStatus.Suppressed)]
    [InlineData(DocumentKind.Part, ReferenceNodeStatus.Hidden)]
    [InlineData(DocumentKind.Assembly, ReferenceNodeStatus.Hidden)]
    [InlineData(DocumentKind.Part, ReferenceNodeStatus.Lightweight)]
    [InlineData(DocumentKind.Assembly, ReferenceNodeStatus.Lightweight)]
    public void SelectingAnotherOccurrenceDoesNotChangeTheIndependentFile(DocumentKind kind, ReferenceNodeStatus status)
    {
        var original = Node(kind);
        var selected = original with { NodeId = Guid.NewGuid(), InstancePath = "other-parent/instance-7", Quantity = 7,
            Status = status, IsSuppressed = status == ReferenceNodeStatus.Suppressed,
            Configuration = kind == DocumentKind.Part ? "另一配置" : original.Configuration };
        Assert.True(ArchiveSafety.SameOwnDesignReferences(original, selected));
        Assert.True(ArchiveSafety.SameOwnDesignReferences(selected, original));
        Assert.Equal(status, selected.Status); // Comparison never mutates the stored tree.
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("quantity")]
    [InlineData("suppressed-status")]
    [InlineData("suppressed-flag")]
    [InlineData("hidden-status")]
    [InlineData("identity")]
    [InlineData("pending")]
    public void ActualChildOccurrenceChangesRemainDifferent(string change)
    {
        var child = Node(DocumentKind.Part);
        var assembly = Node(DocumentKind.Assembly) with { Children = [child] };
        var changed = change switch
        {
            "configuration" => child with { Configuration = "其他配置" },
            "quantity" => child with { Quantity = 2 },
            "suppressed-status" => child with { Status = ReferenceNodeStatus.Suppressed },
            "suppressed-flag" => child with { IsSuppressed = true },
            "hidden-status" => child with { Status = ReferenceNodeStatus.Hidden },
            "identity" => child with { DocumentId = Guid.NewGuid() },
            _ => child with { StructureIndexPending = true }
        };
        Assert.False(ArchiveSafety.SameOwnDesignReferences(assembly, assembly with { Children = [changed] }));
    }

    [Theory]
    [InlineData(DocumentKind.Part, ReferenceNodeStatus.Missing)]
    [InlineData(DocumentKind.Part, ReferenceNodeStatus.Virtual)]
    [InlineData(DocumentKind.Assembly, ReferenceNodeStatus.Missing)]
    [InlineData(DocumentKind.Assembly, ReferenceNodeStatus.Virtual)]
    public void RootMissingAndVirtualStatesAreNotNormalizedAway(DocumentKind kind, ReferenceNodeStatus status)
    {
        var original = Node(kind);
        Assert.False(ArchiveSafety.SameOwnDesignReferences(original, original with { Status = status }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(original, original with { StructureIndexPending = true }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(original, original with { DocumentId = Guid.NewGuid() }));
    }

    [Fact]
    public void DrawingViewModelConfigurationIsStillAnActualReferenceChange()
    {
        var model = Node(DocumentKind.Part);
        var drawing = Node(DocumentKind.Drawing) with { Children = [model] };
        Assert.False(ArchiveSafety.SameOwnDesignReferences(drawing, drawing with { Children = [model with { Configuration = "其他配置" }] }));
    }

    [Theory]
    [InlineData(DocumentKind.Part)]
    [InlineData(DocumentKind.Assembly)]
    [InlineData(DocumentKind.Drawing)]
    public void InventoryVerificationFlagAloneDoesNotCreateAFileVersion(DocumentKind kind)
    {
        var original = Node(kind);
        Assert.True(ArchiveSafety.SameOwnDesignReferences(original, original with { UsesNativeFileInventory = true }));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(original, original with { UsesNativeFileInventory = true, StructureIndexPending = true }));
    }

    [Fact]
    public void VerifiedFileClosureAdditionAndRemovalRemainChangesWhileLegacyScopeIsNotProofOfRemoval()
    {
        var original = Node(DocumentKind.Assembly);
        var companion = Node(DocumentKind.Other) with { FileName = "SOURCE.step", Role = ReferenceRole.ExternalFileDependency };
        var verified = original with { UsesNativeFileInventory = true, Children = [companion] };
        Assert.False(ArchiveSafety.SameOwnDesignReferences(original, verified));
        Assert.False(ArchiveSafety.SameOwnDesignReferences(verified, verified with { Children = [] }));
        Assert.True(ArchiveSafety.SameOwnDesignReferences(verified, original));
    }

    private static DocumentReferenceNode Node(DocumentKind kind) => new(Guid.NewGuid(), Guid.NewGuid(), "parent/instance-1",
        kind == DocumentKind.Assembly ? "ASSEMBLY.SLDASM" : kind == DocumentKind.Drawing ? "DRAWING.SLDDRW" : "PART.SLDPRT",
        "测试图档", kind, "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
}
