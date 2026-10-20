using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryWorkflowNotificationStore : IWorkflowNotificationStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, WorkflowNotificationCatalog> catalogs = [];
    private readonly Dictionary<Guid, WorkflowNoticeJob> jobs = [];
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public Task<WorkflowNotificationCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    { lock (gate) return Task.FromResult(Copy(catalogs.GetValueOrDefault(companyId) ?? new(companyId, 0, [], [], []))); }
    public Task<bool> SaveAsync(WorkflowNotificationCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((catalogs.GetValueOrDefault(catalog.CompanyId)?.RowVersion ?? 0) != expectedVersion) return Task.FromResult(false);
            catalogs[catalog.CompanyId] = Copy(catalog with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task CaptureAsync(WorkflowNoticeJob job, CancellationToken ct)
    {
        lock (gate) if (!jobs.Values.Any(item => item.CompanyId == job.CompanyId && item.SourceKey == job.SourceKey)) jobs[job.Id] = Copy(job with { RowVersion = 1 });
        return Task.CompletedTask;
    }
    public Task<WorkflowNoticeJob?> FindJobAsync(Guid id, CancellationToken ct)
    { lock (gate) return Task.FromResult(jobs.TryGetValue(id, out var job) ? Copy(job) : null); }
    public Task<WorkflowNoticeJob?> FindSourceAsync(Guid companyId, string sourceKey, CancellationToken ct)
    { lock (gate) { var job = jobs.Values.FirstOrDefault(item => item.CompanyId == companyId && item.SourceKey == sourceKey); return Task.FromResult(job is null ? null : Copy(job)); } }
    public Task<bool> SaveJobAsync(WorkflowNoticeJob job, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(job.Id, out var before) || before.RowVersion != expectedVersion) return Task.FromResult(false);
            jobs[job.Id] = Copy(job with { RowVersion = expectedVersion + 1 });
            return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<WorkflowNoticeJob>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<WorkflowNoticeJob>>(jobs.Values.Where(item => (item.State is "Waiting" or "Ready" or "Attention") && item.NextCheckAt <= now).OrderBy(item => item.NextCheckAt).Take(take).Select(Copy).ToArray()); }
    public Task<IReadOnlyList<WorkflowNoticeJob>> ListJobsAsync(Guid companyId, string flowCode, CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<WorkflowNoticeJob>>(jobs.Values.Where(item => item.CompanyId == companyId && item.Rule.FlowCode == flowCode).OrderByDescending(item => item.CreatedAt).Take(20).Select(Copy).ToArray()); }
}

public sealed class MySqlWorkflowNotificationStore(IOptions<PdmDatabaseOptions> options) : IWorkflowNotificationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Connection() => new(options.Value.ConnectionString);
    public async Task<WorkflowNotificationCatalog> ReadAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<Payload>(new CommandDefinition("SELECT payload_json PayloadJson,row_version Version FROM workflow_notification_catalog WHERE company_id=@companyId", new { companyId }, cancellationToken: ct));
        return row is null ? new(companyId, 0, [], [], []) : JsonSerializer.Deserialize<WorkflowNotificationCatalog>(row.PayloadJson, Json)! with { RowVersion = row.Version };
    }
    public async Task<bool> SaveAsync(WorkflowNotificationCatalog catalog, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        var sql = expectedVersion == 0 ? "INSERT IGNORE INTO workflow_notification_catalog(company_id,payload_json,row_version) VALUES(@CompanyId,@json,1)"
            : "UPDATE workflow_notification_catalog SET payload_json=@json,row_version=row_version+1 WHERE company_id=@CompanyId AND row_version=@expectedVersion";
        return await db.ExecuteAsync(new CommandDefinition(sql, new { catalog.CompanyId, json = JsonSerializer.Serialize(catalog, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    public async Task CaptureAsync(WorkflowNoticeJob job, CancellationToken ct)
    {
        await using var db = Connection();
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO workflow_notification_job(id,company_id,source_key,flow_code,state,next_check_at,created_at,payload_json,row_version) VALUES(@Id,@CompanyId,@SourceKey,@flow,@State,@next,@created,@json,1)",
            new { job.Id, job.CompanyId, job.SourceKey, flow = job.Rule.FlowCode, job.State, next = job.NextCheckAt.UtcDateTime, created = job.CreatedAt.UtcDateTime, json = JsonSerializer.Serialize(job, Json) }, cancellationToken: ct));
    }
    public async Task<WorkflowNoticeJob?> FindJobAsync(Guid id, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<Payload>(new CommandDefinition("SELECT payload_json PayloadJson,row_version Version FROM workflow_notification_job WHERE id=@id", new { id }, cancellationToken: ct));
        return row is null ? null : Job(row);
    }
    public async Task<bool> SaveJobAsync(WorkflowNoticeJob job, long expectedVersion, CancellationToken ct)
    {
        await using var db = Connection();
        return await db.ExecuteAsync(new CommandDefinition("UPDATE workflow_notification_job SET state=@State,next_check_at=@next,payload_json=@json,row_version=row_version+1 WHERE id=@Id AND row_version=@expectedVersion",
            new { job.Id, job.State, next = job.NextCheckAt.UtcDateTime, json = JsonSerializer.Serialize(job, Json), expectedVersion }, cancellationToken: ct)) == 1;
    }
    public async Task<WorkflowNoticeJob?> FindSourceAsync(Guid companyId, string sourceKey, CancellationToken ct)
    {
        await using var db = Connection();
        var row = await db.QuerySingleOrDefaultAsync<Payload>(new CommandDefinition("SELECT payload_json PayloadJson,row_version Version FROM workflow_notification_job WHERE company_id=@companyId AND source_key=@sourceKey", new { companyId, sourceKey }, cancellationToken: ct));
        return row is null ? null : Job(row);
    }
    public async Task<IReadOnlyList<WorkflowNoticeJob>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        await using var db = Connection();
        return (await db.QueryAsync<Payload>(new CommandDefinition("SELECT payload_json PayloadJson,row_version Version FROM workflow_notification_job WHERE state IN ('Waiting','Ready','Attention') AND next_check_at<=@now ORDER BY next_check_at,id LIMIT @take", new { now = now.UtcDateTime, take }, cancellationToken: ct))).Select(Job).ToArray();
    }
    public async Task<IReadOnlyList<WorkflowNoticeJob>> ListJobsAsync(Guid companyId, string flowCode, CancellationToken ct)
    {
        await using var db = Connection();
        return (await db.QueryAsync<Payload>(new CommandDefinition("SELECT payload_json PayloadJson,row_version Version FROM workflow_notification_job WHERE company_id=@companyId AND flow_code=@flowCode ORDER BY created_at DESC,id DESC LIMIT 20", new { companyId, flowCode }, cancellationToken: ct))).Select(Job).ToArray();
    }
    private static WorkflowNoticeJob Job(Payload row) => JsonSerializer.Deserialize<WorkflowNoticeJob>(row.PayloadJson, Json)! with { RowVersion = row.Version };
    private sealed record Payload(string PayloadJson, long Version);
}
