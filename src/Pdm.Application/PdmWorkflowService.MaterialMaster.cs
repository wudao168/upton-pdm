using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    private async Task<IReadOnlyList<BomItem>> ReadCurrentBomAsync(Guid projectId, BomKind kind, CancellationToken ct)
    {
        var items = await repository.GetBomAsync(projectId, kind, ct);
        if (materialRepository is null || kind is not (BomKind.Standard or BomKind.Electrical)) return items;

        // 料品引用使用当前已批准主档；CAD身份仍由图档对账处理，发布快照不走此入口。
        var codes = items.Where(MaterialService.UsesCurrentMaterialMaster)
            .Select(item => item.DrawingNumber.Trim()).Where(code => code.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0) return items;
        var materials = (await materialRepository.FindMaterialsByCodesAsync(codes, ct))
            .ToDictionary(material => material.MaterialCode, StringComparer.OrdinalIgnoreCase);
        return items.Select(item => MaterialService.WithCurrentMaterialMaster(item,
            materials.GetValueOrDefault(item.DrawingNumber.Trim()))).ToArray();
    }
}
