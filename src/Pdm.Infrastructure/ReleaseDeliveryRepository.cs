using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryReleaseDeliveryStore(IPdmRepository? repository = null) : IReleaseDeliveryStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ReleaseNotificationConfiguration> configurations = [];
    private readonly Dictionary<Guid, ReleaseDeliveryRecord> records = [];
    private readonly Dictionary<Guid, ReleaseDeliveryClaim> leases = [];
    private static ReleaseDeliveryRecord Copy(ReleaseDeliveryRecord value) => JsonSerializer.Deserialize<ReleaseDeliveryRecord>(JsonSerializer.Serialize(value))!;
    public Task<ReleaseNotificationConfiguration?> FindConfigurationAsync(Guid id, CancellationToken ct) { lock (gate) return Task.FromResult(configurations.GetValueOrDefault(id)); }
    public Task<ReleaseNotificationConfiguration> SaveConfigurationAsync(ReleaseNotificationConfiguration value, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((configurations.GetValueOrDefault(value.CompanyId)?.RowVersion ?? 0) != expectedVersion) throw new PdmConflictException("通知配置已被修改，请刷新后重试。");
            var saved = value with { RowVersion = expectedVersion + 1 }; configurations[value.CompanyId] = saved; return Task.FromResult(saved);
        }
    }
    public Task<ReleaseDeliveryRecord?> FindAsync(Guid id, CancellationToken ct) { lock (gate) return Task.FromResult(records.TryGetValue(id, out var record) ? Copy(record) : null); }
    public Task<ReleaseDeliveryRecord> CreateAsync(ReleaseDeliveryState state, DateTimeOffset next, CancellationToken ct)
    {
        lock (gate) { if (!records.ContainsKey(state.ReleasePackageId)) records[state.ReleasePackageId] = Copy(new(state, 1, next)); return Task.FromResult(Copy(records[state.ReleasePackageId])); }
    }
    public Task<bool> SaveAsync(ReleaseDeliveryRecord value, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if (!records.TryGetValue(value.State.ReleasePackageId, out var old) || old.RowVersion != expectedVersion) return Task.FromResult(false);
            records[value.State.ReleasePackageId] = Copy(value with { RowVersion = expectedVersion + 1 }); return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<ReleaseDeliveryRecord>> ListProjectAsync(Guid id, CancellationToken ct) { lock (gate) return Task.FromResult<IReadOnlyList<ReleaseDeliveryRecord>>(records.Values.Where(item => item.State.ProjectId == id).Select(Copy).ToArray()); }
    public Task<IReadOnlyList<ReleaseDeliveryClaim>> ClaimDueAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        lock (gate)
        {
            var due = records.Values.Where(value => value.NextCheckAt <= now && (!leases.TryGetValue(value.State.ReleasePackageId, out var lease) || lease.LeaseUntil <= now))
                .OrderBy(value => value.NextCheckAt).Take(Math.Clamp(count, 1, 50))
                .Select(value => new ReleaseDeliveryClaim(value.State.ReleasePackageId, value.State.CompanyId, Guid.NewGuid(), now.AddMinutes(3))).ToArray();
            foreach (var claim in due) leases[claim.ReleasePackageId] = claim;
            return Task.FromResult<IReadOnlyList<ReleaseDeliveryClaim>>(due);
        }
    }
    public Task ReleaseClaimAsync(ReleaseDeliveryClaim claim, CancellationToken ct)
    { lock (gate) { if (leases.GetValueOrDefault(claim.ReleasePackageId)?.LeaseId == claim.LeaseId) leases.Remove(claim.ReleasePackageId); } return Task.CompletedTask; }
    public Task<bool> RenewClaimAsync(ReleaseDeliveryClaim claim, DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(claim.ReleasePackageId, out var held) || held.LeaseId != claim.LeaseId || held.LeaseUntil <= now) return Task.FromResult(false);
            leases[claim.ReleasePackageId] = held with { LeaseUntil = now.AddMinutes(3) }; return Task.FromResult(true);
        }
    }
    public async Task<IReadOnlyList<Guid>> ListUntrackedPublishedAsync(int count, CancellationToken ct)
    {
        if (repository is null) return [];
        ReleaseNotificationConfiguration[] enabled; HashSet<Guid> tracked;
        lock (gate) { enabled = configurations.Values.Where(value => value.Enabled && value.EnabledAt.HasValue).ToArray(); tracked = records.Keys.ToHashSet(); }
        var result = new List<Guid>();
        foreach (var project in await repository.ListProjectsAsync(ct))
        {
            var config = enabled.FirstOrDefault(value => value.CompanyId == project.OrganizationId);
            if (config is null) continue;
            result.AddRange((await repository.ListReleasePackagesAsync(project.Id, ct)).Where(value => value.State == Upton.Pdm.Domain.ReleasePackageState.Published
                && value.PublishedAt >= config.EnabledAt && !tracked.Contains(value.Id)).Select(value => value.Id));
            if (result.Count >= count) break;
        }
        return result.Take(count).ToArray();
    }
}

