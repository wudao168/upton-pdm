<script setup lang="ts">
import { CheckCircle2, ChevronRight, FolderTree, ListTodo, RefreshCw, UsersRound } from '@lucide/vue'
import { addProjectManagerNote, createProjectTodo, getMaterialRelationCompleteness, listProjectAudit, listProjectPlanVersions, readProjectPlanPortfolio, readProjectValidationPlan } from '../api'
import OverviewTimeline from './OverviewTimeline.vue'
import { ElMessage } from '../statusMessage'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type { AuditEntry, DocumentNode, DrawingReviewPackage, DrawingReviewTarget, MainProjectStaffingInput, MaterialCodeApplication, MaterialRelationCompleteness, OrganizationDirectory, PdmUser, ProjectPhaseOwnerKey, ProjectPhaseOwners, ProjectPlan, ProjectPlanPortfolio, ProjectSummary, ProjectValidationPlan, ReleasePackageSummary, ReleaseScope } from '../types'

const props = defineProps<{
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
  organizationDirectory: OrganizationDirectory
  pending: boolean
  token: string
  canAddManagerNote: boolean
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
  onUpdatePhaseOwners: (projectId: string, phaseOwners: ProjectPhaseOwners) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ documents: []; bom: []; projectPlan: [taskId?: string]; validationPlan: []; procurement: []; release: [] }>()

const activeProject = computed(() => props.projects.find(item => item.id === props.project.id) ?? props.project)
const rootProject = computed(() => {
  if (!activeProject.value.parentProjectId) return activeProject.value
  return props.projects.find(item => item.id === activeProject.value.parentProjectId) ?? activeProject.value
})

const phaseOwnerDrawerOpen = ref(false)
const savingTeamConfiguration = ref(false)
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
const staffingForm = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const phaseOwnerDraft = reactive<ProjectPhaseOwners>({})
const designerDraft = ref<string[]>([])

const phaseOwnerDefinitions: Array<{ key: ProjectPhaseOwnerKey; label: string; detail: string; preferredRoles: string[] }> = [
  { key: 'StandardProcurement', label: '标准件采购', detail: '标准件询价、下单与到货协调', preferredRoles: ['ProcurementSpecialist', 'ProcurementManager', 'SupplyChain'] },
  { key: 'NonStandardProcurement', label: '非标件采购', detail: '非标件外协采购与交付跟进', preferredRoles: ['ProcurementSpecialist', 'ProcurementManager', 'SupplyChain'] },
  { key: 'NonStandardProduction', label: '非标件生产', detail: '机加、自制与外协生产协调', preferredRoles: ['ProductionManager', 'ProductionAssistant', 'MachiningSupervisor'] },
  { key: 'MechanicalAssembly', label: '机械装配', detail: '机械装配排程与现场协调', preferredRoles: ['AssemblySupervisor', 'AssemblyFitter', 'MechanicalManager'] },
  { key: 'ElectricalAssembly', label: '电气装配', detail: '电气接线与装配协调', preferredRoles: ['ElectricalSupervisor', 'AssemblyElectrician', 'ElectricalEngineer'] },
  { key: 'ElectricalCommissioning', label: '电气调试', detail: '电气调试与问题闭环', preferredRoles: ['CommissioningEngineer', 'ElectricalEngineer'] },
  { key: 'Acceptance', label: '验收', detail: '客户验收、整改与交付协调', preferredRoles: ['ProjectManager', 'ProductionManager', 'BusinessUnitManager'] },
]

function projectDesignLeads(project: ProjectSummary) {
  return project.designLeads?.length ? project.designLeads : project.designLead ? [project.designLead] : []
}

function isUnitWithin(unitId: string, divisionId: string) {
  let current = props.organizationDirectory.units.find(unit => unit.id === unitId)
  while (current) {
    if (current.id === divisionId) return true
    current = current.parentUnitId ? props.organizationDirectory.units.find(unit => unit.id === current!.parentUnitId) : undefined
  }
  return false
}

function divisionOfUser(username: string) {
  const memberships = props.organizationDirectory.memberships.filter(item => item.username === username).sort((left, right) => Number(right.isPrimary) - Number(left.isPrimary))
  for (const membership of memberships) {
    let current = props.organizationDirectory.units.find(unit => unit.id === membership.unitId)
    while (current) {
      if (current.kind === 'BusinessDivision') return current
      current = current.parentUnitId ? props.organizationDirectory.units.find(unit => unit.id === current!.parentUnitId) : undefined
    }
  }
}

