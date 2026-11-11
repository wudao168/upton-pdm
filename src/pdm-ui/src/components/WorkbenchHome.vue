<script setup lang="ts">
import PersonName from './PersonName.vue'
import { useProjectCenterAccess } from '../projectCenterAccess'
const { write: projectWritable, canViewTab } = useProjectCenterAccess()
import ProjectTodoFields from './ProjectTodoFields.vue'
import { CheckCircle2, ChevronRight, FolderTree, ListTodo, RotateCw as RefreshCw, TriangleAlert } from '@lucide/vue'
import { addProjectManagerNote, createProjectTodo, getMaterialRelationCompleteness, readProjectOverviewRecords, listProjectPlanVersions, readProjectPlanPortfolio, readProjectRiskOverview, readProjectValidationPlan } from '../api'
import OverviewTimeline from './OverviewTimeline.vue'
import { chinaToday } from '../projectCalendar'
import { projectRiskRows, type ProjectAttentionDetail, type ProjectRiskOverviewProject } from '../projectAttention'
import { ElMessage } from '../statusMessage'
import { resolveUserDisplayName } from '../userDisplay'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { AuditEntry, DocumentNode, DrawingReviewPackage, MaterialCodeApplication, MaterialRelationCompleteness, PdmUser, ProjectPlan, ProjectPlanPortfolio, ProjectSummary, ProjectValidationPlan, ReleasePackageSummary } from '../types'

const props = withDefaults(defineProps<{
  active?: boolean
  project: ProjectSummary
  projects: ProjectSummary[]
  users: PdmUser[]
  selected: DocumentNode
  currentUsername?: string
  hasDocuments: boolean
  modelCount: number
  drawingCount: number
  bomPendingCount: number
  standardCount: number
  nonStandardCount: number
  electricalCount: number
  standardItemIds: string[]
  nonStandardItemIds: string[]
  electricalItemIds: string[]
  drawingReviews: DrawingReviewPackage[]
  materialApplications: MaterialCodeApplication[]
  releasePackages: ReleasePackageSummary[]
  releasePackage: ReleasePackageSummary | null
  pending: boolean
  token: string
  canAddManagerNote: boolean
}>(), { active: true })

const emit = defineEmits<{ documents: []; bom: []; projectPlan: [taskId?: string]; openPortfolioPlan: [projectId: string]; validationPlan: []; release: [];
  openRisk: [projectId: string, tab: ProjectAttentionDetail['tab']] }>()

const activeProject = computed(() => props.projects.find(item => item.id === props.project.id) ?? props.project)
const rootProject = computed(() => {
  const rootId = activeProject.value.rootProjectId ?? activeProject.value.parentProjectId
  return rootId ? props.projects.find(item => item.id === rootId) ?? activeProject.value : activeProject.value
})

const recordProjects = computed(() => [...new Map([rootProject.value, ...props.projects, activeProject.value]
  .filter(item => item.id === rootProject.value.id || item.parentProjectId === rootProject.value.id || item.rootProjectId === rootProject.value.id)
  .map(item => [item.id, item])).values()])
const projectRecordError = ref('')
const planPortfolio = ref<ProjectPlanPortfolio | null>(null)
const initialBaseline = ref<ProjectPlan | null>(null)
const baselineUnavailable = ref(false)
const validationPlan = ref<ProjectValidationPlan | null>(null)
const relationCompleteness = ref<MaterialRelationCompleteness | null>(null)
const managerNotes = ref<AuditEntry[]>([])
const projectRecordHistory = ref<AuditEntry[]>([])
const managerNoteDialogOpen = ref(false)
const managerNoteTarget = ref<{ id: string; code: string; name: string } | null>(null)
const managerNoteContent = ref('')
const savingManagerNote = ref(false)
const todoDueDate = ref<string>()
const todoRecipients = ref<string[]>([])
let overviewRequestId = 0

const riskProjects = ref<ProjectRiskOverviewProject[]>([])
const riskToday = ref(chinaToday())
const risksPending = ref(false)
const risksFailed = ref(false)
const canReadRisks = computed(() => activeProject.value.canReadContent && canViewTab('overview'))
const riskRows = computed(() => projectRiskRows(riskProjects.value, riskToday.value).filter(row => canViewTab(row.tab)))
const activeRisk = ref<{ projectId: string; key: string }>()
const selectedRisk = computed(() => riskRows.value.find(row => row.projectId === activeRisk.value?.projectId && row.key === activeRisk.value?.key))
const riskDetailOpen = computed({
  get: () => Boolean(selectedRisk.value),
  set: (open: boolean) => { if (!open) activeRisk.value = undefined },
})
const riskTabLabels = { 'project-plan': '计划', procurement: '备料', release: '发布' } as const

