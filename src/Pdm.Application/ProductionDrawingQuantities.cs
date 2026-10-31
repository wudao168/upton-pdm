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
        var historyItems = history.ToArray();
        var previous = package.Scope == ReleaseScope.NonStandardSupplement
            ? historyItems.Where(previous => previous.ProjectId == package.ProjectId && previous.Id != package.Id
                    && previous.State == ReleasePackageState.Published && previous.PublishedAt.HasValue
                    && (!package.PublishedAt.HasValue || previous.PublishedAt < package.PublishedAt)
                    && previous.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement)
                .OrderByDescending(previous => previous.PublishedAt).FirstOrDefault()
            : null;
        var priorItems = previous is null ? BomReleaseAggregation.PriorLongLeadItems(package, historyItems)
            : FrozenItems(previous).Select(item => item with { Quantity = item.Quantity * Math.Max(1, previous.WholeSetMultiplier) });
        var prior = BomReleaseAggregation.PriorQuantities(priorItems, items);
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
