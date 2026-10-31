using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<ArchiveBatchFollowup>> ListArchiveBatchFollowupsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT followup_json FROM archive_batch_followup FORCE INDEX(ix_archive_followup_due) WHERE next_attempt_at<=@Now ORDER BY next_attempt_at LIMIT @Limit FOR UPDATE SKIP LOCKED",
            new { Now = timeProvider.GetUtcNow().UtcDateTime, Limit = Math.Clamp(limit, 1, 20) }, transaction, cancellationToken: cancellationToken));
        var work = rows.Select(json => JsonSerializer.Deserialize<ArchiveBatchFollowup>(json, jsonOptions)!).ToArray();
        foreach (var item in work)
            await connection.ExecuteAsync(new CommandDefinition("UPDATE archive_batch_followup SET next_attempt_at=@Next WHERE batch_id=@Id",
                new { Id = item.Receipt.BatchId, Next = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return work;
    }

    public async Task CompleteArchiveBatchFollowupAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE archive_batch_receipt SET result_json=JSON_SET(result_json,'$.followupPending',CAST('false' AS JSON)) WHERE batch_id=@Id",
            new { Id = batchId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM archive_batch_followup WHERE batch_id=@Id", new { Id = batchId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }
}