function usersInExecutionUnit(role: string) {
  const executionUnitId = rootProject.value.executionUnitId
  if (!executionUnitId) return []
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => isUnitWithin(item.unitId, executionUnitId)).map(item => item.username))
  return props.organizationDirectory.users
    .filter(user => user.isActive && usernames.has(user.username) && [user.role, ...(user.roles ?? [])].includes(role))
    .sort((left, right) => left.displayName.localeCompare(right.displayName, 'zh-CN'))
}

const projectManagerCandidates = computed(() => usersInExecutionUnit('ProjectManager'))
const designLeadCandidates = computed(() => usersInExecutionUnit('Engineer'))
const phaseOwnerCandidates = computed(() => {
  const organizationId = props.project.organizationId ?? rootProject.value.organizationId
  if (!organizationId) return []
  const companyUnitIds = new Set(props.organizationDirectory.units.filter(unit => unit.organizationId === organizationId && unit.isActive).map(unit => unit.id))
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => companyUnitIds.has(item.unitId)).map(item => item.username))
  return props.organizationDirectory.users
    .filter(user => user.isActive && usernames.has(user.username))
    .map(user => {
      const division = divisionOfUser(user.username)
      return { ...user, divisionName: division?.name ?? '未归属事业部' }
    })
    .sort((left, right) => left.divisionName.localeCompare(right.divisionName, 'zh-CN') || left.displayName.localeCompare(right.displayName, 'zh-CN'))
})
const executionEngineerCandidates = computed(() => phaseOwnerCandidates.value.filter(user => [user.role, ...(user.roles ?? [])].some(role => ['Engineer', 'ElectricalEngineer', 'CommissioningEngineer', 'HardwareEngineer', 'MechanicalManager', 'TechnicalAssistant', 'ProcessReviewer', 'Approver'].includes(role))))

function preferredPhaseCandidates(preferredRoles: string[]) {
  return phaseOwnerCandidates.value.filter(user => [user.role, ...(user.roles ?? [])].some(role => preferredRoles.includes(role)))
}

function otherPhaseCandidates(preferredRoles: string[]) {
  const preferred = new Set(preferredPhaseCandidates(preferredRoles).map(user => user.username))
  return phaseOwnerCandidates.value.filter(user => !preferred.has(user.username))
}

async function fetchOperationalSummary() {
  const requestId = ++overviewRequestId
  const token = props.token
  if (!token) {
    initialBaseline.value = null
    baselineUnavailable.value = false
    planPortfolio.value = null
    validationPlan.value = null
    relationCompleteness.value = null
    managerNotes.value = []
    return
  }
  const [planResult, validationResult, relationResult] = await Promise.allSettled([
    readProjectPlanPortfolio(rootProject.value.id, token),
    readProjectValidationPlan(activeProject.value.id, token),
    getMaterialRelationCompleteness(activeProject.value.id, token),
  ])
  if (requestId !== overviewRequestId) return
  planPortfolio.value = planResult.status === 'fulfilled' ? planResult.value : null
  validationPlan.value = validationResult.status === 'fulfilled' ? validationResult.value : null
  relationCompleteness.value = relationResult.status === 'fulfilled' ? relationResult.value : null
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
        if (initialBaseline.value?.baselineVersion !== 1) {
          initialBaseline.value = null
          baselineUnavailable.value = true
        }
      } catch {
        if (requestId !== overviewRequestId) return
        baselineUnavailable.value = true
      }
    } else {
      initialBaseline.value = null
      baselineUnavailable.value = false
    }

    const noteResults = await Promise.allSettled(planResult.value.projects.map(item => listProjectAudit(item.projectId, token)))
    if (requestId !== overviewRequestId) return
    const projectAudits = noteResults.flatMap(result => result.status === 'fulfilled' ? result.value : [])
    managerNotes.value = projectAudits.filter(entry => entry.action === 'project.manager-note')
      .sort((left, right) => right.occurredAt.localeCompare(left.occurredAt))
      .slice(0, 30)
    projectRecordHistory.value = projectAudits.filter(isProjectRecordHistoryEntry)
      .sort((left, right) => right.occurredAt.localeCompare(left.occurredAt))
      .slice(0, 50)
  } else {
    managerNotes.value = []
    projectRecordHistory.value = []
  }
}

