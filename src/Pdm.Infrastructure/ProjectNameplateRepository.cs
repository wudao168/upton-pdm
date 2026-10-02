using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectNameplateRepository : IProjectNameplateRepository
{
    private readonly Dictionary<Guid, ProjectNameplate> records = [];
    private NameplateTemplate? template;
    private readonly object gate = new();
    public Task<ProjectNameplate?> FindAsync(Guid projectId, CancellationToken ct) { lock (gate) return Task.FromResult(records.GetValueOrDefault(projectId)); }
    public Task<NameplateTemplate?> ReadTemplateAsync(CancellationToken ct) { lock (gate) return Task.FromResult(template); }
    public Task<ProjectNameplate> SaveAsync(ProjectNameplate value, long expectedRowVersion, CancellationToken ct)
    {
        lock (gate) {
            if ((records.GetValueOrDefault(value.ProjectId)?.RowVersion ?? 0) != expectedRowVersion) throw new PdmConflictException("铭牌已被修改，请重新打开后重试。");
            var saved = value with { RowVersion = expectedRowVersion + 1 }; records[value.ProjectId] = saved; return Task.FromResult(saved);
        }
    }
    public Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate value, long expectedRowVersion, CancellationToken ct)
    {
        lock (gate) {
            if ((template?.RowVersion ?? 0) != expectedRowVersion) throw new PdmConflictException("铭牌模板已被修改，请重新打开后重试。");
            template = value with { RowVersion = expectedRowVersion + 1 }; return Task.FromResult(template);
        }
    }
}

public sealed class MySqlProjectNameplateRepository(IOptions<PdmDatabaseOptions> options) : IProjectNameplateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string TemplateKey = "project_nameplate_template";
    public async Task<ProjectNameplate?> FindAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM project_nameplate WHERE project_id=@projectId", new { projectId }, cancellationToken: ct));
        return payload is null ? null : JsonSerializer.Deserialize<ProjectNameplate>(payload, JsonOptions);
    }
    public async Task<ProjectNameplate> SaveAsync(ProjectNameplate value, long expectedRowVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE", new { value.ProjectId }, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT row_version FROM project_nameplate WHERE project_id=@ProjectId FOR UPDATE", new { value.ProjectId }, transaction, cancellationToken: ct));
        if ((version ?? 0) != expectedRowVersion) throw new PdmConflictException("铭牌已被修改，请重新打开后重试。");
        var saved = value with { RowVersion = expectedRowVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_nameplate(project_id,row_version,payload_json) VALUES(@ProjectId,@RowVersion,@Payload) ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Payload", new { saved.ProjectId, saved.RowVersion, Payload = JsonSerializer.Serialize(saved, JsonOptions) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
    public async Task<NameplateTemplate?> ReadTemplateAsync(CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT setting_value FROM pdm_system_setting WHERE setting_key=@Key", new { Key = TemplateKey }, cancellationToken: ct));
        return payload is null ? null : JsonSerializer.Deserialize<NameplateTemplate>(payload, JsonOptions);
    }
    public async Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate value, long expectedRowVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES(@Key,@Payload,UTC_TIMESTAMP(6))", new { Key = TemplateKey, Payload = JsonSerializer.Serialize(NameplateTemplate.Default, JsonOptions) }, transaction, cancellationToken: ct));
        var payload = await connection.QuerySingleAsync<string>(new CommandDefinition("SELECT setting_value FROM pdm_system_setting WHERE setting_key=@Key FOR UPDATE", new { Key = TemplateKey }, transaction, cancellationToken: ct));
        if (JsonSerializer.Deserialize<NameplateTemplate>(payload, JsonOptions)!.RowVersion != expectedRowVersion) throw new PdmConflictException("铭牌模板已被修改，请重新打开后重试。");
        var saved = value with { RowVersion = expectedRowVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition("UPDATE pdm_system_setting SET setting_value=@Payload,updated_at=UTC_TIMESTAMP(6) WHERE setting_key=@Key", new { Key = TemplateKey, Payload = JsonSerializer.Serialize(saved, JsonOptions) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
}
