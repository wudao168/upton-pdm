<script setup lang="ts">
import DepartmentSelectField from './DepartmentSelectField.vue'
import { computed, ref, watch } from 'vue'
import ApprovalFlowCanvas from './ApprovalFlowCanvas.vue'
import { moveGraphNode } from '../approvalFlowGraph'
import { bonusId } from '../projectBonus'
import type { BonusDepartment, BonusSettings } from '../projectBonus'
import type { ApprovalFlowNode, ApprovalStage, ApprovalWorkflowTemplate } from '../types'

const nodes = defineModel<BonusSettings['approvalNodes']>({ required: true })
const props = defineProps<{ departments: BonusDepartment[]; version: number; disabled?: boolean }>()
const confirmationStage = 'MechanicalEngineer'
const stage = (id: string): ApprovalStage => `BonusReview:${id}`
const selected = ref<ApprovalStage>(confirmationStage)
const current = computed(() => nodes.value.find(node => stage(node.id) === selected.value))
const currentIndex = computed(() => nodes.value.findIndex(node => node === current.value))
const departmentName = (key: string) => props.departments.find(department => department.key === key)?.name ?? '原部门已移除，请重新选择'
// Adapt the saved bonus records to the shared designer. Only bonus records are submitted;
// the synthetic confirmation stage and canvas layout are never written to the API.
const flow = computed<ApprovalWorkflowTemplate>(() => {
  const steps = [
    { stage: confirmationStage as ApprovalStage, name: '项目经理确认', assigneeSource: 'Submitter' as const },
    ...nodes.value.map(node => ({ stage: stage(node.id), name: node.name, assigneeSource: node.parallel ? 'ParallelDepartments' as const : 'DepartmentMembers' as const })),
  ]
  return { code: 'project-bonus', name: '项目奖金审批', version: props.version, steps,
    layout: steps.map(step => ({ id: step.stage, stage: step.stage, name: step.name, kind: 'Approval' })) }
})
const descriptions = computed(() => ['部门评分完成后，由项目经理确认分配并提交', ...nodes.value.map(node =>
  (node.departmentKeys.map(departmentName).join(' / ') || '请选择审批部门') + (node.parallel ? ' · 并行全部通过' : ' · 部门任一审批人通过'))])
watch(() => nodes.value.map(node => node.id), () => {
  if (!current.value) selected.value = nodes.value[0] ? stage(nodes.value[0].id) : confirmationStage
}, { immediate: true })
function insert(_list: ApprovalFlowNode[], index: number, kind: 'Approval' | 'Condition') {
  if (props.disabled || kind !== 'Approval' || nodes.value.length >= 12) return
  const node = { id: bonusId(), name: '新增审批节点', departmentKeys: [] as string[], parallel: false }
  const reordered = [...nodes.value]
  reordered.splice(Math.max(0, index - 1), 0, node); nodes.value = reordered
  selected.value = stage(node.id)
}
function move(id: string, list: ApprovalFlowNode[], index: number) {
  if (props.disabled) return
  const canvas = flow.value
  moveGraphNode(canvas, id, list, index)
  const byStage = new Map(nodes.value.map(node => [stage(node.id), node]))
  nodes.value = canvas.steps.slice(1).map(step => byStage.get(step.stage)!)
}
function shift(offset: number) {
  const index = currentIndex.value, target = index + offset
  if (props.disabled || index < 0 || target < 0 || target >= nodes.value.length) return
  const reordered = [...nodes.value], [node] = reordered.splice(index, 1)
  reordered.splice(target, 0, node!); nodes.value = reordered
}
function remove() {
  if (props.disabled || !current.value || nodes.value.length <= 1) return
  nodes.value = nodes.value.filter(node => node !== current.value)
}
function changeMode(event: Event) {
  if (!current.value) return
  current.value.parallel = (event.target as HTMLSelectElement).value === 'parallel'
  if (!current.value.parallel) current.value.departmentKeys = current.value.departmentKeys.slice(0, 1)
}
</script>

