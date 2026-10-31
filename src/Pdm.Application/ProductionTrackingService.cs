using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionLine(
    Guid Id, string MaterialKey, IReadOnlyList<Guid> SourceBomItemIds, string DrawingNumber,
    string Name, string Revision, string Unit, decimal RequiredQuantity, string Priority,
    DateOnly? RequiredOn, string? Material, string? SurfaceTreatment, string? HeatTreatment,
    Guid? SourceDocumentId)
{
    public NameplateContent? NameplateContent { get; init; }
    public string? DisplayRevision { get; init; }
    public string MaterialCode { get; init; } = string.Empty;
    public IReadOnlyList<Guid> SourceTrackingIds { get; init; } = [];
    public Guid? SourceVersionId { get; init; }
    public decimal? SourceUnassignedQuantity { get; init; }
}

public sealed record ProductionLot(
    Guid Id, Guid LineId, string Route, decimal Quantity, string? Supplier, string? Assignee,
    DateOnly? PromisedOn, string? ResourceGroupId, DateOnly? PlannedOn, decimal EstimatedHours,
    bool Paused = false, bool Frozen = false, string? FreezeReason = null,
    Guid? FrozenByReleasePackageId = null)
{
    public IReadOnlyList<ProductionScheduleSlot> Schedule { get; init; } = [];
    public Guid? SupplierId { get; init; }
}

public sealed record ProductionScheduleSlot(string ResourceGroupId, DateOnly Date, decimal Hours);

public sealed record ProductionMovement(
    Guid Id, Guid RequestId, Guid LotId, string Kind, decimal Quantity, string Actor,
    DateTimeOffset OccurredAt, string? Note = null, string? Recipient = null)
{
    public string? ActorName { get; init; }
    public string? ActorRoles { get; init; }
    public Guid? BindingId { get; init; }
    public string? Post { get; init; }
    public Guid? WorkstationId { get; init; }
    public string? WorkstationName { get; init; }
    public string? ResourceGroup { get; init; }
    public string? FromStage { get; init; }
    public string? ToStage { get; init; }
    public bool ResetsFlow { get; init; }
    public string? OperationName { get; init; }
    public string? SourceKind { get; init; }
    public IReadOnlyList<ProductionFlowCredit> FlowCredits { get; init; } = [];
}

public sealed record ProductionTrackingState(
    Guid ReleasePackageId, Guid ProjectId, Guid CompanyId, string ProjectCode, string ProjectName,
    string PackageNumber, ReleaseScope Scope, DateTimeOffset PublishedAt,
    IReadOnlyList<ProductionLine> Lines, IReadOnlyList<ProductionLot> Lots,
    IReadOnlyList<ProductionMovement> Movements, string? ReceivedBy = null,
    DateTimeOffset? ReceivedAt = null)
{
    public string FlowMode { get; init; } = "ResourceOperations";
    public IReadOnlyList<ProductionFlowNode>? FlowNodes { get; init; }
    public Guid? FormalReleasePackageId { get; init; }
    public IReadOnlyList<ProductionLineAdjustment> DeliveryAdjustments { get; init; } = [];
    public IReadOnlyList<ProductionRevisionDecision> RevisionDecisions { get; init; } = [];
    public IReadOnlyList<ProductionDemandCancellation> DemandCancellations { get; init; } = [];
}

public sealed record ProductionDemandCancellation(Guid RequestId, Guid LineId, decimal Quantity, string Reason, string Actor, DateTimeOffset OccurredAt);
public sealed record CancelUnassignedProductionDemand(Guid LineId, decimal Quantity, string Reason, Guid RequestId);

public sealed record ProductionLineAdjustment(Guid Id, Guid RequestId, Guid LineId,
    string Priority, DateOnly? RequiredOn, string Reason, string Actor, DateTimeOffset UpdatedAt);
public sealed record ProductionRevisionDecision(Guid RequestId, Guid OldLotId,
    Guid ChangedReleasePackageId, string Action, decimal Quantity, string Reason,
    string Actor, DateTimeOffset DecidedAt);

public sealed record ProductionTrackingRecord(ProductionTrackingState State, long RowVersion);

public sealed record ProductionCapacityDay(string ResourceGroupId, DateOnly Date, decimal AvailableHours);
public sealed record ProductionCapacityState(Guid CompanyId, IReadOnlyList<ProductionCapacityDay> Days,
    long RowVersion = 0);
public sealed record ProductionScheduleInput(Guid LotId, IReadOnlyList<ProductionScheduleSlot> Slots,
    DateOnly PromisedOn, Guid RequestId, string? ChangeReason = null);
public sealed record ProductionCapacityInput(string ResourceGroupId, DateOnly Date,
    decimal AvailableHours, long ExpectedRowVersion);
public sealed record ProductionCapacityUsage(string ResourceGroupId, DateOnly Date, decimal AvailableHours,
    decimal CommittedHours);
public sealed record ProductionScanResult(Guid ReleasePackageId, Guid LineId, Guid? LotId,
    string PackageNumber, string DrawingNumber, string Name, string Revision,
    string Status)
{
    public decimal? RequiredQuantity { get; init; }
    public decimal? UnassignedQuantity { get; init; }
    public decimal? LotQuantity { get; init; }
    public string? Unit { get; init; }
    public bool IsSupplement { get; init; }
    public Guid? RelatedToLineId { get; init; }
}
public sealed record UpdateProductionLineDelivery(Guid LineId, string Priority,
    DateOnly? RequiredOn, string Reason, Guid RequestId);
public sealed record UpdateProductionLotPromise(Guid LotId, DateOnly PromisedOn,
    string Reason, Guid RequestId);
public sealed record ResolveProductionRevision(Guid LotId, Guid ChangedReleasePackageId,
    string Action, string Reason, Guid RequestId, decimal? Quantity = null);
public sealed record ProductionOpeningLot(string MaterialKey, string Route, decimal Quantity,
    string Stage, string? Supplier = null, string? Assignee = null,
    DateOnly? PromisedOn = null, string? Recipient = null);
public sealed record IncludeExistingProductionRelease(DateTimeOffset VerifiedAt, bool Received,
    IReadOnlyList<string> VerifiedMaterialKeys, IReadOnlyList<ProductionOpeningLot> Lots,
    string VerificationNote);

public interface IProductionTrackingStore
{
    Task<ProductionScanCatalog> ScanCatalogAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveScanCatalogAsync(ProductionScanCatalog catalog, long expectedVersion, CancellationToken ct);
    Task<DateTimeOffset> ActivationAtAsync(CancellationToken ct);
    Task<IReadOnlyList<ProductionTrackingRecord>> ListProjectAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<ProductionTrackingRecord>> ListCompanyAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ProductionTrackingRecord>> FindNameplateSerialAsync(string serialNumber, Guid? companyId, CancellationToken ct);
    Task<ProductionTrackingRecord?> FindAsync(Guid releasePackageId, CancellationToken ct);
    Task<ProductionTrackingRecord> CreateAsync(ProductionTrackingState state, CancellationToken ct);
    Task<bool> SaveAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct);
    Task<ProductionCapacityState> CapacityAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveCapacityAsync(ProductionCapacityState value, long expectedVersion, CancellationToken ct);
    Task<bool> SaveScheduleAsync(ProductionTrackingRecord record, long expectedVersion, CancellationToken ct);
    Task<bool> SaveTransferAsync(ProductionTrackingRecord previous, long previousVersion,
        ProductionTrackingRecord current, long currentVersion, CancellationToken ct);
}

public interface IProductionTrackingObserver
{
    Task PublishedAsync(Guid releasePackageId, CancellationToken ct);
}

public sealed record ProductionTrackingProject(Guid Id, string Code, string Name,
    IReadOnlyList<ProductionTrackingSummary> Packages);

public sealed record ProductionTrackingSummary(Guid ReleasePackageId, Guid ProjectId, string ProjectCode,
    string ProjectName, string PackageNumber, ReleaseScope Scope, DateTimeOffset PublishedAt,
    string Status, int TotalLines, int QualifiedLines, int IssuedLines, int UrgentLines,
    int PriorityLines, DateOnly? FirstRequiredOn, DateOnly? ForecastOn,
    int AtRiskLines, int UnplannedLines, decimal TotalQuantity = 0)
{
    public DateTimeOffset? ReceivedAt { get; init; }
    public bool HasReleaseAttention { get; init; }
}

public sealed record AssignProductionLot(Guid LineId, string Route, decimal Quantity, string? Supplier,
    string? Assignee, DateOnly? PromisedOn, Guid RequestId, Guid? SupplierId = null);
public sealed record UpdateProductionLotAllocation(Guid LotId, string Route, decimal Quantity,
    string? Supplier, string? Assignee, DateOnly? PromisedOn, string? Reason,
    Guid RequestId, long ExpectedRowVersion, Guid? SupplierId = null);
public sealed record BatchProductionAllocation(IReadOnlyList<Guid> LineIds, string Route,
    string? Supplier, string? Assignee, DateOnly? PromisedOn, string? Reason,
    Guid RequestId, long ExpectedRowVersion, Guid? SupplierId = null);

public sealed record RecordProductionMovement(Guid LotId, string Kind, decimal Quantity,
    Guid RequestId, string? Note = null, string? Recipient = null, Guid? WorkstationId = null, string? SourceKind = null);

public static class ProductionTrackingRules
{
    public static ProductionTrackingState? SelfProduction(ProductionTrackingState state) => ForRoute(state, "Self");
    public static ProductionTrackingState? ExternalProduction(ProductionTrackingState state) => ForRoute(state, "External");
    private static ProductionTrackingState? ForRoute(ProductionTrackingState state, string route)
    {
        var lots = state.Lots.Where(lot => lot.Route == route).ToArray();
        if (lots.Length == 0) return null;
        var quantities = lots.GroupBy(lot => lot.LineId).ToDictionary(group => group.Key,
            group => group.Sum(lot => lot.Quantity - Total(state, lot.Id, "Scrap") - Total(state, lot.Id, "TaskCancelled")));
        var lotIds = lots.Select(lot => lot.Id).ToHashSet();
        return state with
        {
            Lines = state.Lines.Where(line => quantities.ContainsKey(line.Id))
                .Select(line => line with { RequiredQuantity = quantities[line.Id], SourceUnassignedQuantity = Unassigned(state, line) }).ToArray(),
            Lots = lots, Movements = state.Movements.Where(item => lotIds.Contains(item.LotId)).ToArray()
        };
    }

    public static string? AllocationChangeBlockReason(ProductionTrackingState state, ProductionLot lot)
    {
        if (lot.Frozen || lot.Paused) return "该批次已冻结或暂停，请先处理后再修改分配。";
        return state.Movements.Any(item => item.LotId == lot.Id
            && item.Kind is not ("Assigned" or "Scheduled" or "PromiseChanged" or "AllocationChanged" or "ProductionReceived"))
            ? "该批次已有加工、交付、取消或转版记录，不能修改分配。" : null;
    }

    public static decimal Total(ProductionTrackingState state, Guid lotId, string kind) =>
        ProductionFlowRules.ActiveMovements(state, lotId).Where(item => item.Kind == kind || kind == "Scrap" && item.Kind == "StageScrap").Sum(item => item.Quantity);

