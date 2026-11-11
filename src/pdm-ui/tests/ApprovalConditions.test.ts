import { describe,it,expect } from 'vitest'
import {normalizeGraph,makeCondition,moveGraphNode,graphNodes,removeGraphNode} from '../src/approvalFlowGraph'
import type {ApprovalWorkflowTemplate} from '../src/types'
const flow=():ApprovalWorkflowTemplate=>({code:'expense-procurement',name:'费用审批',version:1,steps:[
 {stage:'MechanicalEngineer',name:'提交',assigneeSource:'Submitter'},
 {stage:'MainDesigner',name:'A审核',assigneeSource:'ProjectDesignLead'},
 {stage:'MechanicalSupervisor',name:'B审核',assigneeSource:'ProjectDesignLead',amountGreaterThan:2000},
]})
describe('独立条件分支',()=>{
 it('converts old conditions without changing their meaning or linking rules to approvers',()=>{
  const template=normalizeGraph(flow());const decision=template.layout![2]!
  expect(decision.kind).toBe('Condition');expect(decision.branches![0]!.conditions!.rules[0]).toEqual({field:'TotalAmount',operator:'Gt',value:'2000'})
  expect(decision.branches![0]!.nodes[0]!.stage).toBe('MechanicalSupervisor');expect(decision.branches![1]!.conditions).toBeNull();expect(decision.branches![1]!.nodes).toEqual([])
  expect(template.steps.every(step=>!step.conditions&&!step.amountGreaterThan&&!step.conditionRole)).toBe(true)
  expect(normalizeGraph(template).layout).toEqual(template.layout)
 })
 it('moves existing approvals into either branch and merges back to the main sequence',()=>{
  const template=normalizeGraph({...flow(),steps:flow().steps.map(step=>({...step,amountGreaterThan:null}))});const decision=makeCondition({match:'All',rules:[{field:'TotalAmount',operator:'Gt',value:'2000'}]})
  template.layout!.splice(1,0,decision)
  moveGraphNode(template,'MainDesigner',decision.branches![0]!.nodes,0);moveGraphNode(template,'MechanicalSupervisor',decision.branches![1]!.nodes,0)
  expect(template.layout).toHaveLength(2);expect(decision.branches!.map(branch=>branch.nodes[0]!.stage)).toEqual(['MainDesigner','MechanicalSupervisor'])
  const before=JSON.stringify(template.layout);moveGraphNode(template,decision.id,decision.branches![0]!.nodes,0);expect(JSON.stringify(template.layout)).toBe(before)
  moveGraphNode(template,'MechanicalEngineer',decision.branches![0]!.nodes,0);expect(JSON.stringify(template.layout)).toBe(before)
  moveGraphNode(template,'MainDesigner',template.layout!,2);expect(template.layout![2]!.stage).toBe('MainDesigner')
  removeGraphNode(template,decision.id);expect(graphNodes(template.layout!).map(node=>node.stage)).toEqual(['MechanicalEngineer','MainDesigner']);expect(template.steps).toHaveLength(2)
 })
 it('preserves role AND any-field legacy rules as nested independent conditions',()=>{
  const original=flow();original.steps[2]!.conditionRole='Engineer';original.steps[2]!.conditions={match:'Any',rules:[{field:'Purpose',operator:'Eq',value:'RepairSupplement'},{field:'TotalAmount',operator:'Gt',value:'5000'}]}
  const converted=normalizeGraph(original);const conditions=graphNodes(converted.layout!).filter(node=>node.kind==='Condition')
  expect(conditions).toHaveLength(3);expect(conditions.map(node=>node.branches![0]!.conditions!.match)).toContain('Any')
 })
})
