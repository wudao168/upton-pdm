<script setup lang="ts">
import { Boxes, CalendarRange, CheckCircle2, ChevronRight, ClipboardCopy, FolderTree, ListChecks, PackageCheck, UsersRound } from '@lucide/vue'
import { getMaterialRelationCompleteness, getProjectProcurementTracking, readProjectPlanPortfolio, readProjectValidationPlan } from '../api'
import { ElMessage } from '../statusMessage'
import { computed, reactive, ref, watch } from 'vue'
import type { DocumentNode, DrawingReviewPackage, DrawingReviewTarget, MainProjectStaffingInput, MaterialCodeApplication, MaterialRelationCompleteness, OrganizationDirectory, PdmUser, ProjectPlanPortfolio, ProjectPlanTask, ProjectProcurementTrackingResult, ProjectSummary, ProjectValidationPlan, ReleasePackageSummary, ReleaseScope } from '../types'

const props = defineProps<{
  project: ProjectSummary
  projects: ProjectSummary[]
  users: PdmUser[]
  selected: DocumentNode
  currentUsername?: string
  hasDocuments: boolean
  documentCount: number
  modelCount: number
  drawingCount: number
  warningCount: number
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
  onUpdateMainStaffing: (projectId: string, input: MainProjectStaffingInput) => Promise<ProjectSummary>
  onUpdateDesigners: (projectId: string, designers: string[]) => Promise<ProjectSummary>
}>()

const emit = defineEmits<{ documents: []; bom: []; projectPlan: []; validationPlan: []; procurement: []; release: [] }>()

const activeProject = computed(() => props.projects.find(item => item.id === props.project.id) ?? props.project)
const rootProject = computed(() => {
  if (!activeProject.value.parentProjectId) return activeProject.value
  return props.projects.find(item => item.id === activeProject.value.parentProjectId) ?? activeProject.value
})

const staffingDialogOpen = ref(false)
const designerDialogOpen = ref(false)
const planPortfolio = ref<ProjectPlanPortfolio | null>(null)
const validationPlan = ref<ProjectValidationPlan | null>(null)
const relationCompleteness = ref<MaterialRelationCompleteness | null>(null)
const procurementTracking = ref<ProjectProcurementTrackingResult | null>(null)
let overviewRequestId = 0
const staffingForm = reactive<MainProjectStaffingInput>({ primaryProjectManager: '', collaborativeProjectManagers: [], designLeads: [] })
const designerDraft = ref<string[]>([])

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
const designerCandidates = computed(() => {
  const organizationId = props.project.organizationId ?? rootProject.value.organizationId
  if (!organizationId) return []
  const ownDivisionId = divisionOfUser(props.currentUsername ?? '')?.id
  const companyUnitIds = new Set(props.organizationDirectory.units.filter(unit => unit.organizationId === organizationId && unit.isActive).map(unit => unit.id))
  const usernames = new Set(props.organizationDirectory.memberships.filter(item => companyUnitIds.has(item.unitId)).map(item => item.username))
  const technicalRoles = new Set(['Engineer', 'ElectricalEngineer', 'CommissioningEngineer', 'HardwareEngineer', 'MechanicalManager', 'TechnicalAssistant', 'ProcessReviewer', 'Approver'])
  const isTechnicalUser = (user: PdmUser) => {
    const roles = [user.role, ...(user.roles ?? [])]
    if (!roles.some(role => technicalRoles.has(role))) return false
    return props.organizationDirectory.memberships.filter(item => item.username === user.username).some(item => {
      const unit = props.organizationDirectory.units.find(candidate => candidate.id === item.unitId)
      const name = unit?.name ?? ''
      return /机械|电气|硬件|标准化|技术|设计|研发/.test(name) || unit?.kind === 'BusinessDivision' && !/采购|供应链|生产|装配|机加|财务|行政|人事部|销售|计划|质量|仓储|物流/.test(name)
    })
  }
  return props.organizationDirectory.users
    .filter(user => user.isActive && usernames.has(user.username) && isTechnicalUser(user))
    .map(user => {
      const division = divisionOfUser(user.username)
      return { ...user, divisionName: division?.name ?? '未归属事业部', ownDivision: division?.id === ownDivisionId }
    })
    .sort((left, right) => Number(right.ownDivision) - Number(left.ownDivision) || left.divisionName.localeCompare(right.divisionName, 'zh-CN') || left.displayName.localeCompare(right.displayName, 'zh-CN'))
})
const hasCrossDivisionSelection = computed(() => designerCandidates.value.some(user => designerDraft.value.includes(user.username) && !user.ownDivision))

