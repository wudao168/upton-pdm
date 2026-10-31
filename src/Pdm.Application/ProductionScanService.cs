using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionWorkstation(Guid Id, string Name, string ResourceGroup, bool IsActive);
public sealed record ProductionPostBinding(Guid Id, string SubjectType, string Subject, string Post,
    IReadOnlyList<Guid> WorkstationIds, bool IsActive)
{
    public Guid? FlowNodeId { get; init; }
}
public sealed record ProductionFlowNode(Guid Id, string Name, string Stage, string Post, string Kind,
    bool Required = true, Guid? AfterNodeId = null);
public sealed record ProductionScanCatalog(Guid CompanyId, IReadOnlyList<ProductionWorkstation> Workstations,
    IReadOnlyList<ProductionPostBinding> Bindings, long RowVersion = 0)
{
    public string FlowMode { get; init; } = "ResourceOperations";
    public IReadOnlyList<string> ControlRoleCodes { get; init; } = [];
    public int LabelWidthMm { get; init; } = 60;
    public int LabelHeightMm { get; init; } = 40;
    public IReadOnlyList<ProductionFlowNode>? FlowNodes { get; init; }
}
public sealed record SaveProductionScanCatalog(IReadOnlyList<ProductionWorkstation> Workstations,
    IReadOnlyList<ProductionPostBinding> Bindings, long ExpectedRowVersion)
{
    public string FlowMode { get; init; } = "ResourceOperations";
    public IReadOnlyList<string> ControlRoleCodes { get; init; } = [];
    public int LabelWidthMm { get; init; } = 60;
    public int LabelHeightMm { get; init; } = 40;
    public IReadOnlyList<ProductionFlowNode>? FlowNodes { get; init; }
}
public sealed record ProductionScanAction(Guid LotId, string Kind, string Label, decimal RemainingQuantity,
    Guid BindingId, string Post, Guid WorkstationId, string WorkstationName, string ResourceGroup)
{
    public string? SourceKind { get; init; }
    public string? SourceName { get; init; }
}
public sealed record ProductionScanContext(IReadOnlyList<ProductionScanAction> Actions, string Hint)
{
    public IReadOnlyList<ProductionScanResult> RelatedDemands { get; init; } = [];
    public bool CanCancelUnassignedDemand { get; init; }
    public bool CanPrintLabels { get; init; }
    public int LabelWidthMm { get; init; } = 60;
    public int LabelHeightMm { get; init; } = 40;
}
public sealed record ProductionScanUserOption(string Username, string DisplayName);
public sealed record ProductionScanRoleOption(string Role, string Name);
public sealed record ProductionScanOptions(IReadOnlyList<ProductionScanUserOption> Users, IReadOnlyList<ProductionScanRoleOption> Roles);

