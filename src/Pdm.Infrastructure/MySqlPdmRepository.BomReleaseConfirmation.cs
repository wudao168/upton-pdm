using System.Text.Json;
using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyDictionary<Guid, Guid>> LatestDocumentVersionIdsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid DocumentId, Guid VersionId)>(new CommandDefinition(
            "SELECT document_id AS DocumentId,id AS VersionId FROM (SELECT v.document_id,v.id,ROW_NUMBER() OVER(PARTITION BY v.document_id ORDER BY v.created_at DESC) AS rn FROM document_version v JOIN document d ON d.id=v.document_id WHERE d.project_id=@ProjectId) latest WHERE rn=1",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.DocumentId, row => row.VersionId);
    }

    public async Task SaveBomReleaseConfirmationsAsync(Guid projectId, IReadOnlyDictionary<Guid, BomReleaseConfirmation?> confirmations, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var pair in confirmations)
        {
            if (pair.Value is null)
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM bom_release_confirmation WHERE project_id=@ProjectId AND bom_item_id=@ItemId",
                    new { ProjectId = projectId, ItemId = pair.Key }, transaction, cancellationToken: cancellationToken));
            else
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO bom_release_confirmation(project_id,bom_item_id,confirmation_json) VALUES(@ProjectId,@ItemId,@Json) ON DUPLICATE KEY UPDATE confirmation_json=VALUES(confirmation_json)",
                    new { ProjectId = projectId, ItemId = pair.Key, Json = JsonSerializer.Serialize(pair.Value, jsonOptions) }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
