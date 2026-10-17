using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyDictionary<string, int>> GetProjectPositionUsageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string PositionKey, int ProjectCount)>(new CommandDefinition("SELECT SUBSTRING(assignment_type,6) PositionKey, COUNT(DISTINCT project_id) ProjectCount FROM project_assignment WHERE assignment_type LIKE 'Post:%' GROUP BY assignment_type", cancellationToken: cancellationToken));
        return rows.ToDictionary(item => item.PositionKey, item => item.ProjectCount);
    }

    public async Task<Project> SetCustomProjectPositionsAsync(Guid projectId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_assignment WHERE project_id=@ProjectId AND assignment_type LIKE 'Post:%'", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        foreach (var position in members)
            foreach (var username in position.Value)
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at) VALUES(@ProjectId,@Username,@AssignmentType,@Actor,UTC_TIMESTAMP(6))", new { ProjectId = projectId, Username = username, AssignmentType = "Post:" + position.Key, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
    }
}
