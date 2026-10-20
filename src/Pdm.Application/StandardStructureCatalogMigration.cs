using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class StandardStructureCatalogMigration
{
    public static StandardStructureCatalog Merge(IReadOnlyList<StandardStructureCatalog> sources)
    {
        var categories = sources.SelectMany(c => c.Categories).GroupBy(c => c.Id).Select(g => g.Distinct().Count() == 1
            ? g.First() : throw new PdmConflictException("原公司标准库存在同标识分类冲突，请先核对后归并。")).ToList();
        var structures = sources.SelectMany(c => c.Structures.Select(s => s with { Revisions = s.Revisions.Select(r => r with
        {
            StorageCompanyId = r.StorageCompanyId ?? c.CompanyId,
            StorageRoot = r.StorageRoot ?? (string.IsNullOrWhiteSpace(c.StorageRoot) ? null : c.StorageRoot)
        }).ToArray() })).ToArray();
        if (structures.GroupBy(s => s.Id).Any(g => g.Count() > 1) || structures.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new PdmConflictException("原公司标准库存在重复标识或编号，请先核对后归并；原数据已保留。");
        categories.RemoveAll(c => c.Id == StandardStructureRules.UnclassifiedId);
        categories.Insert(0, new(StandardStructureRules.UnclassifiedId, "未分类", null, int.MinValue, true));
        var sequences = sources.SelectMany(c => c.Sequences).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Max(p => p.Value));
        foreach (var structure in structures)
        {
            var parts = structure.Code.Split('-');
            if (parts.Length == 3 && parts[0] == "UPST" && int.TryParse(parts[1], out var type) && StandardStructureRules.IsDrawingType(type)
                && int.TryParse(parts[2], out var sequence)) sequences[type] = Math.Max(sequences.GetValueOrDefault(type), sequence);
        }
        return new(StandardStructureRules.GroupId, categories, structures,
            sources.Select(c => c.StorageRoot).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "")
        {
            Sequences = sequences
        };
    }
}
