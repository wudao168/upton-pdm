namespace Upton.Pdm.Domain;

public static class BomSupplementSnapshot
{
    // 未选中的现有物料沿用正式值；当前BOM已移除的物料才从新基线删除。
    public static BomItem[] Merge(IReadOnlyList<BomItem> current, IReadOnlyList<BomItem> selected, IReadOnlyList<BomItem> previous)
    {
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var remaining = previous.ToList();
        var result = new List<BomItem>();
        foreach (var item in current)
        {
            var old = remaining.FirstOrDefault(candidate => candidate.Id == item.Id || candidate.ReleaseTrackingId == item.ReleaseTrackingId)
                ?? remaining.FirstOrDefault(candidate => item.SourceDocumentId.HasValue && candidate.SourceDocumentId == item.SourceDocumentId
                    && candidate.SourceConfiguration == item.SourceConfiguration && candidate.SourceInstancePath == item.SourceInstancePath)
                ?? remaining.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(item.DrawingNumber)
                    && string.Equals(candidate.DrawingNumber, item.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Unit, item.Unit, StringComparison.OrdinalIgnoreCase));
            if (old is not null) remaining.Remove(old);
            if (selectedIds.Contains(item.Id)) result.Add(item);
            else if (old is not null) result.Add(old);
        }
        return result.ToArray();
    }
}