    public static decimal Qualified(ProductionTrackingState state, Guid lineId) =>
        state.Lots.Where(lot => lot.LineId == lineId).Sum(lot => ProductionQuantityFlow.Qualified(state, lot));

    public static decimal Unassigned(ProductionTrackingState state, ProductionLine line) => line.SourceUnassignedQuantity ?? Math.Max(0,
        line.RequiredQuantity - state.DemandCancellations.Where(item => item.LineId == line.Id).Sum(item => item.Quantity)
            - state.Lots.Where(lot => lot.LineId == line.Id)
            .Sum(lot => lot.Quantity - Total(state, lot.Id, "Scrap") - Total(state, lot.Id, "TaskCancelled")));

    public static decimal Issued(ProductionTrackingState state, Guid lineId) =>
        state.Lots.Where(lot => lot.LineId == lineId).Sum(lot => Total(state, lot.Id, "Issue"));

    public static decimal EffectiveDemand(ProductionTrackingState state, ProductionLine line) =>
        Math.Max(0, line.RequiredQuantity - state.Lots.Where(lot => lot.LineId == line.Id)
            .Sum(lot => Total(state, lot.Id, "Cancelled") + Total(state, lot.Id, "Transferred"))
            - (line.SourceUnassignedQuantity.HasValue ? 0 : state.DemandCancellations.Where(item => item.LineId == line.Id).Sum(item => item.Quantity)));

    public static int QualifiedLines(ProductionTrackingState state) =>
        state.Lines.Count(line => EffectiveDemand(state, line) > 0
            && Qualified(state, line.Id) >= EffectiveDemand(state, line));

    public static int IssuedLines(ProductionTrackingState state) =>
        state.Lines.Count(line => EffectiveDemand(state, line) > 0
            && Issued(state, line.Id) >= EffectiveDemand(state, line));

    public static string Status(ProductionTrackingState state)
    {
        if (state.ReceivedAt is null) return "待接收";
        if (state.Lots.Any(lot => lot.Frozen && (state.FlowMode != "DepartmentHandover" || ProductionDepartmentFlow.Outstanding(state, lot) > 0) && !state.RevisionDecisions.Any(decision =>
                decision.OldLotId == lot.Id && decision.ChangedReleasePackageId == lot.FrozenByReleasePackageId)))
            return "变更待处理";
        if (state.Lines.Count == 0) return "无新增生产需求";
        if (state.Lines.Any(line => Unassigned(state, line) > 0
            && state.Lots.Any(lot => lot.LineId == line.Id && Total(state, lot.Id, "Scrap") + Total(state, lot.Id, "TaskCancelled") > 0)))
            return "数量不足，待补齐";
        if (state.FlowMode == "DepartmentHandover" && state.Lines.All(line => Issued(state, line.Id) >= EffectiveDemand(state, line)))
            return state.DemandCancellations.Count > 0 || state.Movements.Any(m => m.Kind is "Cancelled" or "Transferred") ? "变更已结案"
                : state.Lots.All(lot => lot.Route == "Self") ? "领料完成" : "已完结";
        if (state.FlowMode == "DepartmentHandover")
        {
            if (state.Lots.Count == 0) return "待分配";
            if (state.Lines.Any(line => state.Lots.Where(lot => lot.LineId == line.Id).Sum(lot => lot.Quantity) < line.RequiredQuantity)) return "部分分配";
            var stages = state.Lots.Select(lot => ProductionFlowRules.Stage(state, lot)).Distinct().ToArray();
            return stages.Length == 1 ? stages[0] : "分批流转";
        }
        if (state.Lines.All(line => Issued(state, line.Id) >= EffectiveDemand(state, line)))
            return state.DemandCancellations.Count > 0 || state.Movements.Any(item => item.Kind is "Transferred" or "Cancelled")
                ? "变更已结案" : "已完结";
        if (state.Lines.Count > 0 && IssuedLines(state) == state.Lines.Count) return "已完结";
        if (state.Lines.Count > 0 && QualifiedLines(state) == state.Lines.Count) return "已齐套待领用";
        if (state.Lots.Count == 0) return "待分配";
        if (state.Lines.Any(line => state.Lots.Where(lot => lot.LineId == line.Id).Sum(lot => lot.Quantity) < line.RequiredQuantity)) return "部分分配";
        if (state.Movements.Any(item => item.Kind is "Start" or "MachiningCompleted" or "Packed" or "ExternalDelivered" or "QualityReceived" or "Pass" or "Issue")) return "执行中";
        if (state.Lots.Any(lot => lot.Route == "Self" && lot.Schedule.Count == 0 && ProductionFlowRules.Received(state, lot) <= 0)) return "待生产签收";
        if (state.Lots.Any(lot => lot.Route == "Self" && lot.Schedule.Count == 0)) return "待排产";
        return state.Lots.All(lot => lot.Route == "External") ? "外协待交货" : "已排产待执行";
    }

    public static ProductionTrackingSummary Summary(ProductionTrackingState state)
    {
        var remaining = state.Lines.Where(line => Issued(state, line.Id) < EffectiveDemand(state, line)).ToArray();
        var scheduled = state.Lots.Where(lot => remaining.Any(line => line.Id == lot.LineId)).ToArray();
        var unplanned = remaining.Count(line => state.Lots.Where(lot => lot.LineId == line.Id)
            .Sum(lot => lot.Quantity) < line.RequiredQuantity ||
            state.Lots.Any(lot => lot.LineId == line.Id && !lot.PromisedOn.HasValue));
        var atRisk = remaining.Count(line => line.RequiredOn.HasValue && state.Lots
            .Where(lot => lot.LineId == line.Id && lot.PromisedOn.HasValue)
            .Any(lot => lot.PromisedOn > line.RequiredOn));
        var forecast = remaining.Length > 0 && remaining.All(line =>
            state.Lots.Where(lot => lot.LineId == line.Id).Sum(lot => lot.Quantity) >= line.RequiredQuantity)
            && scheduled.All(lot => lot.PromisedOn.HasValue)
            ? scheduled.Max(lot => lot.PromisedOn) : null;
        return new(state.ReleasePackageId, state.ProjectId, state.ProjectCode, state.ProjectName,
            state.PackageNumber, state.Scope, state.PublishedAt, Status(state), state.Lines.Count,
            QualifiedLines(state), IssuedLines(state),
            remaining.Count(line => line.Priority == "Urgent"),
            remaining.Count(line => line.Priority == "Priority"),
            remaining.Where(line => line.RequiredOn.HasValue).Select(line => line.RequiredOn).Min(),
            forecast, atRisk, unplanned, state.Lines.Sum(line => line.RequiredQuantity)) { ReceivedAt = state.ReceivedAt };
    }
}

