using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Domain.Tests;

public sealed class UnifiedExpenseWorkflowTests
{
    [Theory]
    [InlineData("Procurement", 1999.99, 2)]
    [InlineData("Procurement", 2000, 2)]
    [InlineData("Procurement", 2000.01, 3)]
    [InlineData("SupplyChain", 2000, 2)]
    [InlineData("SupplyChain", 3000, 2)]
    [InlineData("SupplyChain", 3000.01, 3)]
    public void MergePreservesDepartmentRoutesThresholdsPoliciesAndFinancialAttribution(string department, decimal amount, int count)
    {
        var buy = ExpenseWorkflowDefaults.Create(false);
        var supply = ExpenseWorkflowDefaults.Create(true);
        var buyId = Guid.NewGuid(); var supplyId = Guid.NewGuid();
        buy = buy with { ExpenseDepartmentId = buyId, Steps = [buy.Steps[0], buy.Steps[1] with { FixedUnitId = buyId, RequireAllApprovers = true }, buy.Steps[2] with { FixedAssignee = "buy-chief" }] };
        supply = supply with { ExpenseDepartmentId = supplyId, Steps = [supply.Steps[0], supply.Steps[1] with { FixedUnitId = supplyId }, supply.Steps[2] with { FixedAssignee = "supply-chief", AmountGreaterThan = 3000 }] };
        var settings = ReleaseApprovalSettings.Default with { ExpenseProcurement = buy, ExpenseSupplyChain = supply };
        var before = JsonSerializer.Serialize(settings);
        var merged = ExpenseWorkflowDefaults.Unified(settings)!;
        var route = ApprovalFlowGraph.Resolve(merged, null, new Dictionary<string,object?> { ["Department"] = department, ["TotalAmount"] = amount });
        Assert.Equal(count, route.Count);
        Assert.Equal(department == "Procurement" ? buyId : supplyId, route[1].FixedUnitId);
        Assert.Equal(department == "Procurement", route[1].RequireAllApprovers);
        Assert.Equal(department == "Procurement" ? buyId : supplyId, merged.ExpenseDepartmentIds![department]);
        if (count == 3) Assert.Equal(department == "Procurement" ? "buy-chief" : "supply-chief", route[2].FixedAssignee);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(5, merged.Steps.Select(step => step.Stage).Distinct().Count());
        Assert.Same(merged, ExpenseWorkflowDefaults.Unified(settings with { Expense = merged }));
    }

    [Fact]
    public void MergeRetainsTwoFullGraphsWithOverlappingNodeIdsAndTenReviewers()
    {
        var stages = new[] { ApprovalStage.MechanicalEngineer, ApprovalStage.MainDesigner, ApprovalStage.MechanicalSupervisor,
            ApprovalStage.HardwareEngineer, ApprovalStage.HardwareSupervisor, ApprovalStage.StandardizationSupervisor };
        ApprovalWorkflowTemplate Flow(string prefix) {
            var steps = stages.Select((stage,index) => new ApprovalWorkflowStepTemplate(stage,prefix+index,
                index == 0 ? ApprovalAssigneeSource.Submitter : ApprovalAssigneeSource.FixedUser, prefix+index)).ToArray();
            ApprovalFlowNode Node(int index) => new("same-id-"+index,"Approval",steps[index].Name,steps[index].Stage);
            return new("expense-procurement",prefix,3,steps) { Layout = [Node(0),Node(1),new("same-decision","Condition","条件",Branches:[
                new("same-yes","超额",new("All",[new("TotalAmount","Gt","2000")]),[Node(2),Node(3)]),
                new("same-no","其他",null,[Node(4)])]),Node(5)] };
        }
        var merged = ExpenseWorkflowDefaults.Unified(ReleaseApprovalSettings.Default with { ExpenseProcurement=Flow("采购"),ExpenseSupplyChain=Flow("供应") })!;
        Assert.Equal(11,merged.Steps.Count);
        Assert.NotNull(ApprovalFlowGraph.Normalize(merged));
        foreach (var department in new[]{"Procurement","SupplyChain"}) {
            var route = ApprovalFlowGraph.Resolve(merged,null,new Dictionary<string,object?>{["Department"]=department,["TotalAmount"]=2500m});
            Assert.Equal((department=="Procurement"?new[]{"采购1","采购2","采购3","采购5"}:new[]{"供应1","供应2","供应3","供应5"}),route.Skip(1).Select(step=>step.Name));
        }
    }
}
