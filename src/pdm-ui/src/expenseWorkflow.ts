import { normalizeGraph } from './approvalFlowGraph'
import type { ApprovalFlowNode, ApprovalFlowBranch, ApprovalStage, ApprovalWorkflowTemplate, ApprovalWorkflowStepTemplate, ReleaseApprovalSettings } from './types'

export const expenseNodeDefaults: ApprovalWorkflowStepTemplate[] = Array.from({ length: 20 }, (_, index) => ({
  stage: `ExpenseReview${index + 1}` as ApprovalStage, name: '审核节点', assigneeSource: 'DepartmentMembers', fixedUnitId: null,
}))
export function expenseDefault(): ApprovalWorkflowTemplate {
  return { code: 'expense-approval', name: '费用审批', version: 1, steps: [
    { stage: 'MechanicalEngineer', name: '提交费用申请', assigneeSource: 'Submitter' },
    { stage: 'MainDesigner', name: '部门审批', assigneeSource: 'DepartmentMembers', fixedUnitId: null },
    { stage: 'MechanicalSupervisor', name: '超额审批', assigneeSource: 'FixedUnitManager', fixedUnitId: null, amountGreaterThan: 2000 },
  ] }
}
export function unifiedExpense(settings: ReleaseApprovalSettings): ApprovalWorkflowTemplate {
  if (settings.expense) return settings.expense
  const procurement = settings.expenseProcurement ?? expenseDefault()
  const supply = settings.expenseSupplyChain ?? expenseDefault()
  const steps: ApprovalWorkflowStepTemplate[] = [{ ...procurement.steps[0]!, stage: 'MechanicalEngineer', name: '提交费用申请' }]
  const departments: Record<string,string> = {}
  function department(source: ApprovalWorkflowTemplate, key: string): ApprovalFlowNode[] {
    const unit = source.expenseDepartmentId ?? source.steps.slice(1).find(step => step.fixedUnitId)?.fixedUnitId
    if (unit) departments[key] = unit
    const flow = normalizeGraph(JSON.parse(JSON.stringify(source)))
    const stages = new Map<ApprovalStage,ApprovalStage>()
    for (const step of flow.steps.slice(1)) {
      const stage = `ExpenseReview${steps.length}` as ApprovalStage
      stages.set(step.stage,stage); steps.push({ ...step, stage })
    }
    let serial = 0
    const id = () => `${key}-${++serial}`
    const copy = (nodes: ApprovalFlowNode[]): ApprovalFlowNode[] => nodes.map(node => ({ ...node, id:id(), stage:node.stage ? stages.get(node.stage) : null,
      branches: node.branches?.map(branch => ({ ...branch, id:id(), nodes:copy(branch.nodes) })),
    }))
    return copy(flow.layout!.slice(1))
  }
  const branches: ApprovalFlowBranch[] = [
    { id:'department-procurement', name:'采购部', conditions:{match:'All',rules:[{field:'Department',operator:'Eq',value:'Procurement'}]}, nodes:department(procurement,'Procurement') },
    { id:'department-supply', name:'供应链', conditions:null, nodes:department(supply,'SupplyChain') },
  ]
  return { code:'expense-approval', name:'费用审批', version:Math.max(procurement.version,supply.version), steps, emergencyRoleCodes:[], expenseDepartmentIds:departments,
    layout:[{id:'expense-submitter',kind:'Approval',name:'提交费用申请',stage:'MechanicalEngineer'}, {id:'expense-department',kind:'Condition',name:'审批部门',branches}],
  }
}
