using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<ReleasePackage> UpdatePublishedDrawingDeliveryAsync(
        Guid releasePackageId, Guid documentId, DrawingDeliveryOverride delivery, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DrawingDeliveryRow>(new CommandDefinition(
            "SELECT state, drawing_delivery_overrides_json FROM release_package WHERE id=@ReleasePackageId FOR UPDATE",
            new { ReleasePackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (row is null) throw new PdmNotFoundException("发布包不存在。");
        if (row.State != nameof(ReleasePackageState.Published)) throw new PdmConflictException("只有已发布图纸可以调整发图信息。");
        var belongs = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM document_version v INNER JOIN document d ON d.id=v.document_id WHERE v.release_package_id=@ReleasePackageId AND v.document_id=@DocumentId AND d.kind='Drawing' AND v.version_status='Released'",
            new { ReleasePackageId = releasePackageId, DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        if (belongs == 0) throw new PdmNotFoundException("该图纸不在发布包中。");
        var overrides = JsonSerializer.Deserialize<Dictionary<Guid, DrawingDeliveryOverride>>(row.DrawingDeliveryOverridesJson ?? "{}", jsonOptions)
            ?? new Dictionary<Guid, DrawingDeliveryOverride>();
        overrides[documentId] = delivery;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET drawing_delivery_overrides_json=@Overrides,row_version=row_version+1 WHERE id=@ReleasePackageId",
            new { ReleasePackageId = releasePackageId, Overrides = JsonSerializer.Serialize(overrides, jsonOptions) },
            transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json) VALUES(@Id,@OccurredAt,@Actor,@Action,@EntityType,@EntityId,@DetailJson)",
            new
            {
                Id = Guid.NewGuid(), OccurredAt = timeProvider.GetUtcNow().UtcDateTime, Actor = actor,
                Action = "production-drawing.delivery.update", EntityType = nameof(DocumentVersion), EntityId = documentId.ToString(),
                DetailJson = JsonSerializer.Serialize(new { detail = $"{releasePackageId}；{delivery.Priority}；需求日期{delivery.RequiredOn:yyyy-MM-dd}" }, jsonOptions)
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
    }

    private sealed class DrawingDeliveryRow
    {
        public string State { get; init; } = string.Empty;
        public string? DrawingDeliveryOverridesJson { get; init; }
    }
}