async function loadOperationalSummary() {
  const requestId = ++overviewRequestId
  const token = props.token
  if (!token) {
    planPortfolio.value = null
    validationPlan.value = null
    relationCompleteness.value = null
    procurementTracking.value = null
    return
  }
  const [planResult, validationResult, relationResult, procurementResult] = await Promise.allSettled([
    readProjectPlanPortfolio(rootProject.value.id, token),
    readProjectValidationPlan(activeProject.value.id, token),
    getMaterialRelationCompleteness(activeProject.value.id, token),
    getProjectProcurementTracking(activeProject.value.id, token),
  ])
  if (requestId !== overviewRequestId) return
  planPortfolio.value = planResult.status === 'fulfilled' ? planResult.value : null
  validationPlan.value = validationResult.status === 'fulfilled' ? validationResult.value : null
  relationCompleteness.value = relationResult.status === 'fulfilled' ? relationResult.value : null
  procurementTracking.value = procurementResult.status === 'fulfilled' ? procurementResult.value : null
}

watch(() => [props.project.id, props.token], loadOperationalSummary, { immediate: true })

function openStaffingDialog() {
  staffingForm.primaryProjectManager = rootProject.value.primaryProjectManager ?? ''
  staffingForm.collaborativeProjectManagers = [...rootProject.value.collaborativeProjectManagers]
  staffingForm.designLeads = [...projectDesignLeads(rootProject.value)]
  staffingDialogOpen.value = true
}

function openDesignerDialog() {
  designerDraft.value = [...activeProject.value.designers]
  designerDialogOpen.value = true
}