function openRiskPage() {
  const risk = selectedRisk.value
  if (!risk) return
  activeRisk.value = undefined
  emit('openRisk', risk.projectId, risk.tab)
}
const risksIncomplete = computed(() => risksFailed.value || riskProjects.value.some(item => item.incomplete))
let riskRequest: AbortController | undefined

async function loadProjectRisks() {
  riskRequest?.abort()
  if (!props.token || !canReadRisks.value || !props.active) {
    risksPending.value = false
    return
  }
  const request = new AbortController()
  riskRequest = request
  risksPending.value = true
  risksFailed.value = false
  try {
    const data = await readProjectRiskOverview(activeProject.value.id, props.token, request.signal)
    if (!request.signal.aborted) {
      riskProjects.value = data
      riskToday.value = chinaToday()
    }
  } catch {
    if (!request.signal.aborted) {
      riskProjects.value = []
      risksFailed.value = true
    }
  } finally {
    if (!request.signal.aborted) {
      risksPending.value = false
      if (!selectedRisk.value) activeRisk.value = undefined
    }
  }
}

watch([() => activeProject.value.id, () => props.token, () => props.active, canReadRisks], () => {
  activeRisk.value = undefined
  riskProjects.value = []
  risksFailed.value = false
  void loadProjectRisks()
}, { immediate: true })

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
}

async function fetchOperationalSummary() {
  const requestId = ++overviewRequestId, token = props.token
  if (!token) {
    initialBaseline.value = null; baselineUnavailable.value = false; planPortfolio.value = null
    validationPlan.value = null; relationCompleteness.value = null
    managerNotes.value = []; projectRecordHistory.value = []; projectRecordError.value = ''
    return
  }
  const visibleProjects = recordProjects.value
  const planRead = (async () => {
    const [planResult] = await Promise.allSettled([readProjectPlanPortfolio(rootProject.value.id, token)])
    if (requestId !== overviewRequestId) return
    planPortfolio.value = planResult.status === 'fulfilled' ? planResult.value : null
    if (planResult.status === 'fulfilled') {
      const current = activePlan.value
      if (current?.baselineVersion) {
        try {
          const versions = await listProjectPlanVersions(current.projectId, token)
          if (requestId !== overviewRequestId) return
          baselineUnavailable.value = false
          initialBaseline.value = [...versions.map(item => item.snapshot), current]
            .filter(item => item.baselineVersion > 0)
            .sort((a, b) => a.baselineVersion - b.baselineVersion || a.updatedAt.localeCompare(b.updatedAt))[0] ?? null
          if (initialBaseline.value?.baselineVersion !== 1) { initialBaseline.value = null; baselineUnavailable.value = true }
        } catch {
          if (requestId !== overviewRequestId) return
          baselineUnavailable.value = true
        }
      } else { initialBaseline.value = null; baselineUnavailable.value = false }
    } else { initialBaseline.value = null; baselineUnavailable.value = false }
  })()
  const validationRead = readProjectValidationPlan(activeProject.value.id, token).then(value => {
    if (requestId === overviewRequestId) validationPlan.value = value
  }, () => { if (requestId === overviewRequestId) validationPlan.value = null })
  const relationRead = (projectWritable.value ? getMaterialRelationCompleteness(activeProject.value.id, token) : Promise.resolve(null)).then(value => {
    if (requestId === overviewRequestId) relationCompleteness.value = value
  }, () => { if (requestId === overviewRequestId) relationCompleteness.value = null })
  const recordsRead = (canViewTab('records') ? readProjectOverviewRecords(activeProject.value.id, token) : Promise.resolve([])).then(records => {
    if (requestId !== overviewRequestId) return
    const visibleIds = new Set(visibleProjects.map(item => item.id.toLowerCase()))
    const projectAudits = [...new Map(records.filter(entry => visibleIds.has(entry.entityId.toLowerCase())).map(entry => [entry.id, entry])).values()]
      .sort((left, right) => right.occurredAt.localeCompare(left.occurredAt))
    managerNotes.value = projectAudits.filter(entry => entry.action === 'project.manager-note').slice(0, 30)
    projectRecordHistory.value = projectAudits.filter(isProjectRecordHistoryEntry).slice(0, 50)
    projectRecordError.value = ''
  }, () => { if (requestId === overviewRequestId) projectRecordError.value = '项目记录读取失败，请刷新项目概览重试。' })
  await Promise.all([planRead, validationRead, relationRead, recordsRead])
}

