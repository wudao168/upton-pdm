using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryStandardStructureStore : IStandardStructureStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, StandardStructureCatalog> catalogs = [];
    private static StandardStructureCatalog Copy(StandardStructureCatalog value) =>
        JsonSerializer.Deserialize<StandardStructureCatalog>(JsonSerializer.Serialize(value))!;
    public Task<StandardStructureCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(catalogs.TryGetValue(StandardStructureRules.GroupId, out var value)
            ? Copy(value) : StandardStructureCatalogMigration.Merge([]));
    }
    public Task<bool> SaveAsync(StandardStructureCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((catalogs.GetValueOrDefault(StandardStructureRules.GroupId)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            catalogs[StandardStructureRules.GroupId] = Copy(catalog with { CompanyId = StandardStructureRules.GroupId, RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
}

public sealed class MySqlStandardStructureStore(IOptions<PdmDatabaseOptions> options) : IStandardStructureStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<StandardStructureCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        companyId = StandardStructureRules.GroupId;
        var row = await db.QuerySingleOrDefaultAsync<CatalogRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM standard_structure_catalog WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        if (row is not null) return Deserialize(row);
        await db.OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        var previous = (await db.QueryAsync<CatalogRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM standard_structure_catalog FOR UPDATE", transaction: transaction, cancellationToken: ct))).ToArray();
        var group = previous.Select(Deserialize).FirstOrDefault(c => c.CompanyId == companyId);
        if (group is not null) { await transaction.CommitAsync(ct); return group; }
        var merged = StandardStructureCatalogMigration.Merge(previous.Select(Deserialize).ToArray());
        if (previous.Length > 0)
        {
            await db.ExecuteAsync(new CommandDefinition("INSERT INTO standard_structure_catalog(company_id,payload_json,row_version) VALUES(@companyId,@payload,1)",
                new { companyId, payload = JsonSerializer.Serialize(merged, Json) }, transaction, cancellationToken: ct));
            merged = merged with { RowVersion = 1 };
        }
        await transaction.CommitAsync(ct);
        return merged;
    }
    private static StandardStructureCatalog Deserialize(CatalogRow row) =>
        JsonSerializer.Deserialize<StandardStructureCatalog>(row.PayloadJson, Json)! with { RowVersion = row.RowVersion };
    public async Task<bool> SaveAsync(StandardStructureCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var sql = expectedVersion == 0
            ? "INSERT IGNORE INTO standard_structure_catalog(company_id,payload_json,row_version) VALUES(@CompanyId,@Payload,1)"
            : "UPDATE standard_structure_catalog SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql,
            new { CompanyId = StandardStructureRules.GroupId, Payload = JsonSerializer.Serialize(catalog with { CompanyId = StandardStructureRules.GroupId }, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    private sealed class CatalogRow
    {
        public string PayloadJson { get; set; } = "";
        public long RowVersion { get; set; }
    }
}
