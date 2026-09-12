<script setup lang="ts">
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, Plus, RefreshCw } from '@lucide/vue'
import { computed, nextTick, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { listProjectPlanTemplates, saveProjectPlanTemplate } from '../api'
import type { ProjectPlanStageDefinition, ProjectPlanTemplate } from '../types'
import { percent } from '../projectPlanAllocation'

const props = defineProps<{ token: string; currentUsername: string; canManage: boolean }>()
const templates = ref<ProjectPlanTemplate[]>([])
const templateDraft = ref<ProjectPlanTemplate | null>(null)
const selectedTemplateId = ref('')
const selectedStageCode = ref('')
const selectedTaskId = ref('')
const collapsedStages = ref(new Set<string>())
const stageGroups = computed(() => (templateDraft.value?.stages ?? []).map(stage => ({
  stage, tasks: (templateDraft.value?.tasks ?? []).filter(task => task.stage === stage.code).sort((a, b) => a.sortOrder - b.sortOrder),
})))
const selectedGroup = computed(() => stageGroups.value.find(group => group.stage.code === selectedStageCode.value) ?? stageGroups.value[0])
const settingsElement = ref<HTMLElement>()
const deliveryStages = computed(() => templateDraft.value?.stages?.filter(stage => stage.participatesInDelivery) ?? [])
const durationTotal = computed(() => deliveryStages.value.reduce((sum, stage) => sum + (stage.durationRatio ?? 0), 0))
const progressTotal = computed(() => deliveryStages.value.reduce((sum, stage) => sum + (stage.progressRatio ?? 0), 0))
const weightTotal = computed(() => selectedGroup.value?.tasks.reduce((sum, task) => sum + task.weight, 0) ?? 0)
const taskRatioTotal = computed(() => selectedGroup.value?.tasks.filter(task => !task.isMilestone && task.fixedDurationDays == null).reduce((sum, task) => sum + task.durationRatio, 0) ?? 0)
const fixedTaskDays = computed(() => selectedGroup.value?.tasks.filter(task => !task.isMilestone && task.fixedDurationDays != null).reduce((sum, task) => sum + task.fixedDurationDays!, 0) ?? 0)
const allocationError = computed(() => {
  if (Math.abs(durationTotal.value - 1) > .000001 || Math.abs(progressTotal.value - 1) > .000001) return '交付阶段工期占比、进度占比须分别合计100%。'
  for (const { stage, tasks } of stageGroups.value) {
    if (stage.participatesInDelivery && (!(stage.durationRatio! > 0) || !(stage.progressRatio! > 0))) return `请填写“${stage.name}”的工期占比和进度占比。`
    if (!stage.participatesInDelivery && !(stage.independentDurationDays! >= 1)) return `请填写“${stage.name}”的交付后阶段工期。`
    if (tasks.reduce((sum, task) => sum + task.weight, 0) <= 0) return `“${stage.name}”的子任务总权重须大于0。`
  }
  return ''
})
function setStageMode(stage: ProjectPlanStageDefinition, delivery: boolean) {
  stage.participatesInDelivery = delivery
  stage.durationRatio = 0
  stage.progressRatio = 0
  stage.independentDurationDays = delivery ? 0 : stage.independentDurationDays || 15
}
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const defaultStages = [
  { code: 'Design', name: '设计' }, { code: 'MaterialPreparation', name: '备料' },
  { code: 'Assembly', name: '装配' }, { code: 'Commissioning', name: '调试' },
  { code: 'ClientCommissioning', name: '客户端调试' }, { code: 'AcceptanceProgress', name: '验收推进' },
  { code: 'FinalAcceptance', name: '终验收' },
]
async function load() {
  templates.value = []
  templateDraft.value = null
  error.value = ''
  if (!props.canManage) return
  loading.value = true
  try {
    templates.value = await listProjectPlanTemplates(props.token, true)
    if (templates.value[0]) selectTemplate(templates.value[0].id)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '计划模板加载失败'
  } finally { loading.value = false }
}
watch(() => [props.token, props.canManage], load, { immediate: true })

function selectTemplate(templateId: string) {
  selectedTemplateId.value = templateId
  const source = templates.value.find(item => item.id === templateId)
  templateDraft.value = source ? JSON.parse(JSON.stringify({ ...source, tasks: [...source.tasks].sort((a, b) => a.sortOrder - b.sortOrder), stages: source.stages ?? defaultStages })) : null
  for (const stage of templateDraft.value?.stages ?? []) {
    if (stage.participatesInDelivery == null) setStageMode(stage, !['ClientCommissioning', 'AcceptanceProgress'].includes(stage.code) && !['客户端调试', '验收推进'].includes(stage.name))
  }
  selectedStageCode.value = templateDraft.value?.stages?.[0]?.code ?? ''
  selectedTaskId.value = ''
  collapsedStages.value = new Set()
}

function addTemplateTask() {
  const draft = templateDraft.value
  const group = selectedGroup.value
  if (!draft || !group) return
  const task = {
    id: crypto.randomUUID(), name: '新任务', stage: group.stage.code, durationRatio: .05,
    predecessorSortOrders: [], startOffsetDays: 0, fixedDurationDays: 1, defaultAssigneeRole: 'ProjectManager', weight: 5,
    isMilestone: false, isRequired: true, sortOrder: Math.max(0, ...draft.tasks.map(item => item.sortOrder)) + 10,
  }
  draft.tasks.push(task)
  applyTaskOrder(draft.tasks)
  selectedTaskId.value = task.id
  collapsedStages.value.delete(group.stage.code)
}

// 序号随显示顺序自动更新，同时重映射前置序号，依赖的任务身份不变。
function applyTaskOrder(tasks: ProjectPlanTemplate['tasks']) {
  const draft = templateDraft.value
  if (!draft) return
  const ordered = (draft.stages ?? []).flatMap(stage => tasks.filter(task => task.stage === stage.code))
  const orderMap = new Map(ordered.map((task, index) => [task.sortOrder, (index + 1) * 10]))
  draft.tasks = ordered.map(task => ({
    ...task, sortOrder: orderMap.get(task.sortOrder)!,
    predecessorSortOrders: task.predecessorSortOrders.map(order => orderMap.get(order) ?? order),
  }))
}
function moveTask(taskId: string, offset: number) {
  const draft = templateDraft.value
  const group = stageGroups.value.find(item => item.tasks.some(task => task.id === taskId))
  if (!draft || !group) return
  const index = group.tasks.findIndex(task => task.id === taskId)
  const sibling = group.tasks[index + offset]
  if (!sibling) return
  const tasks = [...draft.tasks]
  const from = tasks.findIndex(task => task.id === taskId)
  const to = tasks.findIndex(task => task.id === sibling.id)
  ;[tasks[from], tasks[to]] = [tasks[to]!, tasks[from]!]
  applyTaskOrder(tasks)
  selectTask(group.stage.code, taskId)
}
function changeTaskStage(taskId: string, stage: string) {
  const draft = templateDraft.value
  const task = draft?.tasks.find(item => item.id === taskId)
  if (!draft || !task) return
  applyTaskOrder([...draft.tasks.filter(item => item.id !== taskId), { ...task, stage }])
  selectTask(stage, taskId)
}
function selectTask(stageCode: string, taskId = '') {
  selectedStageCode.value = stageCode
  selectedTaskId.value = taskId
  collapsedStages.value.delete(stageCode)
}
function toggleStage(code: string) {
  collapsedStages.value.has(code) ? collapsedStages.value.delete(code) : collapsedStages.value.add(code)
}

async function saveTemplate() {
  const draft = templateDraft.value
  if (!draft || !props.canManage || saving.value) return
  if (allocationError.value) return ElMessage.warning(allocationError.value)
  const scrollPositions = [...(settingsElement.value?.querySelectorAll<HTMLElement>('.pdm-template-tree__body,.pdm-template-table') ?? [])].map(element => ({ element, top: element.scrollTop, left: element.scrollLeft }))
  saving.value = true
  try {
    const saved = await saveProjectPlanTemplate(draft.rowVersion ? draft.id : null, {
      name: draft.name, projectTypeCode: draft.projectTypeCode, isActive: draft.isActive, tasks: draft.tasks,
      expectedRowVersion: draft.rowVersion || undefined, stages: draft.stages,
    }, props.token)
    const index = templates.value.findIndex(item => item.id === saved.id)
    if (index < 0) templates.value.push(saved)
    else templates.value[index] = saved
    selectedTemplateId.value = saved.id
    templateDraft.value = JSON.parse(JSON.stringify(saved))
    await nextTick()
    for (const { element, top, left } of scrollPositions) { element.scrollTop = top; element.scrollLeft = left }
    ElMessage.success('计划模板已保存')
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '模板保存失败')
  } finally {
    saving.value = false
  }
}

