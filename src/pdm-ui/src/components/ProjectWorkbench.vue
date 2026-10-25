<script setup lang="ts">
import PanelActions from './PanelActions.vue'
import { useId as usePanelFormId } from 'vue'
const panelFormId = usePanelFormId()

import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ChevronLeft, ChevronRight, RotateCw as RefreshCw, Maximize2, Minimize2 } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import { readOplAttachment, readProjectWorkbench, readProjectOpl, saveProjectOpl, listProjectAudit, addProjectManagerNote, closeWorkbenchRisk } from '../api'
import { addDays, deliveryDate, effectiveTasks, isEffective, isMine, manualRiskAction, nextTask, progress, projectActions, splitPeople, stageName } from '../projectWorkbench'
import type { WorkbenchData, WorkbenchProject, WorkbenchTab } from '../projectWorkbench'
import type { AuditEntry, OrganizationUnit, PdmUser } from '../types'
import type { OplInput, OplRow, OplView } from '../projectOpl'
import { chinaToday } from '../projectCalendar'

const props = defineProps<{ users: PdmUser[]; token: string; currentUsername: string; companyId: string; organizationUnits?: OrganizationUnit[] }>()
const emit = defineEmits<{ open: [projectId: string, tab?: WorkbenchTab, taskId?: string] }>()
const data = ref<WorkbenchData>(), loading = ref(false), error = ref(''), scope = ref('company')
const selectedId = ref(''), query = ref(''), unit = ref(''), phaseFilter = ref(''), managerFilter = ref('')
const maximized = ref(false), oplFilter = ref('open'), taskFilter = ref('week'), bottomTab = ref('records')
const today = ref(chinaToday())
const timer = setInterval(() => { today.value = chinaToday() }, 60000)
const opl = ref<Record<string, OplView | null | undefined>>({}), oplErrors = ref<Record<string, string>>({})
const notes = ref<AuditEntry[]>([]), notesError = ref(''), notesLoading = ref(false)
const preparingIssue = ref(false)
const detailLoading = ref<Record<string, boolean>>({}), detailErrors = ref<Record<string, string>>({})
let loadedDetails = new Set<string>(), pendingDetails = new Map<string, Promise<void>>()
const oplDialog = ref(false), oplSaving = ref(false), oplSaveError = ref(''), editing = ref<OplRow | null>(null)
const oplTarget = ref(''), editorPeople = ref<OplView['people']>([]), editorProjects = ref<OplView['subprojects']>([])
const emptyIssue = (): OplInput => ({ description: '', priority: '普通', status: '待处理', owner: '', dueDate: null, solution: '', expectedRowVersion: 0, attachmentIds: [], relatedProjectId: null })
const issueDraft = ref(emptyIssue())
const noteDialog = ref(false), noteSaving = ref(false), noteError = ref(''), noteTarget = ref('')
const noteDraft = ref({ conclusion: '', action: '', owner: '', due: '' })
let generation = 0, noteGeneration = 0
let pendingOpl = new Map<string, Promise<void>>()
const memoryKey = computed(() => `pdm.workbench.review.${props.companyId}.${props.currentUsername}`)
const scopeLabels: Record<string, string> = { division: '事业部', company: '公司', sales: '我负责' }
const nameOf = (value?: string) => splitPeople(value).map(username => props.users.find(user => user.username.toLowerCase() === username)?.displayName || username).join('、') || '待分派'
const closed = (row: OplRow) => ['已完成', '已作废'].includes(row.issue.status)
const overdue = (row: OplRow) => !closed(row) && !!row.issue.dueDate && row.issue.dueDate < today.value
const all = computed(() => (data.value?.projects ?? []).filter(item => !['Completed', 'Cancelled', 'Terminated', '已完成', '已取消', '已终止'].includes(item.project.businessStatus || item.plan?.currentStage || '')))
const rootId = (item: WorkbenchProject) => all.value.some(parent => parent.project.id === (item.project.rootProjectId || item.project.parentProjectId)) ? item.project.rootProjectId || item.project.parentProjectId! : item.project.id
const roots = computed(() => all.value.filter(item => rootId(item) === item.project.id))
const familyOf = (root: WorkbenchProject) => all.value.filter(item => rootId(item) === root.project.id)
const units = computed(() => {
  const directory = (props.organizationUnits ?? []).filter(item => item.organizationId === props.companyId)
  const order = new Map<string, number>()
  const visit = (siblings: OrganizationUnit[]) => {
    for (const item of [...siblings].sort((a, b) => a.sortOrder - b.sortOrder || a.code.localeCompare(b.code, 'zh-CN', { numeric: true }))) {
      if (!order.has(item.name)) order.set(item.name, order.size)
      visit(directory.filter(child => child.parentUnitId === item.id))
    }
  }
  visit(directory.filter(item => !item.parentUnitId || !directory.some(parent => parent.id === item.parentUnitId)))
  return [...new Set(roots.value.map(item => item.project.executionUnitName).filter((name): name is string => !!name))]
    .sort((a, b) => (order.get(a) ?? Number.MAX_SAFE_INTEGER) - (order.get(b) ?? Number.MAX_SAFE_INTEGER) || a.localeCompare(b, 'zh-CN', { numeric: true }))
})
const plannedDelivery = (item: WorkbenchProject) => isEffective(item) ? deliveryDate(item) : undefined
const rootDelivery = (root: WorkbenchProject) => plannedDelivery(root) || familyOf(root).map(plannedDelivery).filter(Boolean).sort().at(-1)
const deliveryCountdown = computed(() => {
  const finish = selected.value && rootDelivery(selected.value)
  if (!finish) return '未排程'
  const days = Math.round((Date.parse(`${finish}T00:00:00Z`) - Date.parse(`${today.value}T00:00:00Z`)) / 86400000)
  return days > 0 ? `${days}天` : days === 0 ? '今天交付' : `逾期${-days}天`
})
const projectRisks = (root: WorkbenchProject) => (data.value?.risks ?? []).filter(risk => !risk.closed && familyOf(root).some(item => item.project.id === risk.projectId))
const progressPhase = (item: WorkbenchProject) => isEffective(item) ? stageName(item) : item.plan ? '计划未生效' : '未排程'
const phaseOptions = computed(() => {
  const order = new Map<string, number>()
  for (const item of roots.value) for (const stage of item.plan?.stages ?? []) if (!order.has(stage.name)) order.set(stage.name, order.size)
  return [...new Set(roots.value.map(progressPhase))].sort((a, b) => (order.get(a) ?? 999) - (order.get(b) ?? 999) || a.localeCompare(b, 'zh-CN'))
})
const managerKey = (item: WorkbenchProject) => item.project.primaryProjectManager?.trim().toLowerCase() || '__unassigned'
const managerOptions = computed(() => [...new Set(roots.value.map(managerKey))]
  .map(value => ({ value, label: value === '__unassigned' ? '待分派' : nameOf(value) }))
  .sort((a, b) => a.label.localeCompare(b.label, 'zh-CN')))