public static class ProductionFlowRules
{
    public static readonly IReadOnlyDictionary<string, string> Posts = new Dictionary<string, string>
    {
        ["Reception"] = "生产签收", ["Machining"] = "机加", ["Packing"] = "包装",
        ["QualityReceiving"] = "质检收料", ["Inspection"] = "质量检验", ["Issue"] = "领用", ["SupplyChain"] = "供应链流转",
        ["MachiningReception"] = "机加主管接图", ["Trace"] = "资源组追溯（非必经）"
    };
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["ProductionReceived"] = "生产签收", ["Start"] = "机加开工", ["MachiningCompleted"] = "机加完工",
        ["PackingStarted"] = "包装接收", ["Packed"] = "包装完成／交检", ["QualityReceived"] = "质检收料",
        ["Pass"] = "检验合格", ["Rework"] = "检验返工", ["Scrap"] = "检验报废", ["ReworkCompleted"] = "返工完成",
        ["Issue"] = "确认领用", ["OutsideSent"] = "中间委外送出", ["OutsideReturned"] = "中间委外返回", ["ExternalDelivered"] = "外协交货",
        ["MachiningReceived"] = "机加主管接图", ["Trace"] = "资源组扫码追溯", ["Pause"] = "暂停流程", ["Resume"] = "恢复流程",
        ["Cancelled"] = "取消业务需求", ["TaskCancelled"] = "取消加工任务", ["StageScrap"] = "环节报废"
    };
    public static string Post(string kind, string route, string? flowMode = null) => kind switch
    {
        "ReworkCompleted" when route == "Self" && flowMode == "DepartmentHandover" => "MachiningReception",
        "ProductionReceived" => "Reception", "Start" or "MachiningCompleted" => "Machining",
        "PackingStarted" or "Packed" => "Packing", "QualityReceived" => "QualityReceiving",
        "Pass" or "Rework" or "Scrap" => "Inspection", "Issue" => "Issue",
        "ReworkCompleted" when route == "Self" => "Machining", "MachiningReceived" => "MachiningReception",
        "Trace" => "Trace", "Pause" or "Resume" or "Cancelled" or "TaskCancelled" or "StageScrap" => "Control", _ => "SupplyChain"
    };
    public static string Permission(string kind, string route) => Post(kind, route) switch
    {
        "Reception" or "Machining" or "MachiningReception" or "Packing" => PermissionCodes.ProductionExecute,
        "QualityReceiving" or "Inspection" => PermissionCodes.ProductionInspect,
        "Issue" => PermissionCodes.ProductionIssue, "Trace" or "Control" => PermissionCodes.ProjectContentView, _ => PermissionCodes.ProductionAssign
    };
    public static string Post(ProductionTrackingState state, ProductionLot lot, string kind) =>
        ProductionDepartmentFlow.Node(state, lot, kind)?.Post ?? Post(kind, lot.Route, state.FlowMode);
    public static string Permission(ProductionTrackingState state, ProductionLot lot, string kind) => kind switch
    {
        "StageScrap" => PermissionCodes.ProductionInspect,
        "Cancelled" or "TaskCancelled" => PermissionCodes.ProductionAssign,
        _ => Post(state, lot, kind) switch
    {
        "Reception" or "Machining" or "MachiningReception" or "Packing" => PermissionCodes.ProductionExecute,
        "QualityReceiving" or "Inspection" => PermissionCodes.ProductionInspect,
        "Issue" => PermissionCodes.ProductionIssue, "Trace" or "Control" => PermissionCodes.ProjectContentView, _ => PermissionCodes.ProductionAssign
    }};
    public static string[] ResourceGroups(ProductionLot lot) => lot.Schedule.Select(slot => slot.ResourceGroupId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static IEnumerable<ProductionMovement> ActiveMovements(ProductionTrackingState state, Guid lotId)
    {
        var start = 0;
        for (var index = state.Movements.Count - 1; index >= 0; index--)
            if (state.Movements[index].LotId == lotId && state.Movements[index].ResetsFlow) { start = index + 1; break; }
        return state.Movements.Skip(start).Where(item => item.LotId == lotId);
    }
    public static decimal Total(ProductionTrackingState state, ProductionLot lot, string kind) => ProductionTrackingRules.Total(state, lot.Id, kind);
    public static decimal GroupTotal(ProductionTrackingState state, ProductionLot lot, string kind, string group) => ActiveMovements(state, lot.Id)
        .Where(item => item.Kind == kind && (string.IsNullOrEmpty(item.ResourceGroup)
            || string.Equals(item.ResourceGroup, group, StringComparison.OrdinalIgnoreCase))).Sum(item => item.Quantity);
    public static decimal Received(ProductionTrackingState state, ProductionLot lot) => Math.Max(Total(state, lot, "ProductionReceived"),
        ResourceGroups(lot).DefaultIfEmpty("").Max(group => GroupTotal(state, lot, "Start", group)));
    public static decimal Machined(ProductionTrackingState state, ProductionLot lot) => ResourceGroups(lot).DefaultIfEmpty("")
        .Min(group => GroupTotal(state, lot, "MachiningCompleted", group));
    public static decimal Remaining(ProductionTrackingState state, ProductionLot lot, string kind, string group = "")
        => ProductionQuantityFlow.Remaining(state, lot, kind, group);
    public static string Stage(ProductionTrackingState state, ProductionLot lot)
    {
        if (ProductionDepartmentFlow.Applies(state, lot)) return ProductionDepartmentFlow.Stage(state, lot);
        decimal Sum(string key) => Total(state, lot, key);
        var demand = lot.Quantity - Sum("Transferred") - Sum("Cancelled") - Sum("TaskCancelled");
        if (demand <= 0) return Sum("TaskCancelled") > 0 ? "加工任务已取消" : "已取消／转版";
        if (lot.Frozen) return "变更待处理";
        if (lot.Paused) return "已暂停";
        if (ProductionQuantityFlow.Paused(state, lot) > 0) return "部分暂停";
        if (Sum("Scrap") >= demand) return "已报废";
        if (Sum("Issue") + Sum("Scrap") >= demand) return Sum("Scrap") > 0 ? "已领用／含报废" : "已领用";
        if (ProductionQuantityFlow.Qualified(state, lot) + Sum("Scrap") >= demand) return Sum("Scrap") > 0 ? "质检完成／含报废" : "质检完成／合格待领";
        if (Sum("Rework") > ProductionQuantityFlow.Completed(state, lot, "ReworkCompleted")) return "返工中";
        if (Sum("QualityReceived") > 0) return "质检中";
        if (Sum("Packed") > 0 || Sum("ExternalDelivered") > 0) return "待质检收料";
        if (Sum("PackingStarted") > 0) return "包装中";
        if (lot.Route == "External") return "外协待交货";
        if (Machined(state, lot) >= demand) return "待包装";
        if (Sum("Start") > 0) return "机加中";
        if (lot.Schedule.Count > 0) return "已排产／待开工";
        return Received(state, lot) > 0 ? "生产已签收／待排产" : "待生产签收";
    }
}

