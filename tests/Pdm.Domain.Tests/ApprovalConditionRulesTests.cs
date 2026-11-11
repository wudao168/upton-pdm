using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;
public sealed class ApprovalConditionRulesTests
{
    [Theory]
    [InlineData(1999.99, false)]
    [InlineData(2000, false)]
    [InlineData(2000.01, true)]
    public void DecimalBoundaryAndMultipleConditions(decimal amount, bool expected)
    {
        var rules = new ApprovalConditionSet("All", [new("TotalAmount", "Gt", "2000"), new("Purpose", "Eq", "RepairSupplement"), new("ItemKind", "Eq", "Service"), new("RequiredOn", "Gte", "2026-12-01")]);
        var values = new Dictionary<string, object?> { ["TotalAmount"] = amount, ["Purpose"] = "RepairSupplement", ["ItemKind"] = new[] { "Material", "Service" }, ["RequiredOn"] = new DateOnly(2026,12,1) };
        Assert.Equal(expected, ApprovalConditionRules.Matches(rules, "expense-procurement", null, values));
        Assert.True(ApprovalConditionRules.Matches(rules with { Match = "Any" }, "expense-procurement", null, values));
        values["RequiredOn"] = null;
        Assert.False(ApprovalConditionRules.Matches(rules, "expense-procurement", null, values));
    }
    [Theory]
    [InlineData("expense-procurement", "Unknown", "Eq", "1")]
    [InlineData("standard-release", "TotalAmount", "Gt", "2000")]
    [InlineData("expense-supply-chain", "TotalAmount", "Contains", "2000")]
    [InlineData("expense-procurement", "RequiredOn", "Eq", "bad-date")]
    [InlineData("expense-procurement", "TotalAmount", "Gt", "not-number")]
    [InlineData("expense-procurement", "Purpose", "Eq", "bad-purpose")]
    public void InvalidConfigurationIsRejected(string flow, string field, string op, string value) =>
        Assert.Throws<PdmRuleException>(() => ApprovalConditionRules.Normalize(new("All", [new(field,op,value)]),flow));
    [Theory]
    [InlineData("standard-release", "ReleaseScope", "StandardSupplement")]
    [InlineData("nonstandard-release", "ChangeReason", "设计变更")]
    [InlineData("electrical-release", "ItemCount", "5")]
    [InlineData("validation-plan", "PreparedBy", "项目经理")]
    [InlineData("material-code-application", "Brand", "品牌")]
    public void BusinessFieldsRouteAcrossEveryFlow(string flow, string field, string value)
    {
        var template = ReleaseApprovalSettings.Default.Mechanical with { Code = flow };
        template = template with { Steps = [template.Steps[0], template.Steps[1], template.Steps[2] with { Conditions = new("All", [new(field,"Eq",value)]) }] };
        Assert.Equal(3, ApprovalNodeRules.ResolveRoute(template,null, fields:new Dictionary<string, object?> { [field] = value }).Steps.Count);
        Assert.Equal(2, ApprovalNodeRules.ResolveRoute(template,null, fields:new Dictionary<string, object?> { [field] = null }).Steps.Count);
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.ResolveRoute(template,null));
    }
    [Fact]
    public void EmptyAndNotEqualHaveExplicitMissingValueSemantics()
    {
        var empty = new Dictionary<string, object?> { ["Reason"] = null, ["ItemKind"] = new[] { "Material", "Service" } };
        Assert.True(ApprovalConditionRules.Matches(new("All", [new("Reason","Empty",null)]),"expense-procurement",null,empty));
        Assert.False(ApprovalConditionRules.Matches(new("All", [new("Reason","Ne","x")]),"expense-procurement",null,empty));
        Assert.False(ApprovalConditionRules.Matches(new("All", [new("ItemKind","Ne","Service")]),"expense-procurement",null,empty));
    }

    [Fact]
    public void ConditionsCannotSilentlyApproveWithoutAnApprovalNode()
    {
        var flow = ExpenseWorkflowDefaults.Create(false);
        flow = flow with { Steps = [flow.Steps[0], flow.Steps[1] with { Conditions = new("All", [new("TotalAmount","Gt","9000")]) }] };
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.ResolveRoute(flow,null,fields:new Dictionary<string,object?> { ["TotalAmount"] = 1m }));
    }
}
