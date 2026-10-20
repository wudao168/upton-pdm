using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public class BomSupplementSnapshotTests
{
    [Fact]
    public void PartialSupplementRetainsUnselectedFormalValuesAndOnlyRemovesActualDeletions()
    {
        var project = Guid.NewGuid();
        BomItem Item(string code) => new(Guid.NewGuid(), project, BomKind.NonStandard, 1, code, code, 1, "个", "钢", code, "W1", true);
        var unchanged = Item("OLD");
        var deferred = Item("LATER");
        var removed = Item("REMOVED");
        var added = Item("NEW");
        var unselectedNew = Item("NEXT");
        var baseline = new[] { unchanged, deferred, removed };
        var current = new[] { unchanged with { Revision = "A" }, deferred with { Quantity = 2 }, added, unselectedNew };
        var preserved = BomSupplementSnapshot.Merge(current, [added], baseline);
        Assert.Contains(removed, preserved);
        var snapshot = BomSupplementSnapshot.Merge(current, [added], baseline, [removed.Id]);
        Assert.Equal(3, snapshot.Length);
        Assert.Contains(unchanged, snapshot);
        Assert.Contains(deferred, snapshot);
        Assert.Contains(added, snapshot);
        Assert.DoesNotContain(snapshot, item => item.Id == removed.Id || item.Id == unselectedNew.Id);
    }
}
