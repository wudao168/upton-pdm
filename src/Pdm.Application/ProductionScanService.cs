using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionWorkstation(Guid Id, string Name, string ResourceGroup, bool IsActive);
public sealed record ProductionPostBinding(Guid Id, string SubjectType, string Subject, string Post,
    IReadOnlyList<Guid> WorkstationIds, bool IsActive);
public sealed record ProductionScanCatalog(Guid CompanyId, IReadOnlyList<ProductionWorkstation> Workstations,
    IReadOnlyList<ProductionPostBinding> Bindings, long RowVersion = 0);
public sealed record SaveProductionScanCatalog(IReadOnlyList<ProductionWorkstation> Workstations,
    IReadOnlyList<ProductionPostBinding> Bindings, long ExpectedRowVersion);
public sealed record ProductionScanAction(Guid LotId, string Kind, string Label, decimal RemainingQuantity,
    Guid BindingId, string Post, Guid WorkstationId, string WorkstationName, string ResourceGroup);
public sealed record ProductionScanContext(IReadOnlyList<ProductionScanAction> Actions, string Hint);
public sealed record ProductionScanUserOption(string Username, string DisplayName);
public sealed record ProductionScanRoleOption(string Role, string Name);
public sealed record ProductionScanOptions(IReadOnlyList<ProductionScanUserOption> Users, IReadOnlyList<ProductionScanRoleOption> Roles);