function copyTemplate() {
  if (!templateDraft.value) return
  templateDraft.value = JSON.parse(JSON.stringify({ ...templateDraft.value, id: crypto.randomUUID(), rowVersion: 0, name: `${templateDraft.value.name}（副本）`, createdBy: props.currentUsername }))
}

function addStage(stages: ProjectPlanStageDefinition[]) {
  const stage = { code: crypto.randomUUID(), name: `新阶段${stages.length + 1}`, participatesInDelivery: true, durationRatio: 0, progressRatio: 0, independentDurationDays: 0 }
  stages.push(stage)
  selectTask(stage.code)
}
function moveStage(stages: ProjectPlanStageDefinition[], index: number, offset: number) {
  const next = index + offset
  if (next < 0 || next >= stages.length) return
  const [item] = stages.splice(index, 1)
  stages.splice(next, 0, item!)
  if (templateDraft.value) applyTaskOrder(templateDraft.value.tasks)
}
function removeStage(stages: ProjectPlanStageDefinition[], index: number, tasks: Array<{ stage: string }>) {
  if (tasks.some(item => item.stage === stages[index]?.code)) return ElMessage.warning('请先将该阶段的任务分配到其他阶段')
  stages.splice(index, 1)
  selectedStageCode.value = stages[Math.min(index, stages.length - 1)]?.code ?? ''
}
function removeTemplateTask(id: string) {
  if (!templateDraft.value) return
  const order = templateDraft.value.tasks.find(item => item.id === id)?.sortOrder
  if (templateDraft.value.tasks.some(item => item.predecessorSortOrders.includes(order!))) return ElMessage.warning('请先移除其他任务对该任务的前置引用')
  applyTaskOrder(templateDraft.value.tasks.filter(item => item.id !== id))
  if (selectedTaskId.value === id) selectedTaskId.value = ''
}
</script>