const filteredRoots = computed(() => roots.value.filter(root => (!unit.value || root.project.executionUnitName === unit.value)
  && (!phaseFilter.value || progressPhase(root) === phaseFilter.value)
  && (!managerFilter.value || managerKey(root) === managerFilter.value)
  && familyOf(root).some(({ project: p }) => [p.code, p.name, p.customerName, p.primaryProjectManager, nameOf(p.primaryProjectManager), p.designLead, nameOf(p.designLead)].join(' ').toLowerCase().includes(query.value.trim().toLowerCase())))
  .sort((a, b) => new Date(b.project.createdAt || 0).getTime() - new Date(a.project.createdAt || 0).getTime() || a.project.code.localeCompare(b.project.code)))
const selected = computed(() => filteredRoots.value.find(item => item.project.id === selectedId.value))
const family = computed(() => selected.value ? familyOf(selected.value) : [])
const index = computed(() => filteredRoots.value.findIndex(item => item.project.id === selectedId.value))
const selectedOpl = computed(() => opl.value[selectedId.value])
const selectedIssues = computed(() => selectedOpl.value?.rows ?? [])
const riskRows = computed(() => selected.value ? projectRisks(selected.value) : [])
const visibleIssues = computed(() => selectedIssues.value.filter(row => oplFilter.value === 'closed' ? closed(row) : !closed(row)
  && (oplFilter.value === 'open' || oplFilter.value === 'overdue' && overdue(row) || oplFilter.value === 'mine' && isMine(row.issue.owner, props.currentUsername) || oplFilter.value === 'confirm' && row.issue.status === '待确认'))
  .sort((a, b) => b.issue.number - a.issue.number))
const issueTabs = computed(() => [
  { key: 'open', label: '未完成', count: selectedIssues.value.filter(row => !closed(row)).length },
  { key: 'overdue', label: '逾期', count: selectedIssues.value.filter(overdue).length },
  { key: 'mine', label: '待我处理', count: selectedIssues.value.filter(row => !closed(row) && isMine(row.issue.owner, props.currentUsername)).length },
  { key: 'confirm', label: '待确认', count: selectedIssues.value.filter(row => row.issue.status === '待确认').length },
  { key: 'closed', label: '已关闭', count: selectedIssues.value.filter(closed).length },
])
const actions = computed(() => {
  const seen = new Set<string>()
  return [...family.value.flatMap(item => projectActions(item, 'manager', today.value, 7, props.currentUsername)), ...riskRows.value.map(risk => manualRiskAction(risk, props.currentUsername, today.value))]
    .filter(action => !action.id.endsWith('-budget-missing') && !action.id.endsWith('-budget-overrun'))
    .filter(action => { const task = family.value.find(item => item.project.id === action.projectId)?.plan?.tasks.find(task => task.id === action.taskId); const key = task?.sourceTaskId || task?.id || action.id; if (seen.has(key)) return false; seen.add(key); return true })
    .sort((a, b) => a.priority - b.priority || (a.due || '9999').localeCompare(b.due || '9999'))
})
const visibleActions = computed(() => actions.value.filter(action => taskFilter.value === 'all' ? true : taskFilter.value === 'mine' ? action.group === '待我处理'
  : taskFilter.value === 'approval' ? action.kind === 'approval' : taskFilter.value === 'today' ? !!action.due && action.due <= today.value : action.upcoming || !action.due || action.due <= addDays(today.value, 7)))