const refreshingOverview = ref(false)
let overviewTimer: number | undefined
async function loadOperationalSummary() {
  refreshingOverview.value = true
  const requestId = overviewRequestId + 1
  try { await fetchOperationalSummary() }
  finally { if (requestId === overviewRequestId) refreshingOverview.value = false }
}
watch([() => props.project.id, () => props.token, () => recordProjects.value.map(item => item.id).join('|')], () => {
  managerNotes.value = []; projectRecordHistory.value = []; projectRecordError.value = ''
  managerNoteDialogOpen.value = false; managerNoteTarget.value = null
  initialBaseline.value = null
  baselineUnavailable.value = false
  return loadOperationalSummary()
}, { immediate: true })
function refreshOverview() {
  void loadOperationalSummary()
  if (!risksPending.value) void loadProjectRisks()
}
onMounted(() => {
  overviewTimer = window.setInterval(() => {
    if (props.active && !document.hidden && !refreshingOverview.value) refreshOverview()
  }, 300_000)
})
onBeforeUnmount(() => { window.clearInterval(overviewTimer); ++overviewRequestId; riskRequest?.abort() })

function assignedPeople(usernames: Array<string | undefined>) {
  const uniqueUsernames = [...new Set(usernames.flatMap(item => item?.split('、') ?? []).map(item => item.trim()).filter((item): item is string => Boolean(item)))]
  return uniqueUsernames.map(username => ({
    username,
    name: personName(username),
  }))
}

const stageNames: Record<string, string> = {
  Design: '设计', MaterialPreparation: '备料', Assembly: '装配', Commissioning: '调试',
  ClientCommissioning: '客户端调试', AcceptanceProgress: '验收推进', FinalAcceptance: '终验收',
  Paused: '暂停', Cancelled: '取消', Terminated: '终止',
}
const activePlanItem = computed(() => planPortfolio.value?.projects?.find(item => item.projectId === activeProject.value.id)
  ?? planPortfolio.value?.projects?.find(item => item.isRoot)
  ?? null)
const activePlan = computed(() => activePlanItem.value?.plan ?? null)
function plannedStage(item?: ProjectPlanPortfolio['projects'][number] | null) {
  if (!item?.hasPlan) return undefined
  const isPlanStage = (code?: string) => Boolean(code && (Object.hasOwn(stageNames, code)
    || item.plan?.stages?.some(stage => stage.code === code)))
  return isPlanStage(item.currentStage) ? item.currentStage
    : isPlanStage(item.plan?.currentStage) ? item.plan?.currentStage : undefined
}

function plannedStageLabel(item?: ProjectPlanPortfolio['projects'][number] | null, stage = plannedStage(item)) {
  if (!stage) return '未排程'
  return item?.plan?.stages?.find(definition => definition.code === stage)?.name ?? stageNames[stage] ?? '未排程'
}

const projectStageLabel = computed(() => plannedStageLabel(activePlanItem.value))
const projectProgress = computed(() => activePlanItem.value?.hasPlan
  ? activePlanItem.value.completionPercent
  : activeProject.value.id === rootProject.value.id && planPortfolio.value?.projects?.some(item => item.hasPlan)
    ? planPortfolio.value.completionPercent
    : null)
const projectHealth = computed<{ label: string; tone: 'success' | 'warning' | 'danger' }>(() => {
  if (!activePlanItem.value?.hasPlan) return { label: '计划未建立', tone: 'warning' }
  if (activePlanItem.value.isLagging) return { label: '已滞后', tone: 'danger' }
  if (activePlanItem.value.isAtRisk) return { label: '有风险', tone: 'warning' }
  return { label: '正常', tone: 'success' }
})

function displayDate(value?: string | null) {
  if (!value) return '—'
  return value.slice(0, 10).replaceAll('-', '/')
}

const projectFinish = computed(() => displayDate(activePlanItem.value?.forecastFinish
  ?? activePlanItem.value?.plannedFinish
  ?? (activeProject.value.id === rootProject.value.id ? planPortfolio.value?.plannedFinish : undefined)))

