using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryStockedMaterialStore : IStockedMaterialStore
{
    private readonly object gate = new();
    private readonly Dictionary<StockedMaterialScope, StockedMaterialCatalog> rows = [];
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public Task<StockedMaterialCatalog> ReadAsync(StockedMaterialScope scope, CancellationToken ct)
    { lock (gate) return Task.FromResult(Copy(rows.GetValueOrDefault(scope) ?? new(scope, 0, new(), [], []))); }
    public Task<bool> SaveAsync(StockedMaterialCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((rows.GetValueOrDefault(catalog.Scope)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            rows[catalog.Scope] = Copy(catalog with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<StockedMaterialScope>> ListScopesAsync(CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<StockedMaterialScope>>(rows.Keys.ToArray()); }
}

public sealed class MySqlStockedMaterialStore(IOptions<PdmDatabaseOptions> options) : IStockedMaterialStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<StockedMaterialCatalog> ReadAsync(StockedMaterialScope scope, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var row = await db.QuerySingleOrDefaultAsync<Payload>(new CommandDefinition("""
            SELECT payload_json PayloadJson,row_version Version FROM stocked_material_catalog
            WHERE company_id=@CompanyId AND enterprise_code=@EnterpriseCode AND organization_code=@OrganizationCode
            """, scope, cancellationToken: ct));
        return row is null ? new(scope, 0, new(), [], [], LastError: null)
            : JsonSerializer.Deserialize<StockedMaterialCatalog>(row.PayloadJson, Json)! with { RowVersion = row.Version };
    }
    public async Task<bool> SaveAsync(StockedMaterialCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var sql = expectedVersion == 0 ? """
            INSERT IGNORE INTO stocked_material_catalog(company_id,enterprise_code,organization_code,payload_json,row_version)
            VALUES(@CompanyId,@EnterpriseCode,@OrganizationCode,@json,1)
            """ : """
            UPDATE stocked_material_catalog SET payload_json=@json,row_version=row_version+1
            WHERE company_id=@CompanyId AND enterprise_code=@EnterpriseCode AND organization_code=@OrganizationCode AND row_version=@expectedVersion
            """;
        return await db.ExecuteAsync(new CommandDefinition(sql, new { catalog.Scope.CompanyId, catalog.Scope.EnterpriseCode,
            catalog.Scope.OrganizationCode, json = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    public async Task<IReadOnlyList<StockedMaterialScope>> ListScopesAsync(CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        return (await db.QueryAsync<StockedMaterialScope>(new CommandDefinition("""
            SELECT company_id CompanyId,enterprise_code EnterpriseCode,organization_code OrganizationCode FROM stocked_material_catalog
            """, cancellationToken: ct))).ToArray();
    }
    private sealed record Payload(string PayloadJson, long Version);
}
