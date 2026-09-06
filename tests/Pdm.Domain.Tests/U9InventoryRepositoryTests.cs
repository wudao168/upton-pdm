using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class U9InventoryRepositoryTests
{
    [Fact]
    public async Task ListActiveMaterialCodes_UsesMaterialMasterBeforeFirstSnapshot()
    {
        var time = TimeProvider.System;
        var materials = new InMemoryMaterialRepository(time);
        var category = await materials.FindCategoryAsync("0102", default) ?? throw new InvalidOperationException();
        var now = time.GetUtcNow();
        var material = new PdmMaterial(
            Guid.NewGuid(), "01020000999", "首次库存同步料品", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", null, null, null, null, null, null, null, null, MaterialApprovalStatus.Approved,
            "admin", now, category.Code, null, "01020000999", MaterialSyncStatus.Succeeded,
            "admin", now, "admin", now, 1, category.Code, U9SyncConfirmed: true);
        await materials.CreateMaterialAsync(material, category, default);
        var repository = new InMemoryU9InventoryRepository(materials);

        var result = await repository.ListActiveMaterialCodesAsync(default);

        Assert.Contains(material.MaterialCode, result);
    }

    [Fact]
    public async Task List_FiltersProjectAndSubproject_AndReturnsAllIndexOptions()
    {
        var repository = new InMemoryU9InventoryRepository();
        var runId = Guid.NewGuid();
        var refreshedAt = DateTimeOffset.Parse("2026-09-05T01:02:03Z");
        await repository.ReplaceSnapshotAsync(runId,
        [
            Row("01", "1号常备仓", "0101", "P700005", "P700005-6"),
            Row("02", "2号常备仓", "0102", "P700006", "P700006-1"),
        ], refreshedAt, default);

        var result = await repository.ListAsync(new(
            MaterialCode: null,
            ItemName: null,
            Specification: null,
            Brand: null,
            Warehouse: null,
            ProjectCode: "700005",
            Subproject: "005-6",
            PositiveStockOnly: true,
            Page: 1,
            PageSize: 50), default);

        Assert.Single(result.Items);
        Assert.Equal("0101", result.Items[0].MaterialCode);
        Assert.Equal(["1号常备仓", "2号常备仓"], result.WarehouseNames);
        Assert.Equal(["P700005", "P700006"], result.ProjectCodes);
        Assert.Equal([
            new U9InventorySubprojectOption("P700005", "P700005-6"),
            new U9InventorySubprojectOption("P700006", "P700006-1")
        ], result.SubprojectOptions);
    }

    private static U9InventorySourceRow Row(
        string warehouseCode,
        string warehouseName,
        string materialCode,
        string projectCode,
        string subproject) => new(
            OrganizationCode: "7",
            MaterialCode: materialCode,
            ItemName: $"料品{materialCode}",
            Specification: null,
            WarehouseCode: warehouseCode,
            WarehouseName: warehouseName,
            BinCode: null,
            BinName: null,
            StorageType: null,
            ProjectCode: projectCode,
            ProjectName: null,
            Subproject: subproject,
            StockQuantity: 1,
            AvailableQuantity: 1,
            ReservedQuantity: 0,
            UnavailableQuantity: 0);
}