function addPlanDays(value: string, days: number) {
  const [year, month, day] = value.slice(0, 10).split('-').map(Number)
  const date = new Date(Date.UTC(year, month - 1, day))
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

function dayDifference(start: string, finish: string) {
  return Math.round((new Date(`${finish}T00:00:00`).getTime() - new Date(`${start}T00:00:00`).getTime()) / 86_400_000)
}

const shippingDate = computed(() => {
  const plan = activePlan.value
  if (!plan) return ''
  const deliveryStages = new Set((plan.stages ?? []).filter(stage => stage.participatesInDelivery !== false).map(stage => stage.code))
  const finishes = plan.tasks
    .filter(task => !plan.stages?.length || deliveryStages.has(task.stage))
    .map(task => task.plannedFinish)
    .filter(Boolean)
    .sort()
  return finishes.slice(-1)[0] ?? plan.plannedFinish ?? ''
})

const shippingCountdownState = computed(() => {
  if (!shippingDate.value) return { label: '距发货', value: '—', detail: '未排程', tone: 'neutral' }
  const days = dayDifference(new Date().toISOString().slice(0, 10), shippingDate.value)
  const detail = displayDate(shippingDate.value)
  if (days > 0) return { label: '距发货', value: String(days), detail, tone: 'info' }
  if (days === 0) return { label: '今日发货', value: '0', detail, tone: 'danger' }
  return { label: '已超期', value: String(Math.abs(days)), detail, tone: 'danger' }
})

function personName(username?: string | null) {
  return resolveUserDisplayName(props.users, username)
}

function phaseOwnerText(project: ProjectSummary, stage?: string) {
  const owners = project.phaseOwners ?? {}
  const usernames = stage === 'Design'
    ? (project.designers.length ? project.designers : projectDesignLeads(project))
    : stage === 'MaterialPreparation' ? [owners.StandardProcurement, owners.NonStandardProcurement]
      : stage === 'Assembly' ? [owners.MechanicalAssembly, owners.ElectricalAssembly]
        : stage === 'Commissioning' || stage === 'ClientCommissioning' ? [owners.ElectricalCommissioning]
          : stage === 'AcceptanceProgress' || stage === 'FinalAcceptance' ? [owners.Acceptance]
            : [project.primaryProjectManager ?? rootProject.value.primaryProjectManager]
  const names = assignedPeople(usernames).map(person => person.name)
  return names.length ? names.join('、') : '—'
}

function currentStageFinish(item: NonNullable<ProjectPlanPortfolio['projects'][number]>) {
  const stage = plannedStage(item)
  if (!stage) return ''
  const stageSchedule = item.plan?.stageSchedules?.find(schedule => schedule.stage === stage)
  if (stageSchedule) return addPlanDays(stageSchedule.startDate, Math.max(1, stageSchedule.durationDays) - 1)
  return item.plan?.tasks
    .filter(task => task.stage === stage && Boolean(task.plannedFinish))
    .map(task => task.plannedFinish)
    .sort()
    .slice(-1)[0] ?? ''
}

function remainingWorkPeriod(item: NonNullable<ProjectPlanPortfolio['projects'][number]>) {
  if (!item.hasPlan) return { label: '未排程', tone: 'neutral', basis: '当前阶段尚未建立计划' }
  const finish = currentStageFinish(item)
  if (!finish) return { label: '未排期', tone: 'neutral', basis: '当前阶段未设置计划完成日期' }
  const days = dayDifference(new Date().toISOString().slice(0, 10), finish)
  const basis = `当前阶段计划完成 ${displayDate(finish)}`
  if (days < 0) return { label: `逾期 ${Math.abs(days)} 天`, tone: 'danger', basis }
  if (days <= 7) return { label: `剩余 ${days} 天`, tone: 'warning', basis }
  return { label: `剩余 ${days} 天`, tone: 'success', basis }
}

function overdueTaskSummary(item: NonNullable<ProjectPlanPortfolio['projects'][number]>) {
  if (!item.hasPlan) return { lines: ['未排程'], tone: 'neutral', basis: '项目计划未建立' }
  const today = new Date().toISOString().slice(0, 10)
  const overdueTasks = (item.plan?.tasks ?? [])
    .filter(task => task.status !== 'Completed' && Boolean(task.plannedFinish) && dayDifference(task.plannedFinish, today) > 0)
    .sort((left, right) => right.plannedFinish.localeCompare(left.plannedFinish) || left.sortOrder - right.sortOrder)
  if (!overdueTasks.length) return { lines: ['正常'], tone: 'success', basis: '全部已到期子任务均已完成，或尚未到期' }
  const lines = overdueTasks.slice(0, 2).map(task => `${task.name} 延期 ${dayDifference(task.plannedFinish, today)} 天`)
  if (overdueTasks.length > 2) lines[1] = `${lines[1]}…`
  return {
    lines,
    tone: 'danger',
    basis: overdueTasks.map(task => `${task.name}：计划完成 ${displayDate(task.plannedFinish)}，延期 ${dayDifference(task.plannedFinish, today)} 天`).join('\n'),
  }
}

const portfolioRows = computed(() => (planPortfolio.value?.projects ?? []).map(item => {
  const project = props.projects.find(candidate => candidate.id === item.projectId)
  const stage = plannedStage(item)
  const currentTask = [...(item.plan?.tasks ?? [])]
    .filter(task => task.status !== 'Completed')
    .sort((left, right) => Number(right.status === 'InProgress') - Number(left.status === 'InProgress')
      || left.plannedFinish.localeCompare(right.plannedFinish)
      || left.sortOrder - right.sortOrder)[0]
  const engineers = project ? assignedPeople(project.designers).map(person => person.name) : []
  return {
    ...item,
    planProjectId: item.isRoot || item.hasPlan ? item.projectId : planPortfolio.value?.rootProjectId ?? item.projectId,
    project,
    stage,
    stageLabel: plannedStageLabel(item, stage),
    engineers: engineers.length ? engineers.join('、') : '—',
    owner: project ? phaseOwnerText(project, stage) : '—',
    remainingWorkPeriod: remainingWorkPeriod(item),
    overdueTaskSummary: overdueTaskSummary(item),
    currentTask,
    currentStageFinish: currentStageFinish(item),
  }
}))

function notesForProject(projectId: string) {
  return managerNotes.value.filter(note => note.entityId === projectId).slice(0, 1)
}

function isProjectRecordHistoryEntry(entry: AuditEntry) {
  return entry.action === 'project.manager-note' || entry.action === 'project.todo.create'
}

function projectRecordHistoryLabel(entry: AuditEntry) {
  const project = props.projects.find(item => item.id === entry.entityId)
  return project ? `${project.code} · ${project.name}` : entry.entityId
}

function projectRecordHistoryType(entry: AuditEntry) {
  return entry.action === 'project.todo.create' ? '待办推送' : '项目经理备注'
}

function projectRecordHistoryTime(entry: AuditEntry) {
  return new Date(entry.occurredAt).toLocaleString('zh-CN', { hour12: false })
}

function addProjectRecordHistory(entries: AuditEntry[]) {
  projectRecordHistory.value = [...entries, ...projectRecordHistory.value.filter(existing => !entries.some(entry => entry.id === existing.id))]
    .sort((left, right) => right.occurredAt.localeCompare(left.occurredAt))
    .slice(0, 50)
}

function openManagerNote(projectId: string, projectCode: string, projectName: string) {
  managerNoteTarget.value = { id: projectId, code: projectCode, name: projectName }
  managerNoteContent.value = ''
  todoDueDate.value = undefined
  todoRecipients.value = []
  managerNoteDialogOpen.value = true
}

async function saveManagerNote() {
  if (!props.canAddManagerNote) return ElMessage.warning('当前账号仅可查看项目记录。')
  const content = managerNoteContent.value.trim()
  const target = managerNoteTarget.value
  if (!target || !content) return ElMessage.warning('请输入项目备注')
  if (todoDueDate.value && !todoRecipients.value.length) return ElMessage.warning('设置待办截止日期时请选择接收人')
  savingManagerNote.value = true
  try {
    const entry = await addProjectManagerNote(target.id, content, props.token)
    if (todoRecipients.value.length) {
      await createProjectTodo(target.id, { content, dueDate: todoDueDate.value, recipientUsernames: todoRecipients.value }, props.token)
    }
    managerNotes.value = [entry, ...managerNotes.value.filter(note => note.id !== entry.id)]
      .sort((left, right) => right.occurredAt.localeCompare(left.occurredAt))
      .slice(0, 30)
    const records = [entry]
    if (todoRecipients.value.length) {
      const recipients = todoRecipients.value.map(personName).join('、')
      const dueDate = todoDueDate.value ? `；截止 ${todoDueDate.value}` : ''
      records.push({ ...entry, id: `${entry.id}:todo`, action: 'project.todo.create', detail: `创建待办并推送给 ${recipients}${dueDate}：${content}` })
    }
    addProjectRecordHistory(records)
    managerNoteDialogOpen.value = false
    ElMessage.success(todoRecipients.value.length ? `项目记录已保存，待办已推送给 ${todoRecipients.value.length} 人` : '项目备注已记录')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '项目备注保存失败') }
  finally { savingManagerNote.value = false }
}

