using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionFlowCredit(string Kind, decimal Quantity, string? ResourceGroup = null);
public sealed record ProductionQuantityPool(string Kind, string Name, decimal Quantity, decimal PausedQuantity);

// 异常退出量只抵消后续步骤的待办数量，不伪造加工、合格或领用记录。
public static class ProductionQuantityFlow
{
    public static string[] Steps(ProductionTrackingState state, ProductionLot lot) => lot.Route == "External"
        ? ["ExternalDelivered", "QualityReceived", "Pass", "Issue"]
        : ProductionDepartmentFlow.Applies(state, lot)
            ? (state.FlowNodes ?? ProductionDepartmentFlow.DefaultNodes).Where(node => node.Required).Select(node => node.Kind).ToArray()
            : ["ProductionReceived", "Start", "MachiningCompleted", "PackingStarted", "Packed", "QualityReceived", "Pass", "Issue"];

    public static bool IsRemoval(string kind) => kind is "Scrap" or "StageScrap" or "TaskCancelled" or "Cancelled" or "Transferred";
    public static decimal Raw(ProductionTrackingState state, ProductionLot lot, string kind) => ProductionFlowRules.Total(state, lot, kind);
    private static IEnumerable<ProductionMovement> Moves(ProductionTrackingState state, ProductionLot lot) => ProductionFlowRules.ActiveMovements(state, lot.Id);
    private static decimal Basis(ProductionTrackingState state, ProductionLot lot) => Math.Max(0, lot.Quantity - Moves(state, lot)
        .Where(m => m.Kind is "Cancelled" or "Transferred" && m.FlowCredits.Count == 0).Sum(m => m.Quantity));
    public static decimal Outstanding(ProductionTrackingState state, ProductionLot lot) => Math.Max(0, lot.Quantity
        - Raw(state, lot, "Cancelled") - Raw(state, lot, "Transferred") - Raw(state, lot, "TaskCancelled")
        - Raw(state, lot, "Scrap") - Raw(state, lot, Steps(state, lot).Last()));

    public static decimal Qualified(ProductionTrackingState state, ProductionLot lot) => Math.Max(0,
        Math.Min(lot.Quantity - Raw(state, lot, "Cancelled") - Raw(state, lot, "Transferred") - Raw(state, lot, "TaskCancelled"),
            Raw(state, lot, "Pass") - Moves(state, lot).Where(m => IsRemoval(m.Kind) && m.SourceKind == "Pass").Sum(m => m.Quantity)));

    private static decimal Credit(ProductionTrackingState state, ProductionLot lot, string kind, string? group) => Moves(state, lot)
        .Sum(m => m.FlowCredits.Where(c => c.Kind == kind && (string.Equals(c.ResourceGroup, group, StringComparison.OrdinalIgnoreCase)
                || group is not null && c.ResourceGroup == "")).Sum(c => c.Quantity)
            + (m.Kind == "Scrap" && m.FlowCredits.Count == 0 && Array.IndexOf(Steps(state, lot), kind) >= Array.IndexOf(Steps(state, lot), "Pass")
                && Array.IndexOf(Steps(state, lot), "Pass") >= 0 ? m.Quantity : 0));

    public static decimal Completed(ProductionTrackingState state, ProductionLot lot, string kind, string? group = null)
    {
        if (kind == "Assigned") return Basis(state, lot);
        if (!ProductionDepartmentFlow.Applies(state, lot) && lot.Route == "Self" && group is null && kind is "Start" or "MachiningCompleted")
        {
            var values = ProductionFlowRules.ResourceGroups(lot).DefaultIfEmpty("").Select(g => Completed(state, lot, kind, g));
            return kind == "Start" ? values.Max() : values.Min();
        }
        var raw = group is not null ? ProductionFlowRules.GroupTotal(state, lot, kind, group) : kind switch
        {
            "ProductionReceived" => ProductionFlowRules.Received(state, lot),
            "MachiningReceived" => Math.Max(Raw(state, lot, kind), ProductionFlowRules.ResourceGroups(lot).DefaultIfEmpty("").Max(g => ProductionFlowRules.GroupTotal(state, lot, "Start", g))),
            "PackingStarted" => Math.Max(Raw(state, lot, kind), Raw(state, lot, "Packed")),
            _ => Raw(state, lot, kind)
        };
        var result = raw + Credit(state, lot, kind, group);
        return kind is "ReworkCompleted" or "OutsideReturned" or "OutsideSent" ? result : Math.Min(Basis(state, lot), result);
    }

