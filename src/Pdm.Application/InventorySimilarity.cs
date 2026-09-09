using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class InventorySimilarity
{
    public static string Normalize(string? value) => new string((value ?? string.Empty)
        .Select(c => c == '\u3000' ? ' ' : c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c)
        .ToArray()).Trim().ToUpperInvariant();

    public static U9InventoryFilters Prepare(U9InventoryFilters filters)
    {
        if (filters.SimilarSpecification is null) return filters;
        var specification = Normalize(filters.SimilarSpecification);
        if (specification.Count(char.IsLetterOrDigit) < 4)
            throw new PdmRuleException("相似查询请至少输入4个有效字母、数字或汉字。");
        if (specification.Length > 256)
            throw new PdmRuleException("相似查询规格不能超过256个字符。");
        // Similar search is independent of ordinary filters; brand and stock scope remain explicit.
        return filters with { SimilarSpecification = specification, MaterialCode = null, ItemName = null,
            Specification = null, Warehouse = null, ProjectCode = null, Subproject = null };
    }

    public static U9InventorySnapshotRow[] Match(IEnumerable<U9InventorySnapshotRow> rows, string specification,
        CancellationToken cancellationToken)
    {
        var target = Normalize(specification);
        var matches = new List<U9InventorySnapshotRow>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Normalize(row.Specification);
            if (candidate.Length == 0 || Math.Min(target.Length, candidate.Length) * 4 < Math.Max(target.Length, candidate.Length) * 3) continue;
            var score = Score(target, candidate);
            if (score >= 75m) matches.Add(row with { SimilarityPercent = score });
        }
        // Stable sorting retains warehouse/project/bin order for equally similar inventory rows.
        return matches.OrderByDescending(row => row.SimilarityPercent).ToArray();
    }

    public static decimal Score(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left.Length == 0 || right.Length == 0) return 0m;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 100m * (Math.Max(left.Length, right.Length) - previous[right.Length]) / Math.Max(left.Length, right.Length);
    }
}
