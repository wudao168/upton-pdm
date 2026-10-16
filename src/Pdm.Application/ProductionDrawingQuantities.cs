using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionDrawingBomQuantity(
    string MaterialKey,
    IReadOnlyList<Guid> SourceBomItemIds,
    decimal SingleSetQuantity,
    int WholeSetMultiplier,
    decimal TotalQuantity,
    decimal PriorReleasedQuantity,
    decimal NetIssueQuantity);

/// <summary>与正式发布文件相同的物料、单位和提前发布口径；同一物料关联多张图纸时只计一次。</summary>
public static class ProductionDrawingQuantities
{
    public static IReadOnlyList<BomItem> FrozenItems(ReleasePackage package) =>
        (package.NonStandardBomVersionId.HasValue || package.NonStandardBomSnapshot.Count > 0
            ? package.NonStandardBomSnapshot
            : package.MechanicalBomSnapshot.Where(item => item.Kind is BomKind.NonStandard or BomKind.Mechanical))
        .Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval)
        .DistinctBy(item => item.Id)
        .OrderBy(item => item.Sequence)
        .ToArray();

    public static IReadOnlyList<ProductionDrawingBomQuantity> Build(ReleasePackage package, IEnumerable<ReleasePackage> history)
    {
        var items = FrozenItems(package);
        var multiplier = Math.Max(1, package.WholeSetMultiplier);
        var prior = BomReleaseAggregation.PriorQuantities(BomReleaseAggregation.PriorLongLeadItems(package, history), items);
        return items.GroupBy(BomReleaseAggregation.MaterialKey)
            .Select(group =>
            {
                var singleSet = group.Sum(item => item.Quantity);
                var total = singleSet * multiplier;
                var alreadyReleased = prior.GetValueOrDefault(group.Key);
                return new ProductionDrawingBomQuantity(group.Key, group.Select(item => item.Id).ToArray(),
                    singleSet, multiplier, total, alreadyReleased, Math.Max(0, total - alreadyReleased));
            }).ToArray();
    }
}
