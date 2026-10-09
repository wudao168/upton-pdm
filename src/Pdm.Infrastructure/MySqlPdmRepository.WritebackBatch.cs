using Dapper;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task EnqueueCadPropertyWritebackBatchAsync(IReadOnlyList<CadPropertyWriteback> requests,
        IReadOnlyList<AuditEntry> audits, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var itemIds = requests.Select(request => request.BomItemId).Distinct().ToArray();
        if (itemIds.Length > 0)
            await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM bom_item WHERE id IN @Ids ORDER BY id FOR UPDATE",
                new { Ids = itemIds }, transaction, cancellationToken: cancellationToken));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var group in requests.GroupBy(request => request.SourceDocumentId))
            foreach (var ids in group.Select(request => request.BomItemId).Distinct().Chunk(300))
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE cad_property_writeback SET status='Superseded',completed_at=@Now WHERE source_document_id=@DocumentId AND bom_item_id IN @Ids AND status IN ('Pending','InProgress')",
                    new { DocumentId = group.Key, Ids = ids, Now = now }, transaction, cancellationToken: cancellationToken));
        foreach (var batch in requests.Chunk(100))
        {
            var parameters = new DynamicParameters();
            var values = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var request = batch[i];
                parameters.Add($"Id{i}", request.Id);
                parameters.Add($"Project{i}", request.ProjectId);
                parameters.Add($"Item{i}", request.BomItemId);
                parameters.Add($"Document{i}", request.SourceDocumentId);
                parameters.Add($"Configuration{i}", request.SourceConfiguration);
                parameters.Add($"Version{i}", request.ExpectedVersionId);
                parameters.Add($"Revision{i}", request.ExpectedRevision);
                parameters.Add($"Payload{i}", JsonSerializer.Serialize(request.Properties, jsonOptions));
                parameters.Add($"Status{i}", request.Status.ToString());
                parameters.Add($"Actor{i}", request.RequestedBy);
                parameters.Add($"At{i}", request.RequestedAt.UtcDateTime);
                values.Add($"(@Id{i},@Project{i},@Item{i},@Document{i},@Configuration{i},@Version{i},@Revision{i},@Payload{i},@Status{i},@Actor{i},@At{i})");
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO cad_property_writeback(id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at) VALUES" + string.Join(",", values),
                parameters, transaction, cancellationToken: cancellationToken));
        }
        foreach (var ids in requests.Select(request => request.BomItemId).Distinct().Chunk(300))
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE bom_item SET property_writeback_status='Pending',updated_at=@Now WHERE id IN @Ids",
                new { Ids = ids, Now = now }, transaction, cancellationToken: cancellationToken));
        foreach (var batch in audits.Chunk(100))
        {
            var parameters = new DynamicParameters();
            var values = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var entry = batch[i];
                parameters.Add($"Id{i}", entry.Id);
                parameters.Add($"At{i}", entry.OccurredAt.UtcDateTime);
                parameters.Add($"Actor{i}", entry.Actor);
                parameters.Add($"Action{i}", entry.Action);
                parameters.Add($"Type{i}", entry.EntityType);
                parameters.Add($"Entity{i}", entry.EntityId);
                parameters.Add($"Detail{i}", JsonSerializer.Serialize(new { detail = entry.Detail }, jsonOptions));
                values.Add($"(@Id{i},@At{i},@Actor{i},@Action{i},@Type{i},@Entity{i},@Detail{i})");
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json) VALUES" + string.Join(",", values),
                parameters, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
