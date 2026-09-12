using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryU9ProcurementRepository : IU9ProcurementRepository
{
    private readonly object gate = new();
    private U9ProcurementSyncSettings settings = new(true, 15, "/webapi/QueryCommon/QueryInfoBySql", null, null, null);
    private U9ProcurementSyncRun? latestRun;
    private readonly List<U9ProcurementSnapshotRow> rows = [];

    public Task<U9ProcurementSyncSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(settings);
    }

    public Task<U9ProcurementSyncSettings> SaveSettingsAsync(U9ProcurementSyncSettings value, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            settings = value with { CurrentSnapshotRunId = settings.CurrentSnapshotRunId };
            return Task.FromResult(settings);
        }
    }

    public Task<U9ProcurementSyncRun?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(latestRun);
    }

    public Task<U9ProcurementSyncRun> SaveRunAsync(U9ProcurementSyncRun run, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            latestRun = run;
            return Task.FromResult(run);
        }
    }

    public Task ReplaceSnapshotAsync(Guid snapshotRunId, IReadOnlyList<U9ProcurementSourceRow> sourceRows, DateTimeOffset refreshedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            rows.Clear();
            rows.AddRange(sourceRows.Select(row => new U9ProcurementSnapshotRow(
                snapshotRunId, row.OrganizationCode, row.RecordKind, row.LineId, row.SourcePrLineId,
                row.DocumentNumber, row.LineNumber, row.LineStatus, row.IsCanceled, row.BusinessDate,
                row.MaterialCode, row.ItemName, row.Specification, row.Brand, row.ProjectCode, row.ProjectName,
                row.Subproject, row.RequestedQuantity, row.ApprovedQuantity, row.PurchaseQuantity,
                row.ArrivedQuantity, row.PurchaseRemark, row.DeliveryDate, row.LatestDeliveryDate, refreshedAt)
            {
                SourcePoLineId = row.SourcePoLineId,
                SourceCreatedAt = row.SourceCreatedAt,
                BuyerName = row.BuyerName,
                MovementDate = row.MovementDate,
                MovementQuantity = row.MovementQuantity,
                MovementUnit = row.MovementUnit
            }));
            settings = settings with { CurrentSnapshotRunId = snapshotRunId };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<U9ProcurementSnapshotRow>> ListForProjectAsync(string rootProjectCode, string? subprojectCode, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            bool Matches(U9ProcurementSnapshotRow row) => string.IsNullOrWhiteSpace(subprojectCode)
                ? string.Equals(row.ProjectCode, rootProjectCode, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(row.Subproject)
                : (string.Equals(row.ProjectCode, rootProjectCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Subproject, subprojectCode, StringComparison.OrdinalIgnoreCase))
                  || string.Equals(row.ProjectCode, subprojectCode, StringComparison.OrdinalIgnoreCase);
            var requisitionIds = rows.Where(row => row.RecordKind == U9ProcurementRecordKinds.PurchaseRequisition && Matches(row))
                .Select(row => row.LineId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool MovementMatches(U9ProcurementSnapshotRow row) => !string.IsNullOrWhiteSpace(subprojectCode)
                && row.RecordKind is U9ProcurementRecordKinds.Receipt or U9ProcurementRecordKinds.MaterialIssue or U9ProcurementRecordKinds.MiscShipment or U9ProcurementRecordKinds.TransferReceipt or U9ProcurementRecordKinds.DirectStockIssue
                && string.IsNullOrWhiteSpace(row.ProjectCode)
                && string.Equals(row.Subproject, subprojectCode, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<U9ProcurementSnapshotRow> result = rows
                .Where(row => Matches(row)
                    || MovementMatches(row)
                    || (row.RecordKind == U9ProcurementRecordKinds.PurchaseOrder
                        && requisitionIds.Contains(row.SourcePrLineId ?? string.Empty)))
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