public sealed class MySqlReleaseDeliveryStore(IOptions<PdmDatabaseOptions> options) : IReleaseDeliveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Connection() => new(options.Value.ConnectionString);
    private sealed class ConfigurationRow
    {
        public Guid CompanyId { get; set; }
        public string ProtectedPayload { get; set; } = "";
        public long RowVersion { get; set; }
        public bool Enabled { get; set; }
        public DateTime? EnabledAt { get; set; }
    }
    private sealed class StateRow
    {
        public Guid ReleasePackageId { get; set; }
        public Guid CompanyId { get; set; }
        public string PayloadJson { get; set; } = "";
        public long RowVersion { get; set; }
        public DateTime? NextCheckAt { get; set; }
        public ReleaseDeliveryRecord Value() => new(JsonSerializer.Deserialize<ReleaseDeliveryState>(PayloadJson, Json)!, RowVersion,
            NextCheckAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(NextCheckAt.Value, DateTimeKind.Utc)) : null);
    }
    private const string Select = "SELECT release_package_id ReleasePackageId,company_id CompanyId,payload_json PayloadJson,row_version RowVersion,next_check_at NextCheckAt FROM release_delivery_state";
    public async Task<ReleaseNotificationConfiguration?> FindConfigurationAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<ConfigurationRow>(new CommandDefinition("SELECT company_id CompanyId,protected_payload ProtectedPayload,row_version RowVersion,enabled Enabled,enabled_at EnabledAt FROM release_notification_configuration WHERE company_id=@companyId", new { companyId }, cancellationToken: ct));
        return row is null ? null : new(row.CompanyId, row.ProtectedPayload, row.RowVersion, row.Enabled,
            row.EnabledAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(row.EnabledAt.Value, DateTimeKind.Utc)) : null);
    }
    public async Task<ReleaseNotificationConfiguration> SaveConfigurationAsync(ReleaseNotificationConfiguration value, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO release_notification_configuration(company_id,protected_payload,row_version) VALUES(@CompanyId,'',0)", value, tx, cancellationToken: ct));
        var version = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM release_notification_configuration WHERE company_id=@CompanyId FOR UPDATE", value, tx, cancellationToken: ct));
        if (version != expectedVersion) throw new PdmConflictException("通知配置已被修改，请刷新后重试。");
        var saved = value with { RowVersion = expectedVersion + 1 };
        await db.ExecuteAsync(new CommandDefinition("UPDATE release_notification_configuration SET protected_payload=@ProtectedPayload,row_version=@RowVersion,enabled=@Enabled,enabled_at=@EnabledAt WHERE company_id=@CompanyId",
            new { saved.CompanyId, saved.ProtectedPayload, saved.RowVersion, saved.Enabled, EnabledAt = saved.EnabledAt?.UtcDateTime }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return saved;
    }
    public async Task<ReleaseDeliveryRecord?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = Connection(); return (await db.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(Select + " WHERE release_package_id=@id", new { id }, cancellationToken: ct)))?.Value();
    }
    public async Task<ReleaseDeliveryRecord> CreateAsync(ReleaseDeliveryState value, DateTimeOffset next, CancellationToken ct)
    {
        await using var db = Connection();
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO release_delivery_state(release_package_id,company_id,project_id,payload_json,row_version,next_check_at) VALUES(@ReleasePackageId,@CompanyId,@ProjectId,@Payload,1,@Next)",
            new { value.ReleasePackageId, value.CompanyId, value.ProjectId, Payload = JsonSerializer.Serialize(value, Json), Next = next.UtcDateTime }, cancellationToken: ct));
        return (await FindAsync(value.ReleasePackageId, ct))!;
    }
    public async Task<bool> SaveAsync(ReleaseDeliveryRecord value, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        return await db.ExecuteAsync(new CommandDefinition("UPDATE release_delivery_state SET payload_json=@Payload,row_version=row_version+1,next_check_at=@Next WHERE release_package_id=@Id AND company_id=@CompanyId AND row_version=@Expected",
            new { Payload = JsonSerializer.Serialize(value.State, Json), Next = value.NextCheckAt?.UtcDateTime, Id = value.State.ReleasePackageId, value.State.CompanyId, Expected = expectedVersion }, cancellationToken: ct)) == 1;
    }
    public async Task<IReadOnlyList<ReleaseDeliveryRecord>> ListProjectAsync(Guid id, CancellationToken ct)
    {
        await using var db = Connection();
        return (await db.QueryAsync<StateRow>(new CommandDefinition(Select + " WHERE project_id=@id", new { id }, cancellationToken: ct))).Select(row => row.Value()).ToArray();
    }
    public async Task<IReadOnlyList<ReleaseDeliveryClaim>> ClaimDueAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        var due = (await db.QueryAsync<StateRow>(new CommandDefinition(Select + " WHERE next_check_at<=@Now AND (lease_until IS NULL OR lease_until<=@Now) ORDER BY next_check_at LIMIT @Count FOR UPDATE SKIP LOCKED",
            new { Now = now.UtcDateTime, Count = Math.Clamp(count, 1, 50) }, tx, cancellationToken: ct))).ToArray();
        var claims = due.Select(row => new ReleaseDeliveryClaim(row.ReleasePackageId, row.CompanyId, Guid.NewGuid(), now.AddMinutes(3))).ToArray();
        foreach (var claim in claims) await db.ExecuteAsync(new CommandDefinition("UPDATE release_delivery_state SET lease_id=@LeaseId,lease_until=@Until WHERE release_package_id=@ReleasePackageId",
            new { claim.LeaseId, Until = claim.LeaseUntil.UtcDateTime, claim.ReleasePackageId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return claims;
    }
    public async Task ReleaseClaimAsync(ReleaseDeliveryClaim claim, CancellationToken ct)
    {
        await using var db = Connection();
        await db.ExecuteAsync(new CommandDefinition("UPDATE release_delivery_state SET lease_id=NULL,lease_until=NULL WHERE release_package_id=@ReleasePackageId AND lease_id=@LeaseId", claim, cancellationToken: ct));
    }
    public async Task<bool> RenewClaimAsync(ReleaseDeliveryClaim claim, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = Connection();
        return await db.ExecuteAsync(new CommandDefinition("UPDATE release_delivery_state SET lease_until=@Until WHERE release_package_id=@ReleasePackageId AND lease_id=@LeaseId AND lease_until>@Now",
            new { claim.ReleasePackageId, claim.LeaseId, Now = now.UtcDateTime, Until = now.AddMinutes(3).UtcDateTime }, cancellationToken: ct)) == 1;
    }
    public async Task<IReadOnlyList<Guid>> ListUntrackedPublishedAsync(int count, CancellationToken ct)
    {
        await using var db = Connection();
        return (await db.QueryAsync<Guid>(new CommandDefinition("""
            SELECT rp.id FROM release_package rp
            JOIN project p ON p.id=rp.project_id
            JOIN release_notification_configuration cfg ON cfg.company_id=p.organization_id AND cfg.enabled=1
            LEFT JOIN release_delivery_state tracked ON tracked.release_package_id=rp.id
            WHERE rp.state='Published' AND rp.published_at>=cfg.enabled_at AND tracked.release_package_id IS NULL
            ORDER BY rp.published_at LIMIT @Count
            """, new { Count = Math.Clamp(count, 1, 50) }, cancellationToken: ct))).ToArray();
    }
}
