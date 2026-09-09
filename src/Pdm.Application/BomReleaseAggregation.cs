using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record U9ProductionBomSummaryLine(
    string? ParentMaterialCode,
    string MaterialCode,
    string UnitCode,
    decimal Quantity,
    IReadOnlyList<Guid> SourceBomItemIds);

public sealed record U9PurchaseDemandSummaryLine(
    string MaterialCode,
    string UnitCode,
    decimal Quantity,
    IReadOnlyList<Guid> SourceBomItemIds);

public sealed record U9ReleaseBomSummary(
    IReadOnlyList<U9ProductionBomSummaryLine> ProductionStructure,
    IReadOnlyList<U9PurchaseDemandSummaryLine> PurchaseDemand,
    string Sha256);

public static class BomReleaseAggregation
{
    public static string MaterialKey(BomItem item) =>
        $"{item.DrawingNumber.Trim().ToUpperInvariant()}|{U9UnitCatalog.NormalizeBomUnit(item.Unit)}";

    public static IReadOnlyList<BomItem> PriorLongLeadItems(ReleasePackage package, IEnumerable<ReleasePackage> history) =>
        history.Where(previous => previous.ProjectId == package.ProjectId && previous.Id != package.Id
                && previous.Scope == ReleaseScope.StandardLongLead && previous.State == ReleasePackageState.Published
                && previous.PublishedAt.HasValue
                && (!package.PublishedAt.HasValue || previous.PublishedAt <= package.PublishedAt))
            .SelectMany(previous => previous.StandardBomSnapshot
                .Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval)
                .Select(item => item with { Quantity = item.Quantity * Math.Max(1, previous.WholeSetMultiplier) }))
            .ToArray();

    public static IReadOnlyDictionary<string, decimal> PriorQuantities(IEnumerable<BomItem> items) =>
        items.GroupBy(MaterialKey).ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

    public static U9ReleaseBomSummary Build(ReleasePackage package, IReadOnlyList<BomItem>? priorLongLeadItems = null)
    {
        var items = ReleasedItems(package)
            .Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval)
            .Where(item => !string.IsNullOrWhiteSpace(item.DrawingNumber) && item.Quantity > 0)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Select(item => item with { Quantity = item.Quantity * Math.Max(1, package.WholeSetMultiplier) })
            .ToArray();

        var production = items
            .GroupBy(item => new
            {
                ParentKey = string.IsNullOrWhiteSpace(item.ParentDrawingNumber)
                    ? $"unassigned:{item.Id:N}"
                    : item.ParentDrawingNumber.Trim().ToUpperInvariant(),
                MaterialCode = item.DrawingNumber.Trim().ToUpperInvariant(),
                UnitCode = U9UnitCatalog.NormalizeBomUnit(item.Unit)
            })
            .Select(group => new U9ProductionBomSummaryLine(
                group.First().ParentDrawingNumber?.Trim(),
                group.Key.MaterialCode,
                group.Key.UnitCode,
                group.Sum(item => item.Quantity),
                group.Select(item => item.Id).Order().ToArray()))
            .OrderBy(line => line.ParentMaterialCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.UnitCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var priorQuantities = package.Scope == ReleaseScope.StandardFormal
            ? PriorQuantities(priorLongLeadItems ?? [])
            : new Dictionary<string, decimal>();
        var purchase = items
            .GroupBy(item => new
            {
                MaterialCode = item.DrawingNumber.Trim().ToUpperInvariant(),
                UnitCode = U9UnitCatalog.NormalizeBomUnit(item.Unit)
            })
            .Select(group => new U9PurchaseDemandSummaryLine(
                group.Key.MaterialCode,
                group.Key.UnitCode,
                Math.Max(0, group.Sum(item => item.Quantity) - priorQuantities.GetValueOrDefault($"{group.Key.MaterialCode}|{group.Key.UnitCode}")),
                group.Select(item => item.Id).Order().ToArray()))
            .Where(line => line.Quantity > 0)
            .OrderBy(line => line.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.UnitCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var canonical = string.Join('\n', production.Select(line =>
                $"P|{line.ParentMaterialCode?.Trim().ToUpperInvariant()}|{line.MaterialCode}|{line.UnitCode}|{line.Quantity.ToString(CultureInfo.InvariantCulture)}|{string.Join(",", line.SourceBomItemIds)}")
            .Concat(purchase.Select(line =>
                $"D|{line.MaterialCode}|{line.UnitCode}|{line.Quantity.ToString(CultureInfo.InvariantCulture)}|{string.Join(",", line.SourceBomItemIds)}")));
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new U9ReleaseBomSummary(production, purchase, sha256);
    }

    private static IEnumerable<BomItem> ReleasedItems(ReleasePackage package)
    {
        if (package.Scope == ReleaseScope.StandardLongLead) return package.StandardBomSnapshot;
        if (package.StandardBomSnapshot.Count + package.NonStandardBomSnapshot.Count + package.ElectricalBomSnapshot.Count > 0)
            return package.StandardBomSnapshot.Concat(package.NonStandardBomSnapshot).Concat(package.ElectricalBomSnapshot);
        return package.MechanicalBomSnapshot.Concat(package.ElectricalBomSnapshot);
    }
}
