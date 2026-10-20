namespace Upton.Pdm.Domain;

public static class BomSupplementSnapshot
{
    // 未选中的物料沿用正式值；只有明确确认的删除项才退出新基线。
    public static BomItem[] Merge(IReadOnlyList<BomItem> current, IReadOnlyList<BomItem> selected, IReadOnlyList<BomItem> previous,
        IReadOnlyList<Guid>? confirmedRemovedIds = null)
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
        var removed = (confirmedRemovedIds ?? []).ToHashSet();
        if (removed.Except(remaining.Select(item => item.Id)).Any())
            throw new InvalidOperationException("确认删除的物料仍在当前BOM中，或不属于上一正式版，请刷新变更清单。");
        result.AddRange(remaining.Where(item => !removed.Contains(item.Id)));
        return result.ToArray();
    }
}
