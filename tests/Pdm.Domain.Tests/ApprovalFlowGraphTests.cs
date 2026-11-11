using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Domain.Tests;
public sealed class ApprovalFlowGraphTests
{
    private static ApprovalWorkflowTemplate Flow() {
        var template=ExpenseWorkflowDefaults.Create(false);
        var steps=template.Steps.Select(step=>step with { AmountGreaterThan=null }).ToList();
        steps.Add(new(ApprovalStage.HardwareSupervisor,"共同后续审核",ApprovalAssigneeSource.FixedUser,"final"));
        ApprovalFlowNode Node(int index)=>new("node"+index,"Approval",steps[index].Name,steps[index].Stage);
        return template with { Steps=steps, Layout=[Node(0),new("decision","Condition","金额判断",Branches:[
            new("yes","金额大于2000",new("All",[new("TotalAmount","Gt","2000")]),[Node(1)]),
            new("no","其他情况",null,[Node(2)])]),Node(3)] };
    }
    [Theory]
    [InlineData(1999.99,ApprovalStage.MechanicalSupervisor)]
    [InlineData(2000,ApprovalStage.MechanicalSupervisor)]
    [InlineData(2000.01,ApprovalStage.MainDesigner)]
    public void AmountDecisionExecutesOnlyOneBranchThenMerges(decimal amount,ApprovalStage expected) {
        var result=ApprovalNodeRules.ResolveRoute(Flow(),null,fields:new Dictionary<string,object?> { ["TotalAmount"]=amount });
        Assert.Equal(new[]{ApprovalStage.MechanicalEngineer,expected,ApprovalStage.HardwareSupervisor},result.Steps.Select(step=>step.Stage));
        Assert.Null(result.Layout);
    }
    [Fact]
    public void FirstMatchingBranchWinsAndUnmatchedUsesFallback() {
        var flow=Flow();var decision=flow.Layout![1];var branches=decision.Branches!.ToList();
        branches.Insert(1,new("second","金额大于1000",new("All",[new("TotalAmount","Gt","1000")]),[]));
        flow=flow with { Layout=[flow.Layout[0],decision with{Branches=branches},flow.Layout[2]] };
        Assert.Equal(3,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=3000 }).Count);
        Assert.Equal(2,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=1500 }).Count);
        Assert.Equal(3,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=500 }).Count);
    }
    [Fact]
    public void NestedDecisionExecutesWithinSelectedBranchOnly() {
        var flow=Flow();var decision=flow.Layout![1];var yes=decision.Branches![0];
        var nested=new ApprovalFlowNode("nested","Condition","用途判断",Branches:[
            new("repair","维修补订",new("All",[new("Purpose","Eq","RepairSupplement")]),yes.Nodes),new("site","其他情况",null,[])]);
        flow=flow with { Layout=[flow.Layout[0],decision with { Branches=[yes with {Nodes=[nested]},decision.Branches[1]] },flow.Layout[2]] };
        Assert.Equal(3,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=3000,["Purpose"]="RepairSupplement" }).Count);
        Assert.Equal(2,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=3000,["Purpose"]="SitePurchase" }).Count);
        // Inactive nested branches do not read their business fields.
        Assert.Equal(3,ApprovalFlowGraph.Resolve(flow,null,new Dictionary<string,object?> { ["TotalAmount"]=1000 }).Count);
    }
    [Fact]
    public void InvalidGraphCannotDuplicateNodesOrLoseDefaultOrSkipSelfCheck() {
        var flow=Flow();var graph=flow.Layout!;var decision=graph[1];
        Assert.Throws<PdmRuleException>(()=>ApprovalFlowGraph.Normalize(flow with {Layout=[graph[0],graph[0],decision,graph[2]]}));
        Assert.Throws<PdmRuleException>(()=>ApprovalFlowGraph.Normalize(flow with {Layout=[decision,graph[0],graph[2]]}));
        Assert.Throws<PdmRuleException>(()=>ApprovalFlowGraph.Normalize(flow with {Layout=[graph[0],decision with {Branches=decision.Branches!.Reverse().ToArray()},graph[2]]}));
        Assert.Throws<PdmRuleException>(()=>ApprovalFlowGraph.Normalize(flow with {Layout=[graph[0],graph[2]]}));
    }
}