public sealed class ProductionScanService(IPdmRepository repository, IProductionTrackingStore store, TimeProvider clock)
{
    public async Task<ProductionTrackingRecord> ApplyFlowAsync(ProductionTrackingRecord record, CancellationToken ct)
    {
        var catalog = await store.ScanCatalogAsync(record.State.CompanyId, ct);
        return record with { State = ProductionDepartmentFlow.ApplyCatalog(record.State, catalog) };
    }
    public async Task<ProductionScanCatalog> ReadAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireManageAsync(actor, role, ct);
        return await store.ScanCatalogAsync(TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。"), ct);
    }
    public async Task<ProductionScanOptions> OptionsAsync(string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await ReadAsync(actor, role, ct);
        var users = (await repository.ListUsersAsync(ct)).Where(user => user.IsActive && (!user.CompanyId.HasValue || user.CompanyId == catalog.CompanyId))
            .Select(user => new ProductionScanUserOption(user.Username, user.DisplayName)).OrderBy(user => user.Username).ToArray();
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles.Select(item => new ProductionScanRoleOption(item.Role, item.Name)).ToArray();
        return new(users, roles);
    }
    private async Task RequireManageAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionScanManage, ct))
            throw new UnauthorizedAccessException("没有配置生产扫码岗位和工位的权限。");
    }
    public async Task<ProductionScanCatalog> SaveAsync(SaveProductionScanCatalog input, string actor, UserRole role, CancellationToken ct)
    {
        var current = await ReadAsync(actor, role, ct);
        if (current.RowVersion != input.ExpectedRowVersion) throw new PdmConflictException("扫码岗位配置已被其他人修改，请刷新后重试。");
        if (input.Workstations is null || input.Bindings is null) throw new PdmRuleException("工位及岗位绑定内容不能为空，请刷新设置页。");
        var stations = input.Workstations.Select(item => item with { Name = item.Name?.Trim() ?? "", ResourceGroup = item.ResourceGroup?.Trim() ?? "" }).ToArray();
        if (stations.Any(item => item.Id == Guid.Empty || item.Name.Length is 0 or > 80 || item.ResourceGroup.Length > 80)
            || stations.Select(item => item.Id).Distinct().Count() != stations.Length
            || stations.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != stations.Length)
            throw new PdmRuleException("工位编号和名称不能为空或重复，名称及资源组最多80字。");
        var users = (await repository.ListUsersAsync(ct)).ToDictionary(item => item.Username, StringComparer.OrdinalIgnoreCase);
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles.Select(item => item.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.FlowMode is not ("ResourceOperations" or "DepartmentHandover")) throw new PdmRuleException("请选择有效的生产流程。");
        var controls = (input.ControlRoleCodes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (controls.Any(code => !roles.Contains(code))) throw new PdmRuleException("请选择有效的暂停、恢复和取消授权角色。");
        if (input.LabelWidthMm is < 40 or > 150 || input.LabelHeightMm is < 25 or > 150)
            throw new PdmRuleException("标签宽度应为40至150毫米，高度应为25至150毫米。");
        var nodes = (input.FlowNodes ?? current.FlowNodes)?.Select(node => node with { Name = node.Name?.Trim() ?? "", Stage = node.Stage?.Trim() ?? "" }).ToArray();
        ProductionDepartmentFlow.Validate(nodes);
        var bindings = input.Bindings.Select(item => item with { Subject = item.Subject?.Trim() ?? "", WorkstationIds = item.WorkstationIds?.Distinct().ToArray() ?? [],
            FlowNodeId = item.FlowNodeId ?? current.Bindings.FirstOrDefault(existing => existing.Id == item.Id)?.FlowNodeId }).ToArray();
        if (bindings.Select(item => item.Id).Distinct().Count() != bindings.Length) throw new PdmRuleException("岗位绑定编号不能重复。");
        if (bindings.GroupBy(item => $"{item.SubjectType}|{item.Subject.ToUpperInvariant()}|{item.Post}|{item.FlowNodeId}").Any(group => group.Count() > 1))
            throw new PdmRuleException("同一账号或角色的同一岗位请合并为一条绑定，并选择多个工位。");
        foreach (var binding in bindings)
        {
            if (binding.Id == Guid.Empty || !ProductionFlowRules.Posts.ContainsKey(binding.Post) || binding.SubjectType is not ("User" or "Role")
                || binding.FlowNodeId.HasValue && !(nodes ?? []).Any(node => node.Id == binding.FlowNodeId && node.Post == binding.Post)
                || binding.SubjectType == "Role" && !roles.Contains(binding.Subject)
                || binding.SubjectType == "User" && (!users.TryGetValue(binding.Subject, out var user) || !user.IsActive
                    || user.CompanyId.HasValue && user.CompanyId != current.CompanyId))
                throw new PdmRuleException("请选择当前公司的有效账号、角色和生产岗位。");
            if (binding.WorkstationIds.Count == 0 || binding.WorkstationIds.Any(id => !stations.Any(station => station.Id == id))
                || binding.IsActive && binding.WorkstationIds.Any(id => !stations.Any(station => station.Id == id && station.IsActive)))
                throw new PdmRuleException("岗位绑定必须选择有效工位；启用的绑定不能关联停用工位。");
            if (binding.Post == "Machining" && binding.WorkstationIds.Any(id => string.IsNullOrWhiteSpace(stations.Single(station => station.Id == id).ResourceGroup)))
                throw new PdmRuleException("机加工位必须填写对应排程资源组。");
        }
        var updated = new ProductionScanCatalog(current.CompanyId, stations, bindings, current.RowVersion + 1)
        { FlowMode = input.FlowMode, ControlRoleCodes = controls, LabelWidthMm = input.LabelWidthMm, LabelHeightMm = input.LabelHeightMm, FlowNodes = nodes };
        if (!await store.SaveScanCatalogAsync(updated, current.RowVersion, ct)) throw new PdmConflictException("扫码岗位配置已被其他人修改，请刷新后重试。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "production.scan-settings.save", "ProductionScanCatalog",
            current.CompanyId.ToString(), JsonSerializer.Serialize(new { Before = current, After = updated })), ct);
        return updated;
    }
    public async Task<ProductionScanContext> ActionsAsync(ProductionTrackingState state, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await store.ScanCatalogAsync(state.CompanyId, ct);
        state = ProductionDepartmentFlow.ApplyCatalog(state, catalog);
        var account = await repository.FindUserAsync(actor, ct);
        if (account is not null && !account.IsActive) throw new UnauthorizedAccessException("当前账号已停用。");
        var personal = catalog.Bindings.Where(item => item.SubjectType == "User" && item.Subject.Equals(actor, StringComparison.OrdinalIgnoreCase)).ToArray();
        var roleCodes = account?.EffectiveRoleCodes ?? [role.ToString()];
        var bindings = (personal.Length > 0 ? personal : catalog.Bindings.Where(item => item.SubjectType == "Role"
            && roleCodes.Contains(item.Subject, StringComparer.OrdinalIgnoreCase))).Where(item => item.IsActive).ToArray();
        var actions = new List<ProductionScanAction>();
        var allowed = new Dictionary<string, bool>();
        foreach (var lot in state.Lots)
        foreach (var kind in ProductionFlowRules.Labels.Keys.Concat(ProductionDepartmentFlow.Nodes(state, lot)?.Select(node => node.Kind) ?? []).Distinct())
        {
            var node = ProductionDepartmentFlow.Node(state, lot, kind);
            var permission = ProductionFlowRules.Permission(state, lot, kind);
            if (!allowed.TryGetValue(permission, out var permitted)) allowed[permission] = permitted = await repository.HasUserPermissionAsync(actor, role, permission, ct);
            if (!permitted) continue;
            var post = ProductionFlowRules.Post(state, lot, kind);
            foreach (var binding in bindings.Where(item => item.Post == post && (catalog.FlowMode != "DepartmentHandover" || !item.FlowNodeId.HasValue || item.FlowNodeId == node?.Id)))
            foreach (var station in catalog.Workstations.Where(item => item.IsActive && binding.WorkstationIds.Contains(item.Id)))
            {
                if (post == "Machining" && !ProductionFlowRules.ResourceGroups(lot).Contains(station.ResourceGroup, StringComparer.OrdinalIgnoreCase)) continue;
                var remaining = ProductionFlowRules.Remaining(state, lot, kind, station.ResourceGroup);
                if (remaining > 0 && !actions.Any(item => item.LotId == lot.Id && item.Kind == kind && item.WorkstationId == station.Id))
                    actions.Add(new(lot.Id, kind, node is not null && kind == node.Kind ? node.Name : ProductionFlowRules.Labels.GetValueOrDefault(kind, "流程办理"), remaining, binding.Id, post, station.Id, station.Name, station.ResourceGroup));
            }
        }
        var canControl = roleCodes.Any(code => catalog.ControlRoleCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectContentView, ct);
        if (canControl)
            foreach (var lot in state.Lots)
            {
                if (ProductionQuantityFlow.ControlRemaining(state, lot, "Resume", null) is var legacyRemaining && legacyRemaining > 0)
                    actions.Add(new(lot.Id, "Resume", ProductionFlowRules.Labels["Resume"], legacyRemaining, Guid.Empty, "Control", Guid.Empty, "流程管理", ""));
                foreach (var kind in new[] { "Pause", "Resume", "TaskCancelled", "Cancelled", "StageScrap" })
                {
                    if (!await repository.HasUserPermissionAsync(actor, role, ProductionFlowRules.Permission(state, lot, kind), ct)) continue;
                    if (kind == "Resume" && legacyRemaining > 0) continue;
                    foreach (var pool in ProductionQuantityFlow.Pools(state, lot))
                    {
                        var remaining = ProductionQuantityFlow.ControlRemaining(state, lot, kind, pool.Kind);
                        if (remaining > 0) actions.Add(new(lot.Id, kind, ProductionFlowRules.Labels[kind], remaining, Guid.Empty, "Control", Guid.Empty, "流程管理", "")
                            { SourceKind = pool.Kind, SourceName = pool.Name });
                    }
                }
            }
        return new(actions, bindings.Length == 0 && actions.Count == 0 ? "当前账号未配置可用的扫码岗位和工位，请联系管理员在系统设置中配置。"
            : actions.Count == 0 ? "当前批次暂无本岗位可办理的步骤，请核对操作权限、前序交接、排程和冻结状态。" : "请选择本次步骤和工位，核对数量后确认。")
        { CanCancelUnassignedDemand = canControl && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct),
            CanPrintLabels = bindings.Any(binding => binding.Post == "Packing" && catalog.Workstations.Any(station => station.IsActive && binding.WorkstationIds.Contains(station.Id)))
                && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionExecute, ct),
            LabelWidthMm = catalog.LabelWidthMm, LabelHeightMm = catalog.LabelHeightMm };
    }
}