    private static bool LegacyPaused(ProductionTrackingState state, ProductionLot lot) => lot.Paused
        && (Moves(state, lot).LastOrDefault(m => m.Kind is "Pause" or "Resume") is not { } last || last.Kind == "Pause" && last.SourceKind is null);
    public static decimal Held(ProductionTrackingState state, ProductionLot lot, string source) => Math.Max(0,
        Moves(state, lot).Where(m => m.SourceKind == source).Sum(m => m.Kind == "Pause" ? m.Quantity : m.Kind == "Resume" ? -m.Quantity : 0));
    public static decimal Paused(ProductionTrackingState state, ProductionLot lot) => LegacyPaused(state, lot) ? Outstanding(state, lot)
        : Moves(state, lot).Where(m => m.Kind == "Pause").Select(m => m.SourceKind).OfType<string>().Distinct().Sum(source => Held(state, lot, source));

    public static IReadOnlyList<ProductionQuantityPool> Pools(ProductionTrackingState state, ProductionLot lot)
    {
        var steps = Steps(state, lot);
        var pools = new List<ProductionQuantityPool>();
        var open = Outstanding(state, lot);
        foreach (var source in new[] { "Assigned" }.Concat(steps.SkipLast(1)))
        {
            var next = steps[Array.IndexOf(steps, source) + 1];
            var quantity = Completed(state, lot, source) - Completed(state, lot, next);
            if (next == "Pass") quantity += Completed(state, lot, "ReworkCompleted") - Raw(state, lot, "Rework");
            if (source == "MachiningCompleted" && !ProductionDepartmentFlow.Applies(state, lot))
                quantity += Completed(state, lot, "OutsideReturned") - Raw(state, lot, "OutsideSent");
            Add(source, quantity);
        }
        Add("Rework", Raw(state, lot, "Rework") - Completed(state, lot, "ReworkCompleted"));
        if (lot.Route == "Self" && !ProductionDepartmentFlow.Applies(state, lot))
            Add("OutsideSent", Raw(state, lot, "OutsideSent") - Completed(state, lot, "OutsideReturned"));
        return pools;
        void Add(string source, decimal quantity)
        {
            quantity = Math.Max(0, Math.Min(open, quantity));
            if (quantity > 0) pools.Add(new(source, PoolName(state, lot, source), quantity,
                LegacyPaused(state, lot) ? quantity : Math.Min(quantity, Held(state, lot, source))));
        }
    }

    private static string PoolName(ProductionTrackingState state, ProductionLot lot, string source) => source switch
    {
        "Assigned" => lot.Route == "External" ? "外协加工／待交货" : "待生产签收",
        "ProductionReceived" => "生产已签收", "Start" or "MachiningReceived" => "机加加工中",
        "MachiningCompleted" => "机加完工待交接", "PackingStarted" => "包装中", "Packed" or "ExternalDelivered" => "待质检收料",
        "QualityReceived" => "待检验", "Pass" => "合格待领用", "Rework" => "返工中", "OutsideSent" => "中间委外中",
        _ => state.FlowNodes?.FirstOrDefault(n => n.Kind == source)?.Stage ?? "流程待交接"
    };

    public static decimal ControlRemaining(ProductionTrackingState state, ProductionLot lot, string kind, string? source)
    {
        if (source is null) return kind == "Resume" && LegacyPaused(state, lot) ? Outstanding(state, lot) : 0;
        var pool = Pools(state, lot).FirstOrDefault(p => p.Kind == source);
        return pool is null ? 0 : kind == "Resume" ? pool.PausedQuantity : pool.Quantity - pool.PausedQuantity;
    }

