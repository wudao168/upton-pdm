using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryQualityReviewStore : IQualityReviewStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, QualityReviewCatalog> data = [];
    internal static QualityReviewCatalog Empty(Guid company) => new(company, 0, [], [], [], [], [], []);
    private static QualityReviewCatalog Copy(QualityReviewCatalog value) => JsonSerializer.Deserialize<QualityReviewCatalog>(JsonSerializer.Serialize(value))!;
    public Task<QualityReviewCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Copy(data.GetValueOrDefault(companyId) ?? Empty(companyId)));
    }
    public Task<bool> SaveAsync(QualityReviewCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((data.GetValueOrDefault(catalog.CompanyId)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            data[catalog.CompanyId] = Copy(catalog with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
}

public sealed class MySqlQualityReviewStore(IOptions<PdmDatabaseOptions> options) : IQualityReviewStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<QualityReviewCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM quality_review_catalog WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        return row is null ? InMemoryQualityReviewStore.Empty(companyId)
            : JsonSerializer.Deserialize<QualityReviewCatalog>(row.PayloadJson, Json)! with { CompanyId = companyId, RowVersion = row.RowVersion };
    }
    public async Task<bool> SaveAsync(QualityReviewCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        return await db.ExecuteAsync(new CommandDefinition(expectedVersion == 0
            ? "INSERT IGNORE INTO quality_review_catalog(company_id,payload_json,row_version) VALUES(@CompanyId,@Payload,1)"
            : "UPDATE quality_review_catalog SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion",
            new { catalog.CompanyId, Payload = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    private sealed class Row { public string PayloadJson { get; set; } = ""; public long RowVersion { get; set; } }
}
