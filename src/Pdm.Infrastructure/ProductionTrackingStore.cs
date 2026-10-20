using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProductionTrackingStore(DateTimeOffset? activatedAt = null) : IProductionTrackingStore
{
    private readonly object gate = new();
    private readonly DateTimeOffset activation = activatedAt ?? DateTimeOffset.UtcNow;
    private readonly Dictionary<Guid, ProductionTrackingRecord> records = [];
    private readonly Dictionary<Guid, ProductionCapacityState> capacities = [];
    private readonly Dictionary<Guid, ProductionScanCatalog> scanCatalogs = [];
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public Task<ProductionScanCatalog> ScanCatalogAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Copy(scanCatalogs.GetValueOrDefault(companyId) ?? new ProductionScanCatalog(companyId, [], [])));
    }
    public Task<bool> SaveScanCatalogAsync(ProductionScanCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((scanCatalogs.GetValueOrDefault(catalog.CompanyId)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            scanCatalogs[catalog.CompanyId] = Copy(catalog with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }

    public Task<DateTimeOffset> ActivationAtAsync(CancellationToken ct) => Task.FromResult(activation);
    public Task<IReadOnlyList<ProductionTrackingRecord>> ListProjectAsync(Guid projectId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProductionTrackingRecord>>(
            records.Values.Where(item => item.State.ProjectId == projectId).Select(Copy).ToArray());
    }
    public Task<IReadOnlyList<ProductionTrackingRecord>> ListCompanyAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProductionTrackingRecord>>(
            records.Values.Where(item => item.State.CompanyId == companyId).Select(Copy).ToArray());
    }
    public Task<ProductionTrackingRecord?> FindAsync(Guid releasePackageId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(records.TryGetValue(releasePackageId, out var found) ? Copy(found) : null);
    }
    public Task<ProductionTrackingRecord> CreateAsync(ProductionTrackingState state, CancellationToken ct)
    {
        lock (gate)
        {
            if (!records.TryGetValue(state.ReleasePackageId, out var record))
                records[state.ReleasePackageId] = record = Copy(new ProductionTrackingRecord(state, 1));
            return Task.FromResult(Copy(record));
        }
    }
    public Task<bool> SaveAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if (!records.TryGetValue(record.State.ReleasePackageId, out var current) || current.RowVersion != expectedVersion)
                return Task.FromResult(false);
            records[record.State.ReleasePackageId] = Copy(record with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<ProductionCapacityState> CapacityAsync(Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(capacities.TryGetValue(companyId, out var found)
            ? Copy(found) : new ProductionCapacityState(companyId, []));
    }
    public Task<bool> SaveCapacityAsync(ProductionCapacityState value, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((capacities.GetValueOrDefault(value.CompanyId)?.RowVersion ?? 0) != expectedVersion)
                return Task.FromResult(false);
            capacities[value.CompanyId] = Copy(value with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<bool> SaveScheduleAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if (!records.TryGetValue(record.State.ReleasePackageId, out var current) || current.RowVersion != expectedVersion)
                return Task.FromResult(false);
            ProductionScheduleCapacity.Validate(capacities.GetValueOrDefault(record.State.CompanyId)
                ?? new ProductionCapacityState(record.State.CompanyId, []),
                records.Values.Where(item => item.State.CompanyId == record.State.CompanyId &&
                    item.State.ReleasePackageId != record.State.ReleasePackageId).Select(item => item.State)
                    .Append(record.State).ToArray());
            records[record.State.ReleasePackageId] = Copy(record with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<bool> SaveTransferAsync(ProductionTrackingRecord previous, long previousVersion,
        ProductionTrackingRecord current, long currentVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if (!records.TryGetValue(previous.State.ReleasePackageId, out var previousSaved)
                || !records.TryGetValue(current.State.ReleasePackageId, out var currentSaved)
                || previousSaved.RowVersion != previousVersion || currentSaved.RowVersion != currentVersion
                || previous.State.ProjectId != current.State.ProjectId || previous.State.CompanyId != current.State.CompanyId)
                return Task.FromResult(false);
            records[previous.State.ReleasePackageId] = Copy(previous with { RowVersion = previousVersion + 1 });
            records[current.State.ReleasePackageId] = Copy(current with { RowVersion = currentVersion + 1 });
            return Task.FromResult(true);
        }
    }
}

internal static class ProductionScheduleCapacity
{
    public static void Validate(ProductionCapacityState capacity, IReadOnlyList<ProductionTrackingState> releases)
    {
        var usage = releases.SelectMany(item => item.Lots)
            .Where(lot => lot.Route == "Self")
            .SelectMany(lot => lot.Schedule)
            .GroupBy(slot => (slot.ResourceGroupId, slot.Date));
        foreach (var group in usage)
        {
            var available = capacity.Days.FirstOrDefault(day => day.ResourceGroupId == group.Key.ResourceGroupId
                && day.Date == group.Key.Date);
            if (available is null) throw new PdmRuleException("资源组当天尚未设置可用工时，请先维护产能日历。");
            if (group.Sum(slot => slot.Hours) > available.AvailableHours)
                throw new PdmConflictException($"{group.Key.ResourceGroupId} 在 {group.Key.Date:yyyy-MM-dd} 的排产超过可用工时，请调整日期或工时。");
        }
    }
}

public sealed class MySqlProductionTrackingStore(IOptions<PdmDatabaseOptions> options) : IProductionTrackingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Connection() => new(options.Value.ConnectionString);
    public async Task<ProductionScanCatalog> ScanCatalogAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_scan_catalog WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        return row is null ? new(companyId, [], []) : JsonSerializer.Deserialize<ProductionScanCatalog>(row.PayloadJson, Json)! with { RowVersion = row.RowVersion };
    }
    public async Task<bool> SaveScanCatalogAsync(ProductionScanCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        var sql = expectedVersion == 0 ? "INSERT IGNORE INTO production_scan_catalog(company_id,payload_json,row_version) VALUES(@CompanyId,@Payload,1)"
            : "UPDATE production_scan_catalog SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql, new { catalog.CompanyId, Payload = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }

    private sealed class TrackingRow
    {
        public string PayloadJson { get; set; } = "";
        public long RowVersion { get; set; }
        public ProductionTrackingRecord ToRecord() =>
            new(JsonSerializer.Deserialize<ProductionTrackingState>(PayloadJson, Json)!, RowVersion);
    }

    public async Task<DateTimeOffset> ActivationAtAsync(CancellationToken ct)
    {
        await using var db = Connection();
        var value = await db.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT activated_at FROM production_tracking_activation WHERE id=1", cancellationToken: ct));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
    public async Task<IReadOnlyList<ProductionTrackingRecord>> ListProjectAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = Connection();
        var rows = await db.QueryAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_tracking_state WHERE project_id=@projectId",
            new { projectId }, cancellationToken: ct));
        return rows.Select(item => item.ToRecord()).ToArray();
    }
    public async Task<IReadOnlyList<ProductionTrackingRecord>> ListCompanyAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = Connection();
        var rows = await db.QueryAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_tracking_state WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        return rows.Select(item => item.ToRecord()).ToArray();
    }
    public async Task<ProductionTrackingRecord?> FindAsync(Guid releasePackageId, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_tracking_state WHERE release_package_id=@releasePackageId",
            new { releasePackageId }, cancellationToken: ct));
        return row?.ToRecord();
    }
    public async Task<ProductionTrackingRecord> CreateAsync(ProductionTrackingState state, CancellationToken ct)
    {
        await using var db = Connection();
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO production_tracking_state(release_package_id,project_id,company_id,payload_json,row_version) VALUES(@ReleasePackageId,@ProjectId,@CompanyId,@Payload,1)",
            new { state.ReleasePackageId, state.ProjectId, state.CompanyId, Payload = JsonSerializer.Serialize(state, Json) },
            cancellationToken: ct));
        return (await FindAsync(state.ReleasePackageId, ct))!;
    }
    public async Task<bool> SaveAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        var count = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE production_tracking_state SET payload_json=@Payload,row_version=row_version+1 WHERE release_package_id=@Id AND row_version=@expectedVersion",
            new { Id = record.State.ReleasePackageId, Payload = JsonSerializer.Serialize(record.State, Json), expectedVersion },
            cancellationToken: ct));
        return count == 1;
    }
    public async Task<ProductionCapacityState> CapacityAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_capacity_state WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        var value = row is null ? new ProductionCapacityState(companyId, [])
            : JsonSerializer.Deserialize<ProductionCapacityState>(row.PayloadJson, Json)! with { RowVersion = row.RowVersion };
        return value;
    }
    public async Task<bool> SaveCapacityAsync(ProductionCapacityState value, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        var payload = JsonSerializer.Serialize(value with { RowVersion = expectedVersion + 1 }, Json);
        if (expectedVersion == 0)
        {
            var inserted = await db.ExecuteAsync(new CommandDefinition(
                "INSERT IGNORE INTO production_capacity_state(company_id,payload_json,row_version) VALUES(@CompanyId,@payload,1)",
                new { value.CompanyId, payload }, cancellationToken: ct));
            return inserted == 1;
        }
        var count = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE production_capacity_state SET payload_json=@payload,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion",
            new { value.CompanyId, payload, expectedVersion }, cancellationToken: ct));
        return count == 1;
    }
    public async Task<bool> SaveScheduleAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var companyId = record.State.CompanyId;
        var empty = JsonSerializer.Serialize(new ProductionCapacityState(companyId, []), Json);
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO production_capacity_state(company_id,payload_json,row_version) VALUES(@companyId,@empty,1)",
            new { companyId, empty }, tx, cancellationToken: ct));
        var calendar = await db.QuerySingleAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_capacity_state WHERE company_id=@companyId FOR UPDATE",
            new { companyId }, tx, cancellationToken: ct));
        var current = await db.QuerySingleOrDefaultAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_tracking_state WHERE release_package_id=@Id FOR UPDATE",
            new { Id = record.State.ReleasePackageId }, tx, cancellationToken: ct));
        if (current is null || current.RowVersion != expectedVersion) return false;
        var rows = await db.QueryAsync<TrackingRow>(new CommandDefinition(
            "SELECT payload_json PayloadJson,row_version RowVersion FROM production_tracking_state WHERE company_id=@companyId",
            new { companyId }, tx, cancellationToken: ct));
        var states = rows.Select(row => row.ToRecord().State)
            .Where(item => item.ReleasePackageId != record.State.ReleasePackageId)
            .Append(record.State).ToArray();
        ProductionScheduleCapacity.Validate(JsonSerializer.Deserialize<ProductionCapacityState>(calendar.PayloadJson, Json)!, states);
        var count = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE production_tracking_state SET payload_json=@Payload,row_version=row_version+1 WHERE release_package_id=@Id AND row_version=@expectedVersion",
            new { Id = record.State.ReleasePackageId, Payload = JsonSerializer.Serialize(record.State, Json), expectedVersion },
            tx, cancellationToken: ct));
        if (count != 1) return false;
        await tx.CommitAsync(ct);
        return true;
    }
    public async Task<bool> SaveTransferAsync(ProductionTrackingRecord previous, long previousVersion,
        ProductionTrackingRecord current, long currentVersion, CancellationToken ct)
    {
        if (previous.State.ProjectId != current.State.ProjectId || previous.State.CompanyId != current.State.CompanyId
            || previous.State.ReleasePackageId == current.State.ReleasePackageId) return false;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var ids = new[] { previous.State.ReleasePackageId, current.State.ReleasePackageId }.Order().ToArray();
        var versions = new Dictionary<Guid, long>();
        foreach (var id in ids)
        {
            var version = await db.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT row_version FROM production_tracking_state WHERE release_package_id=@id FOR UPDATE",
                new { id }, tx, cancellationToken: ct));
            if (!version.HasValue) return false;
            versions[id] = version.Value;
        }
        if (versions[previous.State.ReleasePackageId] != previousVersion
            || versions[current.State.ReleasePackageId] != currentVersion) return false;
        foreach (var item in new[] { previous, current })
        {
            var count = await db.ExecuteAsync(new CommandDefinition(
                "UPDATE production_tracking_state SET payload_json=@Payload,row_version=row_version+1 WHERE release_package_id=@Id AND row_version=@Version",
                new { Id = item.State.ReleasePackageId, Payload = JsonSerializer.Serialize(item.State, Json),
                    Version = item.State.ReleasePackageId == previous.State.ReleasePackageId ? previousVersion : currentVersion },
                tx, cancellationToken: ct));
            if (count != 1) return false;
        }
        await tx.CommitAsync(ct);
        return true;
    }
}
