using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class ReferenceTreeDiffTests
{
    [Fact]
    public void Compare_ReportsReplacementConfigurationStatusAndAddition()
    {
        var before = Node("ROOT", "ROOT.SLDASM", "默认", ReferenceNodeStatus.Normal,
            Node("ROOT/P1", "P1.SLDPRT", "默认", ReferenceNodeStatus.Normal));
        var after = Node("ROOT", "ROOT.SLDASM", "默认", ReferenceNodeStatus.Normal,
            Node("ROOT/P1", "P2.SLDPRT", "加工", ReferenceNodeStatus.Suppressed),
            Node("ROOT/P3", "P3.SLDPRT", "默认", ReferenceNodeStatus.Normal));

        var changes = ReferenceTreeDiff.Compare(before, after);

        Assert.Contains(changes, change => change.Kind == ReferenceChangeKind.Replaced && change.InstancePath == "ROOT/P1");
        Assert.Contains(changes, change => change.Kind == ReferenceChangeKind.ConfigurationChanged && change.InstancePath == "ROOT/P1");
        Assert.Contains(changes, change => change.Kind == ReferenceChangeKind.StatusChanged && change.InstancePath == "ROOT/P1");
        Assert.Contains(changes, change => change.Kind == ReferenceChangeKind.Added && change.InstancePath == "ROOT/P3");
    }

    [Fact]
    public void MissingDescendant_BlocksSnapshot()
    {
        var root = Node("ROOT", "ROOT.SLDASM", "默认", ReferenceNodeStatus.Normal,
            Node("ROOT/MISSING", "MISSING.SLDPRT", "默认", ReferenceNodeStatus.Missing));

        Assert.True(root.HasBlockingIssue);
    }

    private static DocumentReferenceNode Node(
        string instancePath,
        string fileName,
        string configuration,
        ReferenceNodeStatus status,
        params DocumentReferenceNode[] children) =>
        new(
            Guid.NewGuid(),
            null,
            instancePath,
            fileName,
            fileName,
            DocumentKind.Part,
            configuration,
            1,
            status,
            null,
            null,
            children);
}
