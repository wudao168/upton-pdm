using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionScanTests : IDisposable
{
    private readonly Guid company = Guid.NewGuid();
    private readonly InMemoryPdmRepository repository = new(TimeProvider.System);
    private readonly InMemoryProductionTrackingStore store = new(DateTimeOffset.UtcNow.AddMinutes(-1));
    private readonly ProductionWorkstation reception = new(Guid.NewGuid(), "生产签收台", "", true);
    private readonly ProductionWorkstation cnc = new(Guid.NewGuid(), "CNC一号工位", "CNC组", true);
    private readonly ProductionWorkstation lathe = new(Guid.NewGuid(), "车床一号工位", "车床组", true);
    private readonly ProductionWorkstation packing = new(Guid.NewGuid(), "包装台", "", true);
    private readonly ProductionWorkstation quality = new(Guid.NewGuid(), "质量台", "", true);
    private readonly ProductionWorkstation issue = new(Guid.NewGuid(), "领料台", "", true);
    private ProductionScanService Scan => new(repository, store, TimeProvider.System);
    private ProductionTrackingService Tracking => new(repository, store, TimeProvider.System, new ProductionDrawingService(repository));
    public ProductionScanTests() => TenantContext.Set(new(Guid.NewGuid(), company, company, "admin", "Administrator", false,
        new HashSet<string> { PermissionCodes.ProductionScanManage }));
    public void Dispose() => TenantContext.Clear();
    private ProductionWorkstation[] Stations => [reception, cnc, lathe, packing, quality, issue];
    private ProductionPostBinding Bind(string user, string post, params ProductionWorkstation[] stations) =>
        new(Guid.NewGuid(), "User", user, post, stations.Select(station => station.Id).ToArray(), true);
    private async Task<ProductionTrackingRecord> CreateAsync()
    {
        foreach (var name in new[] { "receive", "machine", "packing", "quality", "issue" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, "人员-" + name, "unused", UserRole.Administrator, true, CompanyId: company), default);
        var project = await repository.CreateProjectAsync(new("SCAN-" + Guid.NewGuid().ToString("N"), "扫码回归", "admin", "vault", "release"), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "0204001", "支架", 4, "件", "钢", "MODEL-001", "W1", true);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-SCAN", ReleasePackageState.Published, null, "W1", "W1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
        { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        await repository.CreateReleasePackageAsync(package, default);
        var received = await Tracking.ReceiveAsync(package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        return await Tracking.AssignAsync(package.Id, new(received.State.Lines[0].Id, "Self", 4, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, default);
    }
    private async Task ConfigureAsync()
    {
        await Scan.SaveAsync(new(Stations, [Bind("receive", "Reception", reception), Bind("machine", "Machining", cnc, lathe),
            Bind("packing", "Packing", packing), Bind("quality", "QualityReceiving", quality), Bind("quality", "Inspection", quality), Bind("issue", "Issue", issue)], 0), "admin", UserRole.Administrator, default);
    }
    private async Task<ProductionTrackingRecord> Move(ProductionTrackingRecord record, string actor, string kind, decimal quantity,
        ProductionWorkstation station, Guid? requestId = null, string? note = null, string? recipient = null) =>
        await Tracking.RecordAsync(record.State.ReleasePackageId, new(record.State.Lots[0].Id, kind, quantity, requestId ?? Guid.NewGuid(), note, recipient, station.Id), actor, UserRole.Administrator, default);
    private async Task<ProductionTrackingRecord> Plan(ProductionTrackingRecord record)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        await store.SaveCapacityAsync(new(company, [new("CNC组", date, 8), new("车床组", date, 8)]), 0, default);
        return await Tracking.ScheduleAsync(record.State.ReleasePackageId, new(record.State.Lots[0].Id,
            [new("CNC组", date, 1), new("车床组", date, 1)], date, Guid.NewGuid()), "admin", UserRole.Administrator, default);
    }

    [Fact]
    public async Task EmptyConfigurationGrantsNobodyAnOperationAndPersonalBindingsOverrideRoles()
    {
        var record = await CreateAsync();
        Assert.Empty((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "receive", "ProductionReceived", 4, reception));
        var role = new ProductionPostBinding(Guid.NewGuid(), "Role", "Administrator", "Reception", [reception.Id], true);
        var personal = Bind("receive", "Packing", packing);
        var catalog = await Scan.SaveAsync(new(Stations, [role, personal], 0), "admin", UserRole.Administrator, default);
        Assert.Empty((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
        Assert.Single((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions);
        catalog = await Scan.SaveAsync(new(Stations, [role, personal with { IsActive = false }], catalog.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Empty((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
        await Scan.SaveAsync(new(Stations, [role], catalog.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Single((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
        await Assert.ThrowsAsync<PdmConflictException>(() => Scan.SaveAsync(new(Stations, [role], 0), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EachResourceGroupMustFinishBeforePackingAndEveryConfirmationKeepsIdentityAndStation()
    {
        var record = await CreateAsync();
        await ConfigureAsync();
        await Assert.ThrowsAsync<PdmRuleException>(() => Plan(record));
        var requestId = Guid.NewGuid();
        record = await Move(record, "receive", "ProductionReceived", 4, reception, requestId);
        var replay = await Move(record, "receive", "ProductionReceived", 4, reception, requestId);
        Assert.Equal(record.RowVersion, replay.RowVersion);
        await Assert.ThrowsAsync<PdmConflictException>(() => Move(record, "receive", "ProductionReceived", 3, reception, requestId));
        record = await Plan(record);
        Assert.Equal(2, (await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions.Count(action => action.Kind == "Start"));
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "packing", "Start", 4, cnc));
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "machine", "Start", 4, packing));
        record = await Move(record, "machine", "Start", 4, cnc);
        record = await Move(record, "machine", "MachiningCompleted", 4, cnc);
        Assert.Empty((await Scan.ActionsAsync(record.State, "packing", UserRole.Administrator, default)).Actions);
        record = await Move(record, "machine", "Start", 4, lathe);
        record = await Move(record, "machine", "MachiningCompleted", 4, lathe);
        Assert.Equal("待包装", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "packing", "Packed", 4, packing));
        record = await Move(record, "packing", "PackingStarted", 2, packing);
        record = await Move(record, "packing", "Packed", 2, packing);
        record = await Move(record, "quality", "QualityReceived", 2, quality);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "quality", "Pass", 3, quality));
        record = await Move(record, "quality", "Pass", 1, quality);
        record = await Move(record, "quality", "Rework", 1, quality, note: "尺寸返修");
        record = await Move(record, "machine", "ReworkCompleted", 1, cnc);
        record = await Move(record, "quality", "Pass", 1, quality);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "issue", "Issue", 2, issue));
        record = await Move(record, "issue", "Issue", 2, issue, recipient: "领料人甲");
        record = await Move(record, "packing", "PackingStarted", 2, packing);
        record = await Move(record, "packing", "Packed", 2, packing);
        record = await Move(record, "quality", "QualityReceived", 2, quality);
        record = await Move(record, "quality", "Pass", 2, quality);
        record = await Move(record, "issue", "Issue", 2, issue, recipient: "领料人甲");
        Assert.Equal("已领用", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        Assert.Equal(4, ProductionTrackingRules.Total(record.State, record.State.Lots[0].Id, "Issue"));
        var steps = record.State.Movements.Where(item => ProductionFlowRules.Labels.ContainsKey(item.Kind)).ToArray();
        Assert.All(steps, movement => { Assert.NotNull(movement.BindingId); Assert.NotNull(movement.WorkstationId); Assert.NotEmpty(movement.WorkstationName!); Assert.NotEmpty(movement.FromStage!); Assert.NotEmpty(movement.ToStage!); Assert.Equal("人员-" + movement.Actor, movement.ActorName); });
        Assert.Contains(steps, movement => movement.ResourceGroup == "CNC组" && movement.Kind == "MachiningCompleted");
        Assert.Contains(steps, movement => movement.ResourceGroup == "车床组" && movement.Kind == "MachiningCompleted");
    }

    [Fact]
    public async Task BindingCannotGrantMissingPermissionsAndCatalogIsCompanyScoped()
    {
        var record = await CreateAsync();
        await repository.CreateUserAsync(new(Guid.NewGuid(), "reader", "只读人员", "unused", UserRole.Engineer, true, CompanyId: company), default);
        var config = await Scan.SaveAsync(new(Stations, [Bind("reader", "Reception", reception)], 0), "admin", UserRole.Administrator, default);
        Assert.Empty((await Scan.ActionsAsync(record.State, "reader", UserRole.Engineer, default)).Actions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Scan.SaveAsync(new(Stations, [], config.RowVersion), "reader", UserRole.Engineer, default));
        var foreign = Guid.NewGuid();
        TenantContext.Set(new(Guid.NewGuid(), foreign, foreign, "admin", "Administrator", false, new HashSet<string>()));
        Assert.Empty((await Scan.ReadAsync("admin", UserRole.Administrator, default)).Bindings);
        await Assert.ThrowsAsync<PdmRuleException>(() => Scan.SaveAsync(new(Stations, [Bind("receive", "Reception", reception)], 0), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task RouteChangesBeforeMachiningRequireNewReceiptAndPreserveOldHistory()
    {
        var record = await CreateAsync();
        await ConfigureAsync();
        record = await Move(record, "receive", "ProductionReceived", 4, reception);
        var oldReceipt = record.State.Movements.Last();
        var lot = record.State.Lots[0];
        record = await Tracking.UpdateAllocationAsync(record.State.ReleasePackageId, new(lot.Id, "External", 4, "外协厂", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), null, Guid.NewGuid(), record.RowVersion), "admin", UserRole.Administrator, default);
        record = await Tracking.UpdateAllocationAsync(record.State.ReleasePackageId, new(lot.Id, "Self", 4, null, null,
            null, null, Guid.NewGuid(), record.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(0, ProductionFlowRules.Received(record.State, record.State.Lots[0]));
        Assert.Contains(record.State.Movements, movement => movement.Id == oldReceipt.Id && movement.WorkstationId == oldReceipt.WorkstationId);
        Assert.Single((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
    }
}