<template>
  <section ref="settingsElement" class="pdm-plan-template-settings" aria-label="项目计划模板设置">
    <p v-if="!canManage" role="alert">仅开发者和管理员可修改计划模板。</p>
    <p v-else-if="loading" role="status">正在加载计划模板…</p>
    <p v-else-if="error" role="alert">{{ error }} <button class="pdm-secondary-action" @click="load">重试</button></p>
    <template v-else-if="templateDraft">
      <fieldset class="pdm-template-fields" :disabled="saving">
        <div class="pdm-template-picker">
          <label>选择模板<el-select :model-value="selectedTemplateId" :disabled="saving" @update:model-value="selectTemplate"><el-option v-for="item in templates" :key="item.id" :value="item.id" :label="`${item.name} · ${item.isActive ? '启用' : '停用'}`" /></el-select></label>
          <label>模板名称<el-input v-model="templateDraft.name" aria-label="模板名称" /></label>
          <label>项目类型<el-input v-model="templateDraft.projectTypeCode" placeholder="留空表示通用" aria-label="项目类型" /></label>
          <el-checkbox v-model="templateDraft.isActive">启用模板</el-checkbox>
          <span>{{ templateDraft.tasks.length }} 项任务 · {{ templateDraft.stages?.length }} 个阶段</span>
          <button class="pdm-secondary-action" :disabled="saving" @click="copyTemplate">复制为新模板</button>
        </div>
        <div class="pdm-template-workspace">
          <aside class="pdm-template-tree" aria-label="模板阶段与子任务">
            <header><strong>阶段与子任务</strong><div class="pdm-template-tree-actions"><button class="pdm-secondary-action" :disabled="loading || saving" @click="load"><RefreshCw :size="14" />刷新</button><button class="pdm-secondary-action" @click="addStage(templateDraft.stages!)"><Plus :size="14" />增加阶段</button></div></header>
            <div class="pdm-template-delivery-totals" aria-label="交付阶段比例汇总"><span>交付工期 <strong>{{ percent(durationTotal) }}</strong></span><span>交付进度 <strong>{{ percent(progressTotal) }}</strong></span></div>
            <div class="pdm-template-tree__body">
              <section v-for="(group, index) in stageGroups" :key="group.stage.code" class="pdm-template-stage-group" :data-stage="group.stage.code">
                <div class="pdm-template-stage-parent" :class="{ 'is-active': selectedGroup?.stage.code === group.stage.code }">
                  <button class="pdm-template-icon" :aria-label="`${collapsedStages.has(group.stage.code) ? '展开' : '折叠'}阶段：${group.stage.name}`" :aria-expanded="!collapsedStages.has(group.stage.code)" @click="toggleStage(group.stage.code)"><component :is="collapsedStages.has(group.stage.code) ? ChevronRight : ChevronDown" :size="14" /></button>
                  <button class="pdm-template-stage-select" :aria-label="`选择阶段：${group.stage.name}`" @click="selectTask(group.stage.code)"><strong>{{ index + 1 }}. {{ group.stage.name }}</strong><small>{{ group.tasks.length }}</small></button>
                  <button class="pdm-template-icon" :disabled="index === 0" :aria-label="`上移阶段：${group.stage.name}`" title="上移阶段" @click="moveStage(templateDraft.stages!, index, -1)"><ArrowUp :size="14" /></button>
                  <button class="pdm-template-icon" :disabled="index === stageGroups.length - 1" :aria-label="`下移阶段：${group.stage.name}`" title="下移阶段" @click="moveStage(templateDraft.stages!, index, 1)"><ArrowDown :size="14" /></button>
                </div>
                <small class="pdm-template-stage-allocation">{{ group.stage.participatesInDelivery ? `工期 ${percent(group.stage.durationRatio ?? 0)} · 进度 ${percent(group.stage.progressRatio ?? 0)}` : `交付后连续排期 · ${group.stage.independentDurationDays ?? 0} 天` }}</small>
                <div v-if="!collapsedStages.has(group.stage.code)" class="pdm-template-children">
                  <button v-for="task in group.tasks" :key="task.id" :class="{ 'is-active': selectedTaskId === task.id }" :aria-label="`选择任务：${task.name}`" @click="selectTask(group.stage.code, task.id)"><span>{{ task.sortOrder }}</span><span>{{ task.name }}</span></button>
                  <small v-if="!group.tasks.length">暂无子任务</small>
                </div>
              </section>
            </div>
          </aside>
          <section v-if="selectedGroup" class="pdm-template-detail" aria-label="阶段子任务明细">
            <header class="pdm-plan-stage-editor">
              <label>阶段名称<el-input v-model="selectedGroup.stage.name" :maxlength="60" aria-label="模板阶段名称" /></label>
              <span>{{ selectedGroup.tasks.length }} 项子任务</span>
              <button class="pdm-secondary-action" @click="removeStage(templateDraft.stages!, templateDraft.stages!.findIndex(stage => stage.code === selectedGroup!.stage.code), templateDraft.tasks)">删除阶段</button>
              <button class="pdm-primary-action" @click="addTemplateTask"><Plus :size="14" />增加子任务</button>
            </header>
            <div class="pdm-stage-allocation-controls">
              <label>阶段排期<el-select :model-value="selectedGroup.stage.participatesInDelivery" aria-label="阶段排期模式" @update:model-value="setStageMode(selectedGroup.stage, $event)"><el-option label="交付阶段" :value="true" /><el-option label="交付后连续排期" :value="false" /></el-select></label>
              <template v-if="selectedGroup.stage.participatesInDelivery">
                <label>工期占比（%）*<el-input-number :model-value="Number(((selectedGroup.stage.durationRatio ?? 0) * 100).toFixed(2))" :min="0" :max="100" :precision="2" :controls="false" aria-label="阶段工期占比" @update:model-value="selectedGroup.stage.durationRatio = Math.round(($event ?? 0) * 100) / 10000" /></label>
                <label>进度占比（%）*<el-input-number :model-value="Number(((selectedGroup.stage.progressRatio ?? 0) * 100).toFixed(2))" :min="0" :max="100" :precision="2" :controls="false" aria-label="阶段进度占比" @update:model-value="selectedGroup.stage.progressRatio = Math.round(($event ?? 0) * 100) / 10000" /></label>
              </template>
              <label v-else>默认阶段工期（天）*<el-input-number v-model="selectedGroup.stage.independentDurationDays" :min="1" :max="3650" :precision="0" :controls="false" aria-label="默认阶段工期" /></label>
            </div>
            <p>无前置任务可并行，顺序仅用于显示，不强制首末任务占满阶段。固定工期不随总工期变化；比例工期＝阶段天数 × 比例（向下取整，至少1天）；里程碑0天。任务按前置关系及偏移排期；阶段显示范围按子任务统计，权重与工期独立。</p>
            <div class="pdm-allocation-summary pdm-task-allocation-summary"><span>比例任务合计 {{ percent(taskRatioTotal) }}（并行允许超过100%）</span><span>固定任务合计 {{ fixedTaskDays }} 天（并行不代表阶段跨度）</span><span>当前阶段总权重 <strong>{{ Number(weightTotal.toFixed(4)) }}</strong></span><span>贡献合计 {{ weightTotal > 0 ? '100%' : '0%（请配置权重）' }}</span></div>
            <div class="pdm-template-table">
              <div class="pdm-template-head"><span>序号</span><span>任务名称</span><span>所属阶段</span><span>工期方式</span><span>天数 / 比例%</span><span>权重分值</span><span>阶段内贡献</span><span>偏移天数</span><span>前置任务</span><span>默认责任角色</span><span>里程碑</span><span>必需</span><span>顺序 / 操作</span></div>
              <div v-for="(task, index) in selectedGroup.tasks" :key="task.id" class="pdm-template-row" :class="{ 'is-selected': selectedTaskId === task.id }" :data-task-id="task.id">
                <span class="pdm-template-order">{{ task.sortOrder }}</span><el-input v-model="task.name" aria-label="任务名称" />
                <el-select :model-value="task.stage" aria-label="所属阶段" @update:model-value="changeTaskStage(task.id, $event)"><el-option v-for="stage in templateDraft.stages" :key="stage.code" :label="stage.name" :value="stage.code" /></el-select>
                <el-select :model-value="task.isMilestone ? 'milestone' : task.fixedDurationDays == null ? 'ratio' : 'fixed'" :disabled="task.isMilestone" aria-label="工期方式" @update:model-value="task.fixedDurationDays = $event === 'fixed' ? 1 : null"><el-option label="阶段比例" value="ratio" /><el-option label="固定天数" value="fixed" /><el-option v-if="task.isMilestone" label="里程碑" value="milestone" /></el-select>
                <span v-if="task.isMilestone">0 天</span><el-input-number v-else-if="task.fixedDurationDays != null" :model-value="task.fixedDurationDays" :min="1" :max="3650" :precision="0" :controls="false" aria-label="固定工期天数" @update:model-value="task.fixedDurationDays = $event ?? 1" /><el-input-number v-else :model-value="Number((task.durationRatio * 100).toFixed(2))" :min="0" :max="100" :precision="2" :controls="false" aria-label="阶段内工期比例" @update:model-value="task.durationRatio = Math.round(($event ?? 0) * 100) / 10000" /><el-input-number :model-value="task.weight" :min="0" :controls="false" aria-label="权重" @update:model-value="task.weight = $event ?? 0" />
                <span class="pdm-task-contribution">{{ percent(weightTotal > 0 ? task.weight / weightTotal : 0) }}</span><el-input-number v-model="task.startOffsetDays" :min="0" :max="3650" :precision="0" :controls="false" aria-label="开始偏移天数" />
                <el-select v-model="task.predecessorSortOrders" multiple collapse-tags filterable aria-label="前置任务"><el-option v-for="other in templateDraft.tasks.filter(item => item.id !== task.id)" :key="other.id" :value="other.sortOrder" :label="`${other.sortOrder} · ${other.name}`" /></el-select>
                <el-select v-model="task.defaultAssigneeRole" aria-label="默认责任角色"><el-option label="项目经理" value="ProjectManager" /><el-option label="主设" value="DesignLead" /><el-option label="设计人员" value="Designer" /></el-select>
                <el-checkbox v-model="task.isMilestone" aria-label="里程碑" /><el-checkbox v-model="task.isRequired" aria-label="必需任务" />
                <div class="pdm-template-row-actions"><button class="pdm-template-icon" :disabled="index === 0" :aria-label="`上移任务：${task.name}`" title="上移任务" @click="moveTask(task.id, -1)"><ArrowUp :size="14" /></button><button class="pdm-template-icon" :disabled="index === selectedGroup.tasks.length - 1" :aria-label="`下移任务：${task.name}`" title="下移任务" @click="moveTask(task.id, 1)"><ArrowDown :size="14" /></button><button class="pdm-template-delete" @click="removeTemplateTask(task.id)">删除</button></div>
              </div>
              <p v-if="!selectedGroup.tasks.length" class="pdm-template-empty">此阶段暂无子任务，点击“增加子任务”设置初始任务。</p>
            </div>
          </section>
          <p v-else class="pdm-template-empty">请先增加一个阶段，再添加子任务。</p>
        </div>
      </fieldset>
      <footer><span>{{ allocationError || '权重按工作量或成果贡献设定，自动汇总；保存后生效并保留当前位置。' }}</span><button class="pdm-secondary-action" :disabled="saving" @click="selectTemplate(selectedTemplateId)">取消修改</button><button class="pdm-primary-action" :disabled="saving || !!allocationError" @click="saveTemplate">{{ saving ? '保存中…' : '保存模板' }}</button></footer>
    </template>
  </section>
