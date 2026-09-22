namespace Upton.Pdm.Domain;

public sealed record DocumentPreviewArtifact(
    DocumentPreviewFormat Format,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    string SourceSha256);

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
    Guid? ReleasePackageId,
    DocumentPreviewArtifact? Preview = null)
{
    /// <summary>该版本是"仅属性写入"产生还是内容变更产生；仅属性版本不改变零件几何。</summary>
    public DocumentVersionChangeKind ChangeKind { get; init; } = DocumentVersionChangeKind.Content;
}

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
        // 同一图号可能在BOM里出现多行（不同父级/不同规格），按图号分组比较，避免重复键直接抛异常。
        var before = previous.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var after = current.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var drawingNumber in before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(drawingNumber, out var oldItem))
            {
                yield return new BomSnapshotChange(kind, BomChangeKind.Added, drawingNumber, "物料", null, after[drawingNumber][0].Name);
                continue;
            }

            if (!after.TryGetValue(drawingNumber, out var newItem))
            {
                yield return new BomSnapshotChange(kind, BomChangeKind.Removed, drawingNumber, "物料", oldItem[0].Name, null);
                continue;
            }

            var previousQuantity = oldItem.Sum(item => item.Quantity);
            var currentQuantity = newItem.Sum(item => item.Quantity);
            if (previousQuantity != currentQuantity)
                yield return Change(kind, BomChangeKind.QuantityChanged, drawingNumber, "数量", previousQuantity, currentQuantity);
            if (!string.Equals(oldItem[0].Material, newItem[0].Material, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.MaterialChanged, drawingNumber, "材料", oldItem[0].Material, newItem[0].Material);
            if (!string.Equals(oldItem[0].Specification, newItem[0].Specification, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.SpecificationChanged, drawingNumber, "规格", oldItem[0].Specification, newItem[0].Specification);
            if (!string.Equals(oldItem[0].Revision, newItem[0].Revision, StringComparison.Ordinal))
                yield return Change(kind, BomChangeKind.RevisionChanged, drawingNumber, "版本", oldItem[0].Revision, newItem[0].Revision);
        }
    }

    private static BomSnapshotChange Change(BomKind kind, BomChangeKind changeKind, string drawingNumber, string field, object? previous, object? current) =>
        new(kind, changeKind, drawingNumber, field, previous?.ToString(), current?.ToString());
}
