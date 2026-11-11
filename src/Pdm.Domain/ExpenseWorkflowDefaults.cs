namespace Upton.Pdm.Domain;

public static class ExpenseWorkflowDefaults
{
    public const string UnifiedCode = "expense-approval";
    public const int MaximumSteps = 21;
    public const decimal ExtraApprovalThreshold = 2000m;
    public static ApprovalWorkflowTemplate? Unified(ReleaseApprovalSettings settings, bool createDefaults = false)
    {
        if (settings.Expense is not null) return settings.Expense;
        if (!createDefaults && settings.ExpenseProcurement is null && settings.ExpenseSupplyChain is null) return null;
        var procurement = settings.ExpenseProcurement ?? Create(false);
        var supply = settings.ExpenseSupplyChain ?? Create(true);
        var steps = new List<ApprovalWorkflowStepTemplate> { procurement.Steps[0] with {
            Stage = ApprovalStage.MechanicalEngineer, Name = "提交费用申请" } };
        var departments = new Dictionary<string, Guid>();
        IReadOnlyList<ApprovalFlowNode> Department(ApprovalWorkflowTemplate flow, string key)
        {
            var unit = flow.ExpenseDepartmentId ?? flow.Steps.Skip(1).Select(step => step.FixedUnitId).FirstOrDefault(id => id.HasValue);
            if (unit.HasValue) departments[key] = unit.Value;
            var stages = new Dictionary<ApprovalStage, ApprovalStage>();
            foreach (var step in flow.Steps.Skip(1)) {
                var stage = (ApprovalStage)(100 + steps.Count);
                stages.Add(step.Stage, stage);
                steps.Add(step with { Stage = stage, Conditions = null, ConditionRole = null, UnlessRole = false, AmountGreaterThan = null });
            }
            var serial = 0;
            string Id() => key + "-" + ++serial;
            IReadOnlyList<ApprovalFlowNode> Copy(IReadOnlyList<ApprovalFlowNode> nodes) => nodes.Select(node => node with {
                Id = Id(), Stage = node.Stage is { } stage ? stages[stage] : null,
                Branches = node.Branches?.Select(branch => branch with { Id = Id(), Nodes = Copy(branch.Nodes) }).ToArray()
            }).ToArray();
            if (flow.Layout is not null) return Copy(flow.Layout.Skip(1).ToArray());
            return flow.Steps.Skip(1).Select(step => {
                ApprovalFlowNode node = new(Id(), "Approval", step.Name, stages[step.Stage]);
                var conditions = new List<ApprovalConditionSet>();
                if (step.Conditions is not null) conditions.Add(step.Conditions);
                if (step.AmountGreaterThan is { } amount) conditions.Add(new("All", [new("TotalAmount", "Gt", amount.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
                if (!string.IsNullOrWhiteSpace(step.ConditionRole)) conditions.Add(new("All", [new("SubmitterRole", step.UnlessRole ? "Ne" : "Eq", step.ConditionRole)]));
                foreach (var condition in conditions) node = new(Id(), "Condition", "条件分支", Branches: [
                    new(Id(), "满足条件", condition, [node]), new(Id(), "其他情况", null, [])]);
                return node;
            }).ToArray();
        }
        var branches = new ApprovalFlowBranch[] {
            new("department-procurement", "采购部", new("All", [new("Department", "Eq", "Procurement")]), Department(procurement, "Procurement")),
            new("department-supply", "供应链", null, Department(supply, "SupplyChain"))
        };
        return new(UnifiedCode, "费用审批", Math.Max(procurement.Version, supply.Version), steps) {
            EmergencyRoleCodes = [], ExpenseDepartmentIds = departments,
            Layout = [new("expense-submitter", "Approval", "提交费用申请", ApprovalStage.MechanicalEngineer),
                new("expense-department", "Condition", "审批部门", Branches: branches)]
        };
    }
    public static ApprovalWorkflowTemplate Create(bool supplyChain) => new(
        supplyChain ? "expense-supply-chain" : "expense-procurement",
        supplyChain ? "供应链费用审批" : "采购部费用审批", 1,
        [
            new(ApprovalStage.MechanicalEngineer, "提交费用申请", ApprovalAssigneeSource.Submitter),
            new(ApprovalStage.MainDesigner, "部门审批", ApprovalAssigneeSource.DepartmentMembers),
            new(ApprovalStage.MechanicalSupervisor, "超额审批", ApprovalAssigneeSource.FixedUnitManager)
            { AmountGreaterThan = ExtraApprovalThreshold }
        ]);
}
