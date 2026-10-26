using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

// 业务模块只读本地正式常备快照；这里不依赖U9C客户端。
public sealed class StockedMaterialQueryService(IStockedMaterialStore store, IMaterialRepository materials, IU9InventoryRepository inventory)
{
    public async Task<IReadOnlyList<MaterialStockPlan>> ListBusinessPlansAsync(Guid? companyId, CancellationToken ct)
    {
        companyId ??= TenantContext.CompanyId;
        if (companyId is null) return [];
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        var catalog = await store.ReadAsync(new(companyId.Value, config.EnterpriseCode, config.OrganizationCode), ct);
        // 首次升级补齐前保持原业务口径，完整补齐成功后原子切换，之后不再回退到原缓存。
        if (!catalog.BusinessSnapshotReady && catalog.LastSuccessAt.HasValue)
            return (await materials.ListStockedMaterialPlansAsync(config.EnterpriseCode, config.OrganizationCode, ct))
                .Where(plan => !StockedMaterialPolicy.ManagementOnlyCode(plan.MaterialCode)).ToArray();
        var memberIds = catalog.Memberships.Select(item => item.MaterialId).ToHashSet();
        var managementIds = (catalog.ManagementMaterials ?? []).Select(item => item.Id).ToHashSet();
        var result = new List<MaterialStockPlan>();
        foreach (var snapshot in catalog.Snapshots.Where(item => item.IsFormal && memberIds.Contains(item.MaterialId) && !managementIds.Contains(item.MaterialId)))
        {
            var material = await materials.FindMaterialAsync(snapshot.MaterialId, ct);
            if (material is null || material.IsArchived || !material.U9SyncConfirmed || material.ApprovalStatus != MaterialApprovalStatus.Approved
                || StockedMaterialPolicy.ManagementOnlyCategory(material.CategoryCode)) continue;
            if (snapshot.Plan!.EnterpriseCode != config.EnterpriseCode || snapshot.Plan.OrganizationCode != config.OrganizationCode) continue;
            result.Add(snapshot.Plan with { StockQuantity = snapshot.StockQuantity, InventorySyncedAt = snapshot.InventorySyncedAt });
        }
        return result.DistinctBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<U9InventoryPage?> TryReadInventoryAsync(U9InventoryFilters filters, CancellationToken ct)
    {
        if (TenantContext.CompanyId is not Guid company || string.IsNullOrWhiteSpace(filters.MaterialCode)) return null;
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        var catalog = await store.ReadAsync(new(company, config.EnterpriseCode, config.OrganizationCode), ct);
        var snapshot = catalog.Snapshots.FirstOrDefault(item => item.IsFormal
            && item.Plan!.MaterialCode.Equals(filters.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase)
            && catalog.Memberships.Any(member => member.MaterialId == item.MaterialId)
            && !(catalog.ManagementMaterials ?? []).Any(management => management.Id == item.MaterialId));
        if (snapshot is null) return null;
        var material = await materials.FindMaterialAsync(snapshot.MaterialId, ct);
        if (material is null || material.IsArchived || StockedMaterialPolicy.ManagementOnlyCategory(material.CategoryCode)) return null;
        // 旧快照尚未补齐明细时也仅读取本地库存缓存，不发起U9请求。
        if (snapshot.InventoryRows is null) return await inventory.ListAsync(filters, ct);
        var all = snapshot.InventoryRows.Where(row => row.OrganizationCode == config.OrganizationCode
            && row.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool Includes(string? value, string? query) => string.IsNullOrWhiteSpace(query) || value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        bool EqualsFilter(string? value, string? query) => string.IsNullOrWhiteSpace(query) || string.Equals(value, query.Trim(), StringComparison.OrdinalIgnoreCase);
        var scoped = all.Where(row => Includes(material.Name, filters.ItemName) && Includes(material.Specification, filters.Specification)
            && EqualsFilter(material.Brand, filters.Brand) && EqualsFilter(row.WarehouseName, filters.Warehouse)
            && EqualsFilter(row.ProjectCode, filters.ProjectCode) && EqualsFilter(row.Subproject, filters.Subproject)
            && (!filters.PositiveStockOnly || row.StockQuantity > 0)).ToArray();
        var page = Math.Max(1, filters.Page); var size = Math.Clamp(filters.PageSize, 1, 200);
        var rows = scoped.Skip((page - 1) * size).Take(size).Select(row => new U9InventorySnapshotRow(snapshot.MaterialId, row.OrganizationCode,
            row.WarehouseCode ?? "", row.WarehouseName, row.MaterialCode, material.Name, material.Brand, material.Specification,
            row.ProjectCode, row.ProjectName, row.Subproject, row.StockQuantity, row.AvailableQuantity, row.ReservedQuantity,
            row.UnavailableQuantity, row.BinCode, row.BinName, row.StorageType, snapshot.InventorySyncedAt ?? snapshot.Plan!.SyncedAt)).ToArray();
        return new(rows, scoped.Length, page, size, all.Select(row => row.WarehouseName).Distinct().Order().ToArray(),
            string.IsNullOrWhiteSpace(material.Brand) ? [] : [material.Brand],
            all.Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode)).Select(row => row.ProjectCode!).Distinct().Order().ToArray(),
            all.Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode) && !string.IsNullOrWhiteSpace(row.Subproject))
                .Select(row => new U9InventorySubprojectOption(row.ProjectCode!, row.Subproject!)).Distinct().ToArray(), snapshot.InventorySyncedAt);
    }
}
