using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<ArchiveBatchReceipt?> FindArchiveBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT result_json FROM archive_batch_receipt WHERE batch_id=@BatchId", new { BatchId = batchId }, cancellationToken: cancellationToken));
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ArchiveBatchReceipt>(json, jsonOptions);
    }

    public async Task<ArchiveBatchReceipt> CheckInBatchVersionsAsync(Guid batchId, string requestHash, string actor, Guid sessionId,
        IReadOnlyList<ArchiveBatchFile> files, CancellationToken cancellationToken)
    {
        var ordered = ArchiveBatchRules.Order(files);
        var projectId = ordered[0].Commit.ReferenceSnapshot.ProjectId;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO archive_batch_receipt(batch_id,project_id,actor,request_hash) VALUES(@BatchId,@ProjectId,@Actor,@Hash)",
            new { BatchId = batchId, ProjectId = projectId, Actor = actor, Hash = requestHash }, transaction, cancellationToken: cancellationToken));
        var json = await connection.QuerySingleAsync<string?>(new CommandDefinition(
            "SELECT result_json FROM archive_batch_receipt WHERE batch_id=@BatchId FOR UPDATE", new { BatchId = batchId }, transaction, cancellationToken: cancellationToken));
        if (!string.IsNullOrWhiteSpace(json)) return ArchiveBatchRules.RequireMatchingReceipt(JsonSerializer.Deserialize<ArchiveBatchReceipt>(json, jsonOptions)!, actor, requestHash);
        var ids = ordered.Select(file => file.DocumentId).Concat(ordered.SelectMany(file => ArchiveBatchRules.References(file.Commit.ReferenceSnapshot.Root))
            .Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId!.Value)).Distinct().Order().ToArray();
        foreach (var id in ids)
        {
            await LockDocumentAsync(connection, transaction, id, cancellationToken);
            var locked = await FindDocumentAsync(connection, transaction, id, cancellationToken);
            if (locked?.ProjectId != projectId) throw new PdmRuleException("整批存档包含其他项目的图档，已取消提交。");
        }
        var results = new Dictionary<Guid, DocumentCheckInResult>();
        foreach (var file in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commit = ArchiveBatchRules.BindCommittedReferences(file.Commit, results);
            foreach (var node in ArchiveBatchRules.References(commit.ReferenceSnapshot.Root).Where(node => node.Status != ReferenceNodeStatus.Virtual))
            {
                if (node.DocumentId is not Guid id || node.Revision is null) throw new PdmRuleException("引用图档未完整存档，整批提交已取消：" + node.FileName);
                var document = await FindDocumentAsync(connection, transaction, id, cancellationToken);
                var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM document_version WHERE document_id=@Id AND revision_label=@Revision)",
                    new { Id = id, Revision = node.Revision.Value.Display }, transaction, cancellationToken: cancellationToken));
                if (document is null || document.ProjectId != projectId || document.DeletedAt.HasValue || document.PurgedAt.HasValue
                    || document.Kind != node.Kind || !string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase) || !exists)
                    throw new PdmRuleException("引用图档的项目或存档版本无效，整批提交已取消：" + node.FileName);
            }
            results[file.DocumentId] = await CheckInVersionCoreAsync(connection, transaction, file.DocumentId, actor, sessionId,
                commit, file.DrawingReviewWritebackId, cancellationToken);
        }
        var receipt = new ArchiveBatchReceipt(batchId, projectId, actor, requestHash, ordered.Select(file => results[file.DocumentId]).ToArray());
        await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_batch_receipt SET result_json=@Json,committed_at=@Now WHERE batch_id=@BatchId",
            new { BatchId = batchId, Json = JsonSerializer.Serialize(receipt, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }
}
