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
    public static U9ReleaseBomSummary Build(ReleasePackage package)
    {
        var items = ReleasedItems(package)
            .Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval)
            .Where(item => !string.IsNullOrWhiteSpace(item.DrawingNumber) && item.Quantity > 0)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
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

        var purchase = items
            .GroupBy(item => new
            {
                MaterialCode = item.DrawingNumber.Trim().ToUpperInvariant(),
                UnitCode = U9UnitCatalog.NormalizeBomUnit(item.Unit)
            })
            .Select(group => new U9PurchaseDemandSummaryLine(
                group.Key.MaterialCode,
                group.Key.UnitCode,
                group.Sum(item => item.Quantity),
                group.Select(item => item.Id).Order().ToArray()))
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