const refreshingOverview = ref(false)
let overviewTimer: number | undefined
async function loadOperationalSummary() {
  refreshingOverview.value = true
  const requestId = overviewRequestId + 1
  try { await fetchOperationalSummary() }
  finally { if (requestId === overviewRequestId) refreshingOverview.value = false }
}
watch(() => [props.project.id, props.token], () => {
  initialBaseline.value = null
  baselineUnavailable.value = false
  return loadOperationalSummary()
}, { immediate: true })
onMounted(() => { overviewTimer = window.setInterval(() => { if (!refreshingOverview.value) void loadOperationalSummary() }, 300_000) })
onBeforeUnmount(() => { window.clearInterval(overviewTimer); ++overviewRequestId })

function openTeamConfiguration() {
  staffingForm.primaryProjectManager = rootProject.value.primaryProjectManager ?? ''
  staffingForm.collaborativeProjectManagers = [...rootProject.value.collaborativeProjectManagers]
  staffingForm.designLeads = [...projectDesignLeads(rootProject.value)]
  for (const phase of phaseOwnerDefinitions) phaseOwnerDraft[phase.key] = activeProject.value.phaseOwners?.[phase.key] ?? ''
  designerDraft.value = [...activeProject.value.designers]
  phaseOwnerDrawerOpen.value = true
}

async function saveTeamConfiguration() {
  if (savingTeamConfiguration.value) return
  if (rootProject.value.canManageMainStaffing && (!staffingForm.primaryProjectManager || !staffingForm.designLeads.length))
    return ElMessage.warning('请选择一名项目经理和至少一名主设')
  savingTeamConfiguration.value = true
  try {
    if (rootProject.value.canManageMainStaffing) await props.onUpdateMainStaffing(rootProject.value.id, {
      primaryProjectManager: staffingForm.primaryProjectManager,
      collaborativeProjectManagers: [...staffingForm.collaborativeProjectManagers],
      designLeads: [...staffingForm.designLeads],
    })
    if (activeProject.value.canAssignDesigners) {
      await props.onUpdateDesigners(activeProject.value.id, [...designerDraft.value])
      await props.onUpdatePhaseOwners(activeProject.value.id, { ...phaseOwnerDraft })
    }
    phaseOwnerDrawerOpen.value = false
    ElMessage.success('项目团队配置已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '项目团队配置保存失败') }
  finally { savingTeamConfiguration.value = false }
}
function assignedPeople(usernames: Array<string | undefined>) {
  const uniqueUsernames = [...new Set(usernames.map(item => item?.trim()).filter((item): item is string => Boolean(item)))]
  return uniqueUsernames.map(username => ({
    username,
    name: props.users.find(user => user.username.localeCompare(username, undefined, { sensitivity: 'accent' }) === 0)?.displayName || username,
  }))
}

const staffingRows = computed(() => [
  { key: 'manager', stage: '项目管理', role: activeProject.value.parentProjectId ? '子项目负责人' : '项目经理', people: assignedPeople([activeProject.value.primaryProjectManager ?? rootProject.value.primaryProjectManager]) },
  { key: 'collaborative-managers', stage: '项目管理', role: '协同项目经理', people: assignedPeople(rootProject.value.collaborativeProjectManagers) },
  { key: 'design-lead', stage: '设计阶段', role: '主设', people: assignedPeople(projectDesignLeads(rootProject.value)) },
  { key: 'engineers', stage: '设计执行', role: '执行工程师', people: assignedPeople(activeProject.value.designers) },
  { key: 'downstream', stage: '后续阶段', role: '各部门负责人', people: [] },
])

function drawingTargetSummary(target: DrawingReviewTarget) {
  const count = target === 'Model3D' ? props.modelCount : props.drawingCount
  if (count === 0) return '无图档'
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '待发起审核'
  if (review.state === 'Stale') return '版本冲突'
  const states = review.items.map(item => target === 'Model3D' ? item.modelState : item.drawingState)
  const completed = states.filter(state => state === 'Approved' || state === 'Marked').length
  const changesRequested = states.filter(state => state === 'ChangesRequested').length
  if (states.length > 0 && completed === states.length) return '已批准'
  if (changesRequested > 0) return `已退回 ${changesRequested}`
  if (review.state === 'WritingProperties') return `已批准 ${completed}/${states.length}`
  return `待审核 ${completed}/${states.length}`
}

function latestReleaseFor(scopes: ReleaseScope[]) {
  return [...props.releasePackages]
    .filter(item => scopes.includes(item.scope))
    .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))[0]
}