const todoRecipientCandidates = computed(() => props.users
  .filter(user => user.isActive)
  .sort((left, right) => left.displayName.localeCompare(right.displayName, 'zh-CN')))

const latestReleasePackage = computed(() => props.releasePackage ?? [...props.releasePackages]
  .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))[0] ?? null)
const approvalCount = computed(() => {
  const drawing = props.drawingReviews.some(item => ['InReview', 'WritingProperties'].includes(item.state)) ? 1 : 0
  const validation = validationPlan.value?.state === 'PendingApproval' ? 1 : 0
  const releases = props.releasePackages.filter(item => /审批|Pending|Submitted|InReview/i.test(item.state)).length
  return drawing + validation + releases
})
const attentionCount = computed(() => props.bomPendingCount + (relationCompleteness.value?.incompleteGroupCount ?? 0))
const overviewAlerts = computed(() => [
  { key: 'attention', label: '待处理', value: attentionCount.value, tone: attentionCount.value ? 'warning' : 'neutral', open: () => emit('bom') },
  { key: 'approval', label: '审批中', value: approvalCount.value, tone: approvalCount.value ? 'info' : 'neutral', open: () => latestReleasePackage.value ? emit('release') : validationPlan.value?.state === 'PendingApproval' ? emit('validationPlan') : emit('documents') },
])

