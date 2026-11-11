<script setup lang="ts">
import ApprovalFlowInsertion from './ApprovalFlowInsertion.vue'
import { conditionSummary } from '../approvalConditions'
import type { ApprovalFlowNode, ApprovalWorkflowTemplate } from '../types'
const props=defineProps<{nodes:ApprovalFlowNode[];flow:ApprovalWorkflowTemplate;selected:string;disabled:boolean;descriptions:string[];root?:boolean;allowConditions?:boolean;maxSteps?:number}>()
const emit=defineEmits<{
 select:[key:string]; insert:[nodes:ApprovalFlowNode[],index:number,kind:'Approval'|'Condition']; drop:[id:string,nodes:ApprovalFlowNode[],index:number]; addBranch:[node:ApprovalFlowNode]; shiftBranch:[node:ApprovalFlowNode,index:number,offset:number]
}>()
const step=(node:ApprovalFlowNode)=>props.flow.steps.find(step=>step.stage===node.stage)
function drag(event:DragEvent,node:ApprovalFlowNode){event.dataTransfer?.setData('application/pdm-approval-node',node.id)}
function drop(event:DragEvent,index:number){const id=event.dataTransfer?.getData('application/pdm-approval-node');if(id)emit('drop',id,props.nodes,index)}
function insert(index:number,kind:'Approval'|'Condition'){emit('insert',props.nodes,index,kind)}
</script>
<template>
 <div class="flow-sequence">
  <template v-for="(node,index) in nodes" :key="node.id">
   <div v-if="!root||index>0" class="insertion" @dragover.prevent @drop.prevent="drop($event,index)">
    <ApprovalFlowInsertion :disabled="disabled" :full="flow.steps.length >= (maxSteps ?? (flow.code === 'expense-approval' ? 21 : 6))" :allow-conditions="allowConditions" :label="'在'+(step(node)?.name||node.name)+'之前添加节点'" @insert="insert(index,$event)" />
   </div>
   <template v-if="node.kind==='Approval'">
    <button type="button" class="node" :aria-pressed="selected===node.stage" :draggable="!disabled&&node.stage!==flow.steps[0]?.stage" @dragstart="drag($event,node)" @click="emit('select',node.stage!)">
     <span class="node-heading">{{node.stage===flow.steps[0]?.stage?'提交':'审核'}} · {{step(node)?.name}}</span>
     <span>{{descriptions[flow.steps.findIndex(step=>step.stage===node.stage)]}}</span>
    </button>
    <button v-if="step(node)?.copyOnApproval" type="button" class="copy-node" @click="emit('select','copy:'+node.stage)">抄送 · {{step(node)?.copyOnApproval?.message}}</button>
   </template>
   <section v-else class="branch-group">
    <button type="button" class="condition-node" :aria-pressed="selected===node.id" :draggable="!disabled" @dragstart="drag($event,node)" @click="emit('select',node.id)">◇ {{node.name}}</button>
    <button type="button" class="add-branch" :disabled="disabled||(node.branches?.length??0)>=8" @click="emit('addBranch',node)">添加条件分支</button>
    <div class="branch-paths">
     <div v-for="(branch,branchIndex) in node.branches" :key="branch.id" class="branch-path">
      <div class="branch-priority" v-if="branch.conditions"><button type="button" aria-label="提高分支优先级" :disabled="disabled||branchIndex===0" @click="emit('shiftBranch',node,branchIndex,-1)">‹</button><span>优先级 {{branchIndex+1}}</span><button type="button" aria-label="降低分支优先级" :disabled="disabled||branchIndex>=node.branches!.length-2" @click="emit('shiftBranch',node,branchIndex,1)">›</button></div>
      <div v-else class="branch-priority">默认分支</div>
      <button type="button" class="branch-condition" :aria-label="'编辑分支：'+branch.name" :aria-pressed="selected===branch.id" @click="emit('select',branch.id)"><strong>{{branch.name}}</strong><span>{{branch.conditions?conditionSummary(branch,flow.code)||'请设置条件':'所有条件均不满足时进入'}}</span></button>
      <ApprovalFlowSequence :nodes="branch.nodes" :flow="flow" :selected="selected" :disabled="disabled" :descriptions="descriptions" :allow-conditions="allowConditions" :max-steps="maxSteps" @select="emit('select',$event)" @insert="(list,index,kind)=>emit('insert',list,index,kind)" @drop="(id,list,index)=>emit('drop',id,list,index)" @add-branch="emit('addBranch',$event)" @shift-branch="(node,index,offset)=>emit('shiftBranch',node,index,offset)" />
     </div>
    </div>
    <span class="merge">分支汇合</span>
   </section>
  </template>
  <div class="insertion" @dragover.prevent @drop.prevent="drop($event,nodes.length)">
   <ApprovalFlowInsertion :disabled="disabled" :full="flow.steps.length >= (maxSteps ?? (flow.code === 'expense-approval' ? 21 : 6))" :allow-conditions="allowConditions" label="在此处添加节点" @insert="insert(nodes.length,$event)" />
  </div>
 </div>
</template>
<style scoped>
.flow-sequence{display:flex;flex-direction:column;align-items:center;width:100%;min-width:0}.insertion{position:relative;display:grid;place-items:center;height:42px;flex:none;width:100%}.insertion::before{content:'';position:absolute;height:100%;border-left:1px solid var(--pdm-border);left:50%}.add-point{z-index:1;border-radius:50%;border:1px solid var(--pdm-border);background:var(--pdm-surface);width:24px;height:24px;padding:0;color:var(--pdm-text)}.add-menu{position:absolute;top:30px;z-index:5;display:flex;gap:6px;background:var(--pdm-surface);padding:8px;border:1px solid var(--pdm-border);box-shadow:0 4px 15px #0002;white-space:nowrap}.node,.branch-condition,.condition-node,.copy-node{font:inherit;color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface);cursor:pointer}.node,.branch-condition{display:flex;flex-direction:column;gap:8px;width:220px;max-width:100%;padding:12px;text-align:left;overflow-wrap:anywhere}.node-heading{font-weight:600}.node{border-top:4px solid var(--pdm-theme-accent)}button[aria-pressed=true]{outline:2px solid var(--pdm-theme-accent);outline-offset:1px}.branch-group{display:flex;align-items:center;flex-direction:column;min-width:0;max-width:100%;width:max-content}.condition-node{padding:8px 16px}.add-branch{margin:8px 0;padding:4px 10px}.branch-paths{display:flex;align-items:stretch;max-width:100%;overflow-x:auto;border-top:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border)}.branch-path{display:flex;flex-direction:column;align-items:center;min-width:246px;padding:10px 12px 0;box-sizing:border-box;border-inline-start:1px dashed var(--pdm-border)}.branch-path:first-child{border-inline-start:0}.branch-priority{display:flex;justify-content:space-between;align-items:center;min-height:30px;width:220px;font-size:11px;color:var(--pdm-muted)}.branch-priority button{border:0;background:transparent;color:var(--pdm-text);font:inherit}.branch-condition{border-top:3px solid #e6a23c;flex-shrink:0}.branch-path>.flow-sequence{flex:1;justify-content:flex-start}.branch-path>.flow-sequence:deep(>.insertion:last-child){flex:1;min-height:42px}.merge{font-size:11px;color:var(--pdm-muted);padding:5px}.copy-node{margin-top:8px;padding:8px;max-width:220px}
</style>
<style scoped>
.flow-sequence,.node,.branch-group{flex-shrink:0}.branch-paths{flex-shrink:0}
</style>