public static class ProductionFlowRules
{
    public static readonly IReadOnlyDictionary<string, string> Posts = new Dictionary<string, string>
    {
        ["Reception"] = "生产签收", ["Machining"] = "机加", ["Packing"] = "包装",
        ["QualityReceiving"] = "质检收料", ["Inspection"] = "质量检验", ["Issue"] = "领用", ["SupplyChain"] = "供应链流转"
    };
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["ProductionReceived"] = "生产签收", ["Start"] = "机加开工", ["MachiningCompleted"] = "机加完工",
        ["PackingStarted"] = "包装接收", ["Packed"] = "包装完成／交检", ["QualityReceived"] = "质检收料",
        ["Pass"] = "检验合格", ["Rework"] = "检验返工", ["Scrap"] = "检验报废", ["ReworkCompleted"] = "返工完成",
        ["Issue"] = "确认领用", ["OutsideSent"] = "中间委外送出", ["OutsideReturned"] = "中间委外返回", ["ExternalDelivered"] = "外协交货"
    };
    public static string Post(string kind, string route) => kind switch
    {
        "ProductionReceived" => "Reception", "Start" or "MachiningCompleted" => "Machining",
        "PackingStarted" or "Packed" => "Packing", "QualityReceived" => "QualityReceiving",
        "Pass" or "Rework" or "Scrap" => "Inspection", "Issue" => "Issue",
        "ReworkCompleted" when route == "Self" => "Machining", _ => "SupplyChain"
    };
    public static string Permission(string kind, string route) => Post(kind, route) switch
    {
        "Reception" or "Machining" or "Packing" => PermissionCodes.ProductionExecute,
        "QualityReceiving" or "Inspection" => PermissionCodes.ProductionInspect,
        "Issue" => PermissionCodes.ProductionIssue, _ => PermissionCodes.ProductionAssign
    };
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
    {
        if (!state.ReceivedAt.HasValue || lot.Frozen || lot.Paused || state.Scope == ReleaseScope.NonStandardLongLead && state.FormalReleasePackageId is null) return 0;
        decimal Sum(string key) => Total(state, lot, key);
        var effective = Math.Max(0, lot.Quantity - Sum("Transferred") - Sum("Cancelled"));
        if (effective <= 0 || Sum("Issue") + Sum("Scrap") >= effective) return 0;
        var packing = Math.Max(Sum("PackingStarted"), Sum("Packed"));
        var completed = Machined(state, lot) - Sum("OutsideSent") + Sum("OutsideReturned");
        var pool = Sum("QualityReceived") - Sum("Pass") - Sum("Scrap") - Sum("Rework") + Sum("ReworkCompleted");
        var value = kind switch
        {
            "ProductionReceived" when lot.Route == "Self" => effective - Received(state, lot),
            "Start" when lot.Route == "Self" && ResourceGroups(lot).Contains(group, StringComparer.OrdinalIgnoreCase) =>
                Math.Min(Received(state, lot) - GroupTotal(state, lot, "Start", group), effective - GroupTotal(state, lot, "Start", group)),
            "MachiningCompleted" when lot.Route == "Self" && ResourceGroups(lot).Contains(group, StringComparer.OrdinalIgnoreCase) =>
                Math.Min(GroupTotal(state, lot, "Start", group) - GroupTotal(state, lot, kind, group), effective - GroupTotal(state, lot, kind, group)),
            "PackingStarted" when lot.Route == "Self" => Math.Min(completed - packing, effective - packing),
            "Packed" when lot.Route == "Self" => Math.Min(packing - Sum("Packed"), Math.Min(completed - Sum("Packed"), effective - Sum("Packed"))),
            "OutsideSent" when lot.Route == "Self" => Math.Min(completed - packing, effective - Sum("OutsideSent") + Sum("OutsideReturned") - packing),
            "OutsideReturned" when lot.Route == "Self" => Sum("OutsideSent") - Sum("OutsideReturned"),
            "ExternalDelivered" when lot.Route == "External" => effective - Sum(kind),
            "QualityReceived" => Math.Min((lot.Route == "Self" ? Sum("Packed") - Sum("OutsideSent") + Sum("OutsideReturned") : Sum("ExternalDelivered")) - Sum(kind), effective - Sum(kind)),
            "Pass" or "Rework" or "Scrap" => Math.Min(pool, effective - Sum("Pass") - Sum("Scrap")),
            "ReworkCompleted" => Math.Min(Sum("Rework") - Sum(kind), effective - Sum("Pass") - Sum("Scrap")),
            "Issue" => Math.Min(Sum("Pass") - Sum(kind), effective - Sum(kind)), _ => 0
        };
        return Math.Max(0, value);
    }
    public static string Stage(ProductionTrackingState state, ProductionLot lot)
    {
        decimal Sum(string key) => Total(state, lot, key);
        var demand = lot.Quantity - Sum("Transferred") - Sum("Cancelled");
        if (demand <= 0) return "已取消／转版";
        if (lot.Frozen) return "变更待处理";
        if (lot.Paused) return "已暂停";
        if (Sum("Scrap") >= demand) return "已报废";
        if (Sum("Issue") + Sum("Scrap") >= demand) return Sum("Scrap") > 0 ? "已领用／含报废" : "已领用";
        if (Sum("Pass") + Sum("Scrap") >= demand) return Sum("Scrap") > 0 ? "质检完成／含报废" : "质检完成／合格待领";
        if (Sum("Rework") > Sum("ReworkCompleted")) return "返工中";
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
        var bindings = input.Bindings.Select(item => item with { Subject = item.Subject?.Trim() ?? "", WorkstationIds = item.WorkstationIds?.Distinct().ToArray() ?? [] }).ToArray();
        if (bindings.Select(item => item.Id).Distinct().Count() != bindings.Length) throw new PdmRuleException("岗位绑定编号不能重复。");
        if (bindings.GroupBy(item => $"{item.SubjectType}|{item.Subject.ToUpperInvariant()}|{item.Post}").Any(group => group.Count() > 1))
            throw new PdmRuleException("同一账号或角色的同一岗位请合并为一条绑定，并选择多个工位。");
        foreach (var binding in bindings)
        {
            if (binding.Id == Guid.Empty || !ProductionFlowRules.Posts.ContainsKey(binding.Post) || binding.SubjectType is not ("User" or "Role")
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
        var updated = new ProductionScanCatalog(current.CompanyId, stations, bindings, current.RowVersion + 1);
        if (!await store.SaveScanCatalogAsync(updated, current.RowVersion, ct)) throw new PdmConflictException("扫码岗位配置已被其他人修改，请刷新后重试。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "production.scan-settings.save", "ProductionScanCatalog",
            current.CompanyId.ToString(), JsonSerializer.Serialize(new { Before = current, After = updated })), ct);
        return updated;
    }
    public async Task<ProductionScanContext> ActionsAsync(ProductionTrackingState state, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await store.ScanCatalogAsync(state.CompanyId, ct);
        var account = await repository.FindUserAsync(actor, ct);
        if (account is not null && !account.IsActive) throw new UnauthorizedAccessException("当前账号已停用。");
        var personal = catalog.Bindings.Where(item => item.SubjectType == "User" && item.Subject.Equals(actor, StringComparison.OrdinalIgnoreCase)).ToArray();
        var roleCodes = account?.EffectiveRoleCodes ?? [role.ToString()];
        var bindings = (personal.Length > 0 ? personal : catalog.Bindings.Where(item => item.SubjectType == "Role"
            && roleCodes.Contains(item.Subject, StringComparer.OrdinalIgnoreCase))).Where(item => item.IsActive).ToArray();
        var actions = new List<ProductionScanAction>();
        var allowed = new Dictionary<string, bool>();
        foreach (var lot in state.Lots)
        foreach (var kind in ProductionFlowRules.Labels.Keys)
        {
            var permission = ProductionFlowRules.Permission(kind, lot.Route);
            if (!allowed.TryGetValue(permission, out var permitted)) allowed[permission] = permitted = await repository.HasUserPermissionAsync(actor, role, permission, ct);
            if (!permitted) continue;
            var post = ProductionFlowRules.Post(kind, lot.Route);
            foreach (var binding in bindings.Where(item => item.Post == post))
            foreach (var station in catalog.Workstations.Where(item => item.IsActive && binding.WorkstationIds.Contains(item.Id)))
            {
                if (post == "Machining" && !ProductionFlowRules.ResourceGroups(lot).Contains(station.ResourceGroup, StringComparer.OrdinalIgnoreCase)) continue;
                var remaining = ProductionFlowRules.Remaining(state, lot, kind, station.ResourceGroup);
                if (remaining > 0 && !actions.Any(item => item.LotId == lot.Id && item.Kind == kind && item.WorkstationId == station.Id))
                    actions.Add(new(lot.Id, kind, ProductionFlowRules.Labels[kind], remaining, binding.Id, post, station.Id, station.Name, station.ResourceGroup));
            }
        }
        return new(actions, bindings.Length == 0 ? "当前账号未配置可用的扫码岗位和工位，请联系管理员在系统设置中配置。"
            : actions.Count == 0 ? "当前批次暂无本岗位可办理的步骤，请核对操作权限、前序交接、排程和冻结状态。" : "请选择本次步骤和工位，核对数量后确认。");
    }
}
