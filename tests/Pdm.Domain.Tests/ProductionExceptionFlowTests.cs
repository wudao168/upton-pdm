using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionExceptionFlowTests : IDisposable
{
    private readonly Guid company = Guid.NewGuid();
    private readonly InMemoryPdmRepository repository = new(TimeProvider.System);
    private readonly InMemoryProductionTrackingStore store = new(DateTimeOffset.UtcNow.AddMinutes(-1));
    private readonly ProductionWorkstation station = new(Guid.NewGuid(), "模拟综合工位", "CNC组", true);
    private ProductionTrackingService Service => new(repository, store, TimeProvider.System, new ProductionDrawingService(repository));
    private ProductionScanService Scan => new(repository, store, TimeProvider.System);
    private Guid releaseId;
    private Guid lotId;
    public ProductionExceptionFlowTests() => TenantContext.Set(new(Guid.NewGuid(), company, company, "admin", "Administrator", false,
        new HashSet<string> { PermissionCodes.ProductionScanManage }));
    public void Dispose() => TenantContext.Clear();

    public static IEnumerable<object[]> EveryStage()
    {
        foreach (var mode in new[] { "DepartmentHandover", "ResourceOperations" })
        foreach (var route in new[] { "Self", "External" })
        {
            var sources = route == "External" ? new[] { "Assigned", "ExternalDelivered", "QualityReceived", "Pass", "Rework" }
                : mode == "DepartmentHandover" ? ["Assigned", "ProductionReceived", "MachiningReceived", "PackingStarted", "QualityReceived", "Pass", "Rework"]
                : new[] { "Assigned", "ProductionReceived", "Start", "MachiningCompleted", "PackingStarted", "Packed", "QualityReceived", "Pass", "Rework", "OutsideSent" };
            foreach (var source in sources) yield return [route, mode, source];
        }
    }

    private async Task<ProductionTrackingRecord> Create(string route, string mode, decimal assigned = 10)
    {
        var project = await repository.CreateProjectAsync(new("EX-" + Guid.NewGuid().ToString("N"), "制造异常全流程模拟", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "EX-001", "模拟件", 10, "件", "钢", "EX-MODEL", "B", true);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "EX-RP", ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        await repository.CreateReleasePackageAsync(package, default);
        await store.SaveScanCatalogAsync(new(company, [station], ProductionFlowRules.Posts.Keys.Select(post =>
            new ProductionPostBinding(Guid.NewGuid(), "Role", "Administrator", post, [station.Id], true)).ToArray())
            { FlowMode = mode, ControlRoleCodes = ["Administrator"] }, 0, default);
        releaseId = package.Id;
        var record = await Service.ReceiveAsync(releaseId, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        record = await Service.AssignAsync(releaseId, new(record.State.Lines[0].Id, route, assigned,
            route == "External" ? "模拟供应商" : null, "模拟负责人", DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        lotId = record.State.Lots[0].Id;
        if (route == "Self" && mode == "ResourceOperations")
        {
            var date = DateOnly.FromDateTime(DateTime.UtcNow);
            await store.SaveCapacityAsync(new(company, [new("CNC组", date, 8)]), 0, default);
            await Move("ProductionReceived", assigned);
            await Service.ScheduleAsync(releaseId, new(lotId, [new("CNC组", date, 1)], date, Guid.NewGuid()), "admin", UserRole.Administrator, default);
            // 测试从未签收环节开始；排程仅作为旧资源组模式的现有前置设置。
            record = await Read();
            record = record with { State = record.State with { Movements = record.State.Movements.Where(m => m.Kind != "ProductionReceived").ToArray() } };
            Assert.True(await store.SaveAsync(record, record.RowVersion, default));
        }
        return await Read();
    }

    private Task<ProductionTrackingRecord> Read() => Service.FindAsync(releaseId, "admin", UserRole.Administrator, default);
    private async Task<ProductionTrackingRecord> Move(string kind, decimal quantity, string? source = null, Guid? request = null, Guid? workstationId = null)
    {
        var record = await Service.RecordAsync(releaseId, new(lotId, kind, quantity, request ?? Guid.NewGuid(),
            "模拟异常：已隔离并登记实物去向", kind == "Issue" ? "模拟领用人" : null,
            ProductionDepartmentFlow.IsControl(kind) ? null : workstationId ?? station.Id, source), "admin", UserRole.Administrator, default);
        CheckConservation(record.State);
        return record;
    }

    private static void CheckConservation(ProductionTrackingState state)
    {
        foreach (var lot in state.Lots)
        {
            decimal Sum(string kind) => ProductionFlowRules.Total(state, lot, kind);
            var pools = ProductionQuantityFlow.Pools(state, lot);
            Assert.All(pools, p => Assert.InRange(p.PausedQuantity, 0, p.Quantity));
            Assert.Equal(lot.Quantity, pools.Sum(p => p.Quantity) + Sum("Issue") + Sum("Scrap") + Sum("TaskCancelled") + Sum("Cancelled") + Sum("Transferred"));
            Assert.True(ProductionQuantityFlow.Qualified(state, lot) >= Sum("Issue"));
        }
    }

    private async Task Reach(string source)
    {
        var record = await Read();
        var steps = ProductionQuantityFlow.Steps(record.State, record.State.Lots.Single(l => l.Id == lotId));
        var target = source == "Rework" ? "QualityReceived" : source == "OutsideSent" ? "MachiningCompleted" : source;
        foreach (var step in steps.Take(Array.IndexOf(steps, target) + 1)) await Move(step, 10);
        if (source == "Rework") await Move("Rework", 10);
        if (source == "OutsideSent") await Move("OutsideSent", 10);
    }

    private async Task FinishAvailable()
    {
        for (var count = 0; count < 30; count++)
        {
            var record = await Read();
            var context = await Scan.ActionsAsync(record.State, "admin", UserRole.Administrator, default);
            var action = context.Actions.FirstOrDefault(a => a.LotId == lotId && a.Post != "Control" && a.Kind is not ("Trace" or "Scrap" or "Rework" or "OutsideSent"));
            if (action is null) return;
            await Move(action.Kind, action.RemainingQuantity, workstationId: action.WorkstationId);
        }
        Assert.Fail("正常流转未能在有限步骤内结束。");
    }

    [Theory]
    [MemberData(nameof(EveryStage))]
    public async Task EveryStageSupportsPartialPauseCancelScrapResumeAndReplacementWithoutQuantityLoss(string route, string mode, string source)
    {
        await Create(route, mode);
        await Reach(source);
        await Move("Pause", 3, source);
        await Move("TaskCancelled", 2, source);
        await Move("StageScrap", 2, source);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move("StageScrap", 99, source));
        await FinishAvailable();
        var paused = await Read();
        Assert.Equal(3, ProductionTrackingRules.Issued(paused.State, paused.State.Lines[0].Id));
        Assert.Equal(3, ProductionQuantityFlow.Paused(paused.State, paused.State.Lots[0]));
        await Move("Resume", 2, source);
        await FinishAvailable();
        await Move("Resume", 1, source);
        await FinishAvailable();
        var shortfall = await Read();
        var line = shortfall.State.Lines[0];
        Assert.Equal(10, ProductionTrackingRules.EffectiveDemand(shortfall.State, line));
        Assert.Equal(6, ProductionTrackingRules.Issued(shortfall.State, line.Id));
        Assert.Equal(4, ProductionTrackingRules.Unassigned(shortfall.State, line));
        Assert.Equal("数量不足，待补齐", ProductionTrackingRules.Status(shortfall.State));
        Assert.Equal("数量不足，待补齐", ProductionTrackingRules.Status(route == "Self"
            ? ProductionTrackingRules.SelfProduction(shortfall.State)! : ProductionTrackingRules.ExternalProduction(shortfall.State)!));
        var replacement = await Service.AssignAsync(releaseId, new(line.Id, "External", 4, "补货供应商", "供应链", DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        lotId = replacement.State.Lots.Last().Id;
        await Assert.ThrowsAsync<PdmRuleException>(() => Service.AssignAsync(releaseId, new(line.Id, "Self", 1, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, default));
        await FinishAvailable();
        var finished = await Read();
        Assert.Equal(10, ProductionTrackingRules.Issued(finished.State, line.Id));
        Assert.Equal(10, ProductionTrackingRules.Qualified(finished.State, line.Id));
        Assert.Equal(0, ProductionTrackingRules.Unassigned(finished.State, line));
        Assert.Equal(mode == "DepartmentHandover" && finished.State.Lots.All(l => l.Route == "Self") ? "领料完成" : "已完结", ProductionTrackingRules.Status(finished.State));
        foreach (var view in new[] { ProductionTrackingRules.SelfProduction(finished.State), ProductionTrackingRules.ExternalProduction(finished.State) }.OfType<ProductionTrackingState>())
            Assert.Equal(ProductionTrackingRules.EffectiveDemand(view, view.Lines[0]), ProductionTrackingRules.Issued(view, line.Id));
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("External")]
    public async Task DemandCancellationReducesOnlyDemandAndDoesNotConsumeUnallocatedQuantityTwice(string route)
    {
        var record = await Create(route, "DepartmentHandover", 6);
        await Move("Cancelled", 2, "Assigned");
        await FinishAvailable();
        record = await Read();
        var line = record.State.Lines[0];
        Assert.Equal(8, ProductionTrackingRules.EffectiveDemand(record.State, line));
        Assert.Equal(4, ProductionTrackingRules.Unassigned(record.State, line));
        record = await Service.AssignAsync(releaseId, new(line.Id, "External", 4, "补充供应商", null, DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        lotId = record.State.Lots.Last().Id;
        await FinishAvailable();
        record = await Read();
        Assert.Equal(8, ProductionTrackingRules.Issued(record.State, line.Id));
        Assert.Equal(1, ProductionTrackingRules.IssuedLines(record.State));
    }

    [Theory]
    [MemberData(nameof(EveryStage))]
    public async Task DemandCanBePartiallyCancelledAtEveryStageWithoutCancellingCompletedDelivery(string route, string mode, string source)
    {
        await Create(route, mode);
        await Reach(source);
        await Move("Cancelled", 2, source);
        await FinishAvailable();
        var record = await Read();
        Assert.Equal(8, ProductionTrackingRules.EffectiveDemand(record.State, record.State.Lines[0]));
        Assert.Equal(8, ProductionTrackingRules.Issued(record.State, record.State.Lines[0].Id));
        Assert.Equal(0, ProductionTrackingRules.Unassigned(record.State, record.State.Lines[0]));
        Assert.Equal("变更已结案", ProductionTrackingRules.Status(record.State));
        await Assert.ThrowsAsync<PdmRuleException>(() => Move("StageScrap", 1, "Issue"));
    }

    [Fact]
    public async Task MixedStagePauseOnlyBlocksSelectedStageAndFrozenLotsStillPermitControlledDisposal()
    {
        await Create("External", "DepartmentHandover");
        await Move("ExternalDelivered", 6);
        await Move("QualityReceived", 4);
        await Move("Pass", 2);
        await Move("Pause", 2, "Assigned");
        await Move("Pause", 1, "Pass");
        await Move("Issue", 1);
        var record = await Read();
        Assert.Equal(2, ProductionFlowRules.Remaining(record.State, record.State.Lots[0], "ExternalDelivered"));
        Assert.Equal(2, ProductionFlowRules.Remaining(record.State, record.State.Lots[0], "Pass"));
        Assert.Equal(0, ProductionFlowRules.Remaining(record.State, record.State.Lots[0], "Issue"));
        record = record with { State = record.State with { Lots = [record.State.Lots[0] with { Frozen = true }] } };
        Assert.True(await store.SaveAsync(record, record.RowVersion, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => Move("Pass", 1));
        await Move("StageScrap", 1, "ExternalDelivered");
        await Move("Resume", 1, "Pass");
        CheckConservation((await Read()).State);
    }

    [Fact]
    public async Task LegacyWholeLotPauseCanBeResumedBeforeUsingPartialControls()
    {
        var record = await Create("External", "DepartmentHandover");
        record = record with { State = record.State with { Lots = [record.State.Lots[0] with { Paused = true }],
            Movements = record.State.Movements.Append(new ProductionMovement(Guid.NewGuid(), Guid.NewGuid(), lotId, "Pause", 10, "admin", DateTimeOffset.UtcNow, "历史暂停")).ToArray() } };
        Assert.True(await store.SaveAsync(record, record.RowVersion, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => Move("Resume", 2));
        await Move("Resume", 10);
        await Move("Pause", 2, "Assigned");
        await FinishAvailable();
        Assert.Equal(8, ProductionTrackingRules.Issued((await Read()).State, record.State.Lines[0].Id));
    }

    [Theory]
    [InlineData("DepartmentHandover")]
    [InlineData("ResourceOperations")]
    public async Task FullyScrappedDemandCanBeCancelledWithoutRemovingScrapHistory(string mode)
    {
        var record = await Create("External", mode);
        await Move("StageScrap", 10, "Assigned");
        var input = new CancelUnassignedProductionDemand(record.State.Lines[0].Id, 10, "项目取消，不再补货", Guid.NewGuid());
        record = await Service.CancelUnassignedDemandAsync(releaseId, input, "admin", UserRole.Administrator, default);
        Assert.Equal(10, ProductionTrackingRules.Total(record.State, lotId, "Scrap"));
        Assert.Equal(0, ProductionTrackingRules.EffectiveDemand(record.State, record.State.Lines[0]));
        Assert.Equal(0, ProductionTrackingRules.Unassigned(record.State, record.State.Lines[0]));
        Assert.Equal("变更已结案", ProductionTrackingRules.Status(record.State));
        Assert.Equal(record.RowVersion, (await Service.CancelUnassignedDemandAsync(releaseId, input, "admin", UserRole.Administrator, default)).RowVersion);
        await Assert.ThrowsAsync<PdmConflictException>(() => Service.CancelUnassignedDemandAsync(releaseId, input with { Quantity = 9 }, "admin", UserRole.Administrator, default));
        CheckConservation(record.State);
    }

    [Fact]
    public async Task CancellingUnassignedDemandCannotReduceAllocatedWorkAndBatchAllocationUsesReducedDemand()
    {
        var record = await Create("Self", "DepartmentHandover", 6);
        var input = new CancelUnassignedProductionDemand(record.State.Lines[0].Id, 5, "需求减少", Guid.NewGuid());
        await Assert.ThrowsAsync<PdmRuleException>(() => Service.CancelUnassignedDemandAsync(releaseId, input, "admin", UserRole.Administrator, default));
        record = await Service.CancelUnassignedDemandAsync(releaseId, input with { Quantity = 2 }, "admin", UserRole.Administrator, default);
        record = await Service.BatchAllocateAsync(releaseId, new([record.State.Lines[0].Id], "Self", null, null, null, "补齐减量后需求", Guid.NewGuid(), record.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(8, record.State.Lots.Sum(l => l.Quantity));
        Assert.Equal(8, ProductionTrackingRules.EffectiveDemand(record.State, record.State.Lines[0]));
        await Assert.ThrowsAsync<PdmRuleException>(() => Service.CancelUnassignedDemandAsync(releaseId, input with { RequestId = Guid.NewGuid(), Quantity = 1 }, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task RepeatedReworkPartialDeliveriesAndQualityScrapDoNotDuplicateQualifiedQuantity()
    {
        await Create("External", "DepartmentHandover");
        await Move("ExternalDelivered", 6);
        await Move("QualityReceived", 6);
        await Move("Pass", 2);
        await Move("Rework", 4);
        await Move("ReworkCompleted", 4);
        await Move("Rework", 3);
        await Move("Scrap", 1);
        await Move("StageScrap", 1, "Rework");
        await Move("ReworkCompleted", 2);
        await Move("Pass", 2);
        await Move("Issue", 4);
        await Move("ExternalDelivered", 4);
        await FinishAvailable();
        var record = await Read();
        Assert.Equal(8, ProductionTrackingRules.Issued(record.State, record.State.Lines[0].Id));
        Assert.Equal(2, ProductionTrackingRules.Unassigned(record.State, record.State.Lines[0]));
    }

    [Fact]
    public async Task ReplayedAndConcurrentExceptionsCannotRetireTheSameQuantityTwice()
    {
        await Create("External", "DepartmentHandover");
        var request = Guid.NewGuid();
        var record = await Move("TaskCancelled", 2, "Assigned", request);
        Assert.Equal(record.RowVersion, (await Move("TaskCancelled", 2, "Assigned", request)).RowVersion);
        await Assert.ThrowsAsync<PdmConflictException>(() => Move("TaskCancelled", 2, "Pass", request));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await Move("StageScrap", 5, "Assigned"); return true; }
            catch (Exception error) when (error is PdmRuleException or PdmConflictException) { return false; }
        }));
        Assert.Single(outcomes, success => success);
        CheckConservation((await Read()).State);
    }

    [Fact]
    public async Task DifferentResourceGroupProgressKeepsPartialPauseAndScrapConserved()
    {
        var record = await Create("Self", "ResourceOperations");
        var second = new ProductionWorkstation(Guid.NewGuid(), "第二模拟工位", "车床组", true);
        var catalog = await store.ScanCatalogAsync(company, default);
        Assert.True(await store.SaveScanCatalogAsync(catalog with { Workstations = [station, second], Bindings = catalog.Bindings.Select(b =>
            b.Post == "Machining" ? b with { WorkstationIds = [station.Id, second.Id] } : b).ToArray() }, catalog.RowVersion, default));
        record = record with { State = record.State with { Lots = [record.State.Lots[0] with { Schedule = [.. record.State.Lots[0].Schedule,
            new("车床组", DateOnly.FromDateTime(DateTime.UtcNow), 1)] }] } };
        Assert.True(await store.SaveAsync(record, record.RowVersion, default));
        await Move("ProductionReceived", 10);
        await Move("Start", 10);
        await Move("MachiningCompleted", 8);
        await Move("Start", 4, workstationId: second.Id);
        await Move("MachiningCompleted", 2, workstationId: second.Id);
        await Move("Pause", 2, "Start");
        await Move("StageScrap", 2, "Start");
        await FinishAvailable();
        await Move("Resume", 2, "Start");
        await FinishAvailable();
        record = await Read();
        Assert.Equal(8, ProductionTrackingRules.Issued(record.State, record.State.Lines[0].Id));
        Assert.Equal(2, ProductionTrackingRules.Unassigned(record.State, record.State.Lines[0]));
    }
}
