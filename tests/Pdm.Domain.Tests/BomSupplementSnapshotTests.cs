using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public class BomSupplementSnapshotTests
{
    [Fact]
    public void NewKitInstanceBeforeExistingInstanceDoesNotConsumeItsPublishedQuantity()
    {
        var old = Item("01020135090", 1) with { EngineeringKitReferenceId = Guid.NewGuid() };
        var added = Item(old.DrawingNumber, 5) with { EngineeringKitReferenceId = Guid.NewGuid() };
        var snapshot = BomSupplementSnapshot.Merge([added, old], [added], [old]);
        Assert.Equal(6, snapshot.Sum(item => item.Quantity));
        Assert.Contains(old, snapshot);
        Assert.Contains(added, snapshot);
        Assert.Equal(added.Id, Assert.Single(BomSupplementSnapshot.ChangedItems([added, old], [old])).Id);
    }

    [Fact]
    public void UnselectedExistingChangeRetainsItsOwnBaselineRegardlessOfRowOrder()
    {
        var old = Item("SAME", 1);
        var added = Item(old.DrawingNumber, 5);
        var current = old with { Quantity = 3 };
        var snapshot = BomSupplementSnapshot.Merge([added, current], [added], [old]);
        Assert.Equal(6, snapshot.Sum(item => item.Quantity));
        Assert.Equal(1, snapshot.Single(item => item.Id == old.Id).Quantity);
        Assert.Equal(2, BomSupplementSnapshot.ChangedItems([added, current], [old]).Count);
    }

    [Fact]
    public void ReplacingKitReferenceDoesNotSilentlyDeleteUnconfirmedPublishedInstance()
    {
        var old = Item("SAME", 1) with { EngineeringKitReferenceId = Guid.NewGuid() };
        var added = Item("SAME", 5) with { EngineeringKitReferenceId = Guid.NewGuid() };
        Assert.Equal(6, BomSupplementSnapshot.Merge([added], [added], [old]).Sum(item => item.Quantity));
        Assert.Equal(5, BomSupplementSnapshot.Merge([added], [added], [old], [old.Id]).Sum(item => item.Quantity));
    }

    [Fact]
    public void FormalRevisionPromotionAndResequencingAreNotNewMaterialChanges()
    {
        var old = Item("SAME", 1);
        Assert.Empty(BomSupplementSnapshot.ChangedItems([old with { Sequence = 5, Revision = "A" }], [old]));
        Assert.Single(BomSupplementSnapshot.ChangedItems([old with { Quantity = 2 }], [old]));
    }

    private static BomItem Item(string code, decimal quantity) =>
        new(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, code, code, quantity, "001", null, "M1", "W1", true);

    [Fact]
    public void ExcludingAnExistingItemDoesNotDeleteOrChangeItsPublishedQuantity()
    {
        var old = Item("OLD",1);
        var excluded = old with { IsReleaseExcluded = true, Quantity = 9 };
        var added = Item("NEW",2);
        var snapshot = BomSupplementSnapshot.Merge([excluded,added],[added],[old]);
        Assert.Equal(old,snapshot.Single(item=>item.Id==old.Id));
        Assert.Equal(3,snapshot.Sum(item=>item.Quantity));
    }

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
