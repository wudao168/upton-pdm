using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryBomFileStore : IBomFileStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, BomFileCatalog> rows = [];
    public Task<BomFileCatalog> ReadAsync(Guid scopeId, CancellationToken ct)
    { lock (gate) return Task.FromResult(rows.GetValueOrDefault(scopeId) ?? new(scopeId, 0, false, [], [])); }
    public Task<bool> SaveAsync(BomFileCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((rows.GetValueOrDefault(catalog.ScopeId)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            rows[catalog.ScopeId] = catalog with { RowVersion = expectedVersion + 1 };
            return Task.FromResult(true);
        }
    }
}

public sealed class MySqlBomFileStore(IOptions<PdmDatabaseOptions> options) : IBomFileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<BomFileCatalog> ReadAsync(Guid scopeId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var row = await db.QuerySingleOrDefaultAsync<Payload>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version Version FROM bom_file_catalog WHERE scope_id=@scopeId", new { scopeId }, cancellationToken: ct));
        return row is null ? new(scopeId, 0, false, [], [])
            : JsonSerializer.Deserialize<BomFileCatalog>(row.PayloadJson, Json)! with { RowVersion = row.Version };
    }
    public async Task<bool> SaveAsync(BomFileCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var sql = expectedVersion == 0
            ? "INSERT IGNORE INTO bom_file_catalog(scope_id,payload_json,row_version) VALUES(@ScopeId,@json,1)"
            : "UPDATE bom_file_catalog SET payload_json=@json,row_version=row_version+1 WHERE scope_id=@ScopeId AND row_version=@expectedVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql,
            new { catalog.ScopeId, json = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    private sealed record Payload(string PayloadJson, long Version);
}
