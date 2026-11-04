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
        // 在发布事务内读取冻结历史；制造结构是完整目标，采购需求只取本次净增加。
        var rows = await connection.QueryAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT p.*, (SELECT s.root_document_id FROM reference_snapshot s WHERE s.id=p.reference_snapshot_id
                AND JSON_TYPE(JSON_EXTRACT(s.root_json,'$.documentId'))='STRING') AS ScopeRootDocumentId
            FROM release_package p WHERE p.project_id=@ProjectId AND p.id<>@Id AND p.state='Published' AND p.published_at<=@PublishedAt
            """, new {package.ProjectId,package.Id,PublishedAt=publishedAt.UtcDateTime},transaction,cancellationToken:cancellationToken));
        var history=rows.Select(row=>new ReleasePackage(row.Id,row.ProjectId,row.PackageNumber,ReleasePackageState.Published,
            row.ReferenceSnapshotId,row.MechanicalBomRevision,row.ElectricalBomRevision,[],
            DateTime.SpecifyKind(row.CreatedAt,DateTimeKind.Utc),new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAt!.Value,DateTimeKind.Utc)),row.PublishedPath)
        {
            Scope=Enum.Parse<ReleaseScope>(row.ReleaseScope),RootDocumentId=row.ScopeRootDocumentId,WholeSetMultiplier=row.WholeSetMultiplier,
            StandardBomVersionId=row.StandardBomVersionId,NonStandardBomVersionId=row.NonStandardBomVersionId,ElectricalBomVersionId=row.ElectricalBomVersionId,
            StandardBomSnapshot=JsonSerializer.Deserialize<BomItem[]>(row.StandardBomSnapshotJson,jsonOptions)??[],
            NonStandardBomSnapshot=JsonSerializer.Deserialize<BomItem[]>(row.NonStandardBomSnapshotJson,jsonOptions)??[],
            ElectricalBomSnapshot=JsonSerializer.Deserialize<BomItem[]>(row.ElectricalBomSnapshotJson,jsonOptions)??[],
            MechanicalBomSnapshot=JsonSerializer.Deserialize<BomItem[]>(row.MechanicalBomSnapshotJson,jsonOptions)??[]
        }).ToArray();
        var summary = BomReleaseAggregation.BuildForHistory(package with{PublishedAt=publishedAt}, history);
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
                    summary.DemandChanges,
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

}
