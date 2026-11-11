import { createClientUuid } from './clientId'
import type { ApprovalFlowNode, ApprovalFlowBranch, ApprovalWorkflowTemplate } from './types'
import type { ConditionSet } from './approvalConditions'
export const graphId = (kind: string) => `${kind}:${createClientUuid()}`
export function makeCondition(conditions: ConditionSet = { match: 'All', rules: [] }): ApprovalFlowNode {
  return { id:graphId('condition'),kind:'Condition',name:'条件分支',branches:[
    {id:graphId('branch'),name:'条件1',conditions,nodes:[]}, {id:graphId('branch'),name:'其他情况',conditions:null,nodes:[]},
  ] }
}
export function normalizeGraph(flow: ApprovalWorkflowTemplate): ApprovalWorkflowTemplate {
  if (flow.layout) flow.layout = JSON.parse(JSON.stringify(flow.layout))
  else flow.layout = flow.steps.map(step => {
    let node: ApprovalFlowNode = {id:step.stage,kind:'Approval',name:step.name,stage:step.stage}
    const groups: ConditionSet[] = []
    if (step.conditions) groups.push(step.conditions)
    if (step.amountGreaterThan != null) groups.push({match:'All',rules:[{field:'TotalAmount',operator:'Gt',value:String(step.amountGreaterThan)}]})
    if (step.conditionRole) groups.push({match:'All',rules:[{field:'SubmitterRole',operator:step.unlessRole?'Ne':'Eq',value:step.conditionRole}]})
    for (const conditions of groups) { const branch=makeCondition(conditions);branch.branches![0]!.nodes=[node];node=branch }
    return node
  })
  for (const step of flow.steps) { step.conditions=null;step.amountGreaterThan=null;step.conditionRole=null;step.unlessRole=false }
  return flow
}
export function graphNodes(nodes: ApprovalFlowNode[]): ApprovalFlowNode[] { return nodes.flatMap(node=>[node,...(node.branches??[]).flatMap(branch=>graphNodes(branch.nodes))]) }
export function findBranch(flow: ApprovalWorkflowTemplate, id: string): ApprovalFlowBranch | undefined { return graphNodes(flow.layout??[]).flatMap(node=>node.branches??[]).find(branch=>branch.id===id) }
export function locateNode(nodes: ApprovalFlowNode[], id: string): { nodes:ApprovalFlowNode[]; index:number; node:ApprovalFlowNode } | undefined {
  for (let index=0;index<nodes.length;index++) { const node=nodes[index]!;if(node.id===id)return{nodes,index,node};for(const branch of node.branches??[]) {const found=locateNode(branch.nodes,id);if(found)return found} }
}
export function moveGraphNode(flow:ApprovalWorkflowTemplate,id:string,target:ApprovalFlowNode[],index:number) {
  const found=locateNode(flow.layout??[],id);if(!found||found.node.stage===flow.steps[0]?.stage||target===flow.layout&&index===0)return
  const descendants=graphNodes([found.node]);if(descendants.some(node=>node.branches?.some(branch=>branch.nodes===target)))return
  if(found.nodes===target && found.index<index)index--
  found.nodes.splice(found.index,1);target.splice(index,0,found.node)
  const order = new Map(graphNodes(flow.layout ?? []).filter(node => node.kind === 'Approval').map((node, index) => [node.stage, index]))
  flow.steps.sort((left, right) => (order.get(left.stage) ?? Number.MAX_SAFE_INTEGER) - (order.get(right.stage) ?? Number.MAX_SAFE_INTEGER))
  syncGraphSteps(flow)
}
export function syncGraphSteps(flow:ApprovalWorkflowTemplate) { const steps=flow.steps;flow.steps=graphNodes(flow.layout??[]).filter(node=>node.kind==='Approval').map(node=>steps.find(step=>step.stage===node.stage)!) }
export function removeGraphNode(flow:ApprovalWorkflowTemplate,id:string) {
  const found=locateNode(flow.layout??[],id);if(!found||found.node.stage===flow.steps[0]?.stage)return
  const stages=new Set(graphNodes([found.node]).map(node=>node.stage));flow.steps=flow.steps.filter(step=>!stages.has(step.stage));found.nodes.splice(found.index,1)
}
