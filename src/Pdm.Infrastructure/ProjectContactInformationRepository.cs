using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectContactInformationRepository : IProjectContactInformationRepository
{
    private readonly Dictionary<Guid, ProjectContactInformation> records = [];
    private readonly object gate = new();
    public Task<ProjectContactInformation?> FindAsync(Guid projectId, CancellationToken ct) { lock (gate) return Task.FromResult(records.GetValueOrDefault(projectId)); }
    public Task<ProjectContactInformation> SaveAsync(ProjectContactInformation value, long expectedRowVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((records.GetValueOrDefault(value.ProjectId)?.RowVersion ?? 0) != expectedRowVersion)
                throw new PdmConflictException("客户及发货信息已被修改，请刷新后重试。");
            var saved = value with { RowVersion = expectedRowVersion + 1 };
            records[value.ProjectId] = saved;
            return Task.FromResult(saved);
        }
    }
}

public sealed class MySqlProjectContactInformationRepository(IOptions<PdmDatabaseOptions> options) : IProjectContactInformationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<ProjectContactInformation?> FindAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM project_contact_information WHERE project_id=@projectId", new { projectId }, cancellationToken: ct));
        return payload is null ? null : JsonSerializer.Deserialize<ProjectContactInformation>(payload, JsonOptions);
    }
    public async Task<ProjectContactInformation> SaveAsync(ProjectContactInformation value, long expectedRowVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE", new { value.ProjectId }, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT row_version FROM project_contact_information WHERE project_id=@ProjectId FOR UPDATE", new { value.ProjectId }, transaction, cancellationToken: ct));
        if ((version ?? 0) != expectedRowVersion) throw new PdmConflictException("客户及发货信息已被修改，请刷新后重试。");
        var saved = value with { RowVersion = expectedRowVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_contact_information(project_id,row_version,payload_json) VALUES(@ProjectId,@RowVersion,@Payload) ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Payload", new { saved.ProjectId, saved.RowVersion, Payload = JsonSerializer.Serialize(saved, JsonOptions) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
}
