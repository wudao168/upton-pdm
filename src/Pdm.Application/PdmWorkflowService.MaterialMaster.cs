using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    // Query-only consumers must not refresh attachment captures or write BOM metadata.
    public async Task<IReadOnlyList<BomItem>> GetReadOnlyBomAsync(Guid projectId, BomKind kind, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return await BomDrawingRevision.CurrentAsync(repository, await ReadCurrentBomAsync(projectId, kind, ct), ct);
    }

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