</script>

<template>
  <section class="pdm-workbench" aria-label="工作台主页面">
    <div class="pdm-overview-layout">
      <section class="pdm-panel pdm-overview-status" aria-label="项目状态与下一步">
        <header><span><CheckCircle2 :size="18" /></span><h2>项目状态与下一步</h2><p>{{ projectStageLabel }}阶段 · {{ projectHealth.label }} · 项目进度 {{ projectProgress === null ? '—' : `${projectProgress}%` }} · 计划完成 {{ projectFinish }}</p><button class="pdm-overview-refresh pdm-text-action" type="button" :disabled="refreshingOverview" aria-label="刷新项目概览" title="自动每5分钟更新，也可手动刷新" @click="refreshOverview"><RefreshCw :size="13" />刷新</button></header>
        <div class="pdm-overview-status__body">
          <OverviewTimeline :plan="activePlan" :baseline="initialBaseline" :baseline-unavailable="baselineUnavailable" :progress-tone="projectHealth.tone" @open-task="taskId => emit('projectPlan', taskId)" />
        </div>
      </section>
      <section class="pdm-panel pdm-overview-risk" aria-label="项目待办与风险">
        <header><span><ListTodo :size="18" /></span><h2>项目待办与风险</h2></header>
          <div class="pdm-overview-alerts">
            <article class="pdm-overview-shipping" :class="`is-${shippingCountdownState.tone}`" aria-label="发货倒计时"><span>{{ shippingCountdownState.label }}</span><strong>{{ shippingCountdownState.value }}<small v-if="shippingDate">天</small></strong><em>{{ shippingCountdownState.detail }}</em></article>
            <button v-for="alert in overviewAlerts" :key="alert.key" type="button" :class="`is-${alert.tone}`" :aria-label="`查看${alert.label}`" @click="alert.open">
              <span>{{ alert.label }}</span><strong>{{ alert.value }}</strong><em>查看{{ alert.label }} <ChevronRight :size="13" /></em>
            </button>
          </div>
      </section>

      <div class="pdm-overview-summary" aria-label="项目核心业务概览">
        <article v-if="canReadRisks" class="pdm-panel pdm-overview-project-risks" aria-label="项目风险" :aria-busy="risksPending">
          <header><span><TriangleAlert :size="18" /></span><h2>项目风险</h2></header>
          <div class="pdm-overview-project-risks__table-wrap">
            <table class="pdm-fit-columns">
              <colgroup><col class="risk-project"><col class="risk-type"><col><col class="risk-action"></colgroup>
              <thead><tr><th scope="col">项目号</th><th scope="col">风险类型</th><th scope="col">明细</th><th scope="col">操作</th></tr></thead>
              <tbody>
                <tr v-for="risk in riskRows" :key="`${risk.projectId}:${risk.key}`">
                  <td :title="risk.projectCode">{{ risk.projectCode }}</td>
                  <td class="risk-description" :title="risk.reason">{{ risk.reason }}</td>
                  <td :title="risk.subject">{{ risk.subject }}</td>
                  <td><button type="button" class="pdm-text-action" :aria-label="`查看 ${risk.projectCode} ${risk.reason} ${risk.subject}`" @click="activeRisk = { projectId: risk.projectId, key: risk.key }">查看</button></td>
                </tr>
                <tr v-if="!riskRows.length && !risksIncomplete"><td colspan="4" role="status">{{ risksPending ? '正在读取项目风险…' : '当前暂无风险' }}</td></tr>
              </tbody>
            </table>
          </div>
          <div v-if="risksIncomplete" class="pdm-overview-project-risks__notice" role="status">
            <span>{{ risksFailed ? '项目风险读取失败' : '部分项目风险暂未更新' }}</span>
            <button type="button" class="pdm-text-action" :disabled="risksPending" @click="loadProjectRisks">刷新</button>
          </div>
        </article>
      </div>

      <div class="pdm-overview-bottom">
        <article class="pdm-panel pdm-project-portfolio" aria-label="项目总览">
          <header><span><FolderTree :size="18" /></span><h2>项目总览</h2><p>当前主项目及全部子项目的阶段、负责人和计划风险</p><button v-if="!portfolioRows.length && token" type="button" class="pdm-text-action" @click="openManagerNote(activeProject.id, activeProject.code, activeProject.name)">查看记录</button><button type="button" class="pdm-overview-link" @click="emit('projectPlan')">进入项目计划 <ChevronRight :size="13" /></button></header>
          <div class="pdm-project-portfolio__table-wrap">
            <table><thead><tr><th>项目</th><th>执行</th><th>阶段</th><th>计划完成</th><th>剩余工期</th><th>进度</th><th>负责人</th><th>当前</th><th>状态</th><th>备注</th></tr></thead><tbody>
              <tr v-for="row in portfolioRows" :key="row.projectId" :class="{ 'is-root': row.isRoot }" tabindex="0" @click="emit('openPortfolioPlan', row.planProjectId)" @keydown.enter.self.prevent="emit('openPortfolioPlan', row.planProjectId)" @keydown.space.self.prevent="emit('openPortfolioPlan', row.planProjectId)">
                <td><strong>{{ row.projectCode }}</strong><small>{{ row.projectName }}</small></td><td :class="{ 'is-pending': row.engineers === '—' }"><PersonName :name="row.engineers" /></td><td>{{ row.stageLabel }}</td><td :title="row.currentStageFinish ? `当前主任务“${row.stageLabel}”计划完成 ${displayDate(row.currentStageFinish)}` : '当前主任务未排期'">{{ displayDate(row.currentStageFinish) }}</td><td><span :class="`is-${row.remainingWorkPeriod.tone}`" :title="row.remainingWorkPeriod.basis">{{ row.remainingWorkPeriod.label }}</span></td><td><span class="pdm-project-portfolio__progress"><em>{{ row.hasPlan ? `${row.completionPercent}%` : '—' }}</em><i><em :style="{ width: `${row.completionPercent}%` }" /></i></span></td><td :class="{ 'is-pending': row.owner === '—' }"><PersonName :name="row.owner" /></td><td class="pdm-project-portfolio__task" :title="row.currentTask?.name"><strong v-if="row.currentTask">{{ row.currentTask.name }}</strong><small v-if="row.currentTask"><PersonName :name="personName(row.currentTask.assignee)" /> · {{ displayDate(row.currentTask.plannedFinish) }}</small><span v-else>暂无待办</span></td><td><span class="pdm-project-portfolio__overdue" :class="`is-${row.overdueTaskSummary.tone}`" :title="row.overdueTaskSummary.basis"><i v-for="line in row.overdueTaskSummary.lines" :key="line">{{ line }}</i></span></td><td class="pdm-project-portfolio__notes"><ol><li v-for="note in notesForProject(row.projectId)" :key="note.id"><span :title="note.detail">{{ note.detail }}</span></li><li v-if="!notesForProject(row.projectId).length" class="is-empty">暂无备注</li></ol><button type="button" class="pdm-text-action" :aria-label="`${canAddManagerNote ? '维护' : '查看'} ${row.projectCode} 的记录`" v-if="canViewTab('records')" @click.stop="openManagerNote(row.projectId, row.projectCode, row.projectName)">记录</button></td>
              </tr>
              <tr v-if="!portfolioRows.length" class="is-empty"><td colspan="10">尚未加载项目计划总览</td></tr>
            </tbody></table>
          </div>
        </article>

      </div>
    </div>

    <el-drawer v-model="riskDetailOpen" title="项目风险详情" size="min(480px, calc(100vw - 24px))" append-to-body>
      <template v-if="selectedRisk">
        <section class="risk-detail" aria-label="风险明细">
          <span class="risk-description">{{ selectedRisk.reason }}</span>
          <h3>{{ selectedRisk.subject }}</h3>
          <dl>
            <dt>项目号</dt><dd>{{ selectedRisk.projectCode }}</dd>
            <template v-for="field in selectedRisk.fields" :key="field.label">
              <dt>{{ field.label }}</dt><dd>{{ field.value === '' ? '—' : field.value ?? '—' }}</dd>
            </template>
          </dl>
        </section>
      </template>
      <template #footer>
        <button type="button" class="pdm-secondary-action" @click="riskDetailOpen = false">关闭</button>
        <button v-if="selectedRisk" type="button" class="pdm-primary-action" data-pdm-primary @click="openRiskPage">前往{{ riskTabLabels[selectedRisk.tab] }}</button>
      </template>
    </el-drawer>

    <el-dialog v-model="managerNoteDialogOpen" class="pdm-manager-note-dialog" :title="managerNoteTarget ? `${canAddManagerNote ? '维护' : '查看'}项目记录 · ${managerNoteTarget.code}` : '项目记录'" width="520px" append-to-body>
      <template v-if="canAddManagerNote"><label class="pdm-dialog-field">备注内容<textarea v-model="managerNoteContent" class="pdm-manager-note-dialog__input" maxlength="1000" rows="5" placeholder="记录进度、风险或需要跟进的事项" aria-label="项目备注内容" /></label>
      <ProjectTodoFields v-model:due-date="todoDueDate" v-model:recipients="todoRecipients" :users="todoRecipientCandidates" :pending="savingManagerNote" /></template>
      <section class="pdm-project-record-history" aria-label="当前项目及子项目历史记录">
        <header><strong>历史记录</strong><span>当前项目及子项目</span></header>
        <p v-if="projectRecordError" role="alert">{{ projectRecordError }}</p>
        <div v-if="projectRecordHistory.length" class="pdm-project-record-history__list">
          <article v-for="entry in projectRecordHistory" :key="entry.id">
            <header><strong>{{ projectRecordHistoryLabel(entry) }}</strong><span>{{ projectRecordHistoryType(entry) }}</span></header>
            <p><span><PersonName :name="personName(entry.actor)" /></span><time>{{ projectRecordHistoryTime(entry) }}</time></p>
            <small>{{ entry.detail }}</small>
          </article>
        </div>
        <p v-else-if="!projectRecordError" class="pdm-project-record-history__empty">暂无历史记录</p>
      </section>
      <template #footer><el-button @click="managerNoteDialogOpen=false">{{ canAddManagerNote ? '取消' : '关闭' }}</el-button><el-button v-if="canAddManagerNote" type="primary" :loading="savingManagerNote" @click="saveManagerNote">保存记录</el-button></template>
    </el-dialog>


  </section>