const currentNext = computed(() => family.value.flatMap(item => { const task = nextTask(item); return task ? [{ item, task }] : [] }).sort((a, b) => a.task.plannedFinish.localeCompare(b.task.plannedFinish))[0])
const phases = computed(() => selected.value?.plan?.stages?.filter(stage => effectiveTasks(selected.value!).some(task => task.stage === stage.code)) ?? [])
const projectCode = (id?: string | null) => all.value.find(item => item.project.id === id)?.project.code || selectedOpl.value?.subprojects?.find(item => item.id === id)?.code || selected.value?.project.code || '—'
const phaseStatus = (code: string) => { const tasks = selected.value ? effectiveTasks(selected.value).filter(task => task.stage === code) : []; return selected.value?.plan?.currentStage === code ? 'current' : tasks.length && tasks.every(task => task.status === 'Completed') ? 'done' : '' }
const scopeOptions = computed(() => ['division', 'company', 'sales'].filter(key => (data.value?.scopes || ['personal', 'company']).includes(key)))
function friendlyFailure(reason: unknown, text: string) { console.error(text, reason); return text }
async function ensureOpl(id: string, run = generation) {
  if (pendingOpl.has(id)) return pendingOpl.get(id)
  const request = (async () => {
    try { const value = await readProjectOpl(id, props.token, false); if (run === generation) { opl.value = { ...opl.value, [id]: value }; delete oplErrors.value[id] } }
    catch (reason) { if (run === generation) { opl.value = { ...opl.value, [id]: null }; oplErrors.value[id] = friendlyFailure(reason, 'OPL暂时无法读取，请重试或核实项目权限。') } }
  })()
  pendingOpl.set(id, request)
  try { await request } finally { if (run === generation) pendingOpl.delete(id) }
}
async function load(requestedScope = scope.value) {
  const run = ++generation; loadedDetails = new Set(); pendingDetails = new Map(); detailLoading.value = {}; detailErrors.value = {}; noteGeneration++; loading.value = true; error.value = ''; data.value = undefined; notes.value = []; opl.value = {}; oplErrors.value = {}; pendingOpl = new Map()
  try {
    const result = await readProjectWorkbench('', props.token, requestedScope, { summaryOnly: true })
    if (run !== generation) return
    data.value = result; scope.value = requestedScope; localStorage.setItem(memoryKey.value, requestedScope)
  } catch (reason) {
    if (run !== generation) return
    if (requestedScope !== 'company' && (reason as { status?: number }).status === 403) { await load('company'); return }
    error.value = friendlyFailure(reason, '项目总览读取失败，请检查网络后重试。')
  } finally { if (run === generation) loading.value = false }
}
async function loadDetails(id: string) {
  if (loadedDetails.has(id)) return
  if (pendingDetails.has(id)) return pendingDetails.get(id)
  const run = generation
  detailLoading.value[id] = true
  delete detailErrors.value[id]
  const request = (async () => {
    try {
      const details = await readProjectWorkbench('', props.token, 'company', { projectId: id })
      if (run !== generation || !data.value) return
      loadedDetails.add(id)
      const byId = new Map(details.projects.map(item => [item.project.id, item]))
      data.value.projects = data.value.projects.map(item => byId.has(item.project.id) ? { ...byId.get(item.project.id)!, oplOpenCount: item.oplOpenCount } : item)
    } catch (reason) {
      if (run === generation) detailErrors.value[id] = friendlyFailure(reason, '采购和验收详情读取失败，请重试；现有计划和OPL仍可查看。')
    } finally {
      if (run === generation) { detailLoading.value[id] = false; pendingDetails.delete(id) }
    }
  })()
  pendingDetails.set(id, request)
  return request
}
async function loadNotes(id: string) {
  const run = ++noteGeneration; notes.value = []; notesError.value = ''; notesLoading.value = true
  try { const value = await listProjectAudit(id, props.token); if (run === noteGeneration) notes.value = value.filter(entry => entry.action === 'project.manager-note' || entry.action === 'project.todo.create').sort((a, b) => b.occurredAt.localeCompare(a.occurredAt)) }
  catch (reason) { if (run === noteGeneration) notesError.value = friendlyFailure(reason, '跟进记录读取失败，请重试。') }
  finally { if (run === noteGeneration) notesLoading.value = false }
}
watch(filteredRoots, rows => { if (!rows.length && loading.value) return; if (!rows.some(item => item.project.id === selectedId.value)) selectedId.value = rows[0]?.project.id || '' })
watch(() => selected.value?.project.id, id => { if (id) localStorage.setItem(memoryKey.value + '.selected', id); notes.value = []; noteGeneration++; oplFilter.value = 'open'; if (id) { if (opl.value[id] === undefined) void ensureOpl(id); void loadDetails(id); void loadNotes(id) } })
watch([() => props.token, () => props.companyId, () => props.currentUsername], () => { selectedId.value = localStorage.getItem(memoryKey.value + '.selected') || ''; query.value = ''; unit.value = ''; phaseFilter.value = ''; managerFilter.value = ''; oplDialog.value = false; noteDialog.value = false; void load(['division', 'company', 'sales'].includes(localStorage.getItem(memoryKey.value) || '') ? localStorage.getItem(memoryKey.value)! : 'company') }, { immediate: true })
async function selectProject(id: string) { selectedId.value = id; await nextTick(); document.getElementById(`workbench-project-${id}`)?.scrollIntoView?.({ block: 'nearest' }) }
function move(offset: number) { const target = filteredRoots.value[index.value + offset]; if (target) void selectProject(target.project.id) }
async function openIssue(row: OplRow | null) {
  if (!selected.value || !selectedOpl.value || preparingIssue.value) return
  const id = selectedId.value, run = generation
  preparingIssue.value = true
  try {
    const editor = await readProjectOpl(id, props.token)
    if (run !== generation || selectedId.value !== id) return
    if (row) {
      const latest = editor.rows.find(item => item.issue.id === row!.issue.id)
      if (!latest) { ElMessage.warning('问题已被移除，请刷新后重试。'); return }
      row = latest
    }
    opl.value = { ...opl.value, [id]: editor }
  } catch (reason) {
    ElMessage.error(friendlyFailure(reason, '问题编辑信息读取失败，请重试。')); return
  } finally { preparingIssue.value = false }
  oplTarget.value = selected.value.project.id; editing.value = row; editorPeople.value = selectedOpl.value.people; editorProjects.value = selectedOpl.value.subprojects
  issueDraft.value = row ? { description: row.issue.description, priority: row.issue.priority, status: row.issue.status, owner: row.issue.owner, dueDate: row.issue.dueDate, solution: row.issue.solution, expectedRowVersion: row.issue.rowVersion, attachmentIds: row.issue.attachments.map(file => file.id), relatedProjectId: row.issue.relatedProjectId || null } : emptyIssue()
  oplSaveError.value = ''; oplDialog.value = true
}
async function saveIssue() {
  if (oplSaving.value || editing.value && !editing.value.canEdit && !editing.value.canComplete) return
  if (!issueDraft.value.description.trim()) { oplSaveError.value = '请填写问题描述。'; return }
  if (['待确认', '已完成'].includes(issueDraft.value.status) && !issueDraft.value.solution.trim()) { oplSaveError.value = '请先填写解决措施，再提交确认或完成。'; return }
  oplSaving.value = true; oplSaveError.value = ''; const id = oplTarget.value, run = generation
  try { await saveProjectOpl(id, editing.value?.issue.id || null, issueDraft.value, [], props.token); if (run !== generation) return; oplDialog.value = false; await ensureOpl(id, run); ElMessage.success('问题跟进已保存') }
  catch (reason) { oplSaveError.value = friendlyFailure(reason, '问题保存失败，请核实权限或重新读取最新问题后再试。') }
  finally { oplSaving.value = false }
}
function openNote() { if (!selected.value?.canMaintainRecord) return; noteTarget.value = selectedId.value; noteDraft.value = { conclusion: '', action: '', owner: '', due: '' }; noteError.value = ''; noteDialog.value = true }
async function saveNote() {
  const draft = noteDraft.value
  if (!draft.conclusion.trim()) { noteError.value = '请填写本次跟进结论。'; return }
  if (draft.action.trim() && (!draft.owner || !draft.due)) { noteError.value = '有下一步行动时，请明确负责人和完成期限。'; return }
  if (noteSaving.value) return
  noteSaving.value = true; const id = noteTarget.value, run = generation
  const text = `本次结论：${draft.conclusion.trim()}${draft.action.trim() ? `\n下一步行动：${draft.action.trim()}\n负责人：${nameOf(draft.owner)}；完成期限：${draft.due}` : ''}`
  try { await addProjectManagerNote(id, text, props.token); if (run !== generation) return; noteDialog.value = false; if (id === selectedId.value) await loadNotes(id); ElMessage.success('跟进记录已保存') }
  catch (reason) { noteError.value = friendlyFailure(reason, '跟进记录保存失败，请核实项目经理权限后重试。') }
  finally { noteSaving.value = false }
}
async function closeRisk(id: string) { const current = generation; try { await closeWorkbenchRisk(id, props.token); if (current !== generation || !data.value) return; data.value.risks = data.value.risks.map(risk => risk.id === id ? { ...risk, closed: true } : risk); ElMessage.success('风险已关闭') } catch (reason) { ElMessage.error(friendlyFailure(reason, '风险关闭失败，请重试。')) } }
const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const attachmentPreview = ref<{url:string;name:string;pdf:boolean} | null>(null)
function closeAttachmentPreview() { if (attachmentPreview.value) URL.revokeObjectURL(attachmentPreview.value.url); attachmentPreview.value = null }
async function previewAttachment(row: OplRow, file: {id:string;name:string}) {
  try {
    const blob = await readOplAttachment(row.issue.projectId, row.issue.id, file.id, props.token)
    const pdf = /\.pdf$/i.test(file.name), image = /\.(png|jpe?g|gif|webp)$/i.test(file.name)
    const url = URL.createObjectURL(blob)
    if (pdf || image) { closeAttachmentPreview(); attachmentPreview.value = {url,name:file.name,pdf} }
    else { const link=document.createElement('a');link.href=url;link.download=file.name;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000) }
  } catch { ElMessage.error('附件读取失败，请检查网络后重试。') }
}
const oplDate = (value: string | null) => value ? new Date(value).toLocaleDateString('sv-SE') : '—'
onBeforeUnmount(closeAttachmentPreview)
function escape(event: KeyboardEvent) { if (event.key === 'Escape' && !oplDialog.value && !noteDialog.value) maximized.value = false }
window.addEventListener('keydown', escape)
onBeforeUnmount(() => { generation++; noteGeneration++; clearInterval(timer); window.removeEventListener('keydown', escape) })
</script>
<template>
  <section class="pdm-project-workbench review-workbench" :class="{ 'is-maximized': maximized }" aria-label="项目总览" :aria-busy="loading">
    <aside class="wr-projects pdm-panel">
      <header><h2>运行中项目 <em>{{ filteredRoots.length }}</em></h2><button aria-label="刷新项目总览" :disabled="loading" @click="load()"><RefreshCw :size="14" /></button></header>
      <nav class="wr-tabs pdm-tabs" aria-label="项目范围"><button v-for="key in scopeOptions" :key="key" :aria-pressed="scope === key" :disabled="loading" @click="unit = ''; phaseFilter = ''; managerFilter = ''; load(key)">{{ scopeLabels[key] }}</button></nav>
      <input v-model="query" type="search" aria-label="搜索总览项目" placeholder="项目号、名称、客户">
      <div class="wr-filter"><select v-model="unit" aria-label="筛选事业部"><option value="">全部事业部</option><option v-for="value in units" :key="value">{{ value }}</option></select></div>
      <div class="wr-filter"><select v-model="phaseFilter" aria-label="筛选阶段"><option value="">全部阶段</option><option v-for="value in phaseOptions" :key="value">{{ value }}</option></select><select v-model="managerFilter" aria-label="筛选项目经理"><option value="">全部项目经理</option><option v-for="option in managerOptions" :key="option.value" :value="option.value">{{ option.label }}</option></select></div>
      <p v-if="error" class="wr-error" role="alert">{{ error }} <button @click="load()">重试</button></p>
      <div class="wr-project-list" aria-label="运行中项目列表">
        <button v-for="item in filteredRoots" :id="`workbench-project-${item.project.id}`" :key="item.project.id" class="wr-project" :class="{ selected: selectedId === item.project.id }" :aria-label="`选择项目 ${item.project.code}`" :aria-pressed="selectedId === item.project.id" @click="selectProject(item.project.id)">
          <span class="wr-project-first"><b>{{ item.project.code }}</b><span :title="item.project.name">{{ item.project.name }}</span></span>
          <span class="wr-project-second"><span :title="nameOf(item.project.primaryProjectManager)">{{ nameOf(item.project.primaryProjectManager) }}</span><span :title="progressPhase(item)">{{ progressPhase(item) }}</span><time :class="{ danger: rootDelivery(item) && rootDelivery(item)! < today }" :title="rootDelivery(item) || '未排程'">{{ rootDelivery(item)?.slice(5) || '未排程' }}</time></span>
        </button>
        <p v-if="!filteredRoots.length" class="wr-empty">{{ loading ? '正在读取授权项目…' : '暂无符合条件的运行中项目' }}</p>
      </div>
      <footer>共 {{ filteredRoots.length }} 个项目 · 当前第 {{ index + 1 }} 个</footer>
    </aside>
    <main v-if="selected" class="wr-content">
      <section class="pdm-panel wr-identity"><div><h2>{{ selected.project.code }} · {{ selected.project.name }}</h2><p>{{ selected.project.customerName || '客户待补充' }} · {{ selected.project.executionUnitName || '事业部待分配' }} · 项目经理 {{ nameOf(selected.project.primaryProjectManager) }} · 主设 {{ nameOf(selected.project.designLead) }}</p></div><div class="wr-controls"><button @click="emit('open', selectedId, 'overview')">打开项目</button><button :disabled="index <= 0" aria-label="上一项目" @click="move(-1)"><ChevronLeft :size="14" /></button><span>{{ index + 1 }} / {{ filteredRoots.length }}</span><button :disabled="index >= filteredRoots.length - 1" aria-label="下一项目" @click="move(1)"><ChevronRight :size="14" /></button><button :aria-label="maximized ? '退出看板' : '最大化看板'" @click="maximized = !maximized"><component :is="maximized ? Minimize2 : Maximize2" :size="14" /></button></div></section>
      <section class="pdm-panel wr-status" aria-label="项目状态与下一步"><div><div v-if="phases.length" class="wr-phases" role="list" aria-label="项目阶段进度"><span v-for="phase in phases" :key="phase.code" :class="phaseStatus(phase.code)" :aria-current="phaseStatus(phase.code) === 'current' ? 'step' : undefined" :title="phase.name" role="listitem"><i aria-hidden="true" /><small>{{ phase.name }}</small></span></div><span v-else>{{ isEffective(selected) ? stageName(selected) : '计划待审批／未排程' }}</span></div><div>整体进度<strong>{{ progress(selected) == null ? '—' : progress(selected) + '%' }}</strong></div><div>距交期<strong>{{ deliveryCountdown }}</strong></div><div>计划交付<strong>{{ rootDelivery(selected) || '未排程' }}</strong></div><button class="wr-next" @click="emit('open', currentNext?.item.project.id || selectedId, 'project-plan', currentNext?.task.id)"><span class="wr-next-title">下一节点：<strong>{{ currentNext?.task.name || '暂无未完成节点' }}</strong></span><small>{{ currentNext ? `${currentNext.task.plannedFinish} · ${nameOf(currentNext.task.assignee)}` : '—' }}</small></button></section>
      <section class="pdm-panel wr-panel wr-opl" aria-label="OPL优先解决问题">
        <header><h2>OPL · 优先解决问题</h2><div><button :disabled="!selectedOpl || preparingIssue" @click="openIssue(null)">新增问题</button><button @click="emit('open', selectedId, 'opl')">全部OPL</button><button :disabled="loading" @click="ensureOpl(selectedId)">刷新</button></div></header>
        <div class="wr-panel-tools"><nav class="wr-tabs pdm-tabs"><button v-for="tab in issueTabs" :key="tab.key" :aria-pressed="oplFilter === tab.key" @click="oplFilter = tab.key">{{ tab.label }} {{ selectedOpl ? tab.count : '—' }}</button></nav><small>编号由新到旧，与OPL列表一致</small></div>
        <p v-if="oplErrors[selectedId]" role="alert" class="wr-error">{{ oplErrors[selectedId] }} <button @click="ensureOpl(selectedId)">重试</button></p>
        <div v-else class="wr-scroll"><table class="pdm-opl-ratio-table">
          <colgroup><col v-for="(weight, index) in [1,2,1,1,3,2,3,2,1,2,2,1,2]" :key="index" :style="{ '--opl-column-width': `${weight / 23 * 100}%` }" /></colgroup>
          <thead><tr><th>序号</th><th>子项目</th><th>优先级</th><th>实施状态</th><th>问题描述</th><th>附件</th><th>解决措施</th><th>需求日期</th><th>负责人</th><th>完成日期</th><th>提交日期</th><th>提交人</th><th>操作</th></tr></thead>
          <tbody><tr v-for="row in visibleIssues" :key="row.issue.id">
            <td>{{ row.issue.number }}</td><td :title="projectCode(row.issue.relatedProjectId)">{{ row.issue.relatedProjectId ? projectCode(row.issue.relatedProjectId) : '项目整体' }}</td>
            <td :class="row.issue.priority === '紧急' ? 'danger' : row.issue.priority === '高' ? 'warning' : ''">{{ row.issue.priority }}</td><td>{{ row.issue.status }}</td>
            <td :title="row.issue.description">{{ row.issue.description }}</td><td><button v-for="file in row.issue.attachments" :key="file.id" class="wr-opl-attachment" :title="file.name" :aria-label="`查看附件 ${file.name}`" @click="previewAttachment(row,file)">{{ file.name }}</button><span v-if="!row.issue.attachments.length">—</span></td>
            <td :title="row.issue.solution">{{ row.issue.solution || '—' }}</td><td :class="{ danger: overdue(row) }">{{ row.issue.dueDate || '—' }}</td><td>{{ nameOf(row.issue.owner) }}</td>
            <td>{{ oplDate(row.issue.completedAt) }}</td><td>{{ oplDate(row.issue.createdAt) }}</td><td>{{ nameOf(row.issue.createdBy) }}</td>
            <td><button @click="openIssue(row)">{{ row.canEdit || row.canComplete ? '跟进' : '查看' }}</button></td>
          </tr></tbody>
        </table><p v-if="!visibleIssues.length" class="wr-empty">{{ !selectedOpl ? '正在读取开口问题…' : '暂无符合条件的开口问题' }}</p></div>
      </section>
      <section class="pdm-panel wr-panel wr-tasks" aria-label="本周待办与提醒"><header><h2>待办与提醒 <small v-if="detailLoading[selectedId]">详情加载中…</small></h2><nav class="wr-tabs pdm-tabs"><button v-for="(text, key) in { today: '今日及逾期', week: '未来7天', mine: '待我处理', approval: '待审批', all: '全部' }" :key="key" :aria-pressed="taskFilter === key" @click="taskFilter = key">{{ text }}</button></nav></header><p v-if="detailErrors[selectedId]" role="alert" class="wr-error">{{ detailErrors[selectedId] }} <button @click="loadDetails(selectedId)">重试</button></p><div class="wr-scroll"><table><thead><tr><th>事项</th><th>所属项目</th><th>负责人</th><th>截止日期</th><th>状态／提醒</th><th>操作</th></tr></thead><tbody><tr v-for="action in visibleActions" :key="action.id"><td class="wr-description" :title="action.title">{{ action.title }}</td><td>{{ projectCode(action.projectId) }}</td><td>{{ nameOf(action.owner) }}</td><td :class="{ danger: action.due && action.due < today }">{{ action.due || '待明确' }}</td><td class="wr-description" :title="action.impact">{{ action.group }} · {{ action.impact }}</td><td><button @click="emit('open', action.projectId, action.tab, action.taskId)">查看</button></td></tr></tbody></table><p v-if="!visibleActions.length" class="wr-empty">暂无符合条件的待办</p></div></section>
      <section class="pdm-panel wr-panel wr-bottom"><header><nav class="wr-tabs pdm-tabs"><button v-for="(text, key) in { records: '跟进记录', projects: '子项目总览', risks: '人工风险' }" :key="key" :aria-pressed="bottomTab === key" @click="bottomTab = key">{{ text }}</button></nav><button v-if="selected.canMaintainRecord" @click="openNote">记录跟进</button></header>
        <div v-if="bottomTab === 'records'" class="wr-scroll" aria-label="跟进记录"><p v-if="notesError" class="wr-error">{{ notesError }} <button @click="loadNotes(selectedId)">重试</button></p><table v-else><thead><tr><th>时间</th><th>项目</th><th>记录人</th><th>跟进结论与行动</th></tr></thead><tbody><tr v-for="entry in notes" :key="entry.id"><td>{{ new Date(entry.occurredAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai' }) }}</td><td>{{ projectCode(entry.entityId) }}</td><td>{{ nameOf(entry.actor) }}</td><td class="wr-note-content" :title="entry.detail">{{ entry.detail }}</td></tr></tbody></table><p v-if="!notes.length && !notesError" class="wr-empty">{{ notesLoading ? '正在读取跟进记录…' : '暂无跟进记录' }}</p></div>
        <div v-else-if="bottomTab === 'projects'" class="wr-scroll" aria-label="子项目总览"><table><thead><tr><th>项目号</th><th>项目名称</th><th>阶段</th><th>计划交付</th><th>进度</th><th>负责人</th><th>操作</th></tr></thead><tbody><tr v-for="item in family" :key="item.project.id"><td>{{ item.project.code }}</td><td>{{ item.project.name }}</td><td>{{ stageName(item) }}</td><td>{{ plannedDelivery(item) || '未排程' }}</td><td>{{ progress(item) == null ? '—' : progress(item) + '%' }}</td><td>{{ nameOf(item.project.primaryProjectManager) }}</td><td><button @click="emit('open', item.project.id, 'project-plan')">查看计划</button></td></tr></tbody></table></div>
        <div v-else class="wr-scroll"><table><thead><tr><th>项目</th><th>风险</th><th>影响</th><th>负责人</th><th>期限</th><th>操作</th></tr></thead><tbody><tr v-for="risk in riskRows" :key="risk.id"><td>{{ projectCode(risk.projectId) }}</td><td>{{ risk.title }}</td><td class="wr-description" :title="risk.impact">{{ risk.impact }}</td><td>{{ nameOf(risk.owner) }}</td><td>{{ risk.dueDate }}</td><td><button v-if="risk.canClose" @click="closeRisk(risk.id)">关闭风险</button></td></tr></tbody></table><p v-if="!riskRows.length" class="wr-empty">暂无未关闭人工风险</p></div>
      </section>
    </main>
    <div v-else class="pdm-panel wr-empty wr-no-selection">{{ loading ? '正在读取项目…' : '请选择左侧项目，或调整筛选范围' }}</div>
    <el-dialog v-model="oplDialog" :title="editing ? 'OPL问题跟进' : '新增OPL问题'" width="640px" append-to-body :close-on-click-modal="false" :close-on-press-escape="!oplSaving" :show-close="!oplSaving">
      <form :id="panelFormId + '-form-1'" class="wr-form" @submit.prevent="saveIssue"><label>问题描述<textarea v-model="issueDraft.description" maxlength="2000" rows="3" required :disabled="oplSaving || !!editing && !editing.canEdit" /></label><div class="wr-form-grid"><label>优先级<select v-model="issueDraft.priority" :disabled="oplSaving || !!editing && !editing.canEdit"><option v-for="value in ['紧急','高','普通','低']" :key="value">{{ value }}</option></select></label><label>实施状态<select v-model="issueDraft.status" :disabled="oplSaving || !!editing && !editing.canEdit && !editing.canComplete"><option v-for="value in ['待处理','处理中','待确认','已完成','已作废']" :key="value" :disabled="!!editing && ((!editing.canEdit && value !== editing.issue.status && value !== '已完成') || value === '已完成' && !editing.canComplete && editing.issue.status !== '已完成')">{{ value }}</option></select></label><label>负责人<select v-model="issueDraft.owner" :disabled="oplSaving || !!editing && !editing.canEdit"><option value="">待分派</option><option v-for="person in editorPeople" :key="person.username" :value="person.username">{{ person.displayName }}</option></select></label><label>需求完成日期<input v-model="issueDraft.dueDate" type="date" :disabled="oplSaving || !!editing && !editing.canEdit"></label><label>所属项目<select v-model="issueDraft.relatedProjectId" :disabled="oplSaving || !!editing && !editing.canEdit"><option :value="null">主项目</option><option v-for="project in editorProjects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label></div><label>解决措施<textarea v-model="issueDraft.solution" maxlength="4000" rows="3" :disabled="oplSaving || !!editing && !editing.canEdit" /></label><p v-if="editing?.issue.attachments.length">附件 {{ editing.issue.attachments.length }} 个 <button type="button" @click="oplDialog = false; emit('open', oplTarget, 'opl')">查看附件及完整问题</button></p><p v-if="oplSaveError" class="wr-error" role="alert">{{ oplSaveError }}</p><PanelActions><footer><button type="button" :disabled="oplSaving" @click="oplDialog = false">取消</button><button v-if="!editing || editing.canEdit || editing.canComplete" type="submit" :form="panelFormId + '-form-1'" :disabled="oplSaving">{{ oplSaving ? '保存中…' : '保存跟进' }}</button></footer></PanelActions></form>
    </el-dialog>
    <el-dialog v-model="noteDialog" title="记录项目跟进" width="560px" append-to-body :close-on-click-modal="false" :show-close="!noteSaving"><form :id="panelFormId + '-form-2'" class="wr-form" @submit.prevent="saveNote"><label>本次结论<textarea v-model="noteDraft.conclusion" maxlength="350" rows="3" required /></label><label>下一步行动<textarea v-model="noteDraft.action" maxlength="350" rows="2" /></label><div class="wr-form-grid"><label>负责人<select v-model="noteDraft.owner"><option value="">请选择</option><option v-for="user in users.filter(user => user.isActive)" :key="user.username" :value="user.username">{{ user.displayName }}</option></select></label><label>完成期限<el-date-picker v-model="noteDraft.due" type="date" format="YYYY-MM-DD" value-format="YYYY-MM-DD" aria-label="完成期限" placeholder="请选择日期" style="width:100%" /></label></div><p v-if="noteError" class="wr-error" role="alert">{{ noteError }}</p><PanelActions><footer><button type="button" :disabled="noteSaving" @click="noteDialog = false">取消</button><button type="submit" :form="panelFormId + '-form-2'" :disabled="noteSaving">{{ noteSaving ? '保存中…' : '保存记录' }}</button></footer></PanelActions></form></el-dialog>
  </section>
<el-dialog :model-value="!!attachmentPreview" :title="attachmentPreview?.name" width="min(1000px,95vw)" append-to-body @close="closeAttachmentPreview"><PdfDrawingViewer v-if="attachmentPreview?.pdf" :url="attachmentPreview.url" /><img v-else-if="attachmentPreview" :src="attachmentPreview.url" alt="问题附件预览" style="max-width:100%;max-height:75vh;display:block;margin:auto" /></el-dialog>
</template>
<style scoped>
.review-workbench{height:100%;min-height:0;min-width:0;display:grid;grid-template-columns:minmax(310px,28%) minmax(0,1fr);gap:5px;overflow:hidden;color:var(--pdm-text);font-size:12px;container-type:inline-size}
.review-workbench.is-maximized{position:fixed;inset:0;z-index:1000;padding:5px;background:var(--pdm-bg,var(--pdm-surface-muted));height:100dvh;box-sizing:border-box}
.review-workbench h2{font-size:var(--pdm-font-size-heading,14px);font-weight:600;margin:0}.review-workbench button,.review-workbench input,.review-workbench select,.wr-form input,.wr-form select,.wr-form textarea,.wr-form button{font:inherit;color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);padding:5px 7px;box-sizing:border-box;min-width:0}.review-workbench button,.wr-form button{cursor:pointer}.review-workbench button:disabled,.wr-form button:disabled{opacity:.5;cursor:default}.review-workbench button:focus-visible{outline:2px solid var(--pdm-blue);outline-offset:-2px}.review-workbench em{font-style:normal;color:var(--pdm-blue)}
.wr-projects{display:flex;flex-direction:column;min-width:0;min-height:0;overflow:hidden;gap:5px;padding:5px}.wr-projects>header,.wr-panel>header{display:flex;justify-content:space-between;align-items:center;gap:5px}.wr-projects>header{padding:5px}.wr-projects>footer{padding:5px;color:var(--pdm-muted);font-size:11px;flex-shrink:0}.wr-tabs{display:flex;gap:3px;min-width:0;flex-wrap:wrap}.wr-tabs button{border:0;background:transparent;padding:5px 8px;white-space:nowrap;color:var(--pdm-muted)}.wr-tabs button[aria-pressed=true]{color:var(--pdm-blue);background:var(--pdm-theme-accent-soft);box-shadow:inset 0 -2px var(--pdm-blue)}.wr-filter{display:flex;gap:5px;align-items:center}.wr-filter select{flex:1;width:0}.wr-project-list{flex:1;overflow:auto;min-height:0}.wr-project-list .wr-project{display:block;width:100%;padding:7px 5px;border:0;border-bottom:1px solid var(--pdm-border-soft);border-radius:0;text-align:left;border-left:3px solid transparent;background:transparent}.wr-project-list .wr-project.selected{background:var(--pdm-theme-accent-soft);border-left-color:var(--pdm-blue)}.wr-project:hover{background:var(--pdm-surface-muted)}.wr-project-first{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:6px;line-height:19px}.wr-project-first>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.wr-project-second time{font-size:11px;white-space:nowrap}.wr-project-second{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) auto;gap:5px;font-size:11px;line-height:18px;color:var(--pdm-muted)}.wr-project-second>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wr-content{min-width:0;min-height:0;display:grid;grid-template-rows:auto auto minmax(130px,1.6fr) minmax(100px,1fr) minmax(90px,.85fr);gap:5px;overflow:hidden}.wr-identity{padding:8px;display:flex;align-items:center;justify-content:space-between;gap:6px}.wr-identity>div:first-child{min-width:0}.wr-identity h2,.wr-identity p{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.wr-identity p{font-size:11px;margin:4px 0 0;color:var(--pdm-muted)}.wr-controls{display:flex;align-items:center;gap:5px;flex-shrink:0}.wr-controls button{display:flex;align-items:center;white-space:nowrap}.wr-status{display:grid;grid-template-columns:minmax(150px,1.5fr) auto auto minmax(140px,1fr);align-items:center;gap:12px;padding:8px;font-size:11px}.wr-status strong{display:block;font-size:13px;margin-top:5px;color:var(--pdm-blue)}.wr-status>div:first-child{min-width:0}.wr-status .wr-next{border:0;text-align:left;background:transparent;padding:0;overflow:hidden}.wr-next strong,.wr-next small{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.wr-panel{display:flex;flex-direction:column;min-height:0;min-width:0;overflow:hidden}.wr-panel>header{padding:5px 8px;border-bottom:1px solid var(--pdm-border);flex-shrink:0;min-height:28px}.wr-panel>header>div{display:flex;gap:5px}.wr-panel-tools{display:flex;align-items:center;justify-content:space-between;gap:5px;padding:3px 5px;flex-shrink:0}.wr-panel-tools>small{color:var(--pdm-muted);font-size:10px;white-space:nowrap}.wr-scroll{overflow:auto;min-height:0;flex:1}.wr-scroll table{border-collapse:collapse;width:100%;font-size:12px;min-width:660px}.wr-scroll th,.wr-scroll td{border-bottom:1px solid var(--pdm-border-soft);padding:5px 7px;text-align:center;white-space:nowrap;max-width:220px;overflow:hidden;text-overflow:ellipsis}.wr-scroll th{background:var(--pdm-table-header);font-weight:500;position:sticky;top:0;z-index:1}.wr-scroll tr:hover td{background:var(--pdm-theme-accent-soft)}.wr-scroll td button{border:0;background:transparent;color:var(--pdm-blue);padding:2px 5px}.wr-scroll .wr-description{max-width:200px}.wr-scroll .wr-note-content{max-width:400px;white-space:nowrap}.wr-empty{padding:12px;margin:0;color:var(--pdm-muted);text-align:center}.wr-no-selection{display:grid;place-items:center}.wr-error{color:var(--pdm-danger);padding:6px;margin:0;font-size:12px}.danger{color:var(--pdm-danger)!important}.warning{color:var(--pdm-orange)!important}
.wr-form{display:grid;gap:12px;font-size:12px;color:var(--pdm-text)}.wr-form label{display:grid;gap:5px}.wr-form-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}.wr-form footer{display:flex;gap:8px;justify-content:flex-end}.wr-form textarea{resize:vertical}.wr-form button[type=submit]{background:var(--pdm-blue);color:var(--pdm-on-accent,#fff)}
@container(max-width:1100px){.wr-content{grid-template-rows:auto auto minmax(130px,1.6fr) minmax(90px,1fr) minmax(85px,.85fr)}.wr-identity{align-items:flex-start;flex-wrap:wrap}.wr-status{gap:6px;grid-template-columns:minmax(100px,1fr) auto auto}.wr-next{grid-column:1/-1;display:flex;align-items:center;gap:7px}.wr-status .wr-next strong{margin:0}.wr-panel-tools{flex-wrap:wrap}.wr-panel-tools>small{display:none}.wr-panel>header{flex-wrap:wrap}}
@media(max-width:1000px){.review-workbench{grid-template-columns:280px minmax(0,1fr)}.wr-project-second{font-size:10px}.wr-project-first{font-size:11px}.wr-panel .wr-tabs button{padding:4px;font-size:11px}}
.review-workbench { grid-template-columns: 200px minmax(0, 1fr); }
.wr-content { grid-template-columns: repeat(2, minmax(0, 1fr)); grid-template-rows: auto 68px minmax(130px, 1.6fr) minmax(130px, 1fr); }
.wr-content > :is(.wr-identity, .wr-status, .wr-opl) { grid-column: 1 / -1; }
@container(max-width:900px) { .wr-content { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto 68px minmax(130px, 1.6fr) minmax(100px, 1fr) minmax(100px, 1fr); } }
.wr-status { height: 68px; min-height: 68px; grid-template-columns: minmax(0, 5fr) repeat(3, minmax(0, 1fr)) minmax(0, 2fr); gap: 8px; }



.wr-status .wr-next { grid-column: auto; display: flex; flex-direction: column; justify-content: center; align-items: stretch; gap: 4px; height: 40px; min-width: 0; }
.wr-next-title { display: block; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; line-height: 18px; }
.wr-status .wr-next strong { display: inline; margin: 0; font-size: 12px; }
.wr-next small { line-height: 16px; }
@container(max-width:900px) { .wr-status { grid-template-columns: minmax(0, 5fr) repeat(3, minmax(0, 1fr)) minmax(0, 2fr); gap: 5px; } }
.wr-status > div, .wr-status strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }


.wr-phases{display:flex;gap:0;width:100%;height:42px;padding-top:4px;white-space:nowrap;color:var(--pdm-muted)}
.wr-phases>span{position:relative;flex:1;min-width:0;display:flex;flex-direction:column;align-items:center;gap:7px;border:0;padding:0;background:transparent}
.wr-phases>span:not(:first-child)::before{content:'';position:absolute;top:8px;left:-50%;width:100%;height:2px;background:var(--pdm-border);z-index:0}
.wr-phases>span.done::before,.wr-phases>span.current::before{background:var(--pdm-blue)}
.wr-phases i{position:relative;z-index:1;box-sizing:border-box;display:block;flex-shrink:0;width:18px;height:18px;border:2px solid var(--pdm-border);border-radius:50%;background:var(--pdm-surface)}
.wr-phases .done i{border-color:var(--pdm-blue)}
.wr-phases .current i{border-color:var(--pdm-blue);background:var(--pdm-blue);box-shadow:0 0 0 2px var(--pdm-surface),0 0 0 4px var(--pdm-blue)}
.wr-phases small{display:block;max-width:100%;overflow:hidden;text-overflow:ellipsis;font-size:11px;line-height:16px;color:var(--pdm-muted)}
.wr-status>div:first-child{overflow:visible}
.wr-projects > .wr-tabs { flex-wrap: nowrap; gap: 4px; align-items: stretch; }
.wr-projects > .wr-tabs button { min-width: 0; height: 36px; padding: 0 4px; border-bottom: 3px solid transparent; border-radius: 0; font-size: 12px; font-weight: 600; background: transparent; }
.wr-projects > .wr-tabs button[aria-pressed=true] { color: var(--pdm-blue); border-bottom-color: var(--pdm-blue); background: transparent; box-shadow: none; }
.review-workbench .wr-projects, .review-workbench .wr-projects :is(h2, em, button, input, select, label, span, b, time, footer),
.review-workbench .wr-content .wr-scroll, .review-workbench .wr-content .wr-scroll :is(table, th, td, button, span, small, p) { font-size: 11px; }
.wr-projects > .wr-tabs button { flex: 1 1 0; width: 0; text-align: center; }
.wr-project-list .wr-project.selected { border-left-color: transparent; border-radius: 0; background: var(--pdm-surface-muted); box-shadow: inset 5px 0 0 var(--shell-brand); }
.wr-project-list .wr-project.selected .wr-project-first b { color: var(--pdm-blue); }
.review-workbench .wr-tabs { gap: 4px; }
.review-workbench .wr-tabs button { border: 0; border-bottom: 3px solid transparent; border-radius: 0; background: transparent; box-shadow: none; font-weight: 600; }
.review-workbench .wr-tabs button[aria-pressed=true] { color: var(--pdm-blue); background: transparent; border-bottom-color: var(--pdm-blue); box-shadow: none; }
.review-workbench .wr-projects > .wr-tabs button { font-size: 12px; }
.wr-opl .pdm-opl-ratio-table:not(.pdm-fit-columns) > colgroup > col { width: var(--opl-column-width) !important; }
.wr-opl .wr-scroll th, .wr-opl .wr-scroll td { text-align: center; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.wr-opl .wr-scroll tbody tr { height: 30px; }
.wr-opl-attachment { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
