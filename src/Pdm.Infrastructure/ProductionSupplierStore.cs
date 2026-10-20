using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProductionSupplierStore : IProductionSupplierStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ProductionSupplierCatalog> catalogs = [];
    private static ProductionSupplierCatalog Copy(ProductionSupplierCatalog value) =>
        JsonSerializer.Deserialize<ProductionSupplierCatalog>(JsonSerializer.Serialize(value))!;

    public Task<ProductionSupplierCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(catalogs.TryGetValue(companyId, out var found)
            ? Copy(found) : new ProductionSupplierCatalog(companyId, [], []));
    }
    public Task<bool> SaveAsync(ProductionSupplierCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((catalogs.GetValueOrDefault(catalog.CompanyId)?.RowVersion ?? 0) != expectedVersion)
                return Task.FromResult(false);
            catalogs[catalog.CompanyId] = Copy(catalog with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
}

public sealed class MySqlProductionSupplierStore(IOptions<PdmDatabaseOptions> options) : IProductionSupplierStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ProductionSupplierCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var row = await db.QuerySingleOrDefaultAsync<CatalogRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_supplier_catalog WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        return row is null ? new(companyId, [], [])
            : JsonSerializer.Deserialize<ProductionSupplierCatalog>(row.PayloadJson, Json)! with { RowVersion = row.RowVersion };
    }
    public async Task<bool> SaveAsync(ProductionSupplierCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var sql = expectedVersion == 0
            ? "INSERT IGNORE INTO production_supplier_catalog(company_id,payload_json,row_version) VALUES(@CompanyId,@Payload,1)"
            : "UPDATE production_supplier_catalog SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql,
            new { catalog.CompanyId, Payload = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    private sealed class CatalogRow
    {
        public string PayloadJson { get; set; } = "";
        public long RowVersion { get; set; }
    }
}