public sealed class ProductionTrackingService(IPdmRepository repository, IProductionTrackingStore store,
    TimeProvider clock, ProductionDrawingService drawings, ProductionSupplierService? suppliers = null,
    ProductionScanService? scan = null) : IProductionTrackingObserver
{
    private readonly ProductionScanService scanService = scan ?? new(repository, store, clock);
    private static bool TrackedScope(ReleaseScope scope) => scope is ReleaseScope.NonStandardLongLead
        or ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.Nameplate;

    public async Task<ProductionTrackingRecord> CancelUnassignedDemandAsync(Guid id, CancelUnassignedProductionDemand input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (input.RequestId == Guid.Empty || input.Quantity <= 0 || string.IsNullOrWhiteSpace(input.Reason))
            throw new PdmRuleException("请填写大于零的取消数量及需求取消原因。");
        var current = await FindAsync(id, actor, role, ct);
        if (!(await scanService.ActionsAsync(current.State, actor, role, ct)).CanCancelUnassignedDemand)
            throw new UnauthorizedAccessException("当前账号未获授权取消制造需求。");
        return await MutateAsync(id, actor, role, state =>
        {
            if (state.DemandCancellations.FirstOrDefault(item => item.RequestId == input.RequestId) is { } existing)
            {
                if (existing.LineId != input.LineId || existing.Quantity != input.Quantity || existing.Actor != actor || existing.Reason != input.Reason.Trim())
                    throw new PdmConflictException("本次操作编号已用于其他需求取消记录，请刷新后重试。");
                return state;
            }
            var line = state.Lines.FirstOrDefault(item => item.Id == input.LineId) ?? throw new PdmNotFoundException("生产物料不存在。");
            if (input.Quantity > ProductionTrackingRules.Unassigned(state, line))
                throw new PdmRuleException("取消数量超过未分配需求；已分配数量请在对应批次按实际环节处理。");
            return state with { DemandCancellations = state.DemandCancellations.Append(new(input.RequestId, input.LineId,
                input.Quantity, input.Reason.Trim(), actor, clock.GetUtcNow())).ToArray() };
        }, ct);
    }

    private async Task<Project> RequireProjectAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("没有查看该项目生产资料的权限。");
        if (project.OrganizationId is Guid companyId &&
            TenantContext.CompanyId is Guid activeCompany && activeCompany != companyId)
            throw new UnauthorizedAccessException("当前公司无权访问该项目生产资料。");
        return project;
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permission, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, ct))
            throw new UnauthorizedAccessException("没有执行此生产操作的权限。");
    }

    public async Task PublishedAsync(Guid releasePackageId, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, ct);
        if (package is null || package.State != ReleasePackageState.Published || !TrackedScope(package.Scope)
            || package.PublishedAt is null || package.PublishedAt.Value < await store.ActivationAtAsync(ct)) return;
        var project = await repository.FindProjectAsync(package.ProjectId, ct)
            ?? throw new PdmNotFoundException("项目不存在。");
        await EnsureProjectAsync(project, ct);
    }

    public async Task<IReadOnlyList<ProductionTrackingProject>> ListAsync(string actor, UserRole role, CancellationToken ct, bool selfOnly = false, bool externalOnly = false)
    {
        if (selfOnly && externalOnly) throw new PdmRuleException("请分别查看自制追溯或外协追溯。");
        var projects = await repository.ListProjectsForUserAsync(actor, role, ct);
        var result = new List<ProductionTrackingProject>();
        foreach (var group in projects.Where(item => item.CanReadContent).Chunk(8))
        {
            var batch = await Task.WhenAll(group.Select(async project =>
            {
                if (!await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct))
                    return (ProductionTrackingProject?)null;
                var releases = await repository.ListReleasePackagesAsync(project.Id, ct);
                var releasesById = releases.ToDictionary(item => item.Id);
                var packages = await EnsureProjectAsync(project, ct, releases);
                var states = packages.Select(item => item.State);
                if (selfOnly) states = states.Select(ProductionTrackingRules.SelfProduction).OfType<ProductionTrackingState>();
                if (externalOnly) states = states.Select(ProductionTrackingRules.ExternalProduction).OfType<ProductionTrackingState>();
                var summaries = states.Select(state => ProductionTrackingRules.Summary(state) with {
                    HasReleaseAttention = releasesById.TryGetValue(state.ReleasePackageId, out var release)
                        && ReleasePreviewPolicy.RequiresConversion(release)
                        && (release.PreviewState is ReleasePreviewState.Pending or ReleasePreviewState.WaitingForServer or ReleasePreviewState.Running or ReleasePreviewState.Failed)
                }).ToArray();
                return summaries.Length == 0 ? null : new ProductionTrackingProject(project.Id,
                    project.Code, project.Name,
                    summaries);
            }));
            result.AddRange(batch.OfType<ProductionTrackingProject>());
        }
        return result.OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProductionTrackingRecord> FindAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, ct)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await RequireProjectAsync(package.ProjectId, actor, role, ct);
        await EnsureProjectAsync(project, ct);
        var record = await store.FindAsync(releasePackageId, ct);
        var result = await scanService.ApplyFlowAsync(record ?? throw new PdmNotFoundException("该发布包尚未纳入生产跟踪。"), ct);
        var frozen = ProductionDrawingQuantities.FrozenItems(package);
        return result with { State = result.State with { Lines = result.State.Lines.Select(line =>
        {
            var source = frozen.FirstOrDefault(item => line.SourceBomItemIds.Contains(item.Id));
            return line with { DisplayRevision = source is null || !DrawingRevision.IsWork(line.Revision)
                ? DrawingRevision.Display(line.Revision) : BomDrawingRevision.ForPackage(source, package) };
        }).ToArray() } };
    }

    internal async Task<IReadOnlyList<ProductionTrackingRecord>> ProcurementAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        // This projection uses the existing procurement view permission, without granting manufacturing operations.
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, ct)
            || project.OrganizationId is Guid company && TenantContext.CompanyId is Guid active && company != active)
            throw new UnauthorizedAccessException("当前用户没有项目采购跟踪查看权限。");
        return await EnsureProjectAsync(project, ct);
    }

    public async Task<ProductionSupplierChoices> ExternalSupplierChoicesAsync(Guid releasePackageId,
        string actor, UserRole role, CancellationToken ct)
    {
        var record = await FindAsync(releasePackageId, actor, role, ct);
        return suppliers is null ? new(record.State.CompanyId, [], [])
            : await suppliers.TrackingChoicesAsync(record.State, ct);
    }

    public async Task<ProductionTrackingState> PreviewExistingAsync(Guid releasePackageId,
        string actor, UserRole role, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, ct)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await RequireProjectAsync(package.ProjectId, actor, role, ct);
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (package.State != ReleasePackageState.Published || !TrackedScope(package.Scope))
            throw new PdmRuleException("只有已生效的非标件发布包可以纳入生产跟踪。");
        if (await store.FindAsync(releasePackageId, ct) is not null)
            throw new PdmRuleException("该发布包已纳入生产跟踪，请直接查看现有记录。");
        return BuildInitial(project, package, await repository.ListReleasePackagesAsync(project.Id, ct),
            (await store.ListProjectAsync(project.Id, ct)).Select(item => item.State).ToArray());
    }

    public async Task<ProductionTrackingRecord> IncludeExistingAsync(Guid releasePackageId,
        IncludeExistingProductionRelease input, string actor, UserRole role, CancellationToken ct)
    {
        var initial = await PreviewExistingAsync(releasePackageId, actor, role, ct);
        if (input.VerifiedAt == default || input.VerifiedAt > clock.GetUtcNow()
            || string.IsNullOrWhiteSpace(input.VerificationNote))
            throw new PdmRuleException("纳入旧发布包前，必须填写核对时间和核对说明。");
        var keys = initial.Lines.Select(line => line.MaterialKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = input.VerifiedMaterialKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!keys.SetEquals(verified) || input.VerifiedMaterialKeys.Count != verified.Count)
            throw new PdmRuleException("必须逐项核对本发布包所有物料的当前数量和生产环节。");
        if (!input.Received && input.Lots.Count > 0)
            throw new PdmRuleException("未接收的发布包不能登记期初生产批次。");
        var lots = new List<ProductionLot>();
        var movements = new List<ProductionMovement>();
        foreach (var opening in input.Lots)
        {
            var line = initial.Lines.FirstOrDefault(item => item.MaterialKey == opening.MaterialKey)
                ?? throw new PdmRuleException("期初批次对应的物料不在本次发布包中。");
            if (opening.Quantity <= 0 || opening.Route is not ("Self" or "External"))
                throw new PdmRuleException("期初批次必须填写正数数量及自制或外协方式。");
            if (opening.Route == "External" && string.IsNullOrWhiteSpace(opening.Supplier))
                throw new PdmRuleException("外协期初批次必须核对供应商。");
            var stages = opening.Route == "Self"
                ? new[] { "Unstarted", "Started", "MachiningCompleted", "Packed", "QualityReceived", "Passed", "Issued" }
                : new[] { "Unstarted", "ExternalDelivered", "QualityReceived", "Passed", "Issued" };
            var stage = Array.IndexOf(stages, opening.Stage);
            if (stage < 0) throw new PdmRuleException("期初批次的生产环节与自制或外协方式不匹配。");
            if (opening.Stage == "Issued" && string.IsNullOrWhiteSpace(opening.Recipient))
                throw new PdmRuleException("已领用的期初批次必须核对实际领料人。");
            var lot = new ProductionLot(Guid.NewGuid(), line.Id, opening.Route, opening.Quantity,
                opening.Supplier?.Trim(), opening.Assignee?.Trim(), opening.PromisedOn, null, null, 0);
            lots.Add(lot);
            for (var index = 1; index <= stage; index++)
            {
                var kind = stages[index] switch
                {
                    "Started" => "Start", "Passed" => "Pass", "Issued" => "Issue",
                    var value => value
                };
                movements.Add(new ProductionMovement(Guid.NewGuid(), Guid.NewGuid(), lot.Id, kind,
                    opening.Quantity, actor, input.VerifiedAt,
                    $"期初核对：{input.VerificationNote.Trim()}", kind == "Issue" ? opening.Recipient?.Trim() : null));
            }
        }
        foreach (var line in initial.Lines)
            if (lots.Where(lot => lot.LineId == line.Id).Sum(lot => lot.Quantity) > line.RequiredQuantity)
                throw new PdmRuleException($"{line.DrawingNumber} 的期初批次数量超过本包需求，请核对。");
        var state = initial with
        {
            Lots = lots, Movements = movements,
            ReceivedAt = input.Received ? input.VerifiedAt : null,
            ReceivedBy = input.Received ? actor : null
        };
        return await store.CreateAsync(state, ct);
    }

    private async Task<IReadOnlyList<ProductionTrackingRecord>> EnsureProjectAsync(Project project, CancellationToken ct, IReadOnlyList<ReleasePackage>? releasePackages = null)
    {
        var packages = (releasePackages ?? await repository.ListReleasePackagesAsync(project.Id, ct))
            .Where(item => item.State == ReleasePackageState.Published && TrackedScope(item.Scope))
            .OrderBy(item => item.PublishedAt).ThenBy(item => item.Id).ToArray();
        var records = (await store.ListProjectAsync(project.Id, ct)).ToDictionary(item => item.State.ReleasePackageId);
        var activatedAt = await store.ActivationAtAsync(ct);
        foreach (var package in packages)
        {
            if (!records.ContainsKey(package.Id))
            {
                if (package.PublishedAt < activatedAt) continue;
                records[package.Id] = await store.CreateAsync(BuildInitial(project, package, packages, records.Values.Select(item => item.State).ToArray()), ct);
            }
            var items = ProductionDrawingQuantities.FrozenItems(package);
            var current = records[package.Id];
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var lines = current.State.Lines.Select(line =>
                {
                    var item = items.FirstOrDefault(item => line.SourceBomItemIds.Contains(item.Id))
                        ?? items.FirstOrDefault(item => string.Equals(BomReleaseAggregation.MaterialKey(item),
                            line.MaterialKey, StringComparison.OrdinalIgnoreCase));
                    return item is null ? line : line with
                    {
                        DrawingNumber = item.Specification?.Trim() ?? string.Empty,
                        MaterialCode = item.DrawingNumber, Name = item.Name, NameplateContent = item.NameplateContent
                    };
                }).ToArray();
                if (lines.SequenceEqual(current.State.Lines)) { records[package.Id] = current; break; }
                var updated = current with { State = current.State with { Lines = lines }, RowVersion = current.RowVersion + 1 };
                if (await store.SaveAsync(updated, current.RowVersion, ct)) { records[package.Id] = updated; break; }
                current = await store.FindAsync(package.Id, ct)
                    ?? throw new PdmNotFoundException("发布跟踪单不存在。");
                if (attempt == 7) throw new PdmConflictException("生产记录正在更新，请刷新后重试。");
            }
        }
        foreach (var formal in packages.Where(item => item.Scope == ReleaseScope.NonStandardWithDrawing))
        {
            var formalKeys = ProductionDrawingQuantities.FrozenItems(formal)
                .Select(BomReleaseAggregation.MaterialKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var longLead in records.Values.Where(item => item.State.Scope == ReleaseScope.NonStandardLongLead
                         && item.State.FormalReleasePackageId is null
                         && item.State.PublishedAt <= formal.PublishedAt
                         && item.State.Lines.Any(line => formalKeys.Contains(line.MaterialKey))).ToArray())
            {
                var current = longLead;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var updated = current with { State = current.State with { FormalReleasePackageId = formal.Id },
                        RowVersion = current.RowVersion + 1 };
                    if (await store.SaveAsync(updated, current.RowVersion, ct)) { records[current.State.ReleasePackageId] = updated; break; }
                    current = await store.FindAsync(current.State.ReleasePackageId, ct)
                        ?? throw new PdmNotFoundException("长交期生产记录不存在。");
                    if (current.State.FormalReleasePackageId.HasValue) { records[current.State.ReleasePackageId] = current; break; }
                    if (attempt == 7) throw new PdmConflictException("长交期生产记录正在更新，请刷新后重试。");
                }
            }
        }
        foreach (var change in packages.Where(item => item.Scope == ReleaseScope.NonStandardSupplement))
        {
            var selected = change.SelectedBomItemIds.ToHashSet();
            var changed = ProductionDrawingQuantities.FrozenItems(change).Where(item => selected.Contains(item.Id)).ToArray();
            var preceding = packages.Where(item => item.PublishedAt < change.PublishedAt
                    && item.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement)
                .OrderByDescending(item => item.PublishedAt).FirstOrDefault();
            var priorItems = preceding is null ? [] : ProductionDrawingQuantities.FrozenItems(preceding);
            var currentItems = ProductionDrawingQuantities.FrozenItems(change);
            var removed = change.ConfirmedRemovedBomItemIds.ToHashSet();
            foreach (var previous in records.Values.Where(item => item.State.PublishedAt < change.PublishedAt
                         && item.State.ReleasePackageId != change.Id).ToArray())
            {
                var affected = previous.State.Lines.Where(line =>
                {
                    if (line.SourceBomItemIds.Any(removed.Contains)) return true;
                    return changed.Any(item =>
                    {
                        if (!line.SourceTrackingIds.Contains(item.ReleaseTrackingId)
                            && line.MaterialKey != BomReleaseAggregation.MaterialKey(item)) return false;
                        var currentQuantity = currentItems.Where(candidate =>
                            candidate.ReleaseTrackingId == item.ReleaseTrackingId).Sum(candidate => candidate.Quantity)
                            * Math.Max(1, change.WholeSetMultiplier);
                        var priorQuantity = priorItems.Where(candidate =>
                            candidate.ReleaseTrackingId == item.ReleaseTrackingId).Sum(candidate => candidate.Quantity)
                            * Math.Max(1, preceding?.WholeSetMultiplier ?? 1);
                        return line.Revision != item.Revision || currentQuantity < priorQuantity;
                    });
                }).Select(line => line.Id).ToHashSet();
                if (affected.Count == 0) continue;
                var current = previous;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var lots = current.State.Lots.Select(lot =>
                    {
                        if (!affected.Contains(lot.LineId) || lot.Frozen ||
                            ProductionTrackingRules.Total(current.State, lot.Id, "Issue")
                                + ProductionTrackingRules.Total(current.State, lot.Id, "Transferred")
                                + ProductionTrackingRules.Total(current.State, lot.Id, "Cancelled")
                                + ProductionTrackingRules.Total(current.State, lot.Id, "TaskCancelled")
                                + ProductionTrackingRules.Total(current.State, lot.Id, "Scrap") >= lot.Quantity ||
                            current.State.RevisionDecisions.Any(decision => decision.OldLotId == lot.Id
                                && decision.ChangedReleasePackageId == change.Id)) return lot;
                        return lot with { Frozen = true, FrozenByReleasePackageId = change.Id,
                            FreezeReason = $"发布包 {change.Number} 的图纸或BOM已变更" };
                    }).ToArray();
                    if (lots.SequenceEqual(current.State.Lots)) break;
                    var updated = current with { State = current.State with { Lots = lots },
                        RowVersion = current.RowVersion + 1 };
                    if (await store.SaveAsync(updated, current.RowVersion, ct)) { records[current.State.ReleasePackageId] = updated; break; }
                    current = await store.FindAsync(current.State.ReleasePackageId, ct)
                        ?? throw new PdmNotFoundException("旧版生产记录不存在。");
                    if (attempt == 7) throw new PdmConflictException("图纸变更正在处理，请刷新后重试。");
                }
            }
        }
        var catalog = await store.ScanCatalogAsync(project.OrganizationId ?? TenantContext.CompanyId ?? Guid.Empty, ct);
        return records.Values.Select(item => item with { State = ProductionDepartmentFlow.ApplyCatalog(item.State, catalog) })
            .OrderByDescending(item => item.State.PublishedAt).ToArray();
    }

    private static ProductionTrackingState BuildInitial(Project project, ReleasePackage package,
        IReadOnlyList<ReleasePackage> history, IReadOnlyList<ProductionTrackingState> tracked)
    {
        var items = ProductionDrawingQuantities.FrozenItems(package);
        var previous = history.Where(item => item.Id != package.Id && item.State == ReleasePackageState.Published
                && item.PublishedAt <= package.PublishedAt && item.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement)
            .OrderByDescending(item => item.PublishedAt).FirstOrDefault();
        var previousItems = previous is null ? [] : ProductionDrawingQuantities.FrozenItems(previous);
        var lines = new List<ProductionLine>();
        foreach (var group in items.GroupBy(BomReleaseAggregation.MaterialKey))
        {
            var first = group.OrderBy(item => item.Sequence).First();
            var total = group.Sum(item => item.Quantity) * Math.Max(1, package.WholeSetMultiplier);
            var trackingIds = group.Select(item => item.ReleaseTrackingId).ToHashSet();
            var prior = package.Scope == ReleaseScope.NonStandardSupplement
                ? previousItems.Where(item => trackingIds.Contains(item.ReleaseTrackingId)
                        || BomReleaseAggregation.MaterialKey(item) == group.Key)
                    .DistinctBy(item => item.Id).Sum(item => item.Quantity) * Math.Max(1, previous?.WholeSetMultiplier ?? 1)
                : package.Scope == ReleaseScope.NonStandardWithDrawing
                    ? tracked.Where(item => item.Scope == ReleaseScope.NonStandardLongLead)
                        .SelectMany(item => item.Lines).Where(item => item.MaterialKey == group.Key
                            || item.SourceTrackingIds.Any(trackingIds.Contains))
                        .Sum(item => item.RequiredQuantity) : 0;
            var quantity = Math.Max(0, total - prior);
            if (quantity == 0) continue;
            var delivery = group.Select(item => package.BomItemDeliveryOverrides.GetValueOrDefault(item.Id))
                .FirstOrDefault(item => item is not null);
            lines.Add(new(Guid.NewGuid(), group.Key, group.Select(item => item.Id).ToArray(), first.Specification?.Trim() ?? string.Empty,
                first.Name, first.Revision, first.Unit, quantity,
                delivery?.Priority ?? package.DrawingPriority,
                delivery?.RequiredOn ?? package.DrawingRequiredOn, first.Material,
                first.SurfaceTreatment, first.HeatTreatment, first.SourceDocumentId)
            {
                DisplayRevision = BomDrawingRevision.ForPackage(first, package),
                MaterialCode = first.DrawingNumber,
                NameplateContent = first.NameplateContent,
                SourceTrackingIds = group.Select(item => item.ReleaseTrackingId).Distinct().ToArray(),
                SourceVersionId = package.VersionBindings?.FirstOrDefault(binding =>
                    binding.DocumentId == first.SourceDocumentId)?.VersionId
            });
        }
        return new(package.Id, project.Id, project.OrganizationId ?? TenantContext.CompanyId ?? Guid.Empty,
            project.Code, project.Name,
            package.Number, package.Scope, package.PublishedAt ?? DateTimeOffset.UtcNow,
            lines, [], []);
    }

    public async Task<ProductionTrackingRecord> ReceiveAsync(Guid id, Guid requestId, string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        return await MutateAsync(id, actor, role, state => state.ReceivedAt.HasValue ? state
            : state with { ReceivedBy = actor, ReceivedAt = clock.GetUtcNow() }, ct);
    }

    public async Task<ProductionTrackingRecord> AssignAsync(Guid id, AssignProductionLot input, string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (input.RequestId == Guid.Empty || input.Quantity <= 0 || input.Route is not ("Self" or "External"))
            throw new PdmRuleException("请选择自制或外协，并填写大于零的分配数量。");
        if (input.Route == "External" && (input.SupplierId.HasValue || suppliers is not null))
        {
            var existing = await FindAsync(id, actor, role, ct);
            if (existing.State.Movements.Any(item => item.RequestId == input.RequestId)) return existing;
            if (suppliers is null) throw new PdmRuleException("供应商台账暂不可用，请联系管理员。");
            var supplier = await suppliers.ResolveAsync(input.SupplierId, input.Supplier, actor, role, ct);
            input = input with { Supplier = supplier.Name, SupplierId = supplier.Id };
        }
        if (input.Route == "External" && (string.IsNullOrWhiteSpace(input.Supplier) || !input.PromisedOn.HasValue))
            throw new PdmRuleException("外协分配必须填写供应商和承诺交期。");
        return await MutateAsync(id, actor, role, state =>
        {
            if (state.Movements.Any(item => item.RequestId == input.RequestId)) return state;
            if (state.ReceivedAt is null) throw new PdmRuleException("请先接收发布包，再分配自制或外协。");
            var line = state.Lines.FirstOrDefault(item => item.Id == input.LineId)
                ?? throw new PdmNotFoundException("生产物料不存在，请刷新发布包明细。");
            if (input.Quantity > ProductionTrackingRules.Unassigned(state, line))
                throw new PdmRuleException("本次分配数量超过尚未分配的需求数量。");
            var lot = new ProductionLot(Guid.NewGuid(), line.Id, input.Route, input.Quantity,
                input.Route == "External" ? input.Supplier?.Trim() : null, input.Assignee?.Trim(), input.PromisedOn,
                null, null, 0) { SupplierId = input.Route == "External" ? input.SupplierId : null };
            return state with { Lots = state.Lots.Append(lot).ToArray(), Movements = state.Movements
                .Append(new ProductionMovement(Guid.NewGuid(), input.RequestId, lot.Id, "Assigned", input.Quantity,
                    actor, clock.GetUtcNow(), input.Route)).ToArray() };
        }, ct);
    }

    public async Task<ProductionTrackingRecord> UpdateAllocationAsync(Guid id, UpdateProductionLotAllocation input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (input.RequestId == Guid.Empty || input.Quantity <= 0 || input.Route is not ("Self" or "External"))
            throw new PdmRuleException("请选择自制或外协，并填写大于零的分配数量。");
        var current = await FindAsync(id, actor, role, ct);
        if (current.State.Movements.Any(item => item.RequestId == input.RequestId)) return current;
        if (current.RowVersion != input.ExpectedRowVersion)
            throw new PdmConflictException("生产分配或流转记录已被修改，请刷新后重新调整。");
        var state = current.State;
        if (state.ReceivedAt is null) throw new PdmRuleException("请先接收发布包，再修改生产分配。");
        var lot = state.Lots.FirstOrDefault(item => item.Id == input.LotId)
            ?? throw new PdmNotFoundException("生产批次不存在，请刷新后重新选择。");
        if (ProductionTrackingRules.AllocationChangeBlockReason(state, lot) is string blocked)
            throw new PdmConflictException(blocked);
        var line = state.Lines.First(item => item.Id == lot.LineId);
        var otherAssigned = state.Lots.Where(item => item.LineId == line.Id && item.Id != lot.Id)
            .Sum(item => item.Quantity - ProductionTrackingRules.Total(state, item.Id, "TaskCancelled"));
        var scrapped = state.Lots.Where(item => item.LineId == line.Id)
            .Sum(item => ProductionTrackingRules.Total(state, item.Id, "Scrap"));
        if (otherAssigned + input.Quantity > line.RequiredQuantity + scrapped - state.DemandCancellations.Where(item => item.LineId == line.Id).Sum(item => item.Quantity))
            throw new PdmRuleException("修改后的分配数量超过物料需求，请保留其他批次已分配的数量。");
        if (input.Route == "External" && (input.SupplierId.HasValue || suppliers is not null))
        {
            if (suppliers is null) throw new PdmRuleException("供应商台账暂不可用，请联系管理员。");
            var supplier = await suppliers.ResolveAsync(input.SupplierId, input.Supplier, actor, role, ct);
            input = input with { Supplier = supplier.Name, SupplierId = supplier.Id };
        }
        if (input.Route == "External" && (string.IsNullOrWhiteSpace(input.Supplier) || !input.PromisedOn.HasValue))
            throw new PdmRuleException("外协分配必须选择供应商并填写承诺日期。");
        var updatedLot = ReallocateLot(lot, input.Route, input.Quantity, input.Supplier,
            input.SupplierId, input.Assignee, input.PromisedOn);
        var updated = state with
        {
            Lots = state.Lots.Select(item => item.Id == lot.Id ? updatedLot : item).ToArray(),
            Movements = state.Movements.Append(new ProductionMovement(Guid.NewGuid(), input.RequestId,
                lot.Id, "AllocationChanged", input.Quantity, actor, clock.GetUtcNow(), AllocationNote(lot, updatedLot, input.Reason))
                { ResetsFlow = lot.Route != updatedLot.Route || lot.Quantity != updatedLot.Quantity }).ToArray()
        };
        var candidate = current with { State = updated, RowVersion = current.RowVersion + 1 };
        if (!await store.SaveAsync(candidate, current.RowVersion, ct))
            throw new PdmConflictException("生产分配或流转记录已被修改，请刷新后重新调整。");
        return candidate;
    }

    public async Task<ProductionTrackingRecord> BatchAllocateAsync(Guid id, BatchProductionAllocation input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (input.RequestId == Guid.Empty || input.LineIds is null || input.LineIds.Count == 0
            || input.Route is not ("Self" or "External"))
            throw new PdmRuleException("请选择物料及自制或外协分配方式。");
        var current = await FindAsync(id, actor, role, ct);
        if (current.State.Movements.Any(item => item.RequestId == input.RequestId)) return current;
        if (current.RowVersion != input.ExpectedRowVersion)
            throw new PdmConflictException("生产分配或流转记录已被修改，请刷新后重新批量分配。");
        var state = current.State;
        if (state.ReceivedAt is null) throw new PdmRuleException("请先接收发布包，再批量分配。");
        if (input.Route == "External" && (input.SupplierId.HasValue || suppliers is not null))
        {
            if (suppliers is null) throw new PdmRuleException("供应商台账暂不可用，请联系管理员。");
            var supplier = await suppliers.ResolveAsync(input.SupplierId, input.Supplier, actor, role, ct);
            input = input with { Supplier = supplier.Name, SupplierId = supplier.Id };
        }
        if (input.Route == "External" && (string.IsNullOrWhiteSpace(input.Supplier) || !input.PromisedOn.HasValue))
            throw new PdmRuleException("外协分配必须选择供应商并填写承诺日期。");
        var lots = state.Lots.ToList();
        var movements = state.Movements.ToList();
        foreach (var lineId in input.LineIds.Distinct())
        {
            var line = state.Lines.FirstOrDefault(item => item.Id == lineId)
                ?? throw new PdmNotFoundException("所选生产物料不存在，请刷新后重新选择。");
            if (line.RequiredQuantity <= 0) throw new PdmRuleException($"{line.DrawingNumber} 的BOM数量为零，无需分配。");
            var lineLots = state.Lots.Where(item => item.LineId == lineId).ToArray();
            if (lineLots.Sum(lot => lot.Quantity) > ProductionTrackingRules.EffectiveDemand(state, line))
                throw new PdmConflictException($"{line.DrawingNumber} 的现有分配超过BOM数量，请先核对已有批次。");
            foreach (var lot in lineLots)
            {
                if (ProductionTrackingRules.AllocationChangeBlockReason(state, lot) is string blocked)
                    throw new PdmConflictException($"{line.DrawingNumber}：{blocked}请取消勾选后重试。");
                var changed = ReallocateLot(lot, input.Route, lot.Quantity, input.Supplier, input.SupplierId,
                    string.IsNullOrWhiteSpace(input.Assignee) ? lot.Assignee : input.Assignee,
                    input.PromisedOn ?? lot.PromisedOn);
                lots[lots.FindIndex(item => item.Id == lot.Id)] = changed;
                movements.Add(new ProductionMovement(Guid.NewGuid(), input.RequestId, lot.Id, "AllocationChanged",
                    lot.Quantity, actor, clock.GetUtcNow(), AllocationNote(lot, changed, input.Reason)) { ResetsFlow = lot.Route != changed.Route });
            }
            var remaining = ProductionTrackingRules.Unassigned(state, line);
            if (remaining <= 0) continue;
            var added = new ProductionLot(Guid.NewGuid(), lineId, input.Route, remaining,
                input.Route == "External" ? input.Supplier?.Trim() : null, input.Assignee?.Trim(), input.PromisedOn,
                null, null, 0) { SupplierId = input.Route == "External" ? input.SupplierId : null };
            lots.Add(added);
            movements.Add(new ProductionMovement(Guid.NewGuid(), input.RequestId, added.Id, "Assigned", remaining,
                actor, clock.GetUtcNow(), "按发布包BOM数量批量分配" + (string.IsNullOrWhiteSpace(input.Reason) ? "" : $"；原因：{input.Reason.Trim()}")));
        }
        var candidate = current with { State = state with { Lots = lots, Movements = movements }, RowVersion = current.RowVersion + 1 };
        if (!await store.SaveAsync(candidate, current.RowVersion, ct))
            throw new PdmConflictException("生产分配或流转记录已被修改，请刷新后重新批量分配。");
        return candidate;
    }

    private static ProductionLot ReallocateLot(ProductionLot lot, string route, decimal quantity,
        string? supplier, Guid? supplierId, string? assignee, DateOnly? promisedOn)
    {
        var clearSchedule = route != lot.Route || quantity != lot.Quantity;
        if (!clearSchedule && lot.Schedule.Count > 0
            && (!promisedOn.HasValue || promisedOn < lot.Schedule.Max(slot => slot.Date)))
            throw new PdmRuleException("保留原排程时，预计完成日期不能为空或早于最后一个排产日期。");
        return lot with
        {
            Route = route, Quantity = quantity,
            Supplier = route == "External" ? supplier?.Trim() : null,
            SupplierId = route == "External" ? supplierId : null,
            Assignee = assignee?.Trim(), PromisedOn = promisedOn,
            Schedule = clearSchedule ? [] : lot.Schedule,
            ResourceGroupId = clearSchedule ? null : lot.ResourceGroupId,
            PlannedOn = clearSchedule ? null : lot.PlannedOn,
            EstimatedHours = clearSchedule ? 0 : lot.EstimatedHours
        };
    }

    private static string AllocationNote(ProductionLot lot, ProductionLot updatedLot, string? reason)
    {
        string RouteName(string route) => route == "Self" ? "自制" : "外协";
        return $"{RouteName(lot.Route)} {lot.Quantity} → {RouteName(updatedLot.Route)} {updatedLot.Quantity}；"
            + $"供应商：{lot.Supplier ?? "—"} → {updatedLot.Supplier ?? "—"}；负责人：{lot.Assignee ?? "—"} → {updatedLot.Assignee ?? "—"}；"
            + $"承诺日期：{lot.PromisedOn?.ToString("yyyy-MM-dd") ?? "未设置"} → {updatedLot.PromisedOn?.ToString("yyyy-MM-dd") ?? "未设置"}；"
            + (updatedLot.Schedule.Count == 0 && lot.Schedule.Count > 0 ? "原排程已清空并释放工时；" : "")
            + (string.IsNullOrWhiteSpace(reason) ? "" : $"原因：{reason.Trim()}");
    }

    public async Task<ProductionTrackingRecord> UpdateDeliveryAsync(Guid id, UpdateProductionLineDelivery input,
        string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionDrawingManage, ct))
            throw new UnauthorizedAccessException("没有调整生产交期和紧急程度的权限。");
        if (input.RequestId == Guid.Empty || input.Priority is not ("Normal" or "Priority" or "Urgent")
            || string.IsNullOrWhiteSpace(input.Reason))
            throw new PdmRuleException("请选择普通、加急或紧急，并填写调整原因。");
        return await MutateAsync(id, actor, role, state =>
        {
            if (state.DeliveryAdjustments.Any(item => item.RequestId == input.RequestId)) return state;
            if (!state.Lines.Any(item => item.Id == input.LineId))
                throw new PdmNotFoundException("生产物料不存在。");
            return state with
            {
                Lines = state.Lines.Select(item => item.Id == input.LineId
                    ? item with { Priority = input.Priority, RequiredOn = input.RequiredOn } : item).ToArray(),
                DeliveryAdjustments = state.DeliveryAdjustments.Append(new ProductionLineAdjustment(Guid.NewGuid(),
                    input.RequestId, input.LineId, input.Priority, input.RequiredOn,
                    input.Reason.Trim(), actor, clock.GetUtcNow())).ToArray()
            };
        }, ct);
    }

    public async Task<ProductionTrackingRecord> UpdateLotPromiseAsync(Guid id, UpdateProductionLotPromise input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct);
        if (input.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Reason))
            throw new PdmRuleException("调整供应商承诺交期时必须填写原因。");
        return await MutateAsync(id, actor, role, state =>
        {
            if (state.Movements.Any(item => item.RequestId == input.RequestId)) return state;
            var lot = state.Lots.FirstOrDefault(item => item.Id == input.LotId)
                ?? throw new PdmNotFoundException("生产批次不存在。");
            if (lot.Route != "External") throw new PdmRuleException("供应商承诺交期只适用于外协批次。");
            return state with
            {
                Lots = state.Lots.Select(item => item.Id == lot.Id ? item with { PromisedOn = input.PromisedOn } : item).ToArray(),
                Movements = state.Movements.Append(new ProductionMovement(Guid.NewGuid(), input.RequestId,
                    lot.Id, "PromiseChanged", 0, actor, clock.GetUtcNow(), input.Reason.Trim())).ToArray()
            };
        }, ct);
    }

    public async Task<ProductionTrackingRecord> ResolveRevisionAsync(Guid oldReleasePackageId,
        ResolveProductionRevision input, string actor, UserRole role, CancellationToken ct)
    {
        if (input.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Reason)
            || input.Action is not ("ContinueOld" or "CancelRemaining" or "TransferNew"))
            throw new PdmRuleException("请选择旧版继续、取消剩余数量或转新版返工，并填写处置原因。");
        var changedPackage = await repository.FindReleasePackageAsync(input.ChangedReleasePackageId, ct)
            ?? throw new PdmNotFoundException("变更发布包不存在。");
        var previous = await FindAsync(oldReleasePackageId, actor, role, ct);
        var selectedLot = previous.State.Lots.FirstOrDefault(item => item.Id == input.LotId)
            ?? throw new PdmNotFoundException("旧版生产批次不存在。");
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct)
            && (selectedLot.Route != "Self" || !await repository.HasUserPermissionAsync(
                actor, role, PermissionCodes.ProductionSchedule, ct)))
            throw new UnauthorizedAccessException("没有处理该图纸变更生产批次的权限。");
        if (changedPackage.ProjectId != previous.State.ProjectId || changedPackage.State != ReleasePackageState.Published
            || changedPackage.Scope != ReleaseScope.NonStandardSupplement)
            throw new PdmRuleException("请选择同一项目已生效的非标件变更发布包。");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (previous.State.RevisionDecisions.Any(item => item.RequestId == input.RequestId)) return previous;
            var lot = previous.State.Lots.FirstOrDefault(item => item.Id == input.LotId)
                ?? throw new PdmNotFoundException("旧版生产批次不存在。");
            if (!lot.Frozen || lot.FrozenByReleasePackageId != changedPackage.Id)
                throw new PdmConflictException("该批次不在当前变更包的待处置范围内，请刷新页面。");
            var oldLine = previous.State.Lines.Single(item => item.Id == lot.LineId);
            if (input.Action == "ContinueOld" && oldLine.SourceDocumentId is Guid sourceId &&
                (await repository.FindDocumentAsync(sourceId, ct))?.State == DocumentLifecycleState.Obsolete)
                throw new PdmConflictException("旧版图档已作废，不能继续按旧版生产。");
            var remaining = lot.Quantity - ProductionTrackingRules.Total(previous.State, lot.Id, "Issue")
                - ProductionTrackingRules.Total(previous.State, lot.Id, "Cancelled")
                - ProductionTrackingRules.Total(previous.State, lot.Id, "TaskCancelled")
                - ProductionTrackingRules.Total(previous.State, lot.Id, "Transferred");
            if (remaining <= 0) throw new PdmConflictException("该批次已无待处置数量。");
            var dispositionQuantity = input.Action == "CancelRemaining" ? input.Quantity ?? 0 : remaining;
            if (input.Action == "CancelRemaining" && (dispositionQuantity <= 0 || dispositionQuantity > remaining))
                throw new PdmRuleException("请填写大于零且不超过剩余数量的本次取消数量。");
            var now = clock.GetUtcNow();
            var decision = new ProductionRevisionDecision(input.RequestId, lot.Id, changedPackage.Id,
                input.Action, input.Action == "ContinueOld" ? 0 : dispositionQuantity,
                input.Reason.Trim(), actor, now);
            var previousState = previous.State with
            {
                Lots = previous.State.Lots.Select(item => item.Id == lot.Id ? item with
                {
                    Frozen = false, FreezeReason = null, FrozenByReleasePackageId = null,
                    Schedule = input.Action != "ContinueOld" && dispositionQuantity == remaining
                        ? [] : item.Schedule
                } : item).ToArray(),
                RevisionDecisions = previous.State.RevisionDecisions.Append(decision).ToArray(),
                Movements = input.Action == "ContinueOld" ? previous.State.Movements
                    : previous.State.Movements.Append(new ProductionMovement(Guid.NewGuid(), input.RequestId,
                        lot.Id, input.Action == "TransferNew" ? "Transferred" : "Cancelled", dispositionQuantity,
                        actor, now, input.Reason.Trim())).ToArray()
            };
            var previousUpdate = previous with { State = previousState, RowVersion = previous.RowVersion + 1 };
            if (input.Action != "TransferNew")
            {
                if (await store.SaveAsync(previousUpdate, previous.RowVersion, ct)) return previousUpdate;
            }
            else
            {
                var current = await FindAsync(changedPackage.Id, actor, role, ct);
                if (current.State.ReceivedAt is null)
                    throw new PdmRuleException("请先由供应链接收新版发布包，再转移旧版剩余生产数量。");
                var item = ProductionDrawingQuantities.FrozenItems(changedPackage).FirstOrDefault(candidate =>
                    oldLine.SourceTrackingIds.Contains(candidate.ReleaseTrackingId)
                    || BomReleaseAggregation.MaterialKey(candidate) == oldLine.MaterialKey);
                if (item is null) throw new PdmRuleException("新版发布包没有对应物料，请选择取消剩余数量并确认后续需求。");
                var key = BomReleaseAggregation.MaterialKey(item);
                var existing = current.State.Lines.FirstOrDefault(line => line.MaterialKey == key
                    && line.Revision == item.Revision);
                var transferred = existing is null
                    ? new ProductionLine(Guid.NewGuid(), key, [item.Id], item.Specification?.Trim() ?? string.Empty, item.Name,
                        item.Revision, item.Unit, remaining,
                        changedPackage.BomItemDeliveryOverrides.GetValueOrDefault(item.Id)?.Priority
                            ?? changedPackage.DrawingPriority,
                        changedPackage.BomItemDeliveryOverrides.GetValueOrDefault(item.Id)?.RequiredOn
                            ?? changedPackage.DrawingRequiredOn,
                        item.Material, item.SurfaceTreatment, item.HeatTreatment, item.SourceDocumentId)
                    {
                        MaterialCode = item.DrawingNumber,
                        SourceTrackingIds = [item.ReleaseTrackingId],
                        SourceVersionId = changedPackage.VersionBindings?.FirstOrDefault(binding =>
                            binding.DocumentId == item.SourceDocumentId)?.VersionId
                    }
                    : existing with { RequiredQuantity = existing.RequiredQuantity + remaining };
                var currentState = current.State with
                {
                    Lines = existing is null ? current.State.Lines.Append(transferred).ToArray()
                        : current.State.Lines.Select(line => line.Id == existing.Id ? transferred : line).ToArray(),
                    RevisionDecisions = current.State.RevisionDecisions.Append(decision).ToArray()
                };
                var currentUpdate = current with { State = currentState, RowVersion = current.RowVersion + 1 };
                if (await store.SaveTransferAsync(previousUpdate, previous.RowVersion,
                        currentUpdate, current.RowVersion, ct)) return previousUpdate;
            }
            previous = await store.FindAsync(oldReleasePackageId, ct)
                ?? throw new PdmNotFoundException("旧版生产记录不存在。");
        }
        throw new PdmConflictException("图纸变更正在处理，请刷新后重试。");
    }

    public async Task<ProductionTrackingRecord> RecordAsync(Guid id, RecordProductionMovement input,
        string actor, UserRole role, CancellationToken ct)
    {
        if (input.RequestId == Guid.Empty || input.Quantity <= 0) throw new PdmRuleException("请输入有效的操作编号和大于零的本次数量。");
        var current = await FindAsync(id, actor, role, ct);
        var originalLot = current.State.Lots.FirstOrDefault(item => item.Id == input.LotId)
            ?? throw new PdmNotFoundException("生产批次不存在，请重新扫码或刷新页面。");
        if (!ProductionFlowRules.Labels.ContainsKey(input.Kind) && ProductionDepartmentFlow.Node(current.State, originalLot, input.Kind) is null)
            throw new PdmRuleException("生产操作类型无效，请刷新可办理步骤。");
        await RequirePermissionAsync(actor, role, ProductionFlowRules.Permission(current.State, originalLot, input.Kind), ct);
        void CheckReplay(ProductionMovement movement)
        {
            if (movement.Actor != actor || movement.Kind != input.Kind || movement.LotId != input.LotId || movement.Quantity != input.Quantity
                || input.WorkstationId.HasValue && (movement.WorkstationId ?? Guid.Empty) != input.WorkstationId
                || input.SourceKind is not null && movement.SourceKind != input.SourceKind
                || (movement.Note ?? "") != (input.Note?.Trim() ?? "") || (movement.Recipient ?? "") != (input.Kind == "Issue" ? input.Recipient?.Trim() ?? "" : ""))
                throw new PdmConflictException("本次操作编号已用于其他内容，请刷新后重新确认。");
        }
        if (current.State.Movements.FirstOrDefault(item => item.RequestId == input.RequestId) is { } replay) { CheckReplay(replay); return current; }
        if ((originalLot.Frozen || originalLot.Paused) && !ProductionDepartmentFlow.IsControl(input.Kind)) throw new PdmConflictException("该生产批次已暂停或因图纸变更冻结，请先处理后再操作。");
        if (ProductionTrackingRules.Total(current.State, originalLot.Id, "Transferred") + ProductionTrackingRules.Total(current.State, originalLot.Id, "Cancelled")
            + ProductionTrackingRules.Total(current.State, originalLot.Id, "TaskCancelled") >= originalLot.Quantity)
            throw new PdmConflictException("该批次剩余数量已转版或取消，不能继续登记旧版生产记录。");
        var context = await scanService.ActionsAsync(current.State, actor, role, ct);
        var options = context.Actions.Where(item => item.LotId == input.LotId && item.Kind == input.Kind
            && (!input.WorkstationId.HasValue || item.WorkstationId == input.WorkstationId)
            && (input.SourceKind is null || item.SourceKind == input.SourceKind)).ToArray();
        if (options.Length != 1) throw new PdmRuleException(options.Length == 0
            ? "当前账号、工位或前序环节不允许办理此步骤，请刷新可办理步骤后重新选择。" : "请选择本次办理的具体工位或异常发生环节。");
        var grant = options[0];
        if (input.Quantity > grant.RemainingQuantity) throw new PdmRuleException("本次数量超过当前岗位和工位的可办理数量，请核对后重新确认。");
        if (input.Kind == "Resume" && grant.SourceKind is null && input.Quantity != grant.RemainingQuantity)
            throw new PdmRuleException("历史整批暂停请先全部恢复，再按环节和数量暂停。");
        var account = await repository.FindUserAsync(actor, ct);
        return await MutateAsync(id, actor, role, state =>
        {
            if (state.Movements.FirstOrDefault(item => item.RequestId == input.RequestId) is { } existing) { CheckReplay(existing); return state; }
            var lot = state.Lots.FirstOrDefault(item => item.Id == input.LotId) ?? throw new PdmNotFoundException("生产批次不存在。");
            var control = ProductionDepartmentFlow.IsControl(input.Kind);
            var available = control ? ProductionQuantityFlow.ControlRemaining(state, lot, input.Kind, grant.SourceKind)
                : ProductionFlowRules.Remaining(state, lot, input.Kind, grant.ResourceGroup);
            if (ProductionFlowRules.Post(state, lot, input.Kind) != grant.Post || input.Quantity > available)
                throw new PdmConflictException("该批次环节或可办理数量已变化，请刷新后重新确认。");
            if (input.Kind is "Rework" or "Scrap" && string.IsNullOrWhiteSpace(input.Note)) throw new PdmRuleException("返工或报废必须填写原因。");
            if (control && string.IsNullOrWhiteSpace(input.Note)) throw new PdmRuleException("暂停、恢复、取消或环节报废必须填写原因及实物处理说明。");
            if (input.Kind == "Issue" && string.IsNullOrWhiteSpace(input.Recipient)) throw new PdmRuleException("领料时必须填写实际领料人。");
            var source = grant.SourceKind;
            if (input.Kind == "Scrap")
            {
                var steps = ProductionQuantityFlow.Steps(state, lot);
                var index = Array.IndexOf(steps, "Pass");
                source = index > 0 ? steps[index - 1] : "Assigned";
            }
            var movement = new ProductionMovement(Guid.NewGuid(), input.RequestId, lot.Id, input.Kind, input.Quantity,
                actor, clock.GetUtcNow(), input.Note?.Trim(), input.Kind == "Issue" ? input.Recipient?.Trim() : null)
            {
                ActorName = account?.DisplayName ?? actor, ActorRoles = string.Join(",", account?.EffectiveRoleCodes ?? [role.ToString()]),
                BindingId = grant.BindingId == Guid.Empty ? null : grant.BindingId, Post = grant.Post, WorkstationId = grant.WorkstationId == Guid.Empty ? null : grant.WorkstationId,
                WorkstationName = grant.WorkstationId == Guid.Empty ? null : grant.WorkstationName, ResourceGroup = grant.Post is "Machining" or "Trace" ? grant.ResourceGroup : null,
                FromStage = grant.SourceName ?? ProductionFlowRules.Stage(state, lot), OperationName = grant.Label, SourceKind = source,
                FlowCredits = ProductionQuantityFlow.IsRemoval(input.Kind) && source is not null
                    ? ProductionQuantityFlow.RemovalCredits(state, lot, source, input.Quantity) : []
            };
            var after = state with { Movements = state.Movements.Append(movement).ToArray() };
            var open = ProductionQuantityFlow.Outstanding(after, lot);
            var changedLot = lot with { Paused = open > 0 && ProductionQuantityFlow.Paused(after, lot) >= open };
            if (input.Kind is "Cancelled" or "TaskCancelled" && open == 0)
                changedLot = changedLot with { Schedule = [], EstimatedHours = 0, PlannedOn = null, ResourceGroupId = null };
            after = after with { Lots = state.Lots.Select(item => item.Id == lot.Id ? changedLot : item).ToArray() };
            movement = movement with { ToStage = ProductionFlowRules.Stage(after, changedLot) };
            return after with { Movements = state.Movements.Append(movement).ToArray() };
        }, ct);
    }

    public async Task<ProductionTrackingRecord> ScheduleAsync(Guid id, ProductionScheduleInput input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionSchedule, ct);
        if (input.RequestId == Guid.Empty || input.Slots.Count == 0 || input.Slots.Any(slot =>
                string.IsNullOrWhiteSpace(slot.ResourceGroupId) || slot.Hours <= 0))
            throw new PdmRuleException("请选择资源组、日期和大于零的预计工时。");
        var current = await FindAsync(id, actor, role, ct);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (current.State.Movements.Any(item => item.RequestId == input.RequestId)) return current;
            if (current.State.Scope == ReleaseScope.NonStandardLongLead && current.State.FormalReleasePackageId is null)
                throw new PdmRuleException("请先完成正式图纸发布，再确认生产排程。");
            var lot = current.State.Lots.FirstOrDefault(item => item.Id == input.LotId)
                ?? throw new PdmNotFoundException("生产批次不存在。");
            if (lot.Route != "Self") throw new PdmRuleException("内部资源排产只适用于自制批次。");
            if (lot.Schedule.Count == 0 && ProductionFlowRules.Received(current.State, lot) <= 0)
                throw new PdmRuleException("该批次尚未由生产岗位扫码签收，请先完成生产签收。");
            var oldGroups = ProductionFlowRules.ResourceGroups(lot).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (oldGroups.Count > 0 && ProductionFlowRules.ActiveMovements(current.State, lot.Id).Any(item => item.Kind == "Start")
                && !oldGroups.SetEquals(input.Slots.Select(slot => slot.ResourceGroupId)))
                throw new PdmRuleException("该批次已扫码开工，不能增加或移除机加工序资源组；可调整原资源组的日期和工时。");
            var startedGroups = current.State.Movements.Where(item => item.LotId == lot.Id && item.Kind == "Start" && !string.IsNullOrEmpty(item.ResourceGroup))
                .Select(item => item.ResourceGroup!).Distinct(StringComparer.OrdinalIgnoreCase);
            if (startedGroups.Any(group => !input.Slots.Any(slot => slot.ResourceGroupId.Equals(group, StringComparison.OrdinalIgnoreCase))))
                throw new PdmRuleException("已扫码开工的资源组不能从排程中移除，请保留原工序。");
            if (lot.Frozen || lot.Paused) throw new PdmConflictException("该批次已冻结或暂停，不能确认排产。");
            if (lot.Schedule.Count > 0 && string.IsNullOrWhiteSpace(input.ChangeReason))
                throw new PdmRuleException("调整已确认的排程时必须填写原因。");
            if (input.PromisedOn < input.Slots.Max(slot => slot.Date))
                throw new PdmRuleException("预计完成日期不能早于最后一个排产日期。");
            var updatedLot = lot with
            {
                Schedule = input.Slots.ToArray(), ResourceGroupId = input.Slots[0].ResourceGroupId,
                PlannedOn = input.Slots.Min(slot => slot.Date),
                EstimatedHours = input.Slots.Sum(slot => slot.Hours), PromisedOn = input.PromisedOn
            };
            var state = current.State with
            {
                Lots = current.State.Lots.Select(item => item.Id == lot.Id ? updatedLot : item).ToArray(),
                Movements = current.State.Movements.Append(new ProductionMovement(Guid.NewGuid(), input.RequestId,
                    lot.Id, "Scheduled", lot.Quantity, actor, clock.GetUtcNow(),
                    $"{input.Slots.Count} 天；预计 {input.Slots.Sum(slot => slot.Hours)} 小时。{input.ChangeReason?.Trim()}")).ToArray()
            };
            var candidate = current with { State = state, RowVersion = current.RowVersion + 1 };
            if (await store.SaveScheduleAsync(candidate, current.RowVersion, ct)) return candidate;
            current = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("发布跟踪单不存在。");
            current = await scanService.ApplyFlowAsync(current, ct);
        }
        throw new PdmConflictException("排产正被其他人员调整，请刷新后重试。");
    }

    public async Task<(ProductionCapacityState Calendar, IReadOnlyList<ProductionCapacityUsage> Usage)> CapacityAsync(
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, ct);
        var companyId = TenantContext.CompanyId ?? throw new UnauthorizedAccessException("当前公司信息无效。");
        var calendar = await store.CapacityAsync(companyId, ct);
        var reservations = (await store.ListCompanyAsync(companyId, ct))
            .SelectMany(record => record.State.Lots.Where(lot => lot.Route == "Self"))
            .SelectMany(lot => lot.Schedule)
            .GroupBy(slot => (slot.ResourceGroupId, slot.Date))
            .ToDictionary(group => group.Key, group => group.Sum(slot => slot.Hours));
        var usage = calendar.Days.Select(day => new ProductionCapacityUsage(day.ResourceGroupId, day.Date,
            day.AvailableHours, reservations.GetValueOrDefault((day.ResourceGroupId, day.Date)))).ToArray();
        return (calendar, usage);
    }

    public async Task<ProductionCapacityState> SetCapacityAsync(ProductionCapacityInput input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProductionCapacityManage, ct);
        if (string.IsNullOrWhiteSpace(input.ResourceGroupId) || input.AvailableHours < 0 || input.AvailableHours > 1000)
            throw new PdmRuleException("请填写资源组及当天有效的可用工时。");
        var companyId = TenantContext.CompanyId ?? throw new UnauthorizedAccessException("当前公司信息无效。");
        var current = await store.CapacityAsync(companyId, ct);
        if (current.RowVersion != input.ExpectedRowVersion)
            throw new PdmConflictException("资源日历已被其他人员修改，请刷新后重试。");
        var days = current.Days.Where(item => item.ResourceGroupId != input.ResourceGroupId.Trim()
                || item.Date != input.Date)
            .Append(new ProductionCapacityDay(input.ResourceGroupId.Trim(), input.Date, input.AvailableHours)).ToArray();
        var updated = current with { Days = days, RowVersion = current.RowVersion + 1 };
        if (!await store.SaveCapacityAsync(updated, current.RowVersion, ct))
            throw new PdmConflictException("资源日历已被其他人员修改，请刷新后重试。");
        return updated;
    }

    public async Task<IReadOnlyList<ProductionScanResult>> ResolveScanAsync(string code,
        string actor, UserRole role, CancellationToken ct)
    {
        var seeds = await ResolveScanCoreAsync(code, actor, role, ct);
        var results = new List<ProductionScanResult>();
        var projects = new Dictionary<Guid, IReadOnlyList<ProductionTrackingState>>();
        foreach (var group in seeds.GroupBy(item => item.ReleasePackageId))
        {
            var package = await repository.FindReleasePackageAsync(group.Key, ct)
                ?? throw new PdmNotFoundException("扫码对应的发布记录不存在。");
            if (!projects.TryGetValue(package.ProjectId, out var records))
                projects[package.ProjectId] = records = await ScanRecordsAsync(package.ProjectId, ct);
            var state = records.FirstOrDefault(item => item.ReleasePackageId == group.Key);
            if (state is null) continue;
            foreach (var seed in group)
            {
                var line = state.Lines.Single(item => item.Id == seed.LineId);
                if (seed.LotId.HasValue)
                    results.Add(ScanResult(state, line, state.Lots.Single(item => item.Id == seed.LotId)));
                else
                    results.AddRange(records.SelectMany(record => record.Lines.Where(item => SameScanDemand(line, item))
                        .Select(item => ScanResult(record, item))));
            }
        }
        return results.DistinctBy(item => (item.ReleasePackageId, item.LineId, item.LotId)).ToArray();
    }

    public async Task<ProductionScanContext> ActionsAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var record = await FindAsync(id, actor, role, ct);
        var context = await scanService.ActionsAsync(record.State, actor, role, ct);
        var others = (await ScanRecordsAsync(record.State.ProjectId, ct)).Where(state => state.ReleasePackageId != id).ToArray();
        return context with { RelatedDemands = record.State.Lines.SelectMany(line => others.SelectMany(state =>
            state.Lines.Where(other => SameScanDemand(line, other) && ProductionTrackingRules.EffectiveDemand(state, other) > 0)
                .Select(other => ScanResult(state, other) with { RelatedToLineId = line.Id }))).ToArray() };
    }

    private async Task<IReadOnlyList<ProductionTrackingState>> ScanRecordsAsync(Guid projectId, CancellationToken ct)
    {
        var published = (await repository.ListReleasePackagesAsync(projectId, ct))
            .Where(package => package.State == ReleasePackageState.Published && TrackedScope(package.Scope)).Select(package => package.Id).ToHashSet();
        var records = (await store.ListProjectAsync(projectId, ct)).Where(record => published.Contains(record.State.ReleasePackageId)).ToArray();
        if (records.Length == 0) return [];
        var catalog = await store.ScanCatalogAsync(records[0].State.CompanyId, ct);
        return records.Select(record => ProductionDepartmentFlow.ApplyCatalog(record.State, catalog)).OrderBy(state => state.PublishedAt).ToArray();
    }

    private static bool SameScanDemand(ProductionLine left, ProductionLine right) => left.Revision == right.Revision
        && (left.SourceTrackingIds.Intersect(right.SourceTrackingIds).Any()
            || string.Equals(left.MaterialKey, right.MaterialKey, StringComparison.OrdinalIgnoreCase));

    private static ProductionScanResult ScanResult(ProductionTrackingState state, ProductionLine line, ProductionLot? lot = null) =>
        new(state.ReleasePackageId, line.Id, lot?.Id, state.PackageNumber, line.DrawingNumber, line.Name, line.Revision,
            ProductionTrackingRules.Status(state))
        {
            RequiredQuantity = ProductionTrackingRules.EffectiveDemand(state, line), UnassignedQuantity = ProductionTrackingRules.Unassigned(state, line),
            LotQuantity = lot?.Quantity, Unit = line.Unit, IsSupplement = state.Scope == ReleaseScope.NonStandardSupplement
        };

    private async Task<IReadOnlyList<ProductionScanResult>> ResolveScanCoreAsync(string code,
        string actor, UserRole role, CancellationToken ct)
    {
        code = code.Trim();
        if (PdfReleaseIdentity.TryDecodeDisplay(code, out var model, out var revision))
            return await ResolveDisplayScanAsync(model, revision, actor, role, ct);
        if (code.Trim().StartsWith("UPLM-PDF|", StringComparison.Ordinal))
            return await ResolvePdfScanAsync(code, actor, role, ct);
        var parts = code.Trim().Split('|');
        if (parts.Length == 3 && parts[0] == "UPLM-PRODUCTION"
            && Guid.TryParse(parts[1], out var packageId) && Guid.TryParse(parts[2], out var lotId))
        {
            var record = await FindAsync(packageId, actor, role, ct);
            var lot = record.State.Lots.FirstOrDefault(item => item.Id == lotId)
                ?? throw new PdmNotFoundException("批次二维码已失效，请刷新生产任务。");
            var line = record.State.Lines.Single(item => item.Id == lot.LineId);
            return [new(packageId, line.Id, lot.Id, record.State.PackageNumber,
                line.DrawingNumber, line.Name, line.Revision, ProductionTrackingRules.Status(record.State))];
        }
        if (parts.Length == 1 && code.Trim().Length is > 0 and <= 100 && !code.Contains('\n') && !code.Contains('\r'))
            return await ResolveNameplateScanAsync(code.Trim(), actor, role, ct);
        if (parts.Length != 4 || parts[0] != "UPLM-DRAWING" || !Guid.TryParse(parts[3], out var drawingId))
            throw new PdmRuleException("二维码格式不正确，请扫描正式图纸、铭牌序列号二维码或生产批次流转卡。");
        var drawing = await repository.FindDocumentAsync(drawingId, ct)
            ?? throw new PdmNotFoundException("图纸不存在，请重新扫码。");
        await RequireProjectAsync(drawing.ProjectId, actor, role, ct);
        var formal = (await drawings.ListAsync(actor, role, true, drawing.ProjectId, ct))
            .Where(item => item.DocumentId == drawingId && item.Revision == parts[2]
                && string.Equals(item.Model, Uri.UnescapeDataString(parts[1]), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var records = await EnsureProjectAsync((await repository.FindProjectAsync(drawing.ProjectId, ct))!, ct);
        return records.SelectMany(record => record.State.Lines.Select(line => (record, line)))
            .Where(entry => formal.Any(item =>
                (item.ReleasePackageId == entry.record.State.ReleasePackageId
                    || item.ReleasePackageId == entry.record.State.FormalReleasePackageId) &&
                item.BomItems.Any(bom =>
                entry.line.SourceBomItemIds.Contains(bom.Id) ||
                BomReleaseAggregation.MaterialKey(bom) == entry.line.MaterialKey)))
            .Select(entry => new ProductionScanResult(entry.record.State.ReleasePackageId, entry.line.Id, null,
                entry.record.State.PackageNumber, entry.line.DrawingNumber, entry.line.Name,
                entry.line.Revision, ProductionTrackingRules.Status(entry.record.State))).ToArray();
    }

    private async Task<IReadOnlyList<ProductionScanResult>> ResolveNameplateScanAsync(string serialNumber,
        string actor, UserRole role, CancellationToken ct)
    {
        var results = new List<ProductionScanResult>();
        foreach (var candidate in await store.FindNameplateSerialAsync(serialNumber, TenantContext.CompanyId, ct))
        {
            var package = await repository.FindReleasePackageAsync(candidate.State.ReleasePackageId, ct);
            if (package?.State != ReleasePackageState.Published || package.Scope != ReleaseScope.Nameplate
                || !package.NonStandardBomSnapshot.Any(item => item.NameplateContent?.SerialNumber == serialNumber)) continue;
            var record = await FindAsync(package.Id, actor, role, ct);
            results.AddRange(record.State.Lines.Where(line => line.NameplateContent?.SerialNumber == serialNumber)
                .Select(line => ScanResult(record.State, line)));
        }
        return results;
    }

    private async Task<IReadOnlyList<ProductionScanResult>> ResolveDisplayScanAsync(string model, string revision,
        string actor, UserRole role, CancellationToken ct)
    {
        var results = new List<ProductionScanResult>();
        var packages = new Dictionary<Guid, ReleasePackage>();
        foreach (var drawing in (await drawings.ListIndexAsync(actor, role, true, ct))
            .Where(item => item.Revision == revision))
        {
            var formal = await repository.FindDocumentVersionAsync(drawing.DocumentId, drawing.VersionId, ct);
            if (formal is null) continue;
            if (!packages.TryGetValue(drawing.ReleasePackageId, out var package))
            {
                package = await repository.FindReleasePackageAsync(drawing.ReleasePackageId, ct)
                    ?? throw new PdmNotFoundException("二维码对应的发布记录不存在。");
                packages[package.Id] = package;
            }
            if (formal.SourceVersionId is null)
            {
                var legacyModels = ReleaseBoundSourceReader.ModelDocumentIds(formal.ReferenceSnapshot).ToHashSet();
                if (legacyModels.Count == 0) legacyModels.UnionWith((await repository.ListDocumentRelationsAsync(package.ProjectId, ct))
                    .Where(item => item.DrawingDocumentId == drawing.DocumentId).Select(item => item.ModelDocumentId));
                if (!string.Equals(ReleaseBoundSourceReader.DrawingModel(package, formal, legacyModels, drawing.DrawingNumber), model,
                    StringComparison.OrdinalIgnoreCase)) continue;
                await drawings.EnsureProductionUseAllowedAsync(drawing.DocumentId, formal.Id, ct);
                var project = await RequireProjectAsync(drawing.ProjectId, actor, role, ct);
                var frozenBom = ProductionDrawingQuantities.FrozenItems(package);
                var legacyBom = frozenBom.Where(item => item.SourceDocumentId.HasValue && legacyModels.Contains(item.SourceDocumentId.Value)).ToArray();
                if (legacyBom.Length == 0) legacyBom = frozenBom.Where(item => string.Equals(item.DrawingNumber, drawing.DrawingNumber, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var record in (await EnsureProjectAsync(project, ct)).Where(record => record.State.ReleasePackageId == package.Id
                    || record.State.FormalReleasePackageId == package.Id))
                foreach (var line in record.State.Lines.Where(line => legacyBom.Any(item => line.SourceBomItemIds.Contains(item.Id)
                    || line.MaterialKey == BomReleaseAggregation.MaterialKey(item))))
                    results.Add(new(record.State.ReleasePackageId, line.Id, null, record.State.PackageNumber,
                        line.DrawingNumber, line.Name, line.Revision, ProductionTrackingRules.Status(record.State)));
                continue;
            }
            var binding = package.VersionBindings?.FirstOrDefault(item => item.DocumentId == drawing.DocumentId);
            if (binding is null) continue;
            var source = await repository.FindDocumentVersionAsync(binding.DocumentId, binding.VersionId, ct);
            if (source is null) continue;
            var models = ReleaseBoundSourceReader.ModelDocumentIds(source.ReferenceSnapshot).ToHashSet();
            if (models.Count == 0) models.UnionWith((await repository.ListDocumentRelationsAsync(package.ProjectId, ct))
                .Where(item => item.DrawingDocumentId == drawing.DocumentId).Select(item => item.ModelDocumentId));
            if (!string.Equals(ReleaseBoundSourceReader.DrawingModel(package, source, models, drawing.DrawingNumber), model,
                StringComparison.OrdinalIgnoreCase)) continue;
            results.AddRange(await ResolvePdfScanAsync(PdfReleaseIdentity.Encode(package.Id, binding.VersionId), actor, role, ct));
        }
        return results.DistinctBy(item => (item.ReleasePackageId, item.LineId, item.LotId)).ToArray();
    }

    private async Task<IReadOnlyList<ProductionScanResult>> ResolvePdfScanAsync(string code, string actor, UserRole role, CancellationToken ct)
    {
        if (!PdfReleaseIdentity.TryDecode(code, out var packageId, out var sourceId))
            throw new PdmRuleException("PDF二维码格式不正确，请扫描正式生产图纸。");
        var package = await repository.FindReleasePackageAsync(packageId, ct)
            ?? throw new PdmNotFoundException("二维码对应的发布记录不存在，请核对图纸来源。");
        await RequireProjectAsync(package.ProjectId, actor, role, ct);
        if (package.State != ReleasePackageState.Published) throw new PdmRuleException("二维码对应的图纸尚未正式发布，请核对后使用。");
        var binding = package.VersionBindings?.SingleOrDefault(item => item.VersionId == sourceId)
            ?? throw new PdmRuleException("二维码来源不在该发布包的冻结清单中，请核对图纸。");
        var drawing = await repository.FindDocumentAsync(binding.DocumentId, ct)
            ?? throw new PdmNotFoundException("二维码对应的图纸不存在。");
        if (drawing.ProjectId != package.ProjectId || drawing.Kind != DocumentKind.Drawing || drawing.State == DocumentLifecycleState.Obsolete)
            throw new PdmRuleException("二维码对应的图纸已失效，请核对生产图纸。");
        var source = await repository.FindDocumentVersionAsync(binding.DocumentId, sourceId, ct);
        if (source is null || !string.Equals(source.Sha256, binding.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("二维码对应的冻结来源校验失败，请联系项目负责人核对。");
        var models = ReleaseBoundSourceReader.ModelDocumentIds(source.ReferenceSnapshot).ToHashSet();
        if (models.Count == 0) models.UnionWith((await repository.ListDocumentRelationsAsync(package.ProjectId, ct))
            .Where(relation => relation.DrawingDocumentId == drawing.Id).Select(relation => relation.ModelDocumentId));
        var bom = ProductionDrawingQuantities.FrozenItems(package)
            .Where(item => item.SourceDocumentId.HasValue && models.Contains(item.SourceDocumentId.Value)).ToArray();
        var records = await EnsureProjectAsync((await repository.FindProjectAsync(package.ProjectId, ct))!, ct);
        return records.SelectMany(record => record.State.Lines.Select(line => (record, line)))
            .Where(entry => bom.Any(item => entry.line.Revision == item.Revision
                && (entry.line.SourceTrackingIds.Contains(item.ReleaseTrackingId) || entry.line.SourceBomItemIds.Contains(item.Id)
                    || entry.line.MaterialKey == BomReleaseAggregation.MaterialKey(item))))
            .Select(entry => new ProductionScanResult(entry.record.State.ReleasePackageId, entry.line.Id, null,
                entry.record.State.PackageNumber, entry.line.DrawingNumber, entry.line.Name, entry.line.Revision,
                ProductionTrackingRules.Status(entry.record.State))).ToArray();
    }

    private async Task<ProductionTrackingRecord> MutateAsync(Guid id, string actor, UserRole role,
        Func<ProductionTrackingState, ProductionTrackingState> change, CancellationToken ct)
    {
        var current = await FindAsync(id, actor, role, ct);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var updated = change(current.State);
            if (ReferenceEquals(updated, current.State)) return current;
            var candidate = current with { State = updated, RowVersion = current.RowVersion + 1 };
            if (await store.SaveAsync(candidate, current.RowVersion, ct)) return candidate;
            current = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("发布跟踪单不存在。");
            current = await scanService.ApplyFlowAsync(current, ct);
        }
        throw new PdmConflictException("生产记录正被其他人员更新，请刷新后重试。");
    }
}
