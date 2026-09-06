using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryU9InventoryRepository(IMaterialRepository? materials = null) : IU9InventoryRepository
{
    private readonly object gate = new();
    private U9InventorySyncSettings settings = new(true, 60, "/webapi/Invtrans/QueryQohAndAvailable", null, null, null);
    private U9InventorySyncRun? latestRun;
    private readonly List<U9InventorySnapshotRow> rows = [];

    public Task<U9InventorySyncSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(settings);
    }

    public Task<U9InventorySyncSettings> SaveSettingsAsync(U9InventorySyncSettings value, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            settings = value with { CurrentSnapshotRunId = settings.CurrentSnapshotRunId };
            return Task.FromResult(settings);
        }
    }

    public Task<U9InventorySyncRun?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(latestRun);
    }

    public Task<U9InventorySyncRun> SaveRunAsync(U9InventorySyncRun run, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            latestRun = run;
            return Task.FromResult(run);
        }
    }

    public async Task<IReadOnlySet<string>> ListActiveMaterialCodesAsync(CancellationToken cancellationToken)
    {
        if (materials is not null)
        {
            const int pageSize = 200;
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var pageNumber = 1; ; pageNumber++)
            {
                var page = await materials.ListMaterialPageAsync(null, null, null, false, pageNumber, pageSize, cancellationToken);
                foreach (var material in page.Items)
                    if (!string.IsNullOrWhiteSpace(material.MaterialCode)) codes.Add(material.MaterialCode.Trim());
                if (pageNumber * page.PageSize >= page.Total) return codes;
            }
        }

        lock (gate) return rows.Select(row => row.MaterialCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task ReplaceSnapshotAsync(Guid snapshotRunId, IReadOnlyList<U9InventorySourceRow> sourceRows, DateTimeOffset refreshedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            rows.Clear();
            rows.AddRange(sourceRows.Select(row => Map(snapshotRunId, row, refreshedAt)));
            settings = settings with { CurrentSnapshotRunId = snapshotRunId };
        }
        return Task.CompletedTask;
    }

    public Task RefreshMaterialAsync(string materialCode, IReadOnlyList<U9InventorySourceRow> sourceRows, DateTimeOffset refreshedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var snapshotRunId = settings.CurrentSnapshotRunId ?? Guid.NewGuid();
            settings = settings with { CurrentSnapshotRunId = snapshotRunId };
            rows.RemoveAll(row => string.Equals(row.MaterialCode, materialCode, StringComparison.OrdinalIgnoreCase));
            rows.AddRange(sourceRows.Select(row => Map(snapshotRunId, row, refreshedAt)));
        }
        return Task.CompletedTask;
    }

    public Task<U9InventoryPage> ListAsync(U9InventoryFilters filters, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IEnumerable<U9InventorySnapshotRow> query = rows;
            var warehouseNames = rows.Select(row => row.WarehouseName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var brandNames = rows.Select(row => row.Brand)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var projectCodes = rows.Select(row => row.ProjectCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var subprojectOptions = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProjectCode) && !string.IsNullOrWhiteSpace(row.Subproject))
                .Select(row => new U9InventorySubprojectOption(row.ProjectCode!, row.Subproject!))
                .Distinct()
                .OrderBy(option => option.ProjectCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.Subproject, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!string.IsNullOrWhiteSpace(filters.MaterialCode)) query = query.Where(row => string.Equals(row.MaterialCode, filters.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filters.ItemName)) query = query.Where(row => row.ItemName.Contains(filters.ItemName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filters.Specification)) query = query.Where(row => row.Specification?.Contains(filters.Specification.Trim(), StringComparison.OrdinalIgnoreCase) == true);
            if (!string.IsNullOrWhiteSpace(filters.Brand)) query = query.Where(row => string.Equals(row.Brand, filters.Brand.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filters.Warehouse)) query = query.Where(row => row.WarehouseCode.Contains(filters.Warehouse.Trim(), StringComparison.OrdinalIgnoreCase) || row.WarehouseName.Contains(filters.Warehouse.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filters.ProjectCode)) query = query.Where(row => row.ProjectCode?.Contains(filters.ProjectCode.Trim(), StringComparison.OrdinalIgnoreCase) == true);
            if (!string.IsNullOrWhiteSpace(filters.Subproject)) query = query.Where(row => row.Subproject?.Contains(filters.Subproject.Trim(), StringComparison.OrdinalIgnoreCase) == true);
            if (filters.PositiveStockOnly) query = query.Where(row => row.StockQuantity > 0);
            var filtered = query.OrderBy(row => row.WarehouseName).ThenBy(row => row.MaterialCode).ToArray();
            var page = Math.Max(1, filters.Page);
            var pageSize = Math.Clamp(filters.PageSize, 1, 200);
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            return Task.FromResult(new U9InventoryPage(items, filtered.Length, page, pageSize, warehouseNames,
                brandNames, projectCodes, subprojectOptions,
                latestRun?.Status == U9InventorySyncStatus.Succeeded ? latestRun.CompletedAt : null));
        }
    }

    private static U9InventorySnapshotRow Map(Guid runId, U9InventorySourceRow row, DateTimeOffset refreshedAt) => new(
        runId, row.OrganizationCode, row.WarehouseCode ?? string.Empty, row.WarehouseName, row.MaterialCode,
        row.ItemName, null, row.Specification, row.ProjectCode, row.ProjectName, row.Subproject,
        row.StockQuantity, row.AvailableQuantity, row.ReservedQuantity, row.UnavailableQuantity,
        row.BinCode, row.BinName, row.StorageType, refreshedAt);
}