function bomApprovalStatus(scopes: ReleaseScope[]) {
  const release = latestReleaseFor(scopes)
  if (!release) return '未发起'
  const currentStep = release.steps.find(step => step.status === 'current')
  return `${release.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
}

function materialApplicationStatus(itemIds: string[]) {
  const ids = new Set(itemIds)
  const applications = props.materialApplications.filter(item => item.bomItemId && ids.has(item.bomItemId))
  if (!applications.length) return '暂无'
  const pending = applications.filter(item => item.status === 'Pending').length
  const approved = applications.filter(item => item.status === 'Approved').length
  const rejected = applications.filter(item => item.status === 'Rejected').length
  const withdrawn = applications.filter(item => item.status === 'Withdrawn').length
  return [`待审${pending}`, `已批${approved}`, `退回${rejected}`, `撤回${withdrawn}`].filter((_, index) => [pending, approved, rejected, withdrawn][index] > 0).join(' · ')
}

const documentRows = computed(() => [
  { key: 'model', label: '3D图档', count: props.modelCount, approval: drawingTargetSummary('Model3D') },
  { key: 'drawing', label: '2D图纸', count: props.drawingCount, approval: drawingTargetSummary('Drawing2D') },
])

const bomRows = computed(() => [
  { key: 'standard', label: '标准件', count: props.standardCount, approval: bomApprovalStatus(['StandardLongLead', 'StandardFormal', 'StandardSupplement']), application: materialApplicationStatus(props.standardItemIds) },
  { key: 'non-standard', label: '非标件', count: props.nonStandardCount, approval: bomApprovalStatus(['NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement']), application: materialApplicationStatus(props.nonStandardItemIds) },
  { key: 'electrical', label: '电气', count: props.electricalCount, approval: bomApprovalStatus(['ElectricalFormal', 'ElectricalSupplement']), application: materialApplicationStatus(props.electricalItemIds) },
])

const bomApprovalSummary = computed(() => {
  if (!props.releasePackage) return 'BOM审批：未发起'
  const currentStep = props.releasePackage.steps.find(step => step.status === 'current')
  return `BOM审批：${props.releasePackage.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
})

const stageNames: Record<string, string> = {
  Design: '设计', MaterialPreparation: '备料', Assembly: '装配', Commissioning: '调试',
  ClientCommissioning: '客户端调试', AcceptanceProgress: '验收推进', FinalAcceptance: '终验收',
  Paused: '暂停', Cancelled: '取消', Terminated: '终止',
}
const activePlanItem = computed(() => planPortfolio.value?.projects?.find(item => item.projectId === activeProject.value.id)
  ?? planPortfolio.value?.projects?.find(item => item.isRoot)
  ?? null)
const activePlan = computed(() => activePlanItem.value?.plan ?? null)
const projectStage = computed(() => activePlanItem.value?.currentStage ?? activePlan.value?.currentStage ?? activeProject.value.stage)
const projectStageLabel = computed(() => activePlan.value?.stages?.find(item => item.code === projectStage.value)?.name
  ?? stageNames[projectStage.value ?? ''] ?? projectStage.value ?? '未排程')
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
  if (!username) return '待分配'
  return props.users.find(user => user.username.localeCompare(username, undefined, { sensitivity: 'accent' }) === 0)?.displayName ?? username
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
  return names.length ? names.join('、') : '待分配'
}