<template>
  <div class="bonus-approval-designer designer-body" aria-label="项目奖金审批编辑器">
    <ApprovalFlowCanvas :flow="flow" :selected="selected" :descriptions="descriptions" :role-name="key => key"
      :disabled="!!disabled" :show-notification="false" :allow-conditions="false" :max-steps="13"
      @select="selected = $event as ApprovalStage" @insert="insert" @move-node="move" />
    <section class="designer-inspector" aria-label="节点属性">
      <h3>审核节点设置</h3>
      <fieldset v-if="current" class="bonus-node-fields" :disabled="disabled">
        <label>节点名称<input v-model.trim="current.name" aria-label="节点名称" maxlength="80" required /></label>
        <label>审批人来源<select aria-label="审批人来源" :value="current.parallel ? 'parallel' : 'department'" @change="changeMode">
          <option value="department">指定部门成员</option><option value="parallel">多部门并行审批</option>
        </select></label>
        <label v-if="!current.parallel">指定部门<DepartmentSelectField :name="departmentName(current.departmentKeys[0] ?? '')"><select aria-label="指定部门" :value="current.departmentKeys[0] ?? ''" @change="current.departmentKeys = [($event.target as HTMLSelectElement).value]">
          <option value="" disabled>请选择部门</option>
          <option v-if="current.departmentKeys[0] && !departments.some(department => department.key === current!.departmentKeys[0])" :value="current.departmentKeys[0]" disabled>原部门已移除，请重新选择</option>
          <option v-for="department in departments" :key="department.key" :value="department.key">{{ department.name }}</option>
        </select></DepartmentSelectField></label>
        <div v-else class="bonus-parallel-departments">
          <p class="bonus-muted">每个部门独立处理，全部部门通过后流转；任一部门退回则整单退回。同一人负责多个部门时逐部门确认。</p>
          <label v-for="(key, index) in current.departmentKeys" :key="index">并行部门 {{ index + 1 }}
            <DepartmentSelectField :name="departmentName(key)"><select v-model="current.departmentKeys[index]" aria-label="并行审批部门">
              <option value="" disabled>请选择部门</option>
              <option v-if="key && !departments.some(department => department.key === key)" :value="key" disabled>原部门已移除，请重新选择</option>
              <option v-for="department in departments" :key="department.key" :value="department.key" :disabled="department.key !== key && current.departmentKeys.includes(department.key)">{{ department.name }}</option>
            </select></DepartmentSelectField>
            <button type="button" class="pdm-secondary-action" @click="current.departmentKeys.splice(index, 1)">移除部门</button>
          </label>
          <button type="button" class="pdm-secondary-action" :disabled="current.departmentKeys.length >= 12" @click="current.departmentKeys.push('')">添加并行部门</button>
        </div>
        <p class="bonus-muted">审批人员沿用“评分部门”中的部门映射；每个部门由任一有效审批人处理。</p>
        <div class="workflow-node-actions bonus-actions">
          <button type="button" class="pdm-secondary-action" aria-label="上移节点" :disabled="currentIndex === 0" @click="shift(-1)">上移</button>
          <button type="button" class="pdm-secondary-action" aria-label="下移节点" :disabled="currentIndex === nodes.length - 1" @click="shift(1)">下移</button>
          <button type="button" class="pdm-secondary-action" aria-label="删除节点" :disabled="nodes.length <= 1" @click="remove">删除</button>
        </div>
      </fieldset>
      <p v-else class="bonus-muted">部门评分完成后，由项目经理确认人员权重与分配金额并提交审批。此为固定发起节点。</p>
    </section>
  </div>
</template>

<style scoped>
.designer-body{display:grid;grid-template-columns:minmax(220px,.9fr) minmax(300px,1.1fr);align-items:start;gap:16px;min-width:0}
.designer-inspector{display:flex;flex-direction:column;gap:16px;min-width:0;padding:16px;border:1px solid var(--pdm-border);border-radius:8px;background:var(--pdm-surface)}
.designer-inspector h3{font-size:14px}.bonus-node-fields,.bonus-parallel-departments{display:flex;flex-direction:column;gap:14px}.bonus-parallel-departments button{align-self:flex-start}
.bonus-approval-designer :deep(.flow-canvas){max-height:640px;min-height:320px;box-sizing:border-box}
@media(max-width:1000px){.designer-body{grid-template-columns:minmax(0,1fr)}}
</style>
