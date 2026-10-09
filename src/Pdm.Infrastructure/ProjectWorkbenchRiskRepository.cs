using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectWorkbenchRiskRepository : IProjectWorkbenchRiskRepository
{
    private readonly ConcurrentDictionary<Guid, ProjectWorkbenchRisk> values = new();
    public Task<IReadOnlyList<ProjectWorkbenchRisk>> ListAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProjectWorkbenchRisk>>(values.Values.Where(risk => projectIds.Contains(risk.ProjectId)).OrderByDescending(risk => risk.CreatedAt).ToArray());
    public Task<ProjectWorkbenchRisk?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(values.GetValueOrDefault(id));
    public Task SaveAsync(ProjectWorkbenchRisk risk, CancellationToken ct) { values[risk.Id] = risk; return Task.CompletedTask; }
}

public sealed class MySqlProjectWorkbenchRiskRepository(IOptions<PdmDatabaseOptions> options) : IProjectWorkbenchRiskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<ProjectWorkbenchRisk>> ListAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM project_workbench_risk WHERE project_id IN @Ids ORDER BY created_at DESC", new { Ids = projectIds }, cancellationToken: ct));
        return rows.Select(row => JsonSerializer.Deserialize<ProjectWorkbenchRisk>(row, JsonOptions)!).ToArray();
    }
    public async Task<ProjectWorkbenchRisk?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM project_workbench_risk WHERE id=@id", new { id }, cancellationToken: ct));
        return row is null ? null : JsonSerializer.Deserialize<ProjectWorkbenchRisk>(row, JsonOptions);
    }
    public async Task SaveAsync(ProjectWorkbenchRisk risk, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_workbench_risk(id,project_id,created_at,payload_json) VALUES(@Id,@ProjectId,@CreatedAt,@Payload) ON DUPLICATE KEY UPDATE payload_json=@Payload",
            new { risk.Id, risk.ProjectId, CreatedAt = risk.CreatedAt.UtcDateTime, Payload = JsonSerializer.Serialize(risk, JsonOptions) }, cancellationToken: ct));
    }
}
