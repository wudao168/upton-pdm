using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ReleaseQuantityLine(string MaterialKey, string MaterialCode, string Name, string UnitCode,
    IReadOnlyList<Guid> SourceBomItemIds, decimal FrozenBaseQuantity, int Multiplier,
    decimal PreviousBaselineQuantity, decimal EarlyReleasedCredit, decimal TargetQuantity)
{
    public BomKind Kind { get; init; }
    public decimal BaselineChange => TargetQuantity - PreviousBaselineQuantity;
    public decimal DemandChange => BaselineChange - EarlyReleasedCredit;
    public decimal NewDemand => Math.Max(0,DemandChange);
    public decimal ReducedDemand => Math.Max(0,-DemandChange);
}

/// <summary>发布需求的数量对照，不是库存实发或退料台账。提前量仅在物料首次纳入正式基线时抵扣。</summary>
public static class ReleaseQuantityComparison
{
    public static IReadOnlyList<BomItem> FrozenItems(ReleasePackage package, BomKind kind)
    {
        var items = kind switch
        {
            BomKind.Standard => package.StandardBomSnapshot,
            BomKind.NonStandard => package.NonStandardBomSnapshot,
            _ => package.ElectricalBomSnapshot
        };
        var versionId = kind switch
        {
            BomKind.Standard => package.StandardBomVersionId,
            BomKind.NonStandard => package.NonStandardBomVersionId,
            _ => package.ElectricalBomVersionId
        };
        if(items.Count==0 && !versionId.HasValue && kind!=BomKind.Electrical)
            items=package.MechanicalBomSnapshot.Where(item=>kind==BomKind.Standard?item.Kind==BomKind.Standard:item.Kind is BomKind.NonStandard or BomKind.Mechanical).ToArray();
        return items.Where(item=>!item.IsManuallyExcluded&&!item.IsReleaseExcluded&&!item.IsPendingRemoval)
            .DistinctBy(item=>item.Id).ToArray();
    }

    public static IReadOnlyList<ReleaseQuantityLine> Build(ReleasePackage package, BomKind kind, IEnumerable<ReleasePackage> history)
    {
        var current=FrozenItems(package,kind);
        var multiplier=Math.Max(1,package.WholeSetMultiplier);
        var normalScopes=kind switch
        {
            BomKind.Standard => new[]{ReleaseScope.StandardFormal,ReleaseScope.StandardSupplement},
            BomKind.NonStandard => new[]{ReleaseScope.NonStandardWithDrawing,ReleaseScope.NonStandardSupplement},
            _ => new[]{ReleaseScope.ElectricalFormal,ReleaseScope.ElectricalSupplement}
        };
        var earlyScope=kind switch
        {
            BomKind.Standard => ReleaseScope.StandardLongLead,
            BomKind.NonStandard => ReleaseScope.NonStandardLongLead,
            _ => ReleaseScope.ElectricalLongLead
        };
        var packages=history.Where(previous=>previous.ProjectId==package.ProjectId && previous.Id!=package.Id
            && previous.State==ReleasePackageState.Published && previous.PublishedAt.HasValue
            && (!package.RootDocumentId.HasValue || !previous.RootDocumentId.HasValue || previous.RootDocumentId==package.RootDocumentId)
            && Precedes(previous,package)).ToArray();
        var baselines=packages.Where(previous=>previous.Scope==ReleaseScope.LegacyCombined || normalScopes.Contains(previous.Scope))
            .OrderByDescending(previous=>previous.PublishedAt).ThenByDescending(previous=>previous.CreatedAt)
            .ThenByDescending(previous=>previous.Number,StringComparer.Ordinal).ToArray();
        var isSupplement=package.Scope==normalScopes[1];
        var previousItems=isSupplement && baselines.Length>0 ? Scale(FrozenItems(baselines[0],kind),baselines[0].WholeSetMultiplier) : [];
        var previousTotals=BomReleaseAggregation.PriorQuantities(previousItems,current);
        var covered=isSupplement ? BomReleaseAggregation.PriorQuantities(baselines.SelectMany(previous=>FrozenItems(previous,kind)),current).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var early=package.Scope==normalScopes[0] || isSupplement && baselines.Length>0 ? packages.Where(previous=>previous.Scope==earlyScope)
            .SelectMany(previous=>Scale(FrozenItems(previous,kind),previous.WholeSetMultiplier)).ToArray() : [];
        var earlyTotals=BomReleaseAggregation.PriorQuantities(early,current);
        var groups=current.GroupBy(BomReleaseAggregation.MaterialKey).ToDictionary(group=>group.Key,group=>group.ToArray());
        var oldGroups=previousItems.GroupBy(BomReleaseAggregation.MaterialKey).ToDictionary(group=>group.Key,group=>group.ToArray());
        return groups.Keys.Union(previousTotals.Keys,StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(key=>
            {
                var members=groups.GetValueOrDefault(key)??[];
                var first=members.FirstOrDefault()??oldGroups.GetValueOrDefault(key)?.FirstOrDefault()
                    ??previousItems.First(item=>BomReleaseAggregation.MaterialKey(item)==key);
                var quantity=members.Sum(item=>item.Quantity);
                var credit=members.Length>0 && !covered.Contains(key) ? earlyTotals.GetValueOrDefault(key) : 0;
                return new ReleaseQuantityLine(key,first.DrawingNumber,first.Name,U9UnitCatalog.NormalizeBomUnit(first.Unit),
                    (members.Length>0?members:oldGroups.GetValueOrDefault(key)??[]).Select(item=>item.Id).ToArray(),quantity,multiplier,previousTotals.GetValueOrDefault(key),credit,quantity*multiplier){Kind=kind};
            }).ToArray();
    }

    private static BomItem[] Scale(IEnumerable<BomItem> items,int multiplier)=>items.Select(item=>item with{Quantity=item.Quantity*Math.Max(1,multiplier)}).ToArray();
    private static bool Precedes(ReleasePackage previous,ReleasePackage package)
    {
        if(!package.PublishedAt.HasValue)return true;
        var time=previous.PublishedAt!.Value.CompareTo(package.PublishedAt.Value);
        return time<0 || time==0 && (previous.CreatedAt<package.CreatedAt
            || previous.CreatedAt==package.CreatedAt && StringComparer.Ordinal.Compare(previous.Number,package.Number)<0);
    }
}
