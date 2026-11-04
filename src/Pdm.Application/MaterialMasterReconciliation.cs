using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class MaterialMasterReconciliation
{
    public static PdmMaterial Snapshot(PdmMaterial baseline, U9ItemReference item, DateTimeOffset at) => baseline with
    {
        Name = item.U9ItemName ?? baseline.Name, UnitCode = U9UnitCatalog.NormalizeInbound(item.U9UnitCode ?? baseline.UnitCode),
        CategoryCode = item.U9CategoryCode ?? baseline.CategoryCode, Specification = item.U9Specification, Brand = item.U9Brand,
        Material = item.U9Material, SurfaceTreatment = item.U9SurfaceTreatment, Remark = item.U9Description,
        PurchaseLink = item.U9PurchaseLink, Weight = item.U9Weight is > 0 ? item.U9Weight : null, WeightUnit = item.U9WeightUnitCode,
        U9ItemId = item.U9ItemId, U9IsEffective = U9MaterialContract.EffectiveState(item),
        SourceSystem = MaterialDataSource.U9C, LastU9SyncedAt = at
    };

    public static PdmMaterial Merge(PdmMaterial? existing, PdmMaterial incoming)
    {
        if (existing is null)
            return incoming with { MasterOwner = MaterialMasterOwner.Pdm, U9ReconciledAt = incoming.LastU9SyncedAt,
                U9ReconciliationMessage = incoming.U9IsEffective is null ? "U9C启停状态未返回，待核对。" : null };

        var differences = new List<string>();
        void Compare(string field, string? plm, string? u9)
        {
            if (!string.Equals(plm?.Trim() ?? string.Empty, u9?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                differences.Add($"{field}：PLM={plm ?? "空"}，U9C={u9 ?? "空"}");
        }
        Compare("名称", existing.Name, incoming.Name);
        Compare("分类", existing.CategoryCode ?? existing.U9CategoryCode, incoming.CategoryCode);
        Compare("单位", U9UnitCatalog.NormalizeInbound(existing.UnitCode), incoming.UnitCode);
        Compare("型号", existing.Specification, incoming.Specification);
        Compare("品牌", existing.Brand, incoming.Brand);
        Compare("材质", existing.Material, incoming.Material);
        Compare("表面处理", existing.SurfaceTreatment, incoming.SurfaceTreatment);
        Compare("备注", existing.Remark, incoming.Remark);
        Compare("采购链接", existing.PurchaseLink, incoming.PurchaseLink);
        if (existing.Kind != incoming.Kind) differences.Add("物料类型不一致");
        if (existing.SupplyMode != incoming.SupplyMode) differences.Add("供货方式不一致");
        if (existing.Weight != incoming.Weight) differences.Add("重量不一致");
        if (incoming.U9IsEffective is bool effective && effective == existing.IsArchived)
            differences.Add($"启停：PLM={(existing.IsArchived ? "停用" : "启用")}，U9C={(effective ? "启用" : "停用")}");
        if (incoming.U9IsEffective is null) differences.Add("U9C启停状态未返回，待核对。");

        var confirmed = existing.U9SyncConfirmed || existing.SourceSystem == MaterialDataSource.U9C || differences.Count == 0;
        var identityConflict = !string.IsNullOrWhiteSpace(existing.U9ItemId) && !string.IsNullOrWhiteSpace(incoming.U9ItemId)
            && existing.U9ItemId != incoming.U9ItemId;
        if (identityConflict) differences.Add("U9C内部标识与已有映射不一致，保留原映射");
        return existing with
        {
            MasterOwner = MaterialMasterOwner.Pdm,
            U9SyncConfirmed = confirmed,
            U9ItemId = confirmed && !identityConflict ? incoming.U9ItemId ?? existing.U9ItemId : existing.U9ItemId,
            U9ItemCode = confirmed && !identityConflict ? incoming.MaterialCode : existing.U9ItemCode,
            LastU9SyncedAt = incoming.LastU9SyncedAt,
            U9ReconciledAt = incoming.LastU9SyncedAt,
            U9IsEffective = incoming.U9IsEffective,
            U9ReconciliationMessage = differences.Count == 0 ? null : string.Join("；", differences),
            RowVersion = existing.RowVersion + (existing.MasterOwner == MaterialMasterOwner.Pdm ? 0 : 1)
        };
    }
}
