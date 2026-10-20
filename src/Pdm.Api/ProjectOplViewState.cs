using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class ProjectOplViewState(IOptions<PdmDatabaseOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<(Guid, string), byte> memory = new();
    private bool MySql => string.Equals(options.Value.Provider, "MySql", StringComparison.OrdinalIgnoreCase);
    public async Task<IReadOnlySet<Guid>> UnreadAsync(OplView view, string username, CancellationToken ct)
    {
        var baseline = DateTimeOffset.MinValue;
        HashSet<Guid> seen;
        if (MySql)
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            var enabled = await connection.QuerySingleAsync<DateTime>(new CommandDefinition("SELECT applied_at FROM pdm_schema_migration WHERE version='178_project_opl_views'", cancellationToken: ct));
            baseline = new DateTimeOffset(DateTime.SpecifyKind(enabled, DateTimeKind.Utc));
            seen = (await connection.QueryAsync<Guid>(new CommandDefinition("SELECT viewed.issue_id FROM project_opl_issue_view viewed JOIN project_opl_issue issue ON issue.id=viewed.issue_id WHERE issue.project_id=@ProjectId AND viewed.username=@username", new { ProjectId = view.RootProject.Id, username }, cancellationToken: ct))).ToHashSet();
        }
        else seen = view.Rows.Where(row => memory.ContainsKey((row.Issue.Id, username.ToUpperInvariant()))).Select(row => row.Issue.Id).ToHashSet();
        return view.Rows.Where(row => row.Issue.CreatedAt >= baseline && !string.Equals(row.Issue.CreatedBy, username, StringComparison.OrdinalIgnoreCase) && !seen.Contains(row.Issue.Id)).Select(row => row.Issue.Id).ToHashSet();
    }
    public async Task MarkAsync(Guid id, string username, CancellationToken ct)
    {
        if (!MySql) { memory[(id, username.ToUpperInvariant())] = 0; return; }
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_opl_issue_view(issue_id,username,viewed_at) VALUES(@id,@username,@ViewedAt) ON DUPLICATE KEY UPDATE viewed_at=viewed_at", new { id, username, ViewedAt = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
    }
}
