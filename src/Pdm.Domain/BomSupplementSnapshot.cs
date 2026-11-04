namespace Upton.Pdm.Domain;

public static class BomSupplementSnapshot
{
    // 未选中的物料沿用正式值；只有明确确认的删除项才退出新基线。
    public static BomItem[] Merge(IReadOnlyList<BomItem> current, IReadOnlyList<BomItem> selected, IReadOnlyList<BomItem> previous,
        IReadOnlyList<Guid>? confirmedRemovedIds = null)
    {
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var matches = MatchPrevious(current, previous);
        var matchedIds = matches.Values.Select(item => item.Id).ToHashSet();
        var result = new List<BomItem>();
        foreach (var item in current)
        {
            var old = matches.GetValueOrDefault(item.Id);
            if (selectedIds.Contains(item.Id)) result.Add(item);
            else if (old is not null) result.Add(old);
        }
        var remaining = previous.Where(item => !matchedIds.Contains(item.Id)).ToArray();
        var removed = (confirmedRemovedIds ?? []).ToHashSet();
        if (removed.Except(remaining.Select(item => item.Id)).Any())
            throw new InvalidOperationException("确认删除的物料仍在当前BOM中，或不属于上一正式版，请刷新变更清单。");
        result.AddRange(remaining.Where(item => !removed.Contains(item.Id)));
        return result.ToArray();
    }

    public static IReadOnlyList<BomItem> ChangedItems(IReadOnlyList<BomItem> current, IReadOnlyList<BomItem> previous,
        int multiplier = 1, int previousMultiplier = 1)
    {
        var matches = MatchPrevious(current, previous);
        return current.Where(item => multiplier != previousMultiplier || !matches.TryGetValue(item.Id, out var old) || !SameValues(item, old)).ToArray();
    }

    private static Dictionary<Guid, BomItem> MatchPrevious(IReadOnlyList<BomItem> current, IReadOnlyList<BomItem> previous)
    {
        var remaining = previous.ToList();
        var matches = new Dictionary<Guid, BomItem>();
        void Match(Func<BomItem, BomItem, bool> predicate)
        {
            foreach (var item in current.Where(item => !matches.ContainsKey(item.Id)))
            {
                var old = remaining.FirstOrDefault(candidate => predicate(item, candidate));
                if (old is null) continue;
                matches[item.Id] = old;
                remaining.Remove(old);
            }
        }
        // 先为所有仍存在的实例保留正式值，避免排在前面的新增套件子项占用旧实例。
        Match((item, old) => item.Id == old.Id);
        Match((item, old) => item.ReleaseTrackingId != Guid.Empty && item.ReleaseTrackingId == old.ReleaseTrackingId);
        Match((item, old) => item.SourceDocumentId.HasValue && !string.IsNullOrWhiteSpace(item.SourceInstancePath)
            && item.SourceDocumentId == old.SourceDocumentId && item.SourceConfiguration == old.SourceConfiguration
            && item.SourceInstancePath == old.SourceInstancePath);
        Match((item, old) => SameMaterial(item, old) && SameValues(item, old));
        foreach (var item in current.Where(item => !matches.ContainsKey(item.Id)))
        {
            var candidates = remaining.Where(old => SameMaterial(item, old)).ToArray();
            if (candidates.Length != 1 || current.Count(candidate => !matches.ContainsKey(candidate.Id) && SameMaterial(item, candidate)) != 1) continue;
            matches[item.Id] = candidates[0];
            remaining.Remove(candidates[0]);
        }
        return matches;
    }

    private static bool SameMaterial(BomItem item, BomItem old) => !string.IsNullOrWhiteSpace(item.DrawingNumber)
        && string.Equals(item.DrawingNumber.Trim(), old.DrawingNumber.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(item.Unit.Trim(), old.Unit.Trim(), StringComparison.OrdinalIgnoreCase)
        && item.EngineeringKitReferenceId == old.EngineeringKitReferenceId;

    private static bool SameValues(BomItem item, BomItem old)
    {
        static string Text(string? value) => value?.Trim() ?? string.Empty;
        var sameRevision = Text(item.Revision) == Text(old.Revision);
        var work = System.Text.RegularExpressions.Regex.Match(Text(old.Revision), @"^(?:([A-Z]+)-)?W1$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sameRevision |= work.Success && string.Equals(Text(item.Revision), work.Groups[1].Success ? work.Groups[1].Value : "A", StringComparison.OrdinalIgnoreCase);
        return sameRevision && (item.Kind, Text(item.Unit), Text(item.DrawingNumber), Text(item.Name), Text(item.Specification),
            Text(item.Remark), Text(item.Brand), Text(item.Material), Text(item.SurfaceTreatment), Text(item.HeatTreatment), Text(item.Weight), item.Quantity)
            == (old.Kind, Text(old.Unit), Text(old.DrawingNumber), Text(old.Name), Text(old.Specification),
                Text(old.Remark), Text(old.Brand), Text(old.Material), Text(old.SurfaceTreatment), Text(old.HeatTreatment), Text(old.Weight), old.Quantity);
    }
}