</template>

<style scoped>
.pdm-overview-project-risks {
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.pdm-overview-project-risks > header {
  min-height: 42px;
  flex: none;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 13px;
  border-bottom: 1px solid var(--pdm-border-soft);
}
.pdm-overview-project-risks > header > span {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 7px;
  color: var(--pdm-orange);
  background: var(--pdm-orange-soft);
}
.pdm-overview-project-risks h2 {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
}
.pdm-overview-project-risks__table-wrap {
  min-height: 0;
  flex: 1;
  overflow: auto;
}
.pdm-overview-project-risks table {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  font-size: 11px;
}
.pdm-overview-project-risks th {
  height: var(--pdm-overview-table-header-height);
  position: sticky;
  top: 0;
  background: var(--pdm-bg);
  color: var(--pdm-muted);
  font-weight: 500;
}
.pdm-overview-project-risks td {
  height: var(--pdm-detail-row-height);
  border-top: 1px solid var(--pdm-border-soft);
}
.pdm-overview-project-risks :is(th, td) {
  padding: 0 5px;
  text-align: center;
  vertical-align: middle;
  white-space: nowrap;
}
.risk-project { width: 88px; }
.risk-type { width: 118px; }
.risk-action { width: 48px; }
.risk-description { color: var(--pdm-orange); }
.risk-detail h3 {
  margin: 6px 0 16px;
  font-size: 16px;
  overflow-wrap: anywhere;
}
.risk-detail dl {
  display: grid;
  grid-template-columns: minmax(88px, max-content) minmax(0, 1fr);
  gap: 12px;
}
.risk-detail dt { color: var(--pdm-muted); }
.risk-detail dd {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.pdm-overview-project-risks__notice {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 4px;
  color: var(--pdm-muted);
  font-size: 12px;
}
@media (max-width: 1000px) {
  .pdm-overview-project-risks__table-wrap { max-height: 250px; }
}
@media (max-width: 480px) {
  .risk-project { width: 72px; }
  .risk-type { width: 96px; }
  .risk-action { width: 40px; }
}
</style>
