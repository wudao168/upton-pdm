using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ApprovalFlowGraph
{
    public static IReadOnlyList<ApprovalFlowNode>? Normalize(ApprovalWorkflowTemplate template)
    {
        if (template.Layout is null) return null;
        if (template.Layout.Count == 0 || template.Layout[0] is null || template.Layout[0].Kind != "Approval" || template.Layout[0].Stage != template.Steps[0].Stage)
            throw new PdmRuleException("流程必须以提交人节点开始，提交人不能放入条件分支。");
        if (template.Steps.Any(step => step.Conditions is not null || step.ConditionRole is not null || step.AmountGreaterThan is not null))
            throw new PdmRuleException("请将审核节点的旧条件转换为独立条件分支后保存。");
        var identifiers = new HashSet<string>();
        var stages = new HashSet<ApprovalStage>();
        var count = 0;
        void Id(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 120 || !identifiers.Add(id)) throw new PdmRuleException("流程节点标识无效或重复，请重新添加该节点。"); }
        IReadOnlyList<ApprovalFlowNode> Visit(IReadOnlyList<ApprovalFlowNode> nodes, int depth)
        {
            var maximumDepth = template.Code == ExpenseWorkflowDefaults.UnifiedCode ? 7 : 6;
            if (depth > maximumDepth) throw new PdmRuleException($"条件分支最多嵌套{maximumDepth}层。");
            return nodes.Select(node => {
                if (node is null || ++count > (template.Code == ExpenseWorkflowDefaults.UnifiedCode ? 128 : 64)) throw new PdmRuleException("流程节点过多或无效，请简化流程。");
                Id(node.Id);
                if (node.Kind == "Approval") {
                    if (node.Stage is not { } stage || !template.Steps.Any(step => step.Stage == stage) || !stages.Add(stage)
                        || node.Branches is not null || depth > 0 && stage == template.Steps[0].Stage)
                        throw new PdmRuleException("审核节点引用无效、重复或位置不正确。");
                    return node;
                }
                if (node.Kind != "Condition" || node.Stage is not null || node.Branches is null || node.Branches.Count is < 2 or > 8)
                    throw new PdmRuleException("条件节点须包含至少一个判断分支和一个其他情况分支，最多8条分支。");
                var branches = node.Branches.Select((branch,index) => {
                    if (branch is null || branch.Nodes is null) throw new PdmRuleException("条件分支无效。");
                    Id(branch.Id);
                    if ((branch.Conditions is null) != (index == node.Branches.Count - 1)) throw new PdmRuleException("其他情况必须是最后一条分支，前面的分支须配置判断条件。");
                    return branch with { Conditions = ApprovalConditionRules.Normalize(branch.Conditions, template.Code), Nodes = Visit(branch.Nodes,depth+1) };
                }).ToArray();
                return node with { Branches = branches };
            }).ToArray();
        }
        var normalized = Visit(template.Layout,0);
        if (stages.Count != template.Steps.Count) throw new PdmRuleException("部分审核节点未接入流程，请重新检查流程连线。");
        return normalized;
    }

    public static IReadOnlyList<ApprovalWorkflowStepTemplate> Resolve(ApprovalWorkflowTemplate template, UserAccount? submitter, IReadOnlyDictionary<string,object?>? fields)
    {
        var layout = Normalize(template)!;
        var steps = new List<ApprovalWorkflowStepTemplate>();
        void Visit(IReadOnlyList<ApprovalFlowNode> nodes) {
            foreach (var node in nodes) {
                if (node.Kind == "Approval") steps.Add(template.Steps.Single(step => step.Stage == node.Stage));
                else {
                    var branch = node.Branches!.First(branch => branch.Conditions is null || ApprovalConditionRules.Matches(branch.Conditions,template.Code,submitter,fields));
                    Visit(branch.Nodes);
                }
            }
        }
        Visit(layout);
        if (steps.Count < 2) throw new PdmRuleException("当前分支未包含审核节点，请联系管理员检查流程设置。");
        return steps;
    }

    public static IEnumerable<ApprovalFieldCondition> Rules(IReadOnlyList<ApprovalFlowNode>? nodes) =>
        (nodes ?? []).SelectMany(node => (node.Branches ?? []).SelectMany(branch => (branch.Conditions?.Rules ?? []).Concat(Rules(branch.Nodes))));
}