function currentStageFinish(item: NonNullable<ProjectPlanPortfolio['projects'][number]>) {
  const stage = item.currentStage ?? item.plan?.currentStage
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
  const stage = item.currentStage ?? project?.stage
  const currentTask = [...(item.plan?.tasks ?? [])]
    .filter(task => task.status !== 'Completed')
    .sort((left, right) => Number(right.status === 'InProgress') - Number(left.status === 'InProgress')
      || left.plannedFinish.localeCompare(right.plannedFinish)
      || left.sortOrder - right.sortOrder)[0]
  const engineers = project ? assignedPeople(project.designers).map(person => person.name) : []
  return {
    ...item,
    project,
    stage,
    stageLabel: stageNames[stage ?? ''] ?? stage ?? '未排程',
    engineers: engineers.length ? engineers.join('、') : '待分配',
    owner: project ? phaseOwnerText(project, stage) : '待分配',
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
  if (!props.canAddManagerNote) return ElMessage.warning('仅项目经理可以维护项目备注')
  managerNoteTarget.value = { id: projectId, code: projectCode, name: projectName }
  managerNoteContent.value = ''
  todoDueDate.value = undefined
  todoRecipients.value = []
  managerNoteDialogOpen.value = true
}

async function saveManagerNote() {
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

const teamRows = computed(() => [
  ...staffingRows.value.filter(row => ['manager', 'design-lead'].includes(row.key)),
  ...phaseOwnerDefinitions.map(phase => ({ key: phase.key, role: phase.label, people: assignedPeople([activeProject.value.phaseOwners?.[phase.key]]) })),
])
const teamRowPairs = computed(() => Array.from({ length: Math.ceil(teamRows.value.length / 2) }, (_, index) => teamRows.value.slice(index * 2, index * 2 + 2)))
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
        <header><span><CheckCircle2 :size="18" /></span><h2>项目状态与下一步</h2><p>{{ projectStageLabel }}阶段 · {{ projectHealth.label }} · 项目进度 {{ projectProgress === null ? '—' : `${projectProgress}%` }} · 计划完成 {{ projectFinish }}</p><button class="pdm-overview-refresh pdm-text-action" type="button" :disabled="refreshingOverview" aria-label="刷新项目概览" title="自动每5分钟更新，也可手动刷新" @click="loadOperationalSummary"><RefreshCw :size="13" />{{ refreshingOverview ? '刷新中' : '刷新' }}</button></header>
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
        <article class="pdm-panel pdm-overview-team" aria-label="项目团队">
          <header><span><UsersRound :size="18" /></span><h2>项目团队</h2><span class="pdm-overview-team__actions"><button v-if="rootProject.canManageMainStaffing || activeProject.canAssignDesigners" type="button" class="pdm-text-action" @click="openTeamConfiguration">配置团队</button></span></header>
          <div class="pdm-overview-team__table-wrap"><table class="pdm-overview-team__table"><thead><tr><th>职责</th><th>负责人</th><th>职责</th><th>负责人</th></tr></thead><tbody><tr v-for="pair in teamRowPairs" :key="pair[0]?.key"><template v-for="row in pair" :key="row.key"><td class="is-role">{{ row.role }}</td><td :class="{ 'is-pending': !row.people.length }">{{ row.people.map(person => person.name).join('、') || '待分配' }}</td></template></tr></tbody></table></div>
        </article>
        <article class="pdm-panel pdm-overview-reserved" aria-label="预留区域" />
      </div>

      <div class="pdm-overview-bottom">
        <article class="pdm-panel pdm-project-portfolio" aria-label="项目总览">
          <header><span><FolderTree :size="18" /></span><h2>项目总览</h2><p>当前主项目及全部子项目的阶段、负责人和计划风险</p><button type="button" class="pdm-overview-link" @click="emit('projectPlan')">进入项目计划 <ChevronRight :size="13" /></button></header>
          <div class="pdm-project-portfolio__table-wrap">
            <table><thead><tr><th>项目</th><th>执行</th><th>阶段</th><th>计划完成</th><th>剩余工期</th><th>进度</th><th>负责人</th><th>当前</th><th>状态</th><th>备注</th></tr></thead><tbody>
              <tr v-for="row in portfolioRows" :key="row.projectId" :class="{ 'is-root': row.isRoot }" tabindex="0" @click="emit('projectPlan')" @keydown.enter="emit('projectPlan')">
                <td><strong>{{ row.projectCode }}</strong><small>{{ row.projectName }}</small></td><td :class="{ 'is-pending': row.engineers === '待分配' }">{{ row.engineers }}</td><td>{{ row.stageLabel }}</td><td :title="row.currentStageFinish ? `当前主任务“${row.stageLabel}”计划完成 ${displayDate(row.currentStageFinish)}` : '当前主任务未排期'">{{ displayDate(row.currentStageFinish) }}</td><td><span :class="`is-${row.remainingWorkPeriod.tone}`" :title="row.remainingWorkPeriod.basis">{{ row.remainingWorkPeriod.label }}</span></td><td><span class="pdm-project-portfolio__progress"><em>{{ row.hasPlan ? `${row.completionPercent}%` : '—' }}</em><i><em :style="{ width: `${row.completionPercent}%` }" /></i></span></td><td :class="{ 'is-pending': row.owner === '待分配' }">{{ row.owner }}</td><td class="pdm-project-portfolio__task" :title="row.currentTask?.name"><strong v-if="row.currentTask">{{ row.currentTask.name }}</strong><small v-if="row.currentTask">{{ personName(row.currentTask.assignee) }} · {{ displayDate(row.currentTask.plannedFinish) }}</small><span v-else>暂无待办</span></td><td><span class="pdm-project-portfolio__overdue" :class="`is-${row.overdueTaskSummary.tone}`" :title="row.overdueTaskSummary.basis"><i v-for="line in row.overdueTaskSummary.lines" :key="line">{{ line }}</i></span></td><td class="pdm-project-portfolio__notes" @click.stop><ol><li v-for="note in notesForProject(row.projectId)" :key="note.id"><span :title="`${note.detail} · ${personName(note.actor)} · ${note.occurredAt.replace('T', ' ').slice(0, 16)}`">{{ note.detail }} · {{ personName(note.actor) }} · {{ note.occurredAt.replace('T', ' ').slice(5, 16) }}</span></li><li v-if="!notesForProject(row.projectId).length" class="is-empty">暂无备注</li></ol><button type="button" class="pdm-text-action" :aria-label="`维护 ${row.projectCode} 的记录`" @click="openManagerNote(row.projectId, row.projectCode, row.projectName)">记录</button></td>
              </tr>
              <tr v-if="!portfolioRows.length" class="is-empty"><td colspan="10">尚未加载项目计划总览</td></tr>
            </tbody></table>
          </div>
        </article>

      </div>
    </div>

    <el-dialog v-model="managerNoteDialogOpen" class="pdm-manager-note-dialog" :title="managerNoteTarget ? `维护项目记录 · ${managerNoteTarget.code}` : '维护项目记录'" width="520px" append-to-body>
      <label class="pdm-dialog-field">备注内容<textarea v-model="managerNoteContent" class="pdm-manager-note-dialog__input" maxlength="1000" rows="5" placeholder="记录进度、风险或需要跟进的事项" aria-label="项目备注内容" /></label>
      <section class="pdm-project-todo-settings" aria-label="待办设置">
        <header><strong>待办设置</strong><span>保存记录时将上述内容推送给指定人员</span></header>
        <div>
          <label class="pdm-dialog-field">截止日期（可选）<el-date-picker v-model="todoDueDate" type="date" value-format="YYYY-MM-DD" placeholder="不设截止日期" aria-label="待办截止日期" /></label>
          <label class="pdm-dialog-field">接收人<el-select v-model="todoRecipients" multiple filterable collapse-tags collapse-tags-tooltip placeholder="选择接收人" aria-label="待办接收人"><el-option v-for="user in todoRecipientCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        </div>
      </section>
      <section class="pdm-project-record-history" aria-label="当前项目及子项目历史记录">
        <header><strong>历史记录</strong><span>当前项目及子项目</span></header>
        <div v-if="projectRecordHistory.length" class="pdm-project-record-history__list">
          <article v-for="entry in projectRecordHistory" :key="entry.id">
            <header><strong>{{ projectRecordHistoryLabel(entry) }}</strong><span>{{ projectRecordHistoryType(entry) }}</span></header>
            <p><span>{{ personName(entry.actor) }}</span><time>{{ projectRecordHistoryTime(entry) }}</time></p>
            <small>{{ entry.detail }}</small>
          </article>
        </div>
        <p v-else class="pdm-project-record-history__empty">暂无历史记录</p>
      </section>
      <template #footer><el-button @click="managerNoteDialogOpen=false">取消</el-button><el-button type="primary" :loading="savingManagerNote" @click="saveManagerNote">保存记录</el-button></template>
    </el-dialog>

    <el-drawer v-model="phaseOwnerDrawerOpen" class="pdm-phase-owner-drawer" :title="`配置项目团队 · ${project.code}`" size="560px" append-to-body>
      <div v-if="rootProject.canManageMainStaffing" class="pdm-phase-owner-list" aria-label="主项目分工">
        <label class="pdm-phase-owner-row">
          <span class="pdm-phase-owner-row__index">1</span>
          <span class="pdm-phase-owner-row__copy"><strong>项目经理</strong><small>负责主项目统筹，限选1名</small></span>
          <el-select v-model="staffingForm.primaryProjectManager" filterable placeholder="选择项目经理" aria-label="项目经理"><el-option v-for="user in projectManagerCandidates" :key="user.username" :label="`${user.displayName} · ${divisionOfUser(user.username)?.name ?? '未归属事业部'}`" :value="user.username" /></el-select>
        </label>
        <label class="pdm-phase-owner-row">
          <span class="pdm-phase-owner-row__index">2</span>
          <span class="pdm-phase-owner-row__copy"><strong>协同项目经理</strong><small>协助主项目统筹，可多选</small></span>
          <el-select v-model="staffingForm.collaborativeProjectManagers" multiple filterable placeholder="选择协同项目经理" aria-label="协同项目经理"><el-option v-for="user in projectManagerCandidates.filter(item => item.username !== staffingForm.primaryProjectManager)" :key="user.username" :label="`${user.displayName} · ${divisionOfUser(user.username)?.name ?? '未归属事业部'}`" :value="user.username" /></el-select>
        </label>
        <label class="pdm-phase-owner-row">
          <span class="pdm-phase-owner-row__index">3</span>
          <span class="pdm-phase-owner-row__copy"><strong>主设</strong><small>负责主项目设计统筹，可多选</small></span>
          <el-select v-model="staffingForm.designLeads" multiple filterable placeholder="选择主设" aria-label="主设"><el-option v-for="user in designLeadCandidates" :key="user.username" :label="`${user.displayName} · ${divisionOfUser(user.username)?.name ?? '未归属事业部'}`" :value="user.username" /></el-select>
        </label>
      </div>
      <div v-if="activeProject.canAssignDesigners" class="pdm-phase-owner-intro"><strong>配置执行工程师与各交付阶段负责人</strong><span>执行工程师可多选；未分配的阶段可暂时留空，后续在此统一补充。</span></div>
      <div v-if="activeProject.canAssignDesigners" class="pdm-phase-owner-list" aria-label="项目阶段负责人">
        <label class="pdm-phase-owner-row pdm-phase-owner-row--engineers">
          <span class="pdm-phase-owner-row__index">{{ rootProject.canManageMainStaffing ? 4 : 1 }}</span>
          <span class="pdm-phase-owner-row__copy"><strong>执行工程师</strong><small>负责项目设计执行，可多选</small></span>
          <el-select v-model="designerDraft" multiple filterable clearable placeholder="选择执行工程师" aria-label="执行工程师">
            <el-option v-for="user in executionEngineerCandidates" :key="user.username" :label="`${user.displayName} · ${user.divisionName}`" :value="user.username" />
          </el-select>
        </label>
        <label v-for="(phase, index) in phaseOwnerDefinitions" :key="phase.key" class="pdm-phase-owner-row">
          <span class="pdm-phase-owner-row__index">{{ index + (rootProject.canManageMainStaffing ? 5 : 2) }}</span>
          <span class="pdm-phase-owner-row__copy"><strong>{{ phase.label }}</strong><small>{{ phase.detail }}</small></span>
          <el-select v-model="phaseOwnerDraft[phase.key]" filterable clearable placeholder="选择负责人" :aria-label="`${phase.label}负责人`">
            <el-option-group v-if="preferredPhaseCandidates(phase.preferredRoles).length" label="推荐岗位">
              <el-option v-for="user in preferredPhaseCandidates(phase.preferredRoles)" :key="user.username" :label="`${user.displayName} · ${user.divisionName}`" :value="user.username" />
            </el-option-group>
            <el-option-group v-if="otherPhaseCandidates(phase.preferredRoles).length" label="其他人员">
              <el-option v-for="user in otherPhaseCandidates(phase.preferredRoles)" :key="user.username" :label="`${user.displayName} · ${user.divisionName}`" :value="user.username" />
            </el-option-group>
          </el-select>
        </label>
      </div>
      <template #footer><el-button @click="phaseOwnerDrawerOpen=false">取消</el-button><el-button type="primary" :loading="pending || savingTeamConfiguration" @click="saveTeamConfiguration">保存配置</el-button></template>
    </el-drawer>
  </section>
</template>
