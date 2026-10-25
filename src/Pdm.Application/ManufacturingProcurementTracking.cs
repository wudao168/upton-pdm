using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ManufacturingProcurementTracking
{
    public static ProjectProcurementTrackingItem Row(Project root, Project project, ReleasePackage package,
        ProductionLine line, ProductionTrackingState? state, BomItem? item, DateOnly? assembly, DateOnly? commissioning)
    {
        state ??= new(package.Id, project.Id, project.OrganizationId ?? Guid.Empty, project.Code, project.Name,
            package.Number, package.Scope, package.PublishedAt!.Value, [line], [], []);
        var demand = ProductionTrackingRules.EffectiveDemand(state, line);
        var batches = state.Lots.Where(lot => lot.LineId == line.Id).Select(lot =>
        {
            decimal Total(string kind) => ProductionTrackingRules.Total(state, lot.Id, kind);
            var quantity = Math.Max(0, lot.Quantity - Total("Cancelled") - Total("Transferred") - Total("TaskCancelled"));
            return new ManufacturingBatch(lot.Id, lot.Route, quantity, ProductionFlowRules.Stage(state, lot),
                lot.Supplier, lot.Assignee, lot.PromisedOn, Math.Min(quantity, Total("ExternalDelivered")),
                Math.Min(quantity, Total("QualityReceived")), ProductionQuantityFlow.Qualified(state, lot), Math.Min(quantity, Total("Issue")),
                state.Movements.Where(m => m.LotId == lot.Id).OrderBy(m => m.OccurredAt).ToArray());
        }).ToArray();
        var active = batches.Where(b => b.Quantity > 0).ToArray();
        var assigned = Math.Max(0, demand - ProductionTrackingRules.Unassigned(state, line));
        var qualified = Math.Min(demand, active.Sum(b => b.QualifiedQuantity));
        var issued = Math.Min(demand, active.Sum(b => b.IssuedQuantity));
        var submitted = Math.Min(demand, active.Sum(b => b.SubmittedQuantity));
        var remaining = active.Where(b => b.QualifiedQuantity < b.Quantity).ToArray();
        var forecastBatches = remaining.Length > 0 ? remaining : active;
        var forecast = assigned >= demand && forecastBatches.Length > 0 && forecastBatches.All(b => b.PromisedOn.HasValue)
            ? forecastBatches.Max(b => b.PromisedOn) : null;
        var movements = state.Lots.Where(lot => lot.LineId == line.Id)
            .SelectMany(lot => ProductionFlowRules.ActiveMovements(state, lot.Id)).ToArray();
        var statuses = active.Select(b => b.Status).Distinct().ToArray();
        var status = demand <= 0 ? "已取消／转版" : state.ReceivedAt is null ? "待生产接收" : assigned <= 0 ? "待分配"
            : active.Any(b => b.Status is "变更待处理" or "已暂停" or "部分暂停" or "返工中") ? string.Join("、", statuses)
            : issued >= demand ? "已领用" : qualified >= demand ? "合格齐套待领用"
            : ProductionTrackingRules.Unassigned(state, line) > 0 && movements.Any(m => m.Kind is "Scrap" or "StageScrap" or "TaskCancelled") ? "数量不足，待补齐"
            : assigned < demand ? "部分分配" : statuses.Length == 1 ? statuses[0] : "分批流转";
        DateTimeOffset? Last(string kind) => movements.Where(m => m.Kind == kind).Select(m => (DateTimeOffset?)m.OccurredAt).Max();
        var revision = !DrawingRevision.IsWork(line.Revision) ? DrawingRevision.Display(line.Revision)
            : item is null ? line.DisplayRevision ?? DrawingRevision.Display(line.Revision) : BomDrawingRevision.ForPackage(item, package);
        var tracking = new ManufacturingTracking(package.Id, line.Id, revision, state.PublishedAt, state.ReceivedAt,
            status, assigned, submitted, qualified, issued, line.RequiredOn, forecast,
            Join(active.Select(b => b.Assignee)), Join(active.Select(b => b.Supplier)),
            movements.Where(m => !string.IsNullOrWhiteSpace(m.Note)).OrderByDescending(m => m.OccurredAt).FirstOrDefault()?.Note,
            Last("Pass"), Last("Issue"), batches);
        return new(0, root.Code, project.ParentProjectId is null ? null : project.Code,
            line.MaterialCode, line.Name, line.DrawingNumber, item?.Remark, item?.Brand, demand,
            "非标件", package.Number, [], "", null, [], "", 0, 0, null, null, null, null, null, [])
        {
            Manufacturing = tracking, ImpactStage = item?.ImpactStage,
            AssemblyStartDate = assembly, CommissioningStartDate = commissioning,
            IsFullyReceived = demand > 0 && qualified >= demand,
            HasDeliveryDelay = qualified < demand && line.RequiredOn.HasValue
                && remaining.Any(b => b.PromisedOn.HasValue && b.PromisedOn > line.RequiredOn)
        };
    }

    private static string? Join(IEnumerable<string?> values)
    {
        var text = string.Join("；", values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase));
        return text.Length == 0 ? null : text;
    }
}
