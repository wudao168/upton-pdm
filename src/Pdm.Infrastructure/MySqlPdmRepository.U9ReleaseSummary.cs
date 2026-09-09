using System.Data.Common;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private async Task EnqueueU9BomReleaseReadyAsync(
        DbConnection connection,
        DbTransaction transaction,
        ReleasePackage package,
        DateTimeOffset publishedAt,
        Guid? manufacturingBaselineId,
        CancellationToken cancellationToken)
    {
        var priorItems = new List<BomItem>();
        if (package.Scope == ReleaseScope.StandardFormal)
        {
            var rows = await connection.QueryAsync<PriorLongLeadRow>(new CommandDefinition(
                "SELECT standard_bom_snapshot_json,whole_set_multiplier FROM release_package WHERE project_id=@ProjectId AND id<>@Id AND release_scope='StandardLongLead' AND state='Published' AND published_at<=@PublishedAt",
                new { package.ProjectId, package.Id, PublishedAt = publishedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            foreach (var row in rows)
                priorItems.AddRange((JsonSerializer.Deserialize<BomItem[]>(row.StandardBomSnapshotJson, jsonOptions) ?? [])
                    .Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval)
                    .Select(item => item with { Quantity = item.Quantity * Math.Max(1, row.WholeSetMultiplier) }));
        }
        var summary = BomReleaseAggregation.Build(package, priorItems);
        var headerRows = await connection.QueryAsync<BomHeaderCodeRow>(new CommandDefinition(
            "SELECT h.bom_kind,m.u9_item_code AS material_code FROM project_bom_header h INNER JOIN material_master m ON m.id=h.material_id WHERE h.project_id=@ProjectId AND m.u9_sync_confirmed=1 AND m.u9_item_code IS NOT NULL AND m.u9_item_code<>''",
            new { package.ProjectId }, transaction, cancellationToken: cancellationToken));
        var headerCodes = headerRows.ToDictionary(row => row.BomKind, row => row.MaterialCode, StringComparer.OrdinalIgnoreCase);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_outbox(id,event_type,aggregate_type,aggregate_id,payload_json,occurred_at,retry_count)
            VALUES(@Id,'U9BomReleaseReady','ReleasePackage',@AggregateId,@PayloadJson,@OccurredAt,0)
            """,
            new
            {
                Id = Guid.NewGuid(),
                AggregateId = package.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    ReleasePackageId = package.Id,
                    package.ProjectId,
                    ManufacturingBaselineId = manufacturingBaselineId,
                    Scope = package.Scope.ToString(),
                    MasterItemCode = headerCodes.GetValueOrDefault(ProjectBomHeaderKind.Master.ToString()),
                    BomItemCodes = new
                    {
                        Standard = headerCodes.GetValueOrDefault(ProjectBomHeaderKind.Standard.ToString()),
                        NonStandard = headerCodes.GetValueOrDefault(ProjectBomHeaderKind.NonStandard.ToString()),
                        Electrical = headerCodes.GetValueOrDefault(ProjectBomHeaderKind.Electrical.ToString())
                    },
                    SummarySha256 = summary.Sha256,
                    summary.ProductionStructure,
                    summary.PurchaseDemand,
                    Source = "ReleasedSnapshot"
                }, jsonOptions),
                OccurredAt = publishedAt.UtcDateTime
            }, transaction, cancellationToken: cancellationToken));
    }

    private sealed class BomHeaderCodeRow
    {
        public string BomKind { get; init; } = string.Empty;
        public string MaterialCode { get; init; } = string.Empty;
    }

    private sealed class PriorLongLeadRow
    {
        public string StandardBomSnapshotJson { get; init; } = "[]";
        public int WholeSetMultiplier { get; init; } = 1;
    }
}
