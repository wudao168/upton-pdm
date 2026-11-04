using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<ProjectFolderDocumentCount>> ListFolderDocumentCountsAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<ProjectFolderDocumentCount>(new CommandDefinition("""
            SELECT d.folder_id FolderId,d.project_id ProjectId,COUNT(*) Count
            FROM document d
            INNER JOIN project requested ON requested.id=@ProjectId
            INNER JOIN project owner_project ON owner_project.id=d.project_id
            WHERE (owner_project.id=COALESCE(requested.parent_project_id,requested.id)
               OR owner_project.parent_project_id=COALESCE(requested.parent_project_id,requested.id))
              AND d.folder_id IS NOT NULL AND d.deleted_at IS NULL AND d.purged_at IS NULL
            GROUP BY d.folder_id,d.project_id
            """, new { ProjectId = projectId }, cancellationToken: ct))).ToArray();
    }
}
