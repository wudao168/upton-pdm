namespace Upton.Pdm.Domain;

public sealed record DocumentVersion(
    Guid Id,
    Guid DocumentId,
    RevisionLabel Revision,
    DocumentVersionStatus Status,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string ChangeNote,
    IReadOnlyDictionary<string, string?> PropertySnapshot,
    DocumentReferenceNode ReferenceSnapshot,
    IReadOnlyList<BomItem> MechanicalBomSnapshot,
    IReadOnlyList<BomItem> ElectricalBomSnapshot,
    Guid? SourceVersionId,
    string? SourceDescription,
    Guid? ApprovalTaskId,
    Guid? ReleasePackageId);

public sealed record PropertySnapshotChange(
    SnapshotChangeKind Kind,
    string Name,
    string? PreviousValue,
    string? CurrentValue);

public sealed record BomSnapshotChange(
    BomKind BomKind,
    BomChangeKind Kind,
    string DrawingNumber,
    string Field,
    string? PreviousValue,
    string? CurrentValue);

public sealed record DocumentVersionComparison(
    Guid DocumentId,
    DocumentVersion Left,
    DocumentVersion Right,
    IReadOnlyList<PropertySnapshotChange> PropertyChanges,
    IReadOnlyList<ReferenceTreeChange> ReferenceChanges,
    IReadOnlyList<BomSnapshotChange> BomChanges);

public static class DocumentVersionDiff
{
    public static DocumentVersionComparison Compare(DocumentVersion left, DocumentVersion right)
    {
        if (left.DocumentId != right.DocumentId)
        {
            throw new ArgumentException("只能比较同一图档的两个版本。");
        }

        return new DocumentVersionComparison(
            left.DocumentId,
            left,
            right,
            CompareProperties(left.PropertySnapshot, right.PropertySnapshot),
            ReferenceTreeDiff.Compare(left.ReferenceSnapshot, right.ReferenceSnapshot),
            CompareBom(left.MechanicalBomSnapshot, right.MechanicalBomSnapshot, BomKind.Mechanical)
                .Concat(CompareBom(left.ElectricalBomSnapshot, right.ElectricalBomSnapshot, BomKind.Electrical))
                .ToArray());
    }

    private static IReadOnlyList<PropertySnapshotChange> CompareProperties(
        IReadOnlyDictionary<string, string?> previous,
        IReadOnlyDictionary<string, string?> current)
    {
        var names = previous.Keys.Concat(current.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        var changes = new List<PropertySnapshotChange>();
        foreach (var name in names)
        {
            var hadPrevious = previous.TryGetValue(name, out var previousValue);
            var hasCurrent = current.TryGetValue(name, out var currentValue);
            if (!hadPrevious)
            {
                changes.Add(new PropertySnapshotChange(SnapshotChangeKind.Added, name, null, currentValue));
            }
            else if (!hasCurrent)
            {
                changes.Add(new PropertySnapshotChange(SnapshotChangeKind.Removed, name, previousValue, null));
            }
            else if (!string.Equals(previousValue, currentValue, StringComparison.Ordinal))
            {
                changes.Add(new PropertySnapshotChange(SnapshotChangeKind.Modified, name, previousValue, currentValue));
            }
        }

        return changes;
    }

    private static IEnumerable<BomSnapshotChange> CompareBom(IReadOnlyList<BomItem> previous, IReadOnlyList<BomItem> current, BomKind kind)
    {
        var before = previous.ToDictionary(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase);
        var after = current.ToDictionary(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase);
        foreach (var drawingNumber in before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(drawingNumber, out var oldItem))
            {
                yield return new BomSnapshotChange(kind, BomChangeKind.Added, drawingNumber, "物料", null, after[drawingNumber].Name);
                continue;
            }

            if (!after.TryGetValue(drawingNumber, out var newItem))
            {
                yield return new BomSnapshotChange(kind, BomChangeKind.Removed, drawingNumber, "物料", oldItem.Name, null);
                continue;
            }

            if (oldItem.Quantity != newItem.Quantity)
                yield return Change(kind, BomChangeKind.QuantityChanged, drawingNumber, "数量", oldItem.Quantity, newItem.Quantity);
            if (!string.Equals(oldItem.Material, newItem.Material, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.MaterialChanged, drawingNumber, "材料", oldItem.Material, newItem.Material);
            if (!string.Equals(oldItem.Specification, newItem.Specification, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.SpecificationChanged, drawingNumber, "规格", oldItem.Specification, newItem.Specification);
            if (!string.Equals(oldItem.Revision, newItem.Revision, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.RevisionChanged, drawingNumber, "版本", oldItem.Revision, newItem.Revision);
        }
    }

    private static BomSnapshotChange Change(BomKind kind, BomChangeKind changeKind, string drawingNumber, string field, object? previous, object? current) =>
        new(kind, changeKind, drawingNumber, field, previous?.ToString(), current?.ToString());
}
