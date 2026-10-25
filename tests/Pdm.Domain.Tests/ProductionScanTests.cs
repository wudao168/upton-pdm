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

    private async Task ConfigureDepartmentAsync()
    {
        await repository.CreateUserAsync(new(Guid.NewGuid(), "control-manager", "流程经理", "unused", UserRole.Administrator, true,
            CompanyId: company, RoleCode: "ProductionManager"), default);
        await Scan.SaveAsync(new(Stations, [Bind("receive", "Reception", reception), Bind("machine", "MachiningReception", cnc),
            Bind("machine", "Trace", cnc, lathe), Bind("packing", "Packing", packing), Bind("quality", "QualityReceiving", quality),
            Bind("quality", "Inspection", quality), Bind("issue", "Issue", issue)], 0)
        { FlowMode = "DepartmentHandover", ControlRoleCodes = ["ProductionManager"] }, "admin", UserRole.Administrator, default);
    }

    [Fact]
    public async Task DepartmentHandoverHasOptionalTraceAndNoResourceCompletionOrScheduleGate()
    {
        var record = await CreateAsync(); await ConfigureDepartmentAsync();
        record = await Tracking.FindAsync(record.State.ReleasePackageId, "admin", UserRole.Administrator, default);
        Assert.Equal("DepartmentHandover", record.State.FlowMode);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "machine", "Trace", 1, cnc));
        record = await Move(record, "receive", "ProductionReceived", 4, reception);
        Assert.Equal("排产", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        record = await Move(record, "machine", "MachiningReceived", 4, cnc);
        Assert.Equal("机加", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        record = await Move(record, "machine", "Trace", 2, cnc, note: "CNC图纸追溯");
        record = await Move(record, "machine", "Trace", 2, cnc);
        Assert.Equal("CNC组", record.State.Movements.Last().ResourceGroup);
        Assert.Equal(record.State.Movements.Last().FromStage, record.State.Movements.Last().ToStage);
        Assert.Equal(4, ProductionFlowRules.Remaining(record.State, record.State.Lots[0], "PackingStarted"));
        record = await Move(record, "packing", "PackingStarted", 4, packing);
        Assert.Equal("包装", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        Assert.True((await Scan.ActionsAsync(record.State, "packing", UserRole.Administrator, default)).CanPrintLabels);
        Assert.False((await Scan.ActionsAsync(record.State, "quality", UserRole.Administrator, default)).CanPrintLabels);
        record = await Move(record, "quality", "QualityReceived", 4, quality);
        Assert.Equal("质检", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        record = await Move(record, "quality", "Pass", 4, quality);
        Assert.Equal("质检完成", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        record = await Move(record, "issue", "Issue", 4, issue, recipient: "装配领料人");
        Assert.Equal("领料完成", ProductionTrackingRules.Status(record.State));
        Assert.Empty((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions);
    }

    [Fact]
    public async Task ConfiguredControlRolesCanPauseResumeAndCancelOnlyOutstandingQuantityWithAudit()
    {
        var record = await CreateAsync(); await ConfigureDepartmentAsync();
        record = await Move(record, "receive", "ProductionReceived", 4, reception);
        record = await Plan(record);
        async Task<ProductionTrackingRecord> Control(string actor, string kind, decimal quantity, string? note = "流程调整", Guid? request = null) =>
            await Tracking.RecordAsync(record.State.ReleasePackageId, new(record.State.Lots[0].Id, kind, quantity, request ?? Guid.NewGuid(), note), actor, UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => Control("machine", "Pause", 4));
        await Assert.ThrowsAsync<PdmRuleException>(() => Control("control-manager", "Pause", 4, null));
        record = await Control("control-manager", "Pause", 2);
        Assert.False(record.State.Lots[0].Paused);
        record = await Control("control-manager", "Resume", 2);
        record = await Control("control-manager", "Pause", 4);
        Assert.True(record.State.Lots[0].Paused);
        Assert.Empty((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions);
        var request = Guid.NewGuid(); record = await Control("control-manager", "Resume", 4, request: request);
        Assert.Equal(record.RowVersion, (await Control("control-manager", "Resume", 4, request: request)).RowVersion);
        record = await Move(record, "machine", "MachiningReceived", 4, cnc);
        record = await Move(record, "packing", "PackingStarted", 4, packing);
        record = await Move(record, "quality", "QualityReceived", 4, quality);
        record = await Move(record, "quality", "Pass", 4, quality);
        record = await Move(record, "issue", "Issue", 1, issue, recipient: "领料人");
        Assert.True(await store.SaveAsync(record with { State = record.State with { Lots = [record.State.Lots[0] with { Frozen = true, FreezeReason = "图纸变更" }] } }, record.RowVersion, default));
        record = await Control("control-manager", "Cancelled", 3);
        Assert.Equal(1, ProductionFlowRules.Total(record.State, record.State.Lots[0], "Issue"));
        Assert.Equal(3, ProductionFlowRules.Total(record.State, record.State.Lots[0], "Cancelled"));
        Assert.Empty(record.State.Lots[0].Schedule);
        Assert.Equal("已取消剩余流程", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        Assert.Equal("变更已结案", ProductionTrackingRules.Status(record.State));
        Assert.Equal("control-manager", record.State.Movements.Last().Actor);
        Assert.Null(record.State.Movements.Last().WorkstationId);
        Assert.Empty((await Scan.ActionsAsync(record.State, "control-manager", UserRole.Administrator, default)).Actions);
    }

    [Fact]
    public async Task DepartmentPackingAcceptsRolesAndControlRolesAndLabelSizesAreValidated()
    {
        await CreateAsync();
        var input = new SaveProductionScanCatalog(Stations, [new(Guid.NewGuid(), "Role", "Administrator", "Packing", [packing.Id], true)], 0)
        { FlowMode = "DepartmentHandover" };
        var saved = await Scan.SaveAsync(input, "admin", UserRole.Administrator, default);
        Assert.Equal("Role", Assert.Single(saved.Bindings).SubjectType);
        input = input with { ExpectedRowVersion = saved.RowVersion };
        await Assert.ThrowsAsync<PdmRuleException>(() => Scan.SaveAsync(input with { Bindings = [], ControlRoleCodes = ["missing-role"] }, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => Scan.SaveAsync(input with { Bindings = [], LabelWidthMm = 1 }, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task ConfiguredNodesGatePartialQuantitiesAndKeepNamesAfterNodeDeletion()
    {
        var record = await CreateAsync();
        var nodes = ProductionDepartmentFlow.DefaultNodes.ToList();
        var custom = new ProductionFlowNode(Guid.NewGuid(), "清洗接收", "清洗", "Reception", "");
        custom = custom with { Kind = $"Step:{custom.Id:N}" }; nodes.Insert(1, custom);
        var configured = new SaveProductionScanCatalog(Stations,
            [Bind("receive", "Reception", reception) with { FlowNodeId = nodes[0].Id },
             Bind("machine", "Reception", cnc) with { FlowNodeId = custom.Id }, Bind("machine", "MachiningReception", cnc),
             Bind("packing", "Packing", packing), Bind("quality", "QualityReceiving", quality), Bind("quality", "Inspection", quality), Bind("issue", "Issue", issue)], 0)
            { FlowMode = "DepartmentHandover", FlowNodes = nodes };
        var catalog = await Scan.SaveAsync(configured, "admin", UserRole.Administrator, default);
        Assert.Empty((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions);
        record = await Move(record, "receive", "ProductionReceived", 2, reception);
        Assert.Equal(custom.Kind, Assert.Single((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions).Kind);
        await Assert.ThrowsAsync<PdmRuleException>(() => Move(record, "machine", custom.Kind, 3, cnc));
        record = await Move(record, "machine", custom.Kind, 2, cnc);
        var movement = record.State.Movements.Last();
        Assert.Equal("清洗接收", movement.OperationName); Assert.Equal("排产", movement.FromStage); Assert.Equal("清洗", movement.ToStage);
        Assert.Equal("machine", movement.Actor); Assert.Equal(cnc.Id, movement.WorkstationId);
        var action = Assert.Single((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions);
        Assert.Equal("MachiningReceived", action.Kind); Assert.Equal(2, action.RemainingQuantity);
        await Scan.SaveAsync(configured with { ExpectedRowVersion = catalog.RowVersion, FlowNodes = nodes.Where(node => node.Id != custom.Id).ToArray(), Bindings = configured.Bindings.Where(binding => binding.FlowNodeId != custom.Id).ToArray() }, "admin", UserRole.Administrator, default);
        record = await Tracking.FindAsync(record.State.ReleasePackageId, "admin", UserRole.Administrator, default);
        Assert.Equal("清洗接收", record.State.Movements.Last().OperationName);
        Assert.DoesNotContain(record.State.FlowNodes!, node => node.Id == custom.Id);
    }

    [Fact]
    public async Task NodeOrderActuallyChangesFirstStepAndInvalidReferencesAreRejected()
    {
        var record = await CreateAsync();
        var defaults = ProductionDepartmentFlow.DefaultNodes;
        var nodes = new[] { defaults[1], defaults[0] }.Concat(defaults.Skip(2)).ToArray();
        var input = new SaveProductionScanCatalog(Stations, [Bind("machine", "MachiningReception", cnc), Bind("receive", "Reception", reception)], 0)
            { FlowMode = "DepartmentHandover", FlowNodes = nodes };
        await Assert.ThrowsAsync<PdmRuleException>(() => Scan.SaveAsync(input with { FlowNodes = nodes.Reverse().ToArray() }, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => Scan.SaveAsync(input with { FlowNodes = nodes.Select(node => node.Kind == "Trace" ? node with { AfterNodeId = Guid.NewGuid() } : node).ToArray() }, "admin", UserRole.Administrator, default));
        await Scan.SaveAsync(input, "admin", UserRole.Administrator, default);
        Assert.Empty((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions);
        Assert.Equal("MachiningReceived", Assert.Single((await Scan.ActionsAsync(record.State, "machine", UserRole.Administrator, default)).Actions).Kind);
        record = await Move(record, "machine", "MachiningReceived", 4, cnc);
        Assert.Equal("机加", ProductionFlowRules.Stage(record.State, record.State.Lots[0]));
        Assert.Equal("ProductionReceived", Assert.Single((await Scan.ActionsAsync(record.State, "receive", UserRole.Administrator, default)).Actions).Kind);
    }

    [Fact]
    public async Task OlderSettingsRequestsCannotEraseConfiguredNodesOrBindingScope()
    {
        await CreateAsync();
        var nodes = ProductionDepartmentFlow.DefaultNodes;
        var binding = Bind("receive", "Reception", reception) with { FlowNodeId = nodes[0].Id };
        var saved = await Scan.SaveAsync(new(Stations, [binding], 0) { FlowMode = "DepartmentHandover", FlowNodes = nodes }, "admin", UserRole.Administrator, default);
        var legacyRequest = new SaveProductionScanCatalog(Stations, [binding with { FlowNodeId = null }], saved.RowVersion) { FlowMode = "DepartmentHandover" };
        saved = await Scan.SaveAsync(legacyRequest, "admin", UserRole.Administrator, default);
        Assert.Equal(nodes, saved.FlowNodes);
        Assert.Equal(nodes[0].Id, Assert.Single(saved.Bindings).FlowNodeId);
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