</template>

<style scoped>
.pdm-plan-template-settings{--plan-accent:var(--shell-accent,var(--pdm-blue));--plan-accent-soft:var(--shell-accent-soft,var(--pdm-blue-soft));--plan-accent-border:var(--shell-accent-border,var(--pdm-border));min-width:0;min-height:0;height:100%;display:flex;flex-direction:column;gap:14px;overflow:auto;padding:18px;color:var(--pdm-text);background:var(--pdm-surface)}
.pdm-plan-template-settings p{color:var(--pdm-muted);font-size:12px;margin:6px 0;line-height:1.6}
.pdm-template-picker{display:grid;grid-template-columns:minmax(200px,1.2fr) minmax(150px,1fr) minmax(130px,.8fr) auto auto auto;align-items:end;gap:14px;flex-shrink:0}.pdm-template-picker>label{display:flex;flex-direction:column;min-width:0;gap:6px;font-size:12px}.pdm-template-picker .el-select{width:100%}.pdm-template-picker>span{align-self:center;font-size:11px;color:var(--pdm-muted);white-space:nowrap}.pdm-template-picker>button{white-space:nowrap}
.pdm-template-fields{min-width:0;min-height:0;flex:1;display:flex;flex-direction:column;gap:16px;border:0;padding:0;margin:0}.pdm-template-fields:disabled{opacity:.65}
.pdm-template-workspace{display:grid;grid-template-columns:280px minmax(0,1fr);gap:16px;flex:1;min-height:280px;overflow:hidden}
.pdm-template-tree,.pdm-template-detail{min-width:0;min-height:0;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface);display:flex;flex-direction:column;overflow:hidden}
.pdm-template-tree>header{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:8px;padding:10px;border-bottom:1px solid var(--pdm-border);font-size:12px}.pdm-template-tree>header button{padding:5px 7px;font-size:11px}.pdm-template-tree-actions{display:flex;align-items:center;gap:6px;margin-left:auto;white-space:nowrap}
.pdm-template-delivery-totals{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px;padding:9px 10px;border-bottom:1px solid var(--pdm-border);background:var(--pdm-surface-muted);font-size:11px;flex-shrink:0}.pdm-template-delivery-totals>span{white-space:nowrap;color:var(--pdm-muted)}.pdm-template-delivery-totals strong{color:var(--pdm-text);font-variant-numeric:tabular-nums}
.pdm-template-tree__body{min-height:0;overflow:auto;padding:6px;flex:1}.pdm-template-stage-parent{display:flex;align-items:center;gap:2px;padding:4px 0;border-radius:5px}.pdm-template-stage-parent.is-active{background:var(--plan-accent-soft)}
.pdm-template-stage-select{display:flex;align-items:center;gap:6px;flex:1;min-width:0;border:0;background:transparent;color:var(--pdm-text);text-align:left;padding:5px 0}.pdm-template-stage-select strong{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px}.pdm-template-stage-select small{color:var(--pdm-muted)}
.pdm-template-icon{display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;width:26px;height:28px;border:1px solid transparent;border-radius:4px;background:transparent;color:var(--pdm-muted)}.pdm-template-icon:hover:not(:disabled){color:var(--plan-accent);border-color:var(--plan-accent-border);background:var(--plan-accent-soft)}.pdm-template-icon:disabled{opacity:.3;cursor:not-allowed}
.pdm-template-children{display:flex;flex-direction:column;margin-left:13px;border-left:1px solid var(--pdm-border);padding:2px 0 6px 12px}.pdm-template-children>button{display:flex;gap:8px;align-items:center;min-width:0;min-height:30px;padding:4px 7px;border:0;border-radius:4px;background:transparent;color:var(--pdm-text);text-align:left;font-size:11px}.pdm-template-children>button>span:first-child{width:24px;flex-shrink:0;color:var(--pdm-muted)}.pdm-template-children>button>span:last-child{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-template-children>button:hover,.pdm-template-children>button.is-active{background:var(--plan-accent-soft);color:var(--plan-accent)}.pdm-template-children>small{padding:5px 8px;font-size:11px;color:var(--pdm-muted)}
.pdm-plan-stage-editor{display:flex;align-items:end;gap:10px;padding:12px;flex-shrink:0}.pdm-plan-stage-editor>label{display:flex;flex-direction:column;gap:6px;flex:1;min-width:100px;font-size:12px}.pdm-plan-stage-editor>span{align-self:center;color:var(--pdm-muted);font-size:11px;white-space:nowrap}.pdm-plan-stage-editor>button{padding-inline:8px;font-size:11px;white-space:nowrap}.pdm-template-detail>p{padding:0 12px;margin:0 0 10px}
.pdm-template-table{min-height:0;overflow:auto;flex:1;border-top:1px solid var(--pdm-border)}.pdm-template-head,.pdm-template-row{display:grid;grid-template-columns:42px minmax(160px,1fr) 100px 74px 62px 100px 110px 44px 40px 104px;min-width:1020px;gap:7px;align-items:center;padding:7px 10px;border-bottom:1px solid var(--pdm-border)}.pdm-template-head{position:sticky;top:0;z-index:1;background:var(--pdm-surface-muted);color:var(--pdm-muted);font-size:10px}.pdm-template-row{font-size:11px}.pdm-template-row.is-selected{background:var(--plan-accent-soft)}.pdm-template-row :deep(.el-input-number){width:100%}.pdm-template-order{color:var(--pdm-muted);font-variant-numeric:tabular-nums}.pdm-template-row-actions{display:flex;align-items:center;gap:2px}.pdm-template-delete{border:0;background:transparent;color:var(--pdm-muted);padding:5px;font-size:11px;white-space:nowrap}.pdm-template-delete:hover{color:var(--pdm-danger)}.pdm-template-empty{padding:24px;text-align:center}
.pdm-template-head,.pdm-template-row{grid-template-columns:42px minmax(160px,1fr) 100px 100px 86px 72px 76px 74px 150px 110px 44px 40px 104px;min-width:1370px}
.pdm-allocation-summary{display:flex;flex-wrap:wrap;gap:6px 20px;padding:10px 12px;border:1px solid var(--pdm-border);border-radius:5px;font-size:12px;flex-shrink:0;background:var(--pdm-surface-muted)}.pdm-allocation-summary>small,.pdm-allocation-summary>p{width:100%;color:var(--pdm-muted)}.pdm-template-stage-allocation{display:block;margin:0 0 5px 27px;color:var(--pdm-muted);font-size:10px}.pdm-stage-allocation-controls{display:flex;flex-wrap:wrap;gap:12px;padding:0 12px 10px;flex-shrink:0}.pdm-stage-allocation-controls>label{display:flex;flex-direction:column;gap:5px;width:150px;font-size:11px}.pdm-stage-allocation-controls .el-input-number{width:100%}.pdm-task-allocation-summary{margin:0 12px 10px;font-size:11px}.pdm-task-contribution{font-variant-numeric:tabular-nums}
.pdm-plan-template-settings>footer{display:flex;align-items:center;justify-content:flex-end;gap:8px;flex-shrink:0;padding-top:12px;border-top:1px solid var(--pdm-border);background:var(--pdm-surface)}.pdm-plan-template-settings>footer>span{flex:1;color:var(--pdm-muted);font-size:11px}
@media(max-width:1000px){.pdm-template-workspace{grid-template-columns:230px minmax(0,1fr)}.pdm-plan-stage-editor{flex-wrap:wrap}.pdm-template-picker{grid-template-columns:repeat(3,minmax(0,1fr))}}
@media(max-width:760px){.pdm-plan-template-settings{height:auto}.pdm-template-workspace{grid-template-columns:minmax(0,1fr);overflow:visible}.pdm-template-tree{max-height:300px}.pdm-template-table{max-height:450px;flex:auto}.pdm-template-picker{grid-template-columns:minmax(0,1fr)}}
</style>
