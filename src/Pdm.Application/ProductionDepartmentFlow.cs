namespace Upton.Pdm.Application;

public static class ProductionDepartmentFlow
{
    public static IReadOnlyList<ProductionFlowNode> DefaultNodes { get; } = new[]
    {
        ("生产接图", "排产", "Reception", "ProductionReceived"), ("机加接图", "机加", "MachiningReception", "MachiningReceived"),
        ("资源组追溯", "保持当前阶段", "Trace", "Trace"), ("包装接收", "包装", "Packing", "PackingStarted"),
        ("质量收货", "质检", "QualityReceiving", "QualityReceived"), ("检验完成", "质检完成", "Inspection", "Pass"),
        ("物料领料", "领料完成", "Issue", "Issue")
    }.Select((item, index) => new ProductionFlowNode(Guid.Parse($"81000000-0000-0000-0000-{index + 1:000000000000}"),
        item.Item1, item.Item2, item.Item3, item.Item4, index != 2,
        index == 2 ? Guid.Parse("81000000-0000-0000-0000-000000000002") : null)).ToArray();
    public static ProductionTrackingState ApplyCatalog(ProductionTrackingState state, ProductionScanCatalog catalog) =>
        state with { FlowMode = catalog.FlowMode, FlowNodes = catalog.FlowNodes };
    public static IReadOnlyList<ProductionFlowNode>? Nodes(ProductionTrackingState state, ProductionLot lot) => Applies(state, lot) ? state.FlowNodes : null;
    public static ProductionFlowNode? Node(ProductionTrackingState state, ProductionLot lot, string kind) => Nodes(state, lot)?
        .FirstOrDefault(node => node.Kind == (kind is "Rework" or "Scrap" ? "Pass" : kind == "ReworkCompleted" ? "MachiningReceived" : kind));
    public static void Validate(IReadOnlyList<ProductionFlowNode>? nodes)
    {
        if (nodes is null) return;
        if (nodes.Count is 0 or > 40 || !nodes.Any(node => node.Required)
            || nodes.Select(node => node.Id).Distinct().Count() != nodes.Count || nodes.Select(node => node.Kind).Distinct().Count() != nodes.Count)
            throw new PdmRuleException("流程须包含至少一个必经节点，最多40个节点；节点编号和操作不能重复。");
        if (nodes.Any(node => node.Id == Guid.Empty || node.Name.Length is 0 or > 80 || node.Stage.Length is 0 or > 80
            || !ProductionFlowRules.Posts.ContainsKey(node.Post)
            || !DefaultNodes.Any(item => item.Kind == node.Kind) && node.Kind != $"Step:{node.Id:N}"
            || node.Kind == "Trace" && node.Required
            || node.Kind != "Trace" && !node.Kind.StartsWith("Step:", StringComparison.Ordinal) && !node.Required
            || !node.Required && !nodes.Any(parent => parent.Id == node.AfterNodeId && parent.Required)))
            throw new PdmRuleException("请填写节点名称、进入阶段、有效岗位及追溯起始节点；资源组追溯不能设为必经。");
        if (nodes.Any(node => node.Kind == "Issue") && nodes.Last(node => node.Required).Kind != "Issue")
            throw new PdmRuleException("领料是流程结束步骤，请将物料领料放在必经流程的最后。");
    }
    public static bool Applies(ProductionTrackingState state, ProductionLot lot) => state.FlowMode == "DepartmentHandover" && lot.Route == "Self";
    public static bool IsControl(string kind) => kind is "Pause" or "Resume" or "Cancelled" or "TaskCancelled" or "StageScrap";
    public static decimal Outstanding(ProductionTrackingState state, ProductionLot lot) => ProductionQuantityFlow.Outstanding(state, lot);
    private static decimal Accepted(ProductionTrackingState state, ProductionLot lot) => Math.Max(ProductionFlowRules.Total(state, lot, "MachiningReceived"),
        ProductionFlowRules.ResourceGroups(lot).DefaultIfEmpty("").Max(group => ProductionFlowRules.GroupTotal(state, lot, "Start", group)));
    public static decimal Remaining(ProductionTrackingState state, ProductionLot lot, string kind)
        => ProductionQuantityFlow.Remaining(state, lot, kind);
    public static string Stage(ProductionTrackingState state, ProductionLot lot)
    {
        decimal Sum(string key) => ProductionFlowRules.Total(state, lot, key);
        var effective = lot.Quantity - Sum("Transferred") - Sum("Cancelled") - Sum("TaskCancelled");
        if (effective <= 0) return Sum("TaskCancelled") > 0 ? "加工任务已取消" : Sum("Transferred") > 0 ? "已转版" : "已取消";
        if (Outstanding(state, lot) <= 0) return Sum("Scrap") >= effective ? "已报废" : Sum("Scrap") > 0 ? "批次结束／含报废"
            : Sum("TaskCancelled") > 0 ? "批次结束／含任务取消" : Sum("Cancelled") > 0 ? "已取消剩余流程"
            : Nodes(state, lot)?.LastOrDefault(node => node.Required)?.Stage ?? "领料完成";
        if (lot.Paused) return "已暂停";
        if (ProductionQuantityFlow.Paused(state, lot) > 0) return "部分暂停";
        if (lot.Frozen) return "变更待处理";
        if (Nodes(state, lot) is { } nodes)
        {
            if (Sum("Rework") > ProductionQuantityFlow.Completed(state, lot, "ReworkCompleted")) return "返工中";
            return nodes.LastOrDefault(node => node.Required && Completed(state, lot, node.Kind) > 0)?.Stage
                ?? "待" + nodes.First(node => node.Required).Name;
        }
        if (ProductionQuantityFlow.Qualified(state, lot) + Sum("Scrap") >= effective) return "质检完成";
        if (Sum("Rework") > ProductionQuantityFlow.Completed(state, lot, "ReworkCompleted")) return "返工中";
        if (Sum("QualityReceived") > 0) return "质检";
        if (Math.Max(Sum("PackingStarted"), Sum("Packed")) > 0) return "包装";
        if (Accepted(state, lot) > 0) return "机加";
        return ProductionFlowRules.Received(state, lot) > 0 ? "排产" : "待生产经理接图";
    }
    private static decimal Completed(ProductionTrackingState state, ProductionLot lot, string kind) => kind switch
    {
        "ProductionReceived" => ProductionFlowRules.Received(state, lot), "MachiningReceived" => Accepted(state, lot),
        "PackingStarted" => Math.Max(ProductionFlowRules.Total(state, lot, kind), ProductionFlowRules.Total(state, lot, "Packed")),
        _ => ProductionFlowRules.Total(state, lot, kind)
    };
}
