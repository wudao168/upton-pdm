using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryWorkHoursStore : IWorkHoursStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, DateOnly> starts = [];
    private readonly Dictionary<(Guid, string, string), WorkHoursMonth> sheets = [];
    private readonly Dictionary<Guid, WorkHoursCatalog> catalogs = [];
    private static WorkHoursMonth Copy(WorkHoursMonth value) => JsonSerializer.Deserialize<WorkHoursMonth>(JsonSerializer.Serialize(value))!;
    private static (Guid, string, string) Key(WorkHoursMonth value) => (value.CompanyId, value.Username.ToLowerInvariant(), value.Month);
    public Task<DateOnly> StartDateAsync(Guid companyId, DateOnly initialDate, CancellationToken ct)
    { lock (gate) { starts.TryAdd(companyId, initialDate); return Task.FromResult(starts[companyId]); } }
    public Task<IReadOnlyList<WorkHoursMonth>> ReadAsync(Guid companyId, string fromMonth, string toMonth, CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<WorkHoursMonth>>(sheets.Values.Where(x => x.CompanyId == companyId && string.CompareOrdinal(x.Month, fromMonth) >= 0 && string.CompareOrdinal(x.Month, toMonth) <= 0).Select(Copy).ToArray()); }
    public Task<bool> SaveAsync(IReadOnlyList<WorkHoursMonth> values, CancellationToken ct)
    {
        lock (gate)
        {
            if (values.Any(x => (sheets.GetValueOrDefault(Key(x))?.RowVersion ?? 0) != x.RowVersion)) return Task.FromResult(false);
            foreach (var value in values) sheets[Key(value)] = Copy(value with { RowVersion = value.RowVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<WorkHoursCatalog> ReadCatalogAsync(Guid companyId, CancellationToken ct)
    { lock (gate) return Task.FromResult(JsonSerializer.Deserialize<WorkHoursCatalog>(JsonSerializer.Serialize(catalogs.GetValueOrDefault(companyId) ?? new(companyId, 0, [], [], [], [])))!); }
    public Task<bool> SaveCatalogAsync(WorkHoursCatalog catalog, CancellationToken ct)
    {
        lock (gate)
        {
            if ((catalogs.GetValueOrDefault(catalog.CompanyId)?.RowVersion ?? 0) != catalog.RowVersion) return Task.FromResult(false);
            catalogs[catalog.CompanyId] = JsonSerializer.Deserialize<WorkHoursCatalog>(JsonSerializer.Serialize(catalog with { RowVersion = catalog.RowVersion + 1 }))!;
            return Task.FromResult(true);
        }
    }
}

public sealed class MySqlWorkHoursStore(IOptions<PdmDatabaseOptions> options) : IWorkHoursStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<DateOnly> StartDateAsync(Guid companyId, DateOnly initialDate, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO work_hours_settings(company_id,start_date) VALUES(@companyId,@Initial)", new { companyId, Initial = initialDate.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
        return DateOnly.FromDateTime(await db.QuerySingleAsync<DateTime>(new CommandDefinition("SELECT start_date FROM work_hours_settings WHERE company_id=@companyId", new { companyId }, cancellationToken: ct)));
    }
    public async Task<IReadOnlyList<WorkHoursMonth>> ReadAsync(Guid companyId, string fromMonth, string toMonth, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var rows = await db.QueryAsync<SheetRow>(new CommandDefinition("SELECT payload_json Payload,row_version RowVersion FROM work_hours_month WHERE company_id=@companyId AND month_key BETWEEN @fromMonth AND @toMonth", new { companyId, fromMonth, toMonth }, cancellationToken: ct));
        return rows.Select(x => JsonSerializer.Deserialize<WorkHoursMonth>(x.Payload, Json)! with { RowVersion = x.RowVersion }).ToArray();
    }
    public async Task<bool> SaveAsync(IReadOnlyList<WorkHoursMonth> sheets, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        await db.OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        foreach (var sheet in sheets.OrderBy(x => x.Month, StringComparer.Ordinal))
        {
            var sql = sheet.RowVersion == 0
                ? "INSERT IGNORE INTO work_hours_month(company_id,username,month_key,payload_json,row_version) VALUES(@CompanyId,@Username,@Month,@Payload,1)"
                : "UPDATE work_hours_month SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND username=@Username AND month_key=@Month AND row_version=@RowVersion";
            if (await db.ExecuteAsync(new CommandDefinition(sql, new { sheet.CompanyId, sheet.Username, sheet.Month, sheet.RowVersion, Payload = JsonSerializer.Serialize(sheet, Json) }, transaction, cancellationToken: ct)) != 1)
            { await transaction.RollbackAsync(ct); return false; }
        }
        await transaction.CommitAsync(ct);
        return true;
    }
    private sealed class SheetRow { public string Payload { get; set; } = ""; public long RowVersion { get; set; } }
    public async Task<WorkHoursCatalog> ReadCatalogAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var row = await db.QuerySingleOrDefaultAsync<SheetRow>(new CommandDefinition("SELECT payload_json Payload,row_version RowVersion FROM work_hours_management WHERE company_id=@companyId", new { companyId }, cancellationToken: ct));
        return row is null ? new(companyId, 0, [], [], [], []) : JsonSerializer.Deserialize<WorkHoursCatalog>(row.Payload, Json)! with { RowVersion = row.RowVersion };
    }
    public async Task<bool> SaveCatalogAsync(WorkHoursCatalog catalog, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var sql = catalog.RowVersion == 0 ? "INSERT IGNORE INTO work_hours_management(company_id,payload_json,row_version) VALUES(@CompanyId,@Payload,1)"
            : "UPDATE work_hours_management SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@RowVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql, new { catalog.CompanyId, catalog.RowVersion, Payload = JsonSerializer.Serialize(catalog, Json) }, cancellationToken: ct)) == 1;
    }
}