    public static decimal Remaining(ProductionTrackingState state, ProductionLot lot, string kind, string group = "")
    {
        if (!state.ReceivedAt.HasValue || lot.Frozen || LegacyPaused(state, lot)
            || state.Scope == ReleaseScope.NonStandardLongLead && state.FormalReleasePackageId is null) return 0;
        var pools = Pools(state, lot);
        decimal Available(string source) => pools.Where(p => p.Kind == source).Sum(p => p.Quantity - p.PausedQuantity);
        if (ProductionDepartmentFlow.Node(state, lot, kind) is { Required: false } optional)
        {
            var parent = state.FlowNodes?.FirstOrDefault(n => n.Id == optional.AfterNodeId)?.Kind ?? "MachiningReceived";
            return Completed(state, lot, parent) > 0 ? Math.Max(0, Outstanding(state, lot) - Paused(state, lot)) : 0;
        }
        if (kind == "Trace")
        {
            var node = ProductionDepartmentFlow.Node(state, lot, kind);
            var parent = state.FlowNodes?.FirstOrDefault(n => n.Id == node?.AfterNodeId)?.Kind ?? "MachiningReceived";
            return Completed(state, lot, parent) > 0 ? Math.Max(0, Outstanding(state, lot) - Paused(state, lot)) : 0;
        }
        if (kind == "ReworkCompleted") return Available("Rework");
        if (kind == "OutsideReturned") return Available("OutsideSent");
        if (kind == "OutsideSent") return lot.Route == "Self" && !ProductionDepartmentFlow.Applies(state, lot) ? Available("MachiningCompleted") : 0;
        if (lot.Route == "Self" && !ProductionDepartmentFlow.Applies(state, lot) && kind is "Start" or "MachiningCompleted")
        {
            if (!ProductionFlowRules.ResourceGroups(lot).Contains(group, StringComparer.OrdinalIgnoreCase)) return 0;
            var incoming = kind == "Start" ? Completed(state, lot, "ProductionReceived") : Completed(state, lot, "Start", group);
            var machiningHeld = Held(state, lot, "Start");
            var held = (kind == "Start" ? Held(state, lot, "ProductionReceived") : 0)
                + Math.Clamp(Completed(state, lot, "MachiningCompleted") + machiningHeld - Completed(state, lot, kind, group), 0, machiningHeld);
            return Math.Max(0, Math.Min(Outstanding(state, lot), incoming - Completed(state, lot, kind, group) - held));
        }
        var steps = Steps(state, lot);
        var index = Array.IndexOf(steps, kind is "Rework" or "Scrap" ? "Pass" : kind);
        return index < 0 ? 0 : Available(index == 0 ? "Assigned" : steps[index - 1]);
    }

    public static IReadOnlyList<ProductionFlowCredit> RemovalCredits(ProductionTrackingState state, ProductionLot lot, string source, decimal quantity)
    {
        var steps = Steps(state, lot);
        var index = Array.IndexOf(steps, source);
        var result = new List<ProductionFlowCredit>();
        if (source == "Rework") { result.Add(new("ReworkCompleted", quantity)); index = Array.IndexOf(steps, "Pass") - 1; }
        if (source == "OutsideSent") { result.Add(new("OutsideReturned", quantity)); index = Array.IndexOf(steps, "MachiningCompleted"); }
        foreach (var kind in steps.Skip(index + 1))
        {
            if (lot.Route == "Self" && !ProductionDepartmentFlow.Applies(state, lot) && kind is "Start" or "MachiningCompleted")
            {
                var first = source == "Start" ? Completed(state, lot, "MachiningCompleted") : Completed(state, lot, "Start");
                foreach (var group in ProductionFlowRules.ResourceGroups(lot).DefaultIfEmpty(""))
                {
                    var credit = Math.Clamp(first + quantity - Completed(state, lot, kind, group), 0, quantity);
                    if (credit > 0) result.Add(new(kind, credit, group));
                }
            }
            else result.Add(new(kind, quantity));
        }
        // 加工池内部分实物可能尚未在其他资源组开工。
        if (source == "Start")
            foreach (var group in ProductionFlowRules.ResourceGroups(lot).DefaultIfEmpty(""))
            {
                var credit = Math.Clamp(Completed(state, lot, "MachiningCompleted") + quantity - Completed(state, lot, "Start", group), 0, quantity);
                if (credit > 0) result.Add(new("Start", credit, group));
            }
        return result;
    }
}
