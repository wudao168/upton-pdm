<script setup lang="ts">
import ApprovalFlowSequence from './ApprovalFlowSequence.vue'
import type { ApprovalWorkflowTemplate, ApprovalFlowNode } from '../types'
defineProps<{flow:ApprovalWorkflowTemplate;selected:string;disabled:boolean;showNotification:boolean;descriptions:string[];roleName:(code:string)=>string;allowConditions?:boolean;maxSteps?:number}>()
const emit=defineEmits<{select:[key:string];insert:[nodes:ApprovalFlowNode[],index:number,kind:'Approval'|'Condition'];moveNode:[id:string,nodes:ApprovalFlowNode[],index:number];addBranch:[node:ApprovalFlowNode];shiftBranch:[node:ApprovalFlowNode,index:number,offset:number]}>()
</script>
<template>
 <nav class="flow-canvas" :aria-label="flow.name+'流程图'">
  <span class="endpoint">发起</span>
  <ApprovalFlowSequence :nodes="flow.layout??[]" :flow="flow" :selected="selected" :disabled="disabled" :descriptions="descriptions" :allow-conditions="allowConditions" :max-steps="maxSteps" root @select="emit('select',$event)" @insert="(nodes,index,kind)=>emit('insert',nodes,index,kind)" @drop="(id,nodes,index)=>emit('moveNode',id,nodes,index)" @add-branch="emit('addBranch',$event)" @shift-branch="(node,index,offset)=>emit('shiftBranch',node,index,offset)" />
  <span class="endpoint">结束</span>
 </nav>
</template>
<style scoped>
.flow-canvas{display:flex;flex-direction:column;align-items:center;min-width:0;max-width:100%;overflow:auto;padding:20px 12px;background-color:var(--pdm-surface-soft);background-image:radial-gradient(var(--pdm-border) 1px,transparent 1px);background-size:16px 16px;border:1px solid var(--pdm-border);border-radius:8px}.endpoint{background:var(--pdm-text);color:var(--pdm-surface);padding:3px 18px;border-radius:20px;font-size:12px}.notification-node{margin-bottom:14px;padding:10px;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text)}
</style>