async function saveMainStaffing() {
  if (!staffingForm.primaryProjectManager || !staffingForm.designLeads.length) return ElMessage.warning('请选择一名项目经理和至少一名主设')
  try {
    await props.onUpdateMainStaffing(rootProject.value.id, {
      primaryProjectManager: staffingForm.primaryProjectManager,
      collaborativeProjectManagers: [...staffingForm.collaborativeProjectManagers],
      designLeads: [...staffingForm.designLeads],
    })
    staffingDialogOpen.value = false
    ElMessage.success('主项目分工已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '主项目分工保存失败') }
}

async function saveDesigners() {
  try {
    await props.onUpdateDesigners(activeProject.value.id, [...designerDraft.value])
    designerDialogOpen.value = false
    ElMessage.success(designerDraft.value.length ? '执行工程师已保存' : '执行工程师已清空')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '执行工程师保存失败') }
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

const drawingReviewSummary = computed(() => {
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '图纸审核：未发起'
  const stateLabels = {
    InReview: '审核中',
    ChangesRequested: '已退改',
    WritingProperties: '写入标记',
    Approved: '已完成',
    Stale: '版本冲突',
    Withdrawn: '已撤销',
  }
  const reviewed = review.items.reduce((count, item) => count
    + (['Approved', 'Marked'].includes(item.modelState) ? 1 : 0)
    + (['Approved', 'Marked'].includes(item.drawingState) ? 1 : 0), 0)
  return `图纸审核：${stateLabels[review.state]} · 双审 ${reviewed}/${review.items.length * 2}`
})

function drawingTargetSummary(target: DrawingReviewTarget) {
  const count = target === 'Model3D' ? props.modelCount : props.drawingCount
  if (count === 0) return '无图档'
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '未发起'
  if (review.state === 'Stale') return '版本冲突'
  const states = review.items.map(item => target === 'Model3D' ? item.modelState : item.drawingState)
  const completed = states.filter(state => state === 'Approved' || state === 'Marked').length
  const changesRequested = states.filter(state => state === 'ChangesRequested').length
  if (states.length > 0 && completed === states.length) return '已完成'
  if (changesRequested > 0) return `已退改 ${changesRequested}`
  if (review.state === 'WritingProperties') return `写入标记 ${completed}/${states.length}`
  return `审核中 ${completed}/${states.length}`
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
  return [`待审${pending}`, `已批${approved}`, `驳回${rejected}`].filter((_, index) => [pending, approved, rejected][index] > 0).join(' · ')
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

const materialApplicationSummary = computed(() => {
  if (!props.materialApplications.length) return '物料申请：暂无'
  const pending = props.materialApplications.filter(item => item.status === 'Pending').length
  const approved = props.materialApplications.filter(item => item.status === 'Approved').length
  const rejected = props.materialApplications.filter(item => item.status === 'Rejected').length
  return `物料申请：待审 ${pending} · 已批 ${approved} · 驳回 ${rejected}`
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
const projectHealth = computed(() => {
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
const currentStageTasks = computed(() => {
  const tasks = activePlan.value?.tasks ?? []
  const current = tasks.filter(task => task.stage === projectStage.value && task.status !== 'Completed')
  const candidates = current.length ? current : tasks.filter(task => task.status !== 'Completed')
  return [...candidates].sort((left, right) => left.plannedFinish.localeCompare(right.plannedFinish) || left.sortOrder - right.sortOrder).slice(0, 6)
})

function personName(username?: string | null) {
  if (!username) return '待分配'
  return props.users.find(user => user.username.localeCompare(username, undefined, { sensitivity: 'accent' }) === 0)?.displayName ?? username
}

function taskState(task: ProjectPlanTask) {
  if (task.status === 'Completed') return { label: '已完成', tone: 'success' }
  if (task.plannedFinish < new Date().toISOString().slice(0, 10)) return { label: '已逾期', tone: 'danger' }
  if (task.status === 'InProgress') return { label: '进行中', tone: 'success' }
  const days = Math.ceil((new Date(`${task.plannedFinish}T00:00:00`).getTime() - Date.now()) / 86400000)
  if (days <= 3) return { label: '待处理', tone: 'warning' }
  return { label: '未开始', tone: 'neutral' }
}

const teamRows = computed(() => staffingRows.value.filter(row => ['manager', 'design-lead', 'engineers'].includes(row.key)))
const latestReleasePackage = computed(() => props.releasePackage ?? [...props.releasePackages]
  .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))[0] ?? null)
const releaseApprovalText = computed(() => {
  const release = latestReleasePackage.value
  if (!release) return '未发起'
  const step = release.steps.find(item => item.status === 'current')
  return `${release.state}${step ? ` · ${step.stage}` : ''}`
})
const criticalMaterialCount = computed(() => procurementTracking.value?.items?.filter(item => !!item.impactStage).length ?? null)
const unpurchasedCount = computed(() => procurementTracking.value?.items?.filter(item => !(item.purchaseOrderNumbers ?? []).length).length ?? null)
const relationSummary = computed(() => {
  if (!relationCompleteness.value) return '关联核对 —'
  if (!relationCompleteness.value.mainMaterialCount) return '关联配置 暂无'
  return relationCompleteness.value.incompleteGroupCount
    ? `关联待核对 ${relationCompleteness.value.incompleteGroupCount}`
    : '关联物料 已核对'
})
const approvalCount = computed(() => {
  const drawing = props.drawingReviews.some(item => ['InReview', 'WritingProperties'].includes(item.state)) ? 1 : 0
  const validation = validationPlan.value?.state === 'PendingApproval' ? 1 : 0
  const releases = props.releasePackages.filter(item => /审批|Pending|Submitted|InReview/i.test(item.state)).length
  return drawing + validation + releases
})
const attentionCount = computed(() => props.bomPendingCount + (relationCompleteness.value?.incompleteGroupCount ?? 0))
const overviewAlerts = computed(() => [
  { key: 'attention', label: '待处理', value: attentionCount.value, tone: attentionCount.value ? 'warning' : 'neutral', open: () => emit('bom') },
  { key: 'critical', label: '关键物料', value: criticalMaterialCount.value ?? '—', tone: criticalMaterialCount.value ? 'danger' : 'neutral', open: () => emit('procurement') },
  { key: 'approval', label: '审批中', value: approvalCount.value, tone: approvalCount.value ? 'info' : 'neutral', open: () => latestReleasePackage.value ? emit('release') : validationPlan.value?.state === 'PendingApproval' ? emit('validationPlan') : emit('documents') },
])

async function copyLocation(label: string, value: string) {
  try {
    await navigator.clipboard.writeText(value)
    ElMessage.success(`${label}已复制`)
  } catch { ElMessage.error(`${label}复制失败`) }
}
</script>

<template>
  <section class="pdm-workbench" aria-label="工作台主页面">
    <div class="pdm-overview-layout">
      <section class="pdm-panel pdm-overview-status" aria-label="项目状态与下一步">
        <header><span><CheckCircle2 :size="18" /></span><h2>项目状态与下一步</h2></header>
        <div class="pdm-overview-status__body">
          <div class="pdm-overview-phase">
            <div class="pdm-overview-phase__title"><span><CalendarRange :size="20" /></span><strong>{{ projectStageLabel }}阶段</strong><em :class="`is-${projectHealth.tone}`">{{ projectHealth.label }}</em></div>
            <div class="pdm-overview-phase__metrics">
              <div class="pdm-overview-progress"><span>项目进度 <b>{{ projectProgress === null ? '—' : `${projectProgress}%` }}</b></span><i><em :style="{ width: `${projectProgress ?? 0}%` }" /></i></div>
              <div class="pdm-overview-finish"><span>计划完成</span><strong>{{ projectFinish }}</strong></div>
            </div>
          </div>
          <div class="pdm-overview-alerts" aria-label="项目待办与风险">
            <button v-for="alert in overviewAlerts" :key="alert.key" type="button" :class="`is-${alert.tone}`" :aria-label="`查看${alert.label}`" @click="alert.open">
              <span>{{ alert.label }}</span><strong>{{ alert.value }}</strong><em>查看{{ alert.label }} <ChevronRight :size="13" /></em>
            </button>
          </div>
        </div>
      </section>

      <div class="pdm-overview-summary" aria-label="项目核心业务概览">
        <article class="pdm-panel pdm-overview-card" aria-label="图档与审核">
          <header><span class="is-blue"><FolderTree :size="18" /></span><h2>图档与审核</h2></header>
          <strong class="pdm-overview-card__value">{{ documentCount }}</strong>
          <div class="pdm-overview-card__facts"><span>3D <b>{{ modelCount }}</b></span><span>2D <b>{{ drawingCount }}</b></span></div>
          <dl><div><dt>图纸审核</dt><dd>{{ drawingReviewSummary.replace('图纸审核：', '') }}</dd></div><div><dt>异常引用</dt><dd :class="{ 'is-danger': warningCount > 0 }">{{ warningCount }}</dd></div></dl>
          <button type="button" class="pdm-overview-link" aria-label="进入项目图档" @click="emit('documents')">查看图档 <ChevronRight :size="13" /></button>
        </article>

        <article class="pdm-panel pdm-overview-card" aria-label="BOM与物料">
          <header><span class="is-green"><Boxes :size="18" /></span><h2>BOM与物料</h2></header>
          <strong class="pdm-overview-card__value">{{ standardCount + nonStandardCount + electricalCount }}</strong>
          <div class="pdm-overview-card__facts"><span>标准件 <b>{{ standardCount }}</b></span><span>非标件 <b>{{ nonStandardCount }}</b></span><span>电气 <b>{{ electricalCount }}</b></span></div>
          <dl><div><dt>待处理</dt><dd :class="{ 'is-warning': bomPendingCount > 0 }">{{ bomPendingCount }}</dd></div><div><dt>料号申请</dt><dd>{{ materialApplicationSummary.replace('物料申请：', '') }}</dd></div><div><dt>关联物料</dt><dd>{{ relationSummary.replace('关联', '') }}</dd></div></dl>
          <button type="button" class="pdm-overview-link" aria-label="进入BOM数据" @click="emit('bom')">查看BOM <ChevronRight :size="13" /></button>
        </article>

        <article class="pdm-panel pdm-overview-card" aria-label="发布与备料">
          <header><span class="is-orange"><PackageCheck :size="18" /></span><h2>发布与备料</h2></header>
          <strong class="pdm-overview-card__value is-package">{{ latestReleasePackage?.number || '暂无发布包' }}</strong>
          <dl><div><dt>发布审批</dt><dd>{{ releaseApprovalText }}</dd></div><div><dt>关键物料</dt><dd :class="{ 'is-danger': criticalMaterialCount }">{{ criticalMaterialCount ?? '—' }}</dd></div><div><dt>未采购</dt><dd :class="{ 'is-warning': unpurchasedCount }">{{ unpurchasedCount ?? '—' }}</dd></div></dl>
          <div class="pdm-overview-card__actions"><button type="button" class="pdm-overview-link" @click="emit('release')">查看发布 <ChevronRight :size="13" /></button><button type="button" class="pdm-overview-link" @click="emit('procurement')">查看备料 <ChevronRight :size="13" /></button></div>
        </article>
      </div>

      <div class="pdm-overview-bottom">
        <article class="pdm-panel pdm-overview-tasks" aria-label="当前阶段任务">
          <header><span><ListChecks :size="18" /></span><h2>当前阶段任务</h2><button type="button" class="pdm-overview-link" @click="emit('projectPlan')">进入项目计划 <ChevronRight :size="13" /></button></header>
          <div class="pdm-overview-task-table">
            <table><thead><tr><th>任务名称</th><th>负责人</th><th>计划完成</th><th>状态</th></tr></thead><tbody>
              <tr v-for="task in currentStageTasks" :key="task.id" tabindex="0" @click="emit('projectPlan')" @keydown.enter="emit('projectPlan')"><td :title="task.name">{{ task.name }}</td><td>{{ personName(task.assignee) }}</td><td>{{ displayDate(task.plannedFinish).slice(5) }}</td><td><span :class="`is-${taskState(task).tone}`">{{ taskState(task).label }}</span></td></tr>
              <tr v-if="!currentStageTasks.length" class="is-empty"><td colspan="4">{{ activePlanItem?.hasPlan ? '当前阶段没有待处理任务' : '项目计划尚未建立' }}</td></tr>
            </tbody></table>
          </div>
        </article>

        <aside class="pdm-overview-support">
          <article class="pdm-panel pdm-overview-team" aria-label="项目团队">
            <header><span><UsersRound :size="18" /></span><h2>项目团队</h2><span class="pdm-overview-team__actions"><button v-if="rootProject.canManageMainStaffing" type="button" class="pdm-text-action" @click="openStaffingDialog">配置分工</button><button v-if="activeProject.canAssignDesigners" type="button" class="pdm-text-action" @click="openDesignerDialog">配置工程师</button></span></header>
            <dl><div v-for="row in teamRows" :key="row.key"><dt>{{ row.role }}</dt><dd :class="{ 'is-pending': !row.people.length }">{{ row.people.map(person => person.name).join('、') || '待分配' }}<small v-if="row.key === 'manager' && rootProject.executionUnitName">{{ rootProject.executionUnitName }}</small></dd></div></dl>
          </article>

          <article class="pdm-panel pdm-overview-locations" aria-label="项目位置">
            <header><span><FolderTree :size="18" /></span><h2>项目位置</h2></header>
            <dl><div><dt>图档库</dt><dd :title="project.vaultLocation">{{ project.vaultLocation }}</dd><button type="button" aria-label="复制图档库位置" @click="copyLocation('图档库位置', project.vaultLocation)"><ClipboardCopy :size="14" /></button></div><div><dt>发包目录</dt><dd :title="project.releaseLocation">{{ project.releaseLocation }}</dd><button type="button" aria-label="复制发包目录位置" @click="copyLocation('发包目录位置', project.releaseLocation)"><ClipboardCopy :size="14" /></button></div></dl>
          </article>
        </aside>
      </div>
    </div>

    <el-dialog v-model="staffingDialogOpen" :title="`配置主项目分工 · ${rootProject.code}`" width="500px" append-to-body>
      <div class="pdm-project-staffing-form">
        <label class="pdm-dialog-field">项目经理（限1名）<el-select v-model="staffingForm.primaryProjectManager" class="pdm-project-person-select" filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in projectManagerCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label class="pdm-dialog-field">协同项目经理（可多选）<el-select v-model="staffingForm.collaborativeProjectManagers" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in projectManagerCandidates.filter(item => item.username !== staffingForm.primaryProjectManager)" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
        <label class="pdm-dialog-field">主设（可多选）<el-select v-model="staffingForm.designLeads" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option v-for="user in designLeadCandidates" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
      </div>
      <template #footer><el-button @click="staffingDialogOpen=false">取消</el-button><el-button type="primary" :loading="pending" @click="saveMainStaffing">保存分工</el-button></template>
    </el-dialog>

    <el-dialog v-model="designerDialogOpen" :title="`配置执行工程师 · ${project.code}`" width="500px" append-to-body>
      <label class="pdm-dialog-field">工程师（可多选）<el-select v-model="designerDraft" class="pdm-project-person-select" multiple filterable placeholder="输入姓名筛选" style="width:100%"><el-option-group label="本事业部（优先）"><el-option v-for="user in designerCandidates.filter(item => item.ownDivision)" :key="user.username" :label="user.displayName" :value="user.username" /></el-option-group><el-option-group label="其他事业部"><el-option v-for="user in designerCandidates.filter(item => !item.ownDivision)" :key="user.username" :label="`${user.displayName} · ${user.divisionName}`" :value="user.username" /></el-option-group></el-select></label>
      <p v-if="hasCrossDivisionSelection" class="pdm-dialog-note is-warning">已选择其他事业部人员，请确认跨事业部协作安排。</p>
      <p class="pdm-dialog-note">工程师可保持为空；由具备“分配执行工程师”权限的事业部负责人、机械主管、当前项目经理或主设配置。</p>
      <template #footer><el-button @click="designerDialogOpen=false">取消</el-button><el-button type="primary" :loading="pending" @click="saveDesigners">保存工程师</el-button></template>
    </el-dialog>
  </section>
</template>
