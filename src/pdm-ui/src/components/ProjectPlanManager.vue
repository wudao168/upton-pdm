<script setup lang="ts">
import { Calendar, ChevronDown, ChevronRight, History, ListCollapse, ListTree, PanelLeftClose, PanelLeftOpen, RefreshCw } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import {
  generateProjectPlan,
  deleteProjectPlan,
  submitProjectPlan,
  submitProjectPlanChange,
  completeProjectPlanChange,
  abandonProjectPlanChange,
  decideProjectPlan,
  listProjectPlanTemplates,
  listProjectPlanVersions,
  readProjectPlan,
  readProjectPlanPortfolio,
  reuseProjectPlan,
  saveProjectPlan,
  setProjectPlanStage,
  updateProjectPlanTaskProgress,
} from '../api'
import type { ProjectPlan, ProjectPlanPortfolio, ProjectPlanPortfolioItem, ProjectPlanStage, ProjectPlanStageDefinition, ProjectPlanTask, ProjectPlanTemplate, ProjectPlanVersion, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'
import { hasStageAllocation, planProgress, stageProgress } from '../projectPlanAllocation'
import { loadGlobalStatusContent } from '../globalStatusContent'

const props = defineProps<{
  project: ProjectSummary
  projects: ProjectSummary[]
  token: string
  currentUsername: string
  currentRole: string
  developer: boolean
  canEdit: boolean
}>()
const emit = defineEmits<{ switchProject: [projectId: string] }>()
const displayUserName = useUserDisplayName()

type Zoom = 'month' | 'week' | 'day'
type TimelineRow = {
  key: string
  name: string
  code?: string
  level: number
  stage?: ProjectPlanStage
  start?: string
  finish?: string
  actualFinish?: string
  baselineStart?: string
  baselineFinish?: string
  completion: number
  isMilestone?: boolean
  isLagging?: boolean
  isAtRisk?: boolean
  isInherited?: boolean
  plan?: ProjectPlan
  task?: ProjectPlanTask
  isStage?: boolean
  taskCount?: number
  projectId: string
}

const loading = ref(false)
const error = ref('')
const plan = ref<ProjectPlan | null>(null)
const inheritedParentPlan = ref(false)
const portfolio = ref<ProjectPlanPortfolio | null>(null)
const templates = ref<ProjectPlanTemplate[]>([])
const versions = ref<ProjectPlanVersion[]>([])
const zoom = ref<Zoom>('week')
const ganttShell = ref<HTMLElement | null>(null)
const timelineWidth = ref(720)
const infoColumnsCollapsed = ref(false)
const expandedInfoWidth = 590
const collapsedInfoWidth = 350
const updateTimelineWidth = () => {
  if (ganttShell.value) timelineWidth.value = Math.max(1, ganttShell.value.clientWidth - (infoColumnsCollapsed.value ? collapsedInfoWidth : expandedInfoWidth))
}
const resizeObserver = new ResizeObserver(() => {
  updateTimelineWidth()
})
watch(ganttShell, element => {
  resizeObserver.disconnect()
  if (element) {
    updateTimelineWidth()
    resizeObserver.observe(element)
  }
}, { flush: 'post' })
watch(infoColumnsCollapsed, updateTimelineWidth, { flush: 'post' })
onBeforeUnmount(() => resizeObserver.disconnect())
const showBaseline = ref(true)
const showNonWorkingDays = ref(false)
const expanded = ref(new Set<string>())
const collapsedStages = ref(new Set<string>())
const holidayCalendarDays = ref(new Map<string, { name: string; isOffDay: boolean }>())
void loadGlobalStatusContent().then(library => {
  holidayCalendarDays.value = new Map(library.holidayCalendars.flatMap(calendar => calendar.days.map(day => [day.date, { name: day.name, isOffDay: day.isOffDay }] as const)))
}).catch(() => {
  holidayCalendarDays.value = new Map()
})
const stageProgressDialogOpen = ref(false)
const resourceConflictsDialogOpen = ref(false)
const generateDialogOpen = ref(false)
const taskDialogOpen = ref(false)
const reuseDialogOpen = ref(false)
const manageDialogOpen = ref(false)
const deleteDialogOpen = ref(false)
const deleteSelectionDialogOpen = ref(false)
const deleteTarget = ref<ProjectPlan | null>(null)
const deleteIndependentChildren = ref(false)
const masterPlanMode = ref(false)
const syncResultsDialogOpen = ref(false)
const versionsDialogOpen = ref(false)
const compareBaseKey = ref('')
const compareTargetKey = ref('current')
const stageDialogOpen = ref(false)
const hasStageException = computed(() => Boolean(plan.value?.manualStage))
const selectedTask = ref<ProjectPlanTask | null>(null)
const saving = ref(false)
const stagesDialogOpen = ref(false)
const generateTargetProjectId = ref('')
const stagesDraft = ref<ProjectPlanStageDefinition[]>([])
const stageConfigurationPlan = ref<ProjectPlan | null>(null)
const selectedOwnerPlan = computed(() => timelineRows.value.find(row => row.task?.id === selectedTask.value?.id)?.plan)
const isEffective = (value?: ProjectPlan | null) => value?.approvalStatus === 'Approved'
const approvalLabel = (value?: ProjectPlan | null) => !value ? '未排程' : ({ Draft: '草稿', Pending: '待审批', Rejected: '已退回', Approved: '已生效' }[value.approvalStatus ?? 'Draft'])
const canApprove = computed(() => !inheritedParentPlan.value && plan.value?.approvalStatus === 'Pending' && plan.value.approvalAssignee?.toLowerCase() === props.currentUsername.toLowerCase())

const rootProject = computed(() => props.project.parentProjectId
  ? props.projects.find(item => item.id === (props.project.rootProjectId ?? props.project.parentProjectId)) ?? props.project
  : props.project)
const childProjects = computed(() => props.projects.filter(item => item.parentProjectId === rootProject.value.id))
const isMasterWithChildren = computed(() => props.project.id === rootProject.value.id && childProjects.value.length > 0)
const portfolioMode = computed(() => isMasterWithChildren.value && !masterPlanMode.value)
const currentPlan = computed(() => plan.value)
const assigneeOptions = computed(() => {
  const values = [rootProject.value, ...childProjects.value].flatMap(item => [
    item.owner, item.primaryProjectManager, ...item.collaborativeProjectManagers,
    item.designLead, ...(item.designLeads ?? []), ...item.designers,
  ]).filter((item): item is string => Boolean(item?.trim()))
  if (taskForm.assignee) values.push(taskForm.assignee)
  if (inlineEdit.value?.assignee) values.push(inlineEdit.value.assignee)
  return [...new Set(values)].sort((left, right) => displayUserName(left).localeCompare(displayUserName(right), 'zh-CN'))
})
const sourceProjectOptions = computed(() => (portfolio.value?.projects ?? []).filter(item => !item.isRoot && item.hasPlan))
const targetProjectOptions = computed(() => (portfolio.value?.projects ?? []).filter(item => !item.isRoot && item.projectId !== reuseForm.sourceProjectId))
const managedChildPlans = computed(() => (portfolio.value?.projects ?? []).filter(item => !item.isRoot))
const ownsDisplayedPlan = computed(() => Boolean(plan.value && !inheritedParentPlan.value && plan.value.projectId === props.project.id))
const generateTargetProject = computed(() => props.projects.find(item => item.id === generateTargetProjectId.value))
function canManageProject(projectId: string) {
  if (props.developer) return true
  if (projectId === props.project.id) return props.canEdit
  const project = props.projects.find(item => item.id === projectId)
  const username = props.currentUsername.trim().toLowerCase()
  return project?.primaryProjectManager?.trim().toLowerCase() === username
}
function canEditSchedule(owner?: ProjectPlan | null) {
  const approvedDraft = isEffective(owner) && owner?.changeDraftSource && owner.changeRequest?.status === 'Approved'
    && owner.changeRequest.submittedBy.toLowerCase() === props.currentUsername.toLowerCase()
  return Boolean(owner && !(inheritedParentPlan.value && owner.id === plan.value?.id)
    && (!isEffective(owner) || approvedDraft) && canManageProject(owner.projectId))
}
const unapprovedPortfolioPlans = computed(() => portfolioMode.value
  ? (portfolio.value?.projects ?? []).filter(item => !item.isRoot && item.plan && !isEffective(item.plan))
  : [])
const deletablePortfolioPlans = computed(() => unapprovedPortfolioPlans.value.filter(item => canManageProject(item.projectId)))
const toolbarDeleteTarget = computed(() => {
  if (ownsDisplayedPlan.value && canEditSchedule(plan.value)) return plan.value
  return deletablePortfolioPlans.value.length === 1 ? deletablePortfolioPlans.value[0]?.plan ?? null : null
})
const canDeleteDisplayedPlan = computed(() => Boolean(toolbarDeleteTarget.value || deletablePortfolioPlans.value.length > 1))
const deleteDisabledReason = computed(() => {
  if (inheritedParentPlan.value || (props.project.parentProjectId && plan.value && plan.value.projectId !== props.project.id)) return '当前子项目没有独立计划，请在主项目处理'
  if (unapprovedPortfolioPlans.value.length && !deletablePortfolioPlans.value.length) return '当前账号没有删除这些未审批计划的权限'
  if (plan.value && ownsDisplayedPlan.value && !canManageProject(plan.value.projectId)) return '当前账号没有删除该计划的权限'
  if (plan.value && ownsDisplayedPlan.value && isEffective(plan.value) && !canEditSchedule(plan.value)) return '已审批生效的计划不可直接删除，请先申请并取得变更权限'
  if (!canDeleteDisplayedPlan.value) return '当前项目及其子项目没有可删除的未审批计划'
  return ''
})
const canEditSelectedPlan = computed(() => canEditSchedule(selectedOwnerPlan.value))

function editChildPlan(projectId: string) {
  manageDialogOpen.value = false
  emit('switchProject', projectId)
}

function openChildGenerate(item: ProjectPlanPortfolioItem) {
  if (!plan.value || !canManageProject(item.projectId) || item.plan) return
  manageDialogOpen.value = false
  openGenerate(null, item.projectId, plan.value)
}

const deleteTargetProject = computed(() => props.projects.find(item => item.id === deleteTarget.value?.projectId))
const deletableChildPlans = computed(() => deleteTarget.value?.projectId === rootProject.value.id
  ? (portfolio.value?.projects ?? []).filter(item => !item.isRoot && item.plan && !isEffective(item.plan))
  : [])
const followerChildPlans = computed(() => deletableChildPlans.value.filter(item => item.plan?.followsParentPlan && item.plan.parentPlanId === deleteTarget.value?.id))
const independentChildPlans = computed(() => deletableChildPlans.value.filter(item => !followerChildPlans.value.includes(item)))

async function openDelete(target: ProjectPlan) {
  if (!canManageProject(target.projectId) || (isEffective(target) && !canEditSchedule(target))) return
  try {
    saving.value = true
    const latest = await readProjectPlan(target.projectId, props.token)
    if (!latest) {
      await load()
      ElMessage.error('计划数据已更新，请重新确认后操作。')
      return
    }
    if (isEffective(latest) && !canEditSchedule(latest)) {
      await load()
      ElMessage.error('计划已审批生效，请先申请并取得变更权限')
      return
    }
    deleteSelectionDialogOpen.value = false
    deleteTarget.value = latest
    deleteIndependentChildren.value = false
    deleteDialogOpen.value = true
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '读取最新计划失败')
  } finally {
    saving.value = false
  }
}

function openToolbarDelete() {
  if (toolbarDeleteTarget.value) {
    openDelete(toolbarDeleteTarget.value)
    return
  }
  if (deletablePortfolioPlans.value.length > 1) deleteSelectionDialogOpen.value = true
}

async function confirmDelete() {
  const target = deleteTarget.value
  if (!target || !canManageProject(target.projectId) || (isEffective(target) && !canEditSchedule(target))) return
  try {
    saving.value = true
    await deleteProjectPlan(target.projectId, target.rowVersion, deleteIndependentChildren.value, props.token)
    deleteDialogOpen.value = false
    manageDialogOpen.value = false
    deleteTarget.value = null
    await load()
    ElMessage.success(isEffective(target) ? '现行计划和变更草稿已删除，可重新生成计划' : '未批准计划已删除，可重新生成计划')
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '计划删除失败')
  } finally {
    saving.value = false
  }
}

const generateForm = reactive({ templateId: '', startDate: isoDate(new Date()), totalDurationDays: 60, replaceExisting: false, changeReason: '' })
const generationTemplate = computed(() => templates.value.find(item => item.id === generateForm.templateId))
const independentSchedules = ref<Array<{ stage: string; name: string; durationDays: number; deferred: boolean }>>([])
const postDeliveryStartOverride = ref('')
const earliestPostDeliveryStart = computed(() => generateForm.startDate && generateForm.totalDurationDays > 0 ? addDays(generateForm.startDate, generateForm.totalDurationDays) : '')
const scheduledPostDeliveryStages = computed(() => {
  let nextStart = postDeliveryStartOverride.value || earliestPostDeliveryStart.value
  return independentSchedules.value.map(stage => {
    const startDate = nextStart
    nextStart = nextStart && stage.durationDays > 0 ? addDays(nextStart, stage.durationDays) : ''
    return { ...stage, startDate }
  })
})
const allPostDeliveryDeferred = computed(() => independentSchedules.value.length > 0 && independentSchedules.value.every(stage => stage.deferred))
const somePostDeliveryDeferred = computed(() => independentSchedules.value.some(stage => stage.deferred))
function deferOffsitePlan(deferred: boolean) {
  independentSchedules.value.forEach(stage => { stage.deferred = deferred })
}
const postDeliveryDateError = computed(() => scheduledPostDeliveryStages.value.some(stage => !stage.deferred && (!stage.startDate || stage.durationDays < 1))
  ? '请填写交付后阶段的开始日期和工期。'
  : scheduledPostDeliveryStages.value[0] && !scheduledPostDeliveryStages.value[0].deferred && scheduledPostDeliveryStages.value[0].startDate < earliestPostDeliveryStart.value
    ? '客户端调试等交付后阶段不能早于交付完成次日。' : '')
function deferPostDeliveryStage(index: number, deferred: boolean) {
  independentSchedules.value.forEach((stage, position) => {
    if (deferred ? position >= index : position <= index) stage.deferred = deferred
  })
}
watch([generationTemplate, () => generateDialogOpen.value], () => {
  if (!generateDialogOpen.value) return
  postDeliveryStartOverride.value = ''
  independentSchedules.value = (generationTemplate.value?.stages ?? []).filter(stage => stage.participatesInDelivery === false).map(stage => ({
    stage: stage.code, name: stage.name, durationDays: stage.independentDurationDays || 15, deferred: !generateForm.replaceExisting,
  }))
})
const taskForm = reactive({
  name: '', stage: 'Design' as ProjectPlanStage, assignee: '', plannedStart: '', plannedFinish: '',
  completionPercent: 0, actualStart: '', actualFinish: '', weight: 1, isRequired: true,
})
const stageForm = reactive<{ stage: ProjectPlanStage | ''; reason: string }>({ stage: '', reason: '' })
const reuseForm = reactive({ sourceProjectId: '', targetProjectIds: [] as string[], replaceExisting: false, changeReason: '' })

const stageOptions: Array<{ value: ProjectPlanStage; label: string }> = [
  { value: 'Design', label: '设计' },
  { value: 'MaterialPreparation', label: '备料' },
  { value: 'Assembly', label: '装配' },
  { value: 'Commissioning', label: '调试' },
  { value: 'ClientCommissioning', label: '客户端调试' },
  { value: 'AcceptanceProgress', label: '验收推进' },
  { value: 'FinalAcceptance', label: '终验收' },
  { value: 'Paused', label: '暂停' },
  { value: 'Cancelled', label: '取消' },
  { value: 'Terminated', label: '终止' },
]
const defaultStages = stageOptions.slice(0, 7).map(item => ({ code: item.value, name: item.label }))
const taskDateRange = computed<[string, string] | null>({
  get: (): [string, string] | null => taskForm.plannedStart && taskForm.plannedFinish ? [taskForm.plannedStart, taskForm.plannedFinish] : null,
  set: (value: [string, string] | null) => { taskForm.plannedStart = value?.[0] ?? ''; taskForm.plannedFinish = value?.[1] ?? '' },
})
const completionMarks = Object.fromEntries(Array.from({ length: 11 }, (_, index) => [index * 10, `${index * 10}%`]))
function futureActualDate(date: Date) {
  const today = new Date()
  today.setHours(23, 59, 59, 999)
  return date.getTime() > today.getTime()
}
function invalidActualDates(start: string, finish: string) {
  return [start, finish].some(value => value && futureActualDate(new Date(`${value}T00:00:00`)))
}
const inlineEdit = ref<{ row: TimelineRow; field: 'assignee' | 'dates' | 'duration' | 'actualFinish'; assignee: string; dates: [string, string]; duration: number; actualStart: string; actualFinish: string } | null>(null)
function rowDuration(row: TimelineRow) {
  return row.isMilestone ? 0 : row.start && row.finish ? dayDiff(row.start, row.finish) + 1 : null
}

function canReportProgress(row: TimelineRow) {
  return Boolean(!inheritedParentPlan.value && row.task && isEffective(row.plan) && (canManageProject(row.projectId)
    || row.task.assignee?.toLowerCase() === props.currentUsername.toLowerCase()))
}

function beginInlineEdit(row: TimelineRow, field: 'assignee' | 'dates' | 'duration' | 'actualFinish') {
  if (!row.task || saving.value || (field === 'actualFinish' ? !canReportProgress(row) : !canEditSchedule(row.plan))) return
  if (field === 'duration' && row.isMilestone) return
  inlineEdit.value = { row, field, assignee: row.task.assignee ?? '', dates: [row.task.plannedStart, row.task.plannedFinish], duration: rowDuration(row) ?? 1, actualStart: row.task.actualStart ?? '', actualFinish: row.task.actualFinish ?? '' }
}

async function saveInlineEdit() {
  const edit = inlineEdit.value
  const owner = edit?.row.plan
  const task = edit?.row.task
  if (!edit || !owner || !task) return
  if (edit.field === 'actualFinish') {
    if (!canReportProgress(edit.row)) return
    if (invalidActualDates(edit.actualStart, edit.actualFinish)) return ElMessage.warning('实际开始和实际完成不能晚于今天')
    if (!edit.actualFinish || (edit.actualStart && edit.actualFinish < edit.actualStart)) return ElMessage.warning('实际完成日期不能早于实际开始日期')
    saving.value = true
    try {
      await updateProjectPlanTaskProgress(owner.projectId, task.id, {
        completionPercent: 100, actualStart: edit.actualStart || edit.actualFinish,
        actualFinish: edit.actualFinish, expectedRowVersion: owner.rowVersion,
      }, props.token)
      inlineEdit.value = null
      await load()
      ElMessage.success('完成日期已保存，任务已标记为100%完成')
    } catch (reason) {
      ElMessage.error(reason instanceof Error ? reason.message : '完成日期保存失败')
    } finally { saving.value = false }
    return
  }
  if (!canEditSchedule(owner)) return
  if (edit.field === 'duration') {
    if (task.isMilestone || !Number.isInteger(edit.duration) || edit.duration < 1 || edit.duration > 3650) return ElMessage.warning('工期须为1至3650个自然日')
    edit.dates = [task.plannedStart, addDays(task.plannedStart, edit.duration - 1)]
  }
  if (!edit.dates?.[0] || !edit.dates?.[1] || edit.dates[1] < edit.dates[0]) return ElMessage.warning('请选择有效的计划日期区间')
  const changed = edit.field === 'assignee' ? (task.assignee ?? '') !== edit.assignee : task.plannedStart !== edit.dates[0] || task.plannedFinish !== edit.dates[1]
  if (!changed) { inlineEdit.value = null; return }
  saving.value = true
  try {
    const reason = { value: edit.field === 'duration' ? `调整“${task.name}”工期为${edit.duration}天；结束日期${task.plannedFinish} → ${edit.dates[1]}` : isEffective(owner) ? '保存计划安排' : '' }
    const tasks = owner.tasks.map(item => item.id !== task.id ? item : { ...item, ...(edit.field === 'assignee' ? { assignee: edit.assignee || undefined } : { plannedStart: edit.dates[0], plannedFinish: edit.dates[1] }) })
    await saveProjectPlan(owner.projectId, { tasks, changeReason: reason.value, expectedRowVersion: owner.rowVersion }, props.token)
    inlineEdit.value = null
    await load()
    ElMessage.success('计划已保存')
  } catch (reason) {
    if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '计划保存失败')
  } finally { saving.value = false }
}
const hasEffectivePlans = computed(() => portfolioMode.value ? portfolio.value?.projects.some(item => isEffective(item.plan)) : isEffective(plan.value))
const displayedPlans = computed(() => portfolioMode.value ? (portfolio.value?.projects ?? []).flatMap(item => item.plan ? [item.plan] : []) : plan.value ? [plan.value] : [])
const hasVisibleBaseline = computed(() => displayedPlans.value.some(item => item.baselineVersion > 0))

function planStageGroups(owner: ProjectPlan) {
  const definitions: ProjectPlanStageDefinition[] = [...(owner.stages ?? defaultStages)]
  for (const task of owner.tasks) {
    if (!definitions.some(stage => stage.code === task.stage)) definitions.push({ code: task.stage, name: stageLabel(task.stage, owner) })
  }
  return definitions.map(stage => {
    const tasks = owner.tasks.filter(task => task.stage === stage.code).sort((left, right) => left.sortOrder - right.sortOrder)
    const progress = !isEffective(owner) ? 0 : hasStageAllocation(owner.stages) ? Math.round(stageProgress(tasks)) : planProgress(tasks)
    return { ...stage, tasks, progress }
  }).filter(stage => stage.tasks.length > 0)
}

type StageWindow = { stage: ProjectPlanStage; startDate: string; durationDays: number }

function stageMinimumDays(owner: ProjectPlan, stage: ProjectPlanStage) {
  const tasks = owner.tasks.filter(task => task.stage === stage)
  const byId = new Map(tasks.map(task => [task.id, task]))
  const depths = new Map<string, number>()
  const visiting = new Set<string>()
  const depth = (task: ProjectPlanTask): number => {
    if (depths.has(task.id)) return depths.get(task.id)!
    if (visiting.has(task.id)) return 1
    visiting.add(task.id)
    const predecessors = task.predecessorTaskIds.flatMap(id => byId.get(id) ? [byId.get(id)!] : [])
    const value = 1 + (predecessors.length ? Math.max(...predecessors.map(depth)) : 0)
    visiting.delete(task.id)
    depths.set(task.id, value)
    return value
  }
  return Math.max(1, ...tasks.map(depth))
}

function stageWindows(owner: ProjectPlan): StageWindow[] {
  const groups = planStageGroups(owner)
  return groups.map(stage => {
    const startDate = stage.tasks.map(task => task.plannedStart).sort()[0]!
    const finishDate = stage.tasks.map(task => task.plannedFinish).sort().at(-1)!
    return { stage: stage.code, startDate, durationDays: dayDiff(startDate, finishDate) + 1 }
  })
}

function stageWindow(owner: ProjectPlan, stage: ProjectPlanStage) {
  return stageWindows(owner).find(window => window.stage === stage)
}

function redistributeTasksToStageWindows(owner: ProjectPlan, windows: StageWindow[]) {
  const windowByStage = new Map(windows.map(window => [window.stage, window]))
  const proposed = new Map<string, ProjectPlanTask>()
  for (const group of planStageGroups(owner)) {
    const window = windowByStage.get(group.code)
    if (!window) continue
    const stageStart = group.tasks.map(task => task.plannedStart).sort()[0]!
    const stageFinish = group.tasks.map(task => task.plannedFinish).sort().at(-1)!
    const oldSpan = dayDiff(stageStart, stageFinish) + 1
    const newSpan = window.durationDays
    for (const task of group.tasks) {
      if (group.tasks.length === 1 && !task.isMilestone) {
        proposed.set(task.id, { ...task, plannedStart: window.startDate, plannedFinish: addDays(window.startDate, newSpan - 1), durationDays: newSpan })
        continue
      }
      const mapOffset = (date: string) => oldSpan <= 1 ? 0 : Math.round(dayDiff(stageStart, date) * (newSpan - 1) / (oldSpan - 1))
      const plannedStart = addDays(window.startDate, mapOffset(task.plannedStart))
      const plannedFinish = task.isMilestone ? plannedStart : addDays(window.startDate, mapOffset(task.plannedFinish))
      proposed.set(task.id, { ...task, plannedStart, plannedFinish: plannedFinish < plannedStart ? plannedStart : plannedFinish,
        durationDays: task.isMilestone ? 0 : dayDiff(plannedStart, plannedFinish < plannedStart ? plannedStart : plannedFinish) + 1 })
    }
  }
  const byId = new Map(owner.tasks.map(task => [task.id, task]))
  const resolved = new Map<string, ProjectPlanTask>()
  const visiting = new Set<string>()
  const resolve = (task: ProjectPlanTask): ProjectPlanTask => {
    if (resolved.has(task.id)) return resolved.get(task.id)!
    if (visiting.has(task.id)) throw new Error('计划任务存在循环前置依赖')
    visiting.add(task.id)
    const predecessors = task.predecessorTaskIds.flatMap(id => byId.get(id) ? [resolve(byId.get(id)!)] : [])
    const mapped = proposed.get(task.id) ?? task
    const earliestStart = predecessors.length ? predecessors.map(item => addDays(item.plannedFinish, 1)).sort().at(-1)! : mapped.plannedStart
    const plannedStart = mapped.plannedStart < earliestStart ? earliestStart : mapped.plannedStart
    const durationDays = mapped.isMilestone ? 0 : dayDiff(mapped.plannedStart, mapped.plannedFinish) + 1
    const plannedFinish = mapped.isMilestone ? plannedStart
      : mapped.plannedStart < earliestStart ? addDays(plannedStart, durationDays - 1) : mapped.plannedFinish
    const result = { ...mapped, plannedStart, plannedFinish, durationDays: mapped.isMilestone ? 0 : dayDiff(plannedStart, plannedFinish) + 1 }
    visiting.delete(task.id)
    resolved.set(task.id, result)
    return result
  }
  return owner.tasks.map(resolve)
}

function stageRows(owner: ProjectPlan, level: number): TimelineRow[] {
  return planStageGroups(owner).flatMap(stage => {
    const key = `stage-${owner.projectId}-${stage.code}`
    const window = stageWindow(owner, stage.code)
    const rows: TimelineRow[] = [{
      key, name: stage.name, level, stage: stage.code, isStage: true, taskCount: stage.tasks.length,
      projectId: owner.projectId, plan: owner, completion: stage.progress,
      start: window?.startDate ?? stage.tasks.map(task => task.plannedStart).sort()[0],
      finish: window ? addDays(window.startDate, window.durationDays - 1) : stage.tasks.map(task => task.plannedFinish).sort().at(-1),
      actualFinish: stage.tasks.every(task => task.completionPercent === 100 && task.actualFinish)
        ? stage.tasks.map(task => task.actualFinish!).sort().at(-1) : undefined,
      baselineStart: stage.tasks.flatMap(task => task.baselineStart ? [task.baselineStart] : []).sort()[0],
      baselineFinish: stage.tasks.flatMap(task => task.baselineFinish ? [task.baselineFinish] : []).sort().at(-1),
    }]
    if (!collapsedStages.value.has(key)) rows.push(...stage.tasks.map(task => taskRow(task, owner, owner.projectId, level + 1)))
    return rows
  })
}
const stageProgressGroups = computed(() => displayedPlans.value.map(owner => {
  const project = props.projects.find(item => item.id === owner.projectId) ?? props.project
  return { projectId: owner.projectId, label: `${project.code} · ${project.name}`, stages: planStageGroups(owner) }
}))
const stageCount = computed(() => stageProgressGroups.value.reduce((total, group) => total + group.stages.length, 0))
const completedStageCount = computed(() => stageProgressGroups.value.reduce((total, group) => total + group.stages.filter(stage => stage.progress === 100 && stage.tasks.every(task => !task.isRequired || task.completionPercent === 100)).length, 0))
const stageKeys = computed(() => stageProgressGroups.value.flatMap(group => group.stages.map(stage => `stage-${group.projectId}-${stage.code}`)))
const expandedPlanProjectIds = computed(() => new Set([...expanded.value].filter(projectId => displayedPlans.value.some(owner => owner.projectId === projectId))))
const allStagesCollapsed = computed(() => portfolioMode.value
  ? stageKeys.value.length > 0 && expandedPlanProjectIds.value.size === 0
  : stageKeys.value.length > 0 && stageKeys.value.every(key => collapsedStages.value.has(key)))
function toggleStage(key: string) {
  const next = new Set(collapsedStages.value)
  next.has(key) ? next.delete(key) : next.add(key)
  collapsedStages.value = next
}
function toggleAllStages() {
  if (portfolioMode.value) {
    const expanding = allStagesCollapsed.value
    expanded.value = expanding ? new Set(displayedPlans.value.map(owner => owner.projectId)) : new Set()
    collapsedStages.value = expanding ? new Set() : new Set(stageKeys.value)
    return
  }
  collapsedStages.value = allStagesCollapsed.value ? new Set() : new Set(stageKeys.value)
}

const timelineRows = computed<TimelineRow[]>(() => {
  if (!portfolioMode.value) return plan.value ? stageRows(plan.value, 0) : []
  const rows: TimelineRow[] = []
  const rootItem = portfolio.value?.projects.find(item => item.isRoot)
  for (const item of portfolio.value?.projects ?? []) {
    const isInherited = !item.isRoot && !item.hasPlan && Boolean(rootItem?.plan)
    const summary = isInherited ? rootItem! : item
    rows.push({
      key: `project-${item.projectId}`, name: item.projectName, code: item.projectCode, level: 0,
      stage: summary.currentStage, start: summary.plannedStart, finish: summary.plannedFinish,
      completion: summary.completionPercent, isLagging: summary.isLagging, isAtRisk: summary.isAtRisk,
      isInherited, plan: item.plan, projectId: item.projectId,
    })
    if (expanded.value.has(item.projectId) && item.plan) rows.push(...stageRows(item.plan, 1))
  }
  return rows
})

const timelineBounds = computed(() => {
  // Hidden task baselines still participate so folding a group never changes the time scale.
  const rows = [...timelineRows.value, ...displayedPlans.value.flatMap(owner => owner.tasks.map(task => taskRow(task, owner, owner.projectId, 0)))]
  const values = rows.flatMap(row => [row.start, row.finish, row.baselineStart, row.baselineFinish].filter((item): item is string => Boolean(item)))
  const today = isoDate(new Date())
  if (!values.length) {
    const start = isoWeekStart(addDays(today, -7))
    const end = addDays(isoWeekStart(addDays(today, 28)), 6)
    return { start, end, days: dayDiff(start, end) + 1 }
  }
  const start = isoWeekStart(addDays(values.sort()[0], -3))
  const end = addDays(isoWeekStart(addDays(values.sort().at(-1)!, 5)), 6)
  return { start, end, days: dayDiff(start, end) + 1 }
})
const dayWidth = computed(() => timelineWidth.value / timelineBounds.value.days)
type TimelineBand = { key: string; label: string; left: number; width: number }
function buildTimelineBands(part: 'year' | 'month') {
  const bands: TimelineBand[] = []
  let bandStart = 0
  let bandKey = ''
  for (let index = 0; index <= timelineBounds.value.days; index += 1) {
    const date = index < timelineBounds.value.days ? dateValue(addDays(timelineBounds.value.start, index)) : null
    const key = date ? part === 'year' ? String(date.getFullYear()) : `${date.getFullYear()}-${date.getMonth() + 1}` : ''
    if (index > 0 && key !== bandKey) {
      const startDate = dateValue(addDays(timelineBounds.value.start, bandStart))
      bands.push({
        key: `${part}-${bandKey}-${bandStart}`,
        label: part === 'year' ? `${startDate.getFullYear()}年` : `${startDate.getMonth() + 1}月`,
        left: bandStart * dayWidth.value,
        width: (index - bandStart) * dayWidth.value,
      })
      bandStart = index
    }
    if (date) bandKey = key
  }
  return bands
}
const yearBands = computed(() => buildTimelineBands('year'))
const monthBands = computed(() => buildTimelineBands('month'))
const calendarMarkers = computed(() => {
  const markers: Array<{ date: string; left: number; width: number; tone: 'sunday' | 'holiday' | 'workday'; title: string }> = []
  for (let index = 0; index < timelineBounds.value.days; index += 1) {
    const date = addDays(timelineBounds.value.start, index)
    const override = holidayCalendarDays.value.get(date)
    const isSunday = dateValue(date).getDay() === 0
    if (!override && !isSunday) continue
    const tone = override ? override.isOffDay ? 'holiday' : 'workday' : 'sunday'
    const title = override
      ? `${date} · ${override.name}${override.isOffDay ? '（法定节假日）' : '调休上班'}`
      : `${date} · 周日`
    markers.push({ date, left: index * dayWidth.value, width: dayWidth.value, tone, title })
  }
  return markers
})
const tickStep = computed(() => {
  if (zoom.value === 'week') return 7
  const minimumStep = zoom.value === 'month' ? 14 : 1
  const minimumSpacing = zoom.value === 'month' ? 72 : 14
  const requiredStep = Math.max(minimumStep, Math.ceil(minimumSpacing / dayWidth.value))
  const preferredSteps = [1, 2, 3, 5, 7, 10, 14, 21, 28, 42, 56, 84]
  return preferredSteps.find(step => step >= requiredStep) ?? Math.ceil(requiredStep / 28) * 28
})
const ticks = computed(() => {
  const result: Array<{ date: string; endDate: string; left: number; width: number }> = []
  for (let index = 0; index < timelineBounds.value.days; index += tickStep.value) {
    if (index > 0 && index * dayWidth.value + 12 > timelineWidth.value) break
    const date = addDays(timelineBounds.value.start, index)
    const days = Math.min(tickStep.value, timelineBounds.value.days - index)
    const endDate = addDays(date, days - 1)
    result.push({ date, endDate, left: index * dayWidth.value, width: days * dayWidth.value })
  }
  return result
})
const todayDate = computed(() => isoDate(new Date()))
const todayLeft = computed(() => dayDiff(timelineBounds.value.start, todayDate.value) * dayWidth.value)
const todayLabel = computed(() => '今天')
const overallProgress = computed(() => portfolioMode.value ? portfolio.value?.completionPercent ?? 0 : isEffective(plan.value) ? planProgress(plan.value?.tasks ?? [], plan.value?.stages) : 0)
const progressLabel = computed(() => {
  const visiblePlans = portfolioMode.value ? (portfolio.value?.projects ?? []).flatMap(item => item.plan ? [item.plan] : []) : plan.value ? [plan.value] : []
  const allocated = visiblePlans.filter(item => hasStageAllocation(item.stages)).length
  return allocated === 0 ? '总体进度（原规则）' : allocated === visiblePlans.length ? '交付进度' : '进度（含原规则计划）'
})
const resourceConflicts = computed(() => {
  const visiblePlans = portfolioMode.value ? (portfolio.value?.projects ?? []).flatMap(item => item.plan ? [item.plan] : []) : plan.value ? [plan.value] : []
  const tasks = visiblePlans.flatMap(item => item.tasks).filter(task => task.assignee && !task.isMilestone && task.completionPercent < 100)
  return tasks.flatMap((task, index) => tasks.slice(index + 1).filter(other => task.assignee === other.assignee && task.plannedStart <= other.plannedFinish && other.plannedStart <= task.plannedFinish).map(other => `${task.name} / ${other.name}（${displayUserName(task.assignee!)}）`))
})
const activeStage = computed(() => portfolioMode.value ? portfolio.value?.currentStage : plan.value?.currentStage)
const activeStageSummary = computed(() => {
  if (!hasEffectivePlans.value) return '待计划生效'
  if (portfolioMode.value || plan.value?.manualStage || !plan.value) return stageLabel(activeStage.value)
  const active = planStageGroups(plan.value).filter(stage => stage.tasks.some(task => task.status === 'InProgress'
    || (task.status !== 'Completed' && task.plannedStart <= todayDate.value && task.plannedFinish >= todayDate.value)))
  return active.length ? active.map(stage => stage.name).join('、') : stageLabel(activeStage.value, plan.value)
})
const displayedDateRange = computed(() => {
  const start = portfolioMode.value ? portfolio.value?.plannedStart : plan.value?.plannedStart
  const finish = portfolioMode.value ? portfolio.value?.plannedFinish : plan.value?.plannedFinish
  return start && finish ? `${start} — ${finish}` : '尚未生成计划'
})

function taskRow(task: ProjectPlanTask, taskPlan: ProjectPlan, projectId: string, level: number): TimelineRow {
  return {
    key: `task-${projectId}-${task.id}`, name: task.name, level, stage: task.stage, start: task.plannedStart, finish: task.plannedFinish,
    actualFinish: task.actualFinish,
    baselineStart: task.baselineStart, baselineFinish: task.baselineFinish, completion: task.completionPercent,
    isMilestone: task.isMilestone, task, plan: taskPlan, projectId,
  }
}

function stageLabel(stage?: ProjectPlanStage, owner?: ProjectPlan | null) {
  return (owner?.stages ?? plan.value?.stages)?.find(item => item.code === stage)?.name
    ?? portfolio.value?.projects.find(item => item.currentStage === stage)?.plan?.stages?.find(item => item.code === stage)?.name
    ?? stageOptions.find(item => item.value === stage)?.label ?? stage ?? '未排程'
}

function stageTone(stage?: ProjectPlanStage) {
  if (stage === 'Paused') return 'is-warning'
  if (stage === 'Cancelled' || stage === 'Terminated') return 'is-danger'
  if (stage === 'FinalAcceptance') return 'is-complete'
  return 'is-active'
}

type BarDragMode = 'move' | 'resize-start' | 'resize-finish'
const barDrag = ref<{ row: TimelineRow; mode: BarDragMode; originX: number; days: number; pointerId: number; element: HTMLElement } | null>(null)
function canResizeStage(owner?: ProjectPlan | null) {
  return canEditSchedule(owner)
}
function stageIndex(row: TimelineRow) {
  return row.plan && row.stage ? stageWindows(row.plan).findIndex(window => window.stage === row.stage) : -1
}
function canResizeStageEdge(row: TimelineRow, mode: BarDragMode) {
  const index = stageIndex(row)
  return row.isStage && canResizeStage(row.plan) && index >= 0 && (mode === 'resize-start' || mode === 'resize-finish')
}
function resizedStageWindows(drag: NonNullable<typeof barDrag.value>) {
  const windows = stageWindows(drag.row.plan!).map(window => ({ ...window }))
  const index = windows.findIndex(window => window.stage === drag.row.stage)
  if (index < 0) return windows
  if (drag.mode === 'move') {
    windows[index]!.startDate = addDays(windows[index]!.startDate, drag.days)
    return windows
  }
  if (drag.mode === 'resize-start') {
    windows[index]!.startDate = addDays(windows[index]!.startDate, drag.days)
    windows[index]!.durationDays -= drag.days
  } else {
    windows[index]!.durationDays += drag.days
  }
  return windows
}
function clampBarDragDays(row: TimelineRow, mode: BarDragMode, days: number) {
  if (row.isStage && row.plan && row.stage) {
    const windows = stageWindows(row.plan)
    const index = windows.findIndex(window => window.stage === row.stage)
    if (index < 0) return 0
    if (mode === 'move') return days
    const currentMinimum = stageMinimumDays(row.plan, row.stage)
    return mode === 'resize-start'
      ? Math.min(days, windows[index]!.durationDays - currentMinimum)
      : Math.max(days, currentMinimum - windows[index]!.durationDays)
  }
  if (!row.start || !row.finish || mode === 'move') return days
  const durationDays = dayDiff(row.start, row.finish) + 1
  return mode === 'resize-start' ? Math.min(days, durationDays - 1) : Math.max(days, 1 - durationDays)
}
function startBarDrag(event: PointerEvent, row: TimelineRow, mode: BarDragMode = 'move') {
  if (event.button !== 0 || !row.plan || saving.value) return
  if (row.isStage ? mode === 'move' ? !canResizeStage(row.plan) : !canResizeStageEdge(row, mode) : !row.task || !canEditSchedule(row.plan) || (mode !== 'move' && row.isMilestone)) return
  const element = event.currentTarget as HTMLElement
  element.setPointerCapture(event.pointerId)
  barDrag.value = { row, mode, originX: event.clientX, days: 0, pointerId: event.pointerId, element }
}
function moveBarDrag(event: PointerEvent) {
  const drag = barDrag.value
  if (drag && drag.pointerId === event.pointerId) drag.days = clampBarDragDays(drag.row, drag.mode, Math.round((event.clientX - drag.originX) / dayWidth.value))
}
function cancelBarDrag() { barDrag.value = null }
async function finishBarDrag(event: PointerEvent) {
  const drag = barDrag.value
  if (!drag || drag.pointerId !== event.pointerId) return
  barDrag.value = null
  drag.element.releasePointerCapture(event.pointerId)
  const owner = drag.row.plan!
  if (!drag.days) return
  if (drag.row.isStage) {
    const windows = resizedStageWindows(drag)
    saving.value = true
    try {
      const tasks = redistributeTasksToStageWindows(owner, windows)
      const index = windows.findIndex(window => window.stage === drag.row.stage)
      const boundary = drag.mode === 'resize-start' ? windows[index]!.startDate : addDays(windows[index]!.startDate, windows[index]!.durationDays - 1)
      const action = drag.mode === 'move' ? '整体平移' : drag.mode === 'resize-start' ? '开始边界' : '结束边界'
      const description = `拖动“${drag.row.name}”阶段${action}至 ${boundary}，仅重排本阶段任务；显式前置关系按依赖联动，其他阶段不再保持首尾连续`
      await saveProjectPlan(owner.projectId, { tasks, changeReason: description, expectedRowVersion: owner.rowVersion }, props.token)
      ElMessage.success(isEffective(owner) ? '变更草稿已保存，原计划继续生效' : '本阶段及依赖任务排期已保存')
      await load()
    } catch (reason) {
      if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '阶段排期保存失败')
    } finally { saving.value = false }
    return
  }
  const task = drag.row.task!
  const start = drag.mode === 'resize-finish' ? task.plannedStart : addDays(task.plannedStart, drag.days)
  const finish = drag.mode === 'resize-start' ? task.plannedFinish : addDays(task.plannedFinish, drag.days)
  saving.value = true
  try {
    const resizing = drag.mode !== 'move'
    const description = `${task.name}：${task.plannedStart} ～ ${task.plannedFinish} → ${start} ～ ${finish}。${resizing ? `工期由 ${dayDiff(task.plannedStart, task.plannedFinish) + 1} 天调整为 ${dayDiff(start, finish) + 1} 天` : '工期不变'}，其他任务不自动移动。`
    const reason = `${resizing ? '拖动调整工期' : '拖动排期'}：${description}`
    await saveProjectPlan(owner.projectId, { tasks: owner.tasks.map(item => item.id === task.id ? { ...item, plannedStart: start, plannedFinish: finish } : item), changeReason: reason, expectedRowVersion: owner.rowVersion }, props.token)
    await load()
    ElMessage.success(resizing ? '任务工期已保存' : '任务排期已保存')
  } catch (reason) {
    if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '拖动排期保存失败')
  } finally { saving.value = false }
}

function rowBarStyle(row: TimelineRow) {
  if (!row.start || !row.finish) return { display: 'none' }
  const activeDrag = barDrag.value
  const previewWindow = row.isStage && row.plan && activeDrag?.row.isStage && activeDrag.row.plan?.id === row.plan.id
    ? resizedStageWindows(activeDrag).find(window => window.stage === row.stage) : null
  const start = previewWindow?.startDate ?? row.start
  const finish = previewWindow ? addDays(previewWindow.startDate, previewWindow.durationDays - 1) : row.finish
  const drag = !row.isStage && activeDrag?.row.key === row.key ? activeDrag : null
  const leftDays = dayDiff(timelineBounds.value.start, start) + (drag?.mode === 'move' || drag?.mode === 'resize-start' ? drag.days : 0)
  const widthDays = dayDiff(start, finish) + 1 + (drag?.mode === 'resize-start' ? -drag.days : drag?.mode === 'resize-finish' ? drag.days : 0)
  const left = leftDays * dayWidth.value
  const width = row.isMilestone ? 12 : Math.max(8, widthDays * dayWidth.value)
  return { left: `${left}px`, width: `${width}px`, '--progress': `${row.completion}%` }
}

function rowBarLabelStyle(row: TimelineRow) {
  const style = rowBarStyle(row)
  if (style.display === 'none') return style
  return { left: `calc(${style.left} + ${style.width} + 6px)` }
}

function baselineStyle(row: TimelineRow) {
  if (!row.baselineStart || !row.baselineFinish) return { display: 'none' }
  return {
    left: `${dayDiff(timelineBounds.value.start, row.baselineStart) * dayWidth.value}px`,
    width: `${Math.max(6, (dayDiff(row.baselineStart, row.baselineFinish) + 1) * dayWidth.value)}px`,
  }
}

function toggleExpanded(projectId: string) {
  const next = new Set(expanded.value)
  next.has(projectId) ? next.delete(projectId) : next.add(projectId)
  expanded.value = next
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [loadedTemplates, loadedPlan, loadedPortfolio, loadedParentPlan] = await Promise.all([
      listProjectPlanTemplates(props.token, false),
      readProjectPlan(props.project.id, props.token),
      readProjectPlanPortfolio(props.project.id, props.token),
      props.project.parentProjectId ? readProjectPlan(rootProject.value.id, props.token) : Promise.resolve(null),
    ])
    templates.value = loadedTemplates
    inheritedParentPlan.value = Boolean(!loadedPlan && loadedParentPlan)
    plan.value = loadedPlan ?? loadedParentPlan
    portfolio.value = loadedPortfolio
    if (!generateForm.templateId) generateForm.templateId = loadedTemplates.find(item => item.isActive && (!item.projectTypeCode || item.projectTypeCode === props.project.projectTypeCode))?.id ?? loadedTemplates[0]?.id ?? ''
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '项目计划加载失败'
  } finally {
    loading.value = false
  }
}

function openGenerate(targetPlan: ProjectPlan | null = plan.value, targetProjectId = props.project.id, scheduleSource = targetPlan) {
  generateTargetProjectId.value = targetProjectId
  const targetProject = props.projects.find(item => item.id === targetProjectId)
  generateForm.templateId = templates.value.find(item => item.isActive && (!item.projectTypeCode || item.projectTypeCode === targetProject?.projectTypeCode))?.id ?? templates.value.find(item => item.isActive)?.id ?? ''
  const deliverySchedules = scheduleSource?.stageSchedules?.filter(item => scheduleSource.stages?.some(stage => stage.code === item.stage && stage.participatesInDelivery)) ?? []
  generateForm.startDate = deliverySchedules[0]?.startDate ?? scheduleSource?.plannedStart ?? isoDate(new Date())
  generateForm.totalDurationDays = deliverySchedules.length ? deliverySchedules.reduce((sum, item) => sum + item.durationDays, 0) : scheduleSource ? Math.max(1, dayDiff(scheduleSource.plannedStart, scheduleSource.plannedFinish) + 1) : 60
  generateForm.replaceExisting = Boolean(targetPlan)
  generateForm.changeReason = ''
  generateDialogOpen.value = true
}

async function generate() {
  if (!generateForm.templateId) return ElMessage.warning('请选择计划模板')
  if (postDeliveryDateError.value) return ElMessage.warning(postDeliveryDateError.value)
  saving.value = true
  try {
    const targetProjectId = generateTargetProjectId.value || props.project.id
    const generated = await generateProjectPlan(targetProjectId, {
      ...generateForm,
      independentStages: scheduledPostDeliveryStages.value.filter(stage => !stage.deferred).map(({ stage, startDate, durationDays }) => ({ stage, startDate, durationDays })),
      deferredStages: independentSchedules.value.filter(stage => stage.deferred).map(stage => stage.stage),
    }, props.token)
    if (targetProjectId === props.project.id) {
      plan.value = generated
      inheritedParentPlan.value = false
    }
    generateDialogOpen.value = false
    ElMessage.success(targetProjectId === props.project.id ? '计划草稿已生成，调整确认后请提交事业部总经理审批' : '子项目独立计划草稿已生成')
    await load()
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '计划生成失败')
  } finally {
    saving.value = false
  }
}

function openTask(row: TimelineRow) {
  if (inheritedParentPlan.value) return
  if (!row.task || !row.plan) {
    if (row.projectId !== props.project.id) emit('switchProject', row.projectId)
    return
  }
  selectedTask.value = row.task
  Object.assign(taskForm, {
    name: row.task.name, stage: row.task.stage, assignee: row.task.assignee ?? '', plannedStart: row.task.plannedStart,
    plannedFinish: row.task.plannedFinish, completionPercent: row.task.completionPercent,
    actualStart: row.task.actualStart ?? '', actualFinish: row.task.actualFinish ?? '', weight: row.task.weight, isRequired: row.task.isRequired,
  })
  taskDialogOpen.value = true
}

async function saveTask() {
  const task = selectedTask.value
  const ownerPlan = timelineRows.value.find(row => row.task?.id === task?.id)?.plan
  if (!task || !ownerPlan) return
  if (invalidActualDates(taskForm.actualStart, taskForm.actualFinish)) return ElMessage.warning('实际开始和实际完成不能晚于今天')
  if (!taskForm.plannedStart || !taskForm.plannedFinish || taskForm.plannedFinish < taskForm.plannedStart) return ElMessage.warning('请选择有效的计划日期区间')
  const scheduleChanged = (task.assignee ?? '') !== taskForm.assignee.trim()
    || task.plannedStart !== taskForm.plannedStart || task.plannedFinish !== taskForm.plannedFinish || task.isRequired !== taskForm.isRequired
  if (scheduleChanged && !canEditSelectedPlan.value) return ElMessage.error('仅该项目的主项目经理或开发者可以修改计划安排')
  saving.value = true
  try {
    let saved: ProjectPlan
    if (scheduleChanged || !isEffective(ownerPlan)) {
      const tasks = ownerPlan.tasks.map(item => item.id === task.id ? {
        ...item, assignee: taskForm.assignee.trim() || undefined,
        plannedStart: taskForm.plannedStart, plannedFinish: taskForm.plannedFinish, isRequired: taskForm.isRequired,
      } : item)
      saved = await saveProjectPlan(ownerPlan.projectId, { tasks, changeReason: isEffective(ownerPlan) ? '保存计划安排' : '', expectedRowVersion: ownerPlan.rowVersion }, props.token)
    } else {
      saved = await updateProjectPlanTaskProgress(ownerPlan.projectId, task.id, {
        completionPercent: taskForm.completionPercent,
        actualStart: taskForm.actualStart || undefined,
        actualFinish: taskForm.actualFinish || undefined,
        expectedRowVersion: ownerPlan.rowVersion,
      }, props.token)
    }
    if (saved.projectId === props.project.id) plan.value = saved
    taskDialogOpen.value = false
    ElMessage.success(!isEffective(ownerPlan) ? '草稿已保存；需重新提交并批准后生效' : scheduleChanged ? '变更草稿已保存，原计划继续生效' : '实际进度已更新，项目阶段已重新判断')
    await load()
  } catch (reason) {
    if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '任务保存失败')
  } finally {
    saving.value = false
  }
}

async function shiftPlan() {
  if (!plan.value) return
  try {
    const daysInput = await ElMessageBox.prompt('输入正数向后顺延，输入负数向前调整。基线不会移动。', '批量调整计划（自然日）', { confirmButtonText: '下一步', cancelButtonText: '取消', inputPattern: /^-?\d+$/, inputErrorMessage: '请输入整数天数' })
    const days = Number(daysInput.value)
    if (!days) return
    plan.value = await saveProjectPlan(props.project.id, {
      tasks: plan.value.tasks.map(task => ({ ...task, plannedStart: addDays(task.plannedStart, days), plannedFinish: addDays(task.plannedFinish, days) })),
      changeReason: '批量调整计划',
      expectedRowVersion: plan.value.rowVersion,
    }, props.token)
    ElMessage.success(`计划已整体${days > 0 ? '顺延' : '提前'}${Math.abs(days)}天`)
    await load()
  } catch (reason) {
    if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '批量调整失败')
  }
}

async function openVersions() {
  if (!plan.value) return
  try {
    versions.value = await listProjectPlanVersions(props.project.id, props.token)
    compareBaseKey.value = plan.value.changeDraftSource ? 'source' : versions.value[0]?.id ?? 'current'
    compareTargetKey.value = 'current'
    versionsDialogOpen.value = true
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '计划版本加载失败')
  }
}

const comparisonOptions = computed(() => {
  const options: Array<{ key: string; label: string; snapshot: ProjectPlan }> = []
  if (plan.value) options.push({ key: 'current', label: editingChangeDraft.value ? '当前变更草稿' : '当前生效计划', snapshot: plan.value })
  if (plan.value?.changeDraftSource) options.push({ key: 'source', label: '本次变更前的现行计划', snapshot: plan.value.changeDraftSource })
  options.push(...versions.value.map(item => ({ key: item.id, label: `V${item.versionNumber} · ${item.changeReason}`, snapshot: item.snapshot })))
  return options
})
const comparisonRows = computed(() => {
  const before = comparisonOptions.value.find(item => item.key === compareBaseKey.value)?.snapshot
  const after = comparisonOptions.value.find(item => item.key === compareTargetKey.value)?.snapshot
  if (!before || !after) return []
  const ids = [...new Set([...before.tasks.map(task => task.id), ...after.tasks.map(task => task.id)])]
  return ids.map(id => {
    const old = before.tasks.find(task => task.id === id)
    const next = after.tasks.find(task => task.id === id)
    const changed = !old || !next || old.plannedStart !== next.plannedStart || old.plannedFinish !== next.plannedFinish
      || old.assignee !== next.assignee || old.completionPercent !== next.completionPercent || old.actualFinish !== next.actualFinish
    return { id, name: next?.name ?? old?.name ?? '未知任务', stage: next?.stage ?? old?.stage,
      old, next, state: !old ? '新增' : !next ? '删除' : changed ? '变更' : '未变' }
  }).filter(row => row.state !== '未变')
})

function openStage() {
  stageForm.stage = hasStageException.value ? '' : (plan.value?.manualStage ?? '') as ProjectPlanStage | ''
  stageForm.reason = ''
  stageDialogOpen.value = true
}

async function saveStage() {
  if (!plan.value || !stageForm.reason.trim()) return ElMessage.warning('请填写阶段调整原因')
  saving.value = true
  try {
    plan.value = await setProjectPlanStage(props.project.id, { stage: stageForm.stage || undefined, reason: stageForm.reason, expectedRowVersion: plan.value.rowVersion }, props.token)
    stageDialogOpen.value = false
    ElMessage.success(stageForm.stage ? '例外阶段已更新' : '已恢复按任务条件自动判断阶段')
    await load()
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '阶段调整失败')
  } finally {
    saving.value = false
  }
}


function openReuse() {
  reuseForm.sourceProjectId = sourceProjectOptions.value[0]?.projectId ?? ''
  reuseForm.targetProjectIds = targetProjectOptions.value.filter(item => !item.hasPlan).map(item => item.projectId)
  reuseForm.replaceExisting = false
  reuseForm.changeReason = '同批设备复用标准计划时间'
  reuseDialogOpen.value = true
}

function targetSelectable(item: NonNullable<ProjectPlanPortfolio['projects']>[number]) {
  return !isEffective(item.plan) && (!item.hasPlan || reuseForm.replaceExisting)
}

const selectableTargets = computed(() => targetProjectOptions.value.filter(targetSelectable))
const allTargetsSelected = computed(() => selectableTargets.value.length > 0 && selectableTargets.value.every(item => reuseForm.targetProjectIds.includes(item.projectId)))
function selectAllTargets(checked: boolean) {
  reuseForm.targetProjectIds = checked ? selectableTargets.value.map(item => item.projectId) : []
}
watch(() => reuseForm.replaceExisting, () => {
  reuseForm.targetProjectIds = reuseForm.targetProjectIds.filter(id => selectableTargets.value.some(item => item.projectId === id))
})

async function submitApproval() {
  if (!plan.value) return
  saving.value = true
  try {
    await ElMessageBox.confirm('提交给项目执行事业部总经理审批。审批人处理前仍可修改；修改会自动撤回本次审批并回到草稿。', '提交首版计划审批', { confirmButtonText: '提交审批', cancelButtonText: '取消' })
    plan.value = await submitProjectPlan(props.project.id, plan.value.rowVersion, props.token)
    ElMessage.success('已提交事业部总经理审批')
    await load()
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '提交失败') }
  finally { saving.value = false }
}

const changeDialogOpen = ref(false)
const changeReviewOpen = ref(false)
const changeReason = ref('')
const changeSubmitError = ref<{ type: string; message: string } | null>(null)
const changeVersion = ref(0)
const pendingChange = computed(() => plan.value?.changeRequest?.status === 'Pending' ? plan.value.changeRequest : null)
const editingChangeDraft = computed(() => Boolean(plan.value?.changeDraftSource && plan.value.changeRequest?.status === 'Approved'))
const canApproveChange = computed(() => pendingChange.value?.approvalAssignee.toLowerCase() === props.currentUsername.toLowerCase())
function openChangeRequest() {
  if (!plan.value || !isEffective(plan.value) || !props.canEdit || pendingChange.value || editingChangeDraft.value) return
  changeVersion.value = plan.value.rowVersion
  changeReason.value = ''
  changeSubmitError.value = null
  changeDialogOpen.value = true
}
async function submitChangeRequest() {
  changeSubmitError.value = null
  if (!plan.value) return
  if (!changeReason.value.trim()) {
    changeSubmitError.value = { type: '表单校验失败', message: '请填写变更原因' }
    return
  }
  saving.value = true
  try {
    await submitProjectPlanChange(props.project.id, { tasks: [], reason: changeReason.value.trim(), expectedRowVersion: changeVersion.value }, props.token)
    changeDialogOpen.value = false
    await load()
    ElMessage.success('变更权限申请已提交，原计划继续生效')
  } catch (reason) {
    changeSubmitError.value = { type: '变更申请提交失败', message: reason instanceof Error ? reason.message : '请稍后重试' }
  }
  finally { saving.value = false }
}
async function decideChange(approve: boolean) {
  if (!plan.value || !canApproveChange.value) return
  saving.value = true
  try {
    const result = await ElMessageBox.prompt(approve ? '批准后申请人获得一次变更权限并从现行计划创建草稿；草稿完成前原计划继续生效。' : '请填写退回原因；原计划不受影响。', approve ? '批准变更权限' : '退回变更权限', { confirmButtonText: approve ? '批准权限' : '退回', cancelButtonText: '取消', ...(approve ? {} : { inputPattern: /\S+/, inputErrorMessage: '请输入退回原因' }) })
    await decideProjectPlan(props.project.id, { expectedRowVersion: plan.value.rowVersion, approve, comment: result.value }, props.token)
    changeReviewOpen.value = false
    await load()
    ElMessage.success(approve ? '变更权限已批准，已创建可编辑草稿' : '变更权限已退回，原计划不变')
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '审批失败') }
  finally { saving.value = false }
}

async function completeChangeDraft() {
  if (!plan.value || !editingChangeDraft.value) return
  try {
    await ElMessageBox.confirm('完成后，本草稿将替换当前生效计划；跟随主计划的未审批子项目同步更新，独立或已生效子项目保留并列出差异。', '完成变更并生效', { confirmButtonText: '完成并生效', cancelButtonText: '取消' })
    saving.value = true
    const activated = await completeProjectPlanChange(props.project.id, plan.value.rowVersion, props.token)
    plan.value = activated
    await load()
    ElMessage.success(`变更已完成并生效，已自动形成基线V${activated.baselineVersion}`)
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '完成变更失败') }
  finally { saving.value = false }
}

async function abandonChangeDraft() {
  if (!plan.value || !editingChangeDraft.value) return
  try {
    await ElMessageBox.confirm('放弃后删除本次变更草稿并恢复现行计划；已发生的实际进度不会丢失。', '放弃本次变更', { confirmButtonText: '确认放弃', cancelButtonText: '继续编辑', type: 'warning' })
    saving.value = true
    plan.value = await abandonProjectPlanChange(props.project.id, plan.value.rowVersion, props.token)
    await load()
    ElMessage.success('变更草稿已放弃，原计划继续生效')
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '放弃变更失败') }
  finally { saving.value = false }
}

async function decideApproval(approve: boolean) {
  if (!plan.value) return
  saving.value = true
  try {
    const result = await ElMessageBox.prompt(approve ? '批准后首版计划正式生效，并自动冻结基线V1。' : '请填写退回原因，项目经理可修改后重新提交。', approve ? '批准首版计划' : '退回首版计划', { confirmButtonText: approve ? '批准生效' : '退回', cancelButtonText: '取消', ...(approve ? {} : { inputPattern: /\S+/, inputErrorMessage: '请输入退回原因' }) })
    plan.value = await decideProjectPlan(props.project.id, { expectedRowVersion: plan.value.rowVersion, approve, comment: result.value }, props.token)
    ElMessage.success(approve ? '首版计划已批准生效，基线V1已冻结' : '计划已退回')
    await load()
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '审批失败') }
  finally { saving.value = false }
}


function addStage(stages: ProjectPlanStageDefinition[]) {
  stages.push({ code: crypto.randomUUID(), name: `新阶段${stages.length + 1}` })
}
function moveStage(stages: ProjectPlanStageDefinition[], index: number, offset: number) {
  const next = index + offset
  if (next < 0 || next >= stages.length) return
  const [item] = stages.splice(index, 1)
  stages.splice(next, 0, item!)
}
function removeStage(stages: ProjectPlanStageDefinition[], index: number, tasks: Array<{ stage: string }>) {
  if (tasks.some(item => item.stage === stages[index]?.code)) return ElMessage.warning('请先将该阶段的任务分配到其他阶段')
  stages.splice(index, 1)
}
function openStageConfiguration() {
  if (!plan.value) return
  stageConfigurationPlan.value = plan.value
  stagesDraft.value = JSON.parse(JSON.stringify(plan.value.stages ?? defaultStages))
  stagesDialogOpen.value = true
}
async function saveStageConfiguration() {
  const owner = stageConfigurationPlan.value
  if (!owner) return
  saving.value = true
  try {
    const reason = isEffective(owner) ? '保存变更草稿阶段配置' : ''
    await saveProjectPlan(owner.projectId, { tasks: owner.tasks, stages: stagesDraft.value, changeReason: reason, expectedRowVersion: owner.rowVersion }, props.token)
    stagesDialogOpen.value = false
    await load()
    ElMessage.success('项目阶段配置已保存')
  } catch (reason) { if (reason !== 'cancel' && reason !== 'close') ElMessage.error(reason instanceof Error ? reason.message : '阶段配置保存失败') }
  finally { saving.value = false }
}

async function reusePlan() {
  if (!reuseForm.sourceProjectId) return ElMessage.warning('请选择来源子项目')
  if (!reuseForm.targetProjectIds.length) return ElMessage.warning('请至少选择一个目标子项目')
  if (!reuseForm.changeReason.trim()) return ElMessage.warning('请填写复制原因')
  saving.value = true
  try {
    const saved = await reuseProjectPlan(rootProject.value.id, { ...reuseForm }, props.token)
    reuseDialogOpen.value = false
    ElMessage.success(`已将计划复制到${saved.length}个子项目；后续计划互相独立`)
    await load()
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '批量复制计划失败')
  } finally {
    saving.value = false
  }
}

async function syncChildren() {
  if (!plan.value || !props.canEdit || !isMasterWithChildren.value) return
  saving.value = true
  try {
    await saveProjectPlan(plan.value.projectId, { tasks: plan.value.tasks, changeReason: '同步主项目计划到跟随子项目', expectedRowVersion: plan.value.rowVersion, createMissingFollowers: true }, props.token)
    await load()
    manageDialogOpen.value = false
    syncResultsDialogOpen.value = true
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : '同步跟随项目失败')
  } finally { saving.value = false }
}

function openSyncResults() {
  manageDialogOpen.value = false
  syncResultsDialogOpen.value = true
}

function isoDate(date: Date) {
  const year = date.getFullYear()
  return `${year}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

function dateValue(value: string) {
  const [year, month, day] = value.split('-').map(Number)
  return new Date(year, month - 1, day)
}

function isoWeekStart(value: string) {
  const date = dateValue(value)
  const weekday = date.getDay() || 7
  date.setDate(date.getDate() - weekday + 1)
  return isoDate(date)
}

function addDays(value: string, days: number) {
  const date = dateValue(value)
  date.setDate(date.getDate() + days)
  return isoDate(date)
}

function dayDiff(start: string, end: string) {
  return Math.round((dateValue(end).getTime() - dateValue(start).getTime()) / 86_400_000)
}

function formatDayNumber(value: string) {
  return String(dateValue(value).getDate())
}

function formatIsoWeek(value: string) {
  const date = dateValue(value)
  const weekDate = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()))
  weekDate.setUTCDate(weekDate.getUTCDate() + 4 - (weekDate.getUTCDay() || 7))
  const yearStart = new Date(Date.UTC(weekDate.getUTCFullYear(), 0, 1))
  const week = Math.ceil((((weekDate.getTime() - yearStart.getTime()) / 86_400_000) + 1) / 7)
  return `W${String(week).padStart(2, '0')}`
}

watch(() => props.project.id, () => { masterPlanMode.value = false; return load() }, { immediate: true })
</script>

<template>
  <section class="pdm-plan-page" :aria-busy="loading">
    <div v-if="error" class="pdm-plan-state is-error"><strong>计划加载失败</strong><span>{{ error }}</span><button type="button" class="pdm-secondary-action" @click="load">重试</button></div>
    <div v-else-if="loading" class="pdm-plan-state"><span class="pdm-plan-loading" />正在加载项目计划…</div>
    <template v-else>
      <section class="pdm-plan-summary">
        <article><span>{{ portfolioMode ? '当前阶段' : '当前活动阶段' }}</span><strong><i class="pdm-plan-stage-dot" :class="hasEffectivePlans ? stageTone(activeStage) : 'is-neutral'" />{{ activeStageSummary }}</strong><small>{{ plan?.manualStage ? '人工例外状态' : portfolioMode ? '按配置的阶段顺序及必需任务判断' : '依据实际排期，允许多阶段并行' }}</small></article>
        <article><span>{{ progressLabel }}</span><strong>{{ overallProgress }}%</strong><div class="pdm-plan-progress"><i :style="{ width: `${overallProgress}%` }" /></div></article>
        <article><span>计划风险</span><strong>{{ portfolioMode ? `${portfolio?.laggingProjectCount ?? 0} 滞后 / ${portfolio?.riskProjectCount ?? 0} 风险` : !isEffective(plan) ? '待审批生效' : `${plan?.tasks.filter(item => item.status !== 'Completed' && item.plannedFinish < isoDate(new Date())).length ?? 0} 项逾期` }}</strong><small>生效后提醒：7天、3天、到期日、逾期每日</small></article>
        <article><span>计划基线</span><strong>{{ plan?.baselineVersion ? `V${plan.baselineVersion}` : '未设置' }}</strong><small>{{ plan?.baselineVersion ? '灰色细条为冻结基线' : '设置后可比较计划与实际' }}</small></article>
        <article><button type="button" class="pdm-plan-summary-action" aria-label="查看阶段进度详情" @click="stageProgressDialogOpen = true"><span>阶段进度</span><strong>{{ stageCount ? `${completedStageCount} / ${stageCount}` : '暂无计划' }}</strong><small>已完成阶段 · 点击查看详情</small></button></article>
        <article><span>生效信息</span><strong>{{ approvalLabel(plan) }}</strong><small v-if="isEffective(plan)">{{ displayUserName(plan?.approvedBy) }} 批准{{ plan?.approvedAt ? ` · ${new Date(plan.approvedAt).toLocaleString()}` : '' }}</small><small v-else-if="plan?.approvalStatus === 'Pending'">审批人：{{ displayUserName(plan.approvalAssignee) }} · 等待处理</small><small v-else-if="plan?.approvalStatus === 'Rejected'">{{ plan.approvalComment ? `退回原因：${plan.approvalComment}` : '计划已退回修改' }}</small><small v-else>{{ plan ? '经执行事业部总经理批准后生效' : '尚未建立计划' }}</small></article>
      </section>

      <section class="pdm-plan-panel">
        <header class="pdm-plan-toolbar">
          <div class="pdm-plan-view-tabs"><strong class="pdm-plan-view-label">{{ portfolioMode ? `全部子项目（${timelineRows.length}）` : '任务甘特图' }}</strong><span v-if="!portfolioMode">{{ timelineRows.length }} 行</span></div>
          <div class="pdm-plan-toolbar__actions">
            <button type="button" class="pdm-plan-delete-action" :disabled="saving || !canDeleteDisplayedPlan" :title="deleteDisabledReason || (isEffective(plan) ? '删除现行计划和变更草稿' : '删除未审批计划')" @click="openToolbarDelete">删除计划</button>
            <template v-if="canApprove"><button type="button" class="pdm-primary-action" :disabled="saving" @click="decideApproval(true)">批准生效</button><button type="button" class="pdm-secondary-action" :disabled="saving" @click="decideApproval(false)">退回修改</button></template>
            <button v-if="!portfolioMode && ownsDisplayedPlan && isEffective(plan) && canEdit && !pendingChange && !editingChangeDraft" type="button" class="pdm-secondary-action" :disabled="saving" @click="openChangeRequest">申请变更权限</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan && plan?.changeRequest" type="button" class="pdm-secondary-action" @click="changeReviewOpen = true">{{ pendingChange ? '权限待审批' : '变更申请记录' }}</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan && editingChangeDraft && canEditSchedule(plan)" type="button" class="pdm-primary-action" :disabled="saving" @click="completeChangeDraft">完成变更并生效</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan && editingChangeDraft && canEditSchedule(plan)" type="button" class="pdm-secondary-action" :disabled="saving" @click="abandonChangeDraft">放弃变更</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan && plan && canEdit && !isEffective(plan) && plan.approvalStatus !== 'Pending'" type="button" class="pdm-primary-action" :disabled="saving" @click="submitApproval">提交审批</button>
            <button v-if="portfolioMode && canEdit" type="button" class="pdm-secondary-action" :disabled="sourceProjectOptions.length === 0" @click="openReuse">复制计划</button>
            <button v-if="portfolioMode && isMasterWithChildren" type="button" class="pdm-primary-action" @click="masterPlanMode = true">编辑主计划</button>
            <button v-if="portfolioMode && (managedChildPlans.length || plan?.childSyncResults?.length || canEditSchedule(plan))" type="button" class="pdm-secondary-action" @click="manageDialogOpen = true">编辑子计划</button>
            <button type="button" class="pdm-secondary-action" :disabled="loading" @click="load"><RefreshCw :size="14" />刷新</button>
            <button v-if="!portfolioMode && isMasterWithChildren" type="button" class="pdm-secondary-action" @click="masterPlanMode = false">查看子项目汇总</button>
            <button v-if="!portfolioMode && canEdit && ((!plan && !project.parentProjectId) || (ownsDisplayedPlan && canEditSchedule(plan)))" type="button" class="pdm-primary-action" @click="openGenerate()"><Calendar :size="14" />{{ plan ? '重新生成' : '生成初始计划' }}</button>
            <label v-if="hasVisibleBaseline" class="pdm-plan-switch"><input v-model="showBaseline" type="checkbox">显示基线</label>
            <label class="pdm-plan-switch"><input v-model="showNonWorkingDays" aria-label="显示节假日和周日背景色" type="checkbox">休息日</label>
            <div class="pdm-plan-zoom" aria-label="甘特图缩放"><button v-for="item in ([['month','月'],['week','周'],['day','日']] as const)" :key="item[0]" :class="{ 'is-active': zoom === item[0] }" @click="zoom = item[0]">{{ item[1] }}</button></div>
            <button v-if="!portfolioMode && canEditSchedule(plan)" type="button" class="pdm-secondary-action" @click="shiftPlan">批量调整</button>
            <button v-if="!portfolioMode && plan && canEditSchedule(plan) && !hasStageAllocation(plan.stages)" type="button" class="pdm-secondary-action" @click="openStageConfiguration">阶段配置</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan" type="button" class="pdm-secondary-action" @click="openVersions"><History :size="14" />版本对比</button>
            <button v-if="!portfolioMode && ownsDisplayedPlan && isEffective(plan) && canEdit" type="button" class="pdm-secondary-action" @click="openStage">{{ hasStageException ? '恢复正常阶段' : '阶段例外' }}</button>
          </div>
        </header>

        <div v-if="inheritedParentPlan" class="pdm-plan-edit-hint is-inherited">当前子项目未建立独立计划，正在只读使用主项目 {{ rootProject.code }} · {{ rootProject.name }} 的计划；如需单独排期，请由项目经理在主项目“子项目计划”中设置。</div>
        <div v-if="!portfolioMode && editingChangeDraft" class="pdm-plan-edit-hint is-change-draft"><strong>正在编辑变更草稿</strong>：原计划继续用于生产、提醒和进度统计；可多次保存，完成变更并生效后才会替换。已完成任务不可改排期。</div>
        <div v-if="project.parentProjectId && plan && !inheritedParentPlan" class="pdm-plan-edit-hint">{{ plan.followsParentPlan ? '跟随主项目计划：单独修改计划日期后转为独立计划；修改责任人不解除跟随。' : '独立计划：主项目调整不会覆盖此计划。' }}</div>

        <div v-if="timelineRows.length" ref="ganttShell" class="pdm-gantt-shell">
          <div class="pdm-gantt-table" :class="{ 'is-info-collapsed': infoColumnsCollapsed }">
            <div class="pdm-gantt-head pdm-gantt-info-head">
              <span class="pdm-gantt-head-primary"><button v-if="stageKeys.length" type="button" class="pdm-gantt-head-icon" :aria-label="allStagesCollapsed ? '展开全部阶段' : '收起全部阶段'" :title="allStagesCollapsed ? '展开全部阶段' : '收起全部阶段'" @click.stop="toggleAllStages"><component :is="allStagesCollapsed ? ListTree : ListCollapse" :size="15" /></button>项目 / 任务</span>
              <span v-if="!infoColumnsCollapsed">责任人</span><span v-if="!infoColumnsCollapsed">进度</span><span>计划日期</span><span>工期</span><span v-if="!infoColumnsCollapsed">完成日期</span>
              <button type="button" class="pdm-gantt-head-icon" :aria-label="infoColumnsCollapsed ? '展开信息列' : '折叠信息列'" :title="infoColumnsCollapsed ? '展开信息列' : '折叠信息列'" :aria-pressed="infoColumnsCollapsed" @click.stop="infoColumnsCollapsed = !infoColumnsCollapsed"><component :is="infoColumnsCollapsed ? PanelLeftOpen : PanelLeftClose" :size="15" /></button>
            </div>
            <div class="pdm-gantt-head pdm-gantt-timeline-head" :style="{ width: `${timelineWidth}px` }">
              <span v-for="marker in showNonWorkingDays ? calendarMarkers : []" :key="`head-${marker.date}`" class="pdm-gantt-calendar-shade is-header" :class="`is-${marker.tone}`" :title="marker.title" :style="{ left: `${marker.left}px`, width: `${Math.max(marker.width, 1)}px` }" />
              <span v-for="band in yearBands" :key="band.key" class="pdm-gantt-calendar-band is-year" :style="{ left: `${band.left}px`, width: `${band.width}px` }">{{ band.label }}</span>
              <span v-for="band in monthBands" :key="band.key" class="pdm-gantt-calendar-band is-month" :style="{ left: `${band.left}px`, width: `${band.width}px` }">{{ band.label }}</span>
              <span v-for="tick in ticks" :key="tick.date" class="pdm-gantt-tick" :class="{ 'is-first': tick.left === 0, 'is-week': zoom === 'week' }" :title="`${tick.date} ～ ${tick.endDate}`" :style="{ left: `${tick.left}px`, width: `${tick.width}px` }">
                <template v-if="zoom === 'week'"><span class="pdm-gantt-tick__week">{{ formatIsoWeek(tick.date) }}</span><time class="pdm-gantt-tick__week-start" :datetime="tick.date">{{ formatDayNumber(tick.date) }}</time></template>
                <time v-else class="pdm-gantt-tick__date" :datetime="tick.date">{{ formatDayNumber(tick.date) }}</time>
              </span>
              <span v-if="todayLeft >= 0 && todayLeft <= timelineWidth" class="pdm-gantt-today is-header" :title="todayDate" :style="{ left: `${todayLeft}px` }"><b class="pdm-gantt-today__label">{{ todayLabel }}</b></span>
            </div>
            <template v-for="row in timelineRows" :key="row.key">
              <div role="button" tabindex="0" class="pdm-gantt-info-row" :class="{ 'is-project': !row.task && !row.isStage, 'is-stage': row.isStage, 'is-task': Boolean(row.task), 'is-child': row.level > 0, 'is-inherited-plan': row.isInherited }" :style="{ '--gantt-indent': `${10 + row.level * 16}px` }" :title="row.isInherited ? '默认跟随主计划' : undefined" :aria-expanded="row.isStage ? !collapsedStages.has(row.key) : !row.task && row.plan ? expanded.has(row.projectId) : undefined" @keydown.enter.self.prevent="row.isStage ? toggleStage(row.key) : row.task ? openTask(row) : toggleExpanded(row.projectId)" @keydown.space.self.prevent="row.isStage ? toggleStage(row.key) : row.task ? openTask(row) : toggleExpanded(row.projectId)" @click="row.isStage ? toggleStage(row.key) : row.task ? openTask(row) : toggleExpanded(row.projectId)">
                <span class="pdm-gantt-name">
                  <component :is="(row.isStage ? !collapsedStages.has(row.key) : expanded.has(row.projectId)) ? ChevronDown : ChevronRight" v-if="!row.task && row.plan" :size="13" />
                  <i v-else class="pdm-gantt-indent" />
                  <span v-if="!row.task && !row.isStage"><strong :title="row.code">{{ row.code }}</strong><small :title="row.name">{{ row.name }}</small></span>
                  <span v-else><strong :title="row.name">{{ row.name }}</strong><small v-if="row.isStage">{{ row.taskCount }} 项任务</small></span>
                </span>
                <span v-if="!infoColumnsCollapsed" @click.stop>
                  <button v-if="row.task && canEditSchedule(row.plan)" type="button" class="pdm-gantt-cell-edit" :aria-label="`编辑${row.name}责任人`" :disabled="saving" @click="beginInlineEdit(row, 'assignee')">{{ displayUserName(row.task.assignee, '待分配') }}</button>
                  <span v-else>{{ row.task ? displayUserName(row.task.assignee, '待分配') : '—' }}</span>
                  <div v-if="inlineEdit?.row.key === row.key && inlineEdit.field === 'assignee'" class="pdm-gantt-inline-editor" @keydown.stop>
                    <el-select v-model="inlineEdit.assignee" aria-label="行内责任人" filterable clearable><el-option v-for="username in assigneeOptions" :key="username" :label="displayUserName(username)" :value="username" /></el-select>
                    <div><button type="button" :disabled="saving" @click="inlineEdit = null">取消</button><button type="button" :disabled="saving" @click="saveInlineEdit">保存责任人</button></div>
                  </div>
                </span>
                <span v-if="!infoColumnsCollapsed" :class="{ 'is-lagging': row.isLagging, 'is-risk': row.isAtRisk }">{{ row.completion }}%</span>
                <span @click.stop>
                  <button v-if="row.task && canEditSchedule(row.plan)" type="button" class="pdm-gantt-cell-edit pdm-gantt-date-lines" :aria-label="`编辑${row.name}计划日期`" :disabled="saving" @click="beginInlineEdit(row, 'dates')"><time :datetime="row.start">{{ row.start }}</time> ~ <time :datetime="row.finish">{{ row.finish }}</time></button>
                  <span v-else-if="row.start" class="pdm-gantt-date-lines"><time :datetime="row.start">{{ row.start }}</time> ~ <time :datetime="row.finish">{{ row.finish }}</time></span><span v-else>—</span>
                  <div v-if="inlineEdit?.row.key === row.key && inlineEdit.field === 'dates'" class="pdm-gantt-inline-editor is-dates" @keydown.stop>
                    <el-date-picker v-model="inlineEdit.dates" aria-label="行内计划日期区间" type="daterange" value-format="YYYY-MM-DD" range-separator="至" start-placeholder="开始日期" end-placeholder="完成日期" :clearable="false" />
                    <div><button type="button" :disabled="saving" @click="inlineEdit = null">取消</button><button type="button" :disabled="saving" @click="saveInlineEdit">保存日期</button></div>
                  </div>
                </span>
                <span @click.stop>
                  <button v-if="row.task && !row.isMilestone && canEditSchedule(row.plan)" type="button" class="pdm-gantt-cell-edit" :aria-label="`编辑${row.name}工期`" :disabled="saving" @click="beginInlineEdit(row, 'duration')">{{ rowDuration(row) }}天</button>
                  <span v-else>{{ rowDuration(row) === null ? '—' : `${rowDuration(row)}天` }}</span>
                  <div v-if="inlineEdit?.row.key === row.key && inlineEdit.field === 'duration'" class="pdm-gantt-inline-editor is-duration" @keydown.stop>
                    <label>工期（自然日）<el-input-number v-model="inlineEdit.duration" aria-label="行内工期" :min="1" :max="3650" :precision="0" /></label>
                    <small>开始日期不变，结束日期按工期自动调整。</small>
                    <div><button type="button" :disabled="saving" @click="inlineEdit = null">取消</button><button type="button" :disabled="saving" @click="saveInlineEdit">保存工期</button></div>
                  </div>
                </span>
                <span v-if="!infoColumnsCollapsed" @click.stop>
                  <button v-if="canReportProgress(row)" type="button" class="pdm-gantt-cell-edit" :aria-label="`编辑${row.name}完成日期`" :disabled="saving" @click="beginInlineEdit(row, 'actualFinish')">{{ row.actualFinish || '—' }}</button>
                  <span v-else :title="row.isStage ? '全部子任务完成后，显示最晚的实际完成日期' : ''">{{ row.actualFinish || '—' }}</span>
                  <div v-if="inlineEdit?.row.key === row.key && inlineEdit.field === 'actualFinish'" class="pdm-gantt-inline-editor is-actual-finish" @keydown.stop>
                    <label>实际开始<el-date-picker v-model="inlineEdit.actualStart" :disabled-date="futureActualDate" aria-label="行内实际开始日期" type="date" value-format="YYYY-MM-DD" /></label>
                    <label>完成日期<el-date-picker v-model="inlineEdit.actualFinish" :disabled-date="futureActualDate" aria-label="行内完成日期" type="date" value-format="YYYY-MM-DD" :clearable="false" /></label>
                    <small>保存即标记100%完成。未填写实际开始时，按完成当天记录；可在上方调整。</small>
                    <div><button type="button" :disabled="saving" @click="inlineEdit = null">取消</button><button type="button" :disabled="saving || !inlineEdit.actualFinish" @click="saveInlineEdit">保存完成日期</button></div>
                  </div>
                </span>
                <span class="pdm-gantt-row-control" aria-hidden="true" />
              </div>
              <div class="pdm-gantt-timeline-row" :class="{ 'is-project': !row.task && !row.isStage, 'is-stage': row.isStage, 'is-inherited-plan': row.isInherited }" :title="row.isInherited ? '默认跟随主计划' : undefined" :style="{ width: `${timelineWidth}px` }" @dblclick="row.isStage ? toggleStage(row.key) : openTask(row)">
                <span v-for="marker in showNonWorkingDays ? calendarMarkers : []" :key="`${row.key}-calendar-${marker.date}`" aria-hidden="true" class="pdm-gantt-calendar-shade" :class="`is-${marker.tone}`" :style="{ left: `${marker.left}px`, width: `${Math.max(marker.width, 1)}px` }" />
                <span v-for="tick in ticks" :key="`${row.key}-${tick.date}`" class="pdm-gantt-gridline" :style="{ left: `${tick.left}px` }" />
                <span v-if="todayLeft >= 0 && todayLeft <= timelineWidth" class="pdm-gantt-today" :style="{ left: `${todayLeft}px` }" />
                <span v-if="showBaseline" class="pdm-gantt-baseline" :style="baselineStyle(row)" />
                <span class="pdm-gantt-bar" :class="{ 'is-draggable': row.task && canEditSchedule(row.plan) && !saving, 'is-stage-adjustable': row.isStage && canResizeStage(row.plan) && !saving, 'is-dragging': barDrag?.row.key === row.key, 'is-project': !row.task && !row.isStage, 'is-milestone': row.isMilestone, 'is-lagging': row.isLagging, 'is-neutral': !isEffective(row.plan) }" :style="rowBarStyle(row)" @pointerdown.stop="startBarDrag($event, row)" @pointermove="moveBarDrag" @pointerup="finishBarDrag" @pointercancel="cancelBarDrag" @lostpointercapture="cancelBarDrag">
                  <button v-if="row.isStage ? canResizeStageEdge(row, 'resize-start') && !saving : row.task && !row.isMilestone && canEditSchedule(row.plan) && !saving" type="button" class="pdm-gantt-resize-handle is-start" :class="{ 'is-stage-handle': row.isStage }" :aria-label="row.isStage ? `拖动调整${row.name}阶段开始边界` : `拖动调整${row.name}开始日期`" @pointerdown.stop="startBarDrag($event, row, 'resize-start')" @dblclick.stop />
                  <i /><b v-if="!row.isMilestone">{{ row.completion }}%</b>
                  <button v-if="row.isStage ? canResizeStageEdge(row, 'resize-finish') && !saving : row.task && !row.isMilestone && canEditSchedule(row.plan) && !saving" type="button" class="pdm-gantt-resize-handle is-finish" :class="{ 'is-stage-handle': row.isStage }" :aria-label="row.isStage ? `拖动调整${row.name}阶段结束边界` : `拖动调整${row.name}结束日期`" @pointerdown.stop="startBarDrag($event, row, 'resize-finish')" @dblclick.stop />
                </span>
                <span v-if="row.isStage || row.task" aria-hidden="true" class="pdm-gantt-bar-label" :class="{ 'is-stage': row.isStage }" :style="rowBarLabelStyle(row)">{{ row.name }}</span>
              </div>
            </template>
          </div>
        </div>
        <div v-else class="pdm-plan-empty">
          <Calendar :size="42" />
          <h2>{{ project.parentProjectId ? '主项目尚未建立计划' : portfolioMode ? '各子项目尚未建立计划' : '尚未建立项目计划' }}</h2>
          <p>{{ project.parentProjectId ? '子项目不单独创建初始计划，请返回主项目建立计划；建立后本页默认只读使用主项目计划。' : portfolioMode ? '请先编辑并建立主项目计划；子项目默认跟随主计划，特殊子项目可在“子项目计划”中单独设置。' : '按模板比例和前置关系生成初始计划，之后可持续调整并保留版本。' }}</p>
          <button v-if="!portfolioMode && !project.parentProjectId && canEdit" type="button" class="pdm-primary-action" @click="openGenerate()">生成初始计划</button>
        </div>
        <footer class="pdm-plan-legend"><span><i class="is-plan" />当前计划</span><span><i class="is-baseline" />冻结基线</span><span><i class="is-progress" />实际完成</span><span><i class="is-today" />今天</span><small>工期按自然日计算；初始阶段连续，后续调整允许阶段交叉，仅显式前置关系联动。</small></footer>
      </section>
    </template>

    <el-dialog v-model="generateDialogOpen" :title="generateTargetProjectId !== project.id ? '单独设置子项目计划' : '按规则生成初始计划'" width="560px" destroy-on-close>
      <div class="pdm-plan-form">
        <p v-if="generateTargetProjectId !== project.id && generateTargetProject"><strong>{{ generateTargetProject.code }} · {{ generateTargetProject.name }}</strong> 将建立独立计划，之后不再自动跟随主项目排期。</p>
        <label>计划模板<el-select v-model="generateForm.templateId"><el-option v-for="item in templates.filter(template => template.isActive)" :key="item.id" :label="item.name" :value="item.id" /></el-select></label>
        <p>任务按固定天数或初始阶段比例及前置关系生成；首次生成时各阶段在主项目计划范围内首尾连续。生成后可独立平移或调整各阶段，允许交叉，仅显式前置关系联动。</p>
        <div class="pdm-plan-form__grid"><label>交付计划开始<el-date-picker v-model="generateForm.startDate" value-format="YYYY-MM-DD" type="date" /></label><label>交付总工期（自然日）<el-input-number v-model="generateForm.totalDurationDays" aria-label="交付总工期" :min="1" :max="3650" :precision="0" /></label></div>
        <p v-if="!hasStageAllocation(generationTemplate?.stages)">此模板尚未配置两级分配，请管理员在系统管理 → 项目计划模板中补齐阶段比例。</p>
        <p v-if="earliestPostDeliveryStart && scheduledPostDeliveryStages.length">交付计划完成：{{ addDays(earliestPostDeliveryStart, -1) }}</p>
        <section v-if="scheduledPostDeliveryStages.length" class="pdm-offsite-plan" aria-label="厂外调试计划">
          <header><strong>厂外调试计划</strong><el-checkbox :model-value="allPostDeliveryDeferred" :indeterminate="somePostDeliveryDeferred && !allPostDeliveryDeferred" aria-label="厂外调试计划暂不建立" @update:model-value="deferOffsitePlan(Boolean($event))">暂不建立</el-checkbox></header>
          <small>首次生成时客户端调试 → 验收推进按顺序连续排期，生成后可独立调整。</small>
          <div v-for="(stage, index) in scheduledPostDeliveryStages" :key="stage.stage" class="pdm-post-delivery-stage">
          <header><strong>{{ stage.name }}</strong><el-checkbox :model-value="stage.deferred" :aria-label="`${stage.name}暂不建立`" @update:model-value="deferPostDeliveryStage(index, Boolean($event))">暂不建立</el-checkbox></header>
          <div class="pdm-plan-form__grid"><label>{{ stage.name }} · 开始日期<el-date-picker :model-value="stage.startDate" :aria-label="`${stage.name}开始日期`" value-format="YYYY-MM-DD" type="date" :disabled="stage.deferred || index > 0" @update:model-value="postDeliveryStartOverride = $event || ''" /></label><label>阶段工期（天）<el-input-number v-model="independentSchedules[index]!.durationDays" :aria-label="`${stage.name}阶段工期`" :min="1" :max="3650" :precision="0" :disabled="stage.deferred" /></label></div>
          <small>{{ stage.deferred ? '本次不生成该阶段及其子任务。' : index === 0 ? '默认从交付完成次日开始，可调整为更晚日期。' : '自动接在上一阶段完成次日开始。' }}</small>
          </div>
        </section>
        <p v-if="postDeliveryDateError" role="alert">{{ postDeliveryDateError }}</p>
        <p>客户端调试、验收推进仅在首次生成时按顺序连续排期，不计入交付工期和交付进度。暂不建立前一阶段时，后续阶段也一并暂缓；恢复后续阶段时，前序阶段同时恢复。生成后可独立调整，阶段内无前置的子任务仍可并行。草稿需经事业部总经理审批后生效。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="generateDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving || !generateForm.startDate || !generateForm.totalDurationDays || !hasStageAllocation(generationTemplate?.stages) || Boolean(postDeliveryDateError)" @click="generate">生成计划</button></template>
    </el-dialog>

    <el-dialog v-model="stageProgressDialogOpen" title="阶段进度详情" width="620px" destroy-on-close>
      <div class="pdm-plan-form pdm-plan-stage-details">
        <p>交付100%不代表终验收完成；厂外调试计划另行统计。未生效计划按0%显示，已完成阶段需同时满足必需任务完成。</p>
        <article v-for="group in stageProgressGroups" :key="group.projectId"><strong>{{ group.label }}</strong><div class="pdm-plan-stage-progress"><span v-for="stage in group.stages" :key="stage.code">{{ stage.name }}{{ stage.participatesInDelivery === false ? '（交付后）' : '' }}：{{ stage.progress }}%</span></div></article>
        <p v-if="!stageCount">尚未建立阶段任务。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="stageProgressDialogOpen = false">关闭</button></template>
    </el-dialog>

    <el-dialog v-model="resourceConflictsDialogOpen" title="资源冲突详情" width="620px" destroy-on-close>
      <div class="pdm-plan-form"><p>{{ resourceConflicts.length ? `${resourceConflicts.length} 组同责任人排期重叠，请确认资源安排（不会自动串行）` : '未发现同责任人排期重叠。' }}</p><ul v-if="resourceConflicts.length" class="pdm-plan-conflict-list"><li v-for="(conflict, index) in resourceConflicts" :key="index">{{ conflict }}</li></ul></div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="resourceConflictsDialogOpen = false">关闭</button></template>
    </el-dialog>

    <el-dialog v-model="changeDialogOpen" title="申请计划变更权限" width="560px" destroy-on-close>
      <div class="pdm-plan-form">
        <p>本次申请只获取一次编辑权限，不预先修改排期。由 {{ displayUserName(plan?.approvedBy) }} 批准后，系统从现行计划创建变更草稿；草稿完成前原计划继续生效。</p>
        <label><span>变更原因<span class="pdm-plan-required" aria-hidden="true">*</span></span><el-input v-model="changeReason" aria-label="计划变更原因" type="textarea" :maxlength="300" @input="changeSubmitError = null" /></label>
        <div v-if="changeSubmitError" class="pdm-plan-dialog-error" role="alert"><strong>{{ changeSubmitError.type }}</strong><span>{{ changeSubmitError.message }}</span></div>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="saving" @click="changeDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving" @click="submitChangeRequest">提交权限申请</button></template>
    </el-dialog>

    <el-dialog v-model="changeReviewOpen" title="计划变更申请与差异" width="760px" destroy-on-close>
      <div v-if="plan?.changeRequest" class="pdm-plan-form">
        <p>{{ plan.changeRequest.status === 'Pending' ? '变更权限待审批，原计划继续生效' : plan.changeDraftSource ? '变更权限已批准，草稿编辑中，原计划继续生效' : plan.changeRequest.status === 'Approved' ? '本次变更已结束' : '变更权限已退回，原计划不变' }}；审批人：{{ displayUserName(plan.changeRequest.approvalAssignee) }}</p>
        <p>申请人：{{ displayUserName(plan.changeRequest.submittedBy) }}；原因：{{ plan.changeRequest.reason }}<br v-if="plan.changeRequest.comment" />{{ plan.changeRequest.comment ? `审批意见：${plan.changeRequest.comment}` : '' }}</p>
        <div v-if="plan.changeRequest.tasks.length" class="pdm-plan-change-review">
          <article v-for="change in plan.changeRequest.tasks" :key="change.taskId">
            <strong>{{ plan.tasks.find(task => task.id === change.taskId)?.name }}</strong>
            <span>当前：{{ plan.tasks.find(task => task.id === change.taskId)?.plannedStart }} ～ {{ plan.tasks.find(task => task.id === change.taskId)?.plannedFinish }} · {{ displayUserName(plan.tasks.find(task => task.id === change.taskId)?.assignee, '待分配') }}</span>
            <span>申请：{{ change.plannedStart }} ～ {{ change.plannedFinish }} · {{ displayUserName(change.assignee, '待分配') }}</span>
          </article>
        </div>
        <p v-else>此申请仅申请变更权限，未预先提交任务排期修改。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="changeReviewOpen = false">关闭</button><template v-if="canApproveChange"><button type="button" class="pdm-secondary-action" :disabled="saving" @click="decideChange(false)">退回权限</button><button type="button" class="pdm-primary-action" :disabled="saving" @click="decideChange(true)">批准权限</button></template></template>
    </el-dialog>

    <el-dialog v-model="taskDialogOpen" title="任务详情与实际进度" width="620px" destroy-on-close>
      <div class="pdm-plan-form">
        <label>任务名称<el-input :model-value="taskForm.name" aria-label="任务名称" readonly /></label>
        <div class="pdm-plan-form__grid"><label>阶段<el-input :model-value="stageLabel(taskForm.stage, selectedOwnerPlan)" aria-label="阶段" readonly /></label><label>责任人<el-select v-model="taskForm.assignee" :disabled="!canEditSelectedPlan" filterable clearable placeholder="选择责任人"><el-option v-for="username in assigneeOptions" :key="username" :label="displayUserName(username)" :value="username" /></el-select></label></div>
        <label>计划日期<el-date-picker v-model="taskDateRange" class="pdm-plan-date-range" aria-label="计划日期区间" value-format="YYYY-MM-DD" type="daterange" range-separator="至" start-placeholder="开始日期" end-placeholder="完成日期" :clearable="false" :disabled="!canEditSelectedPlan" /></label>
        <p v-if="!isEffective(selectedOwnerPlan)">当前为{{ approvalLabel(selectedOwnerPlan) }}，可修改安排并保存草稿；批准生效后才可填报实际进度。</p>
        <label class="pdm-progress-ruler">实际完成比例 <strong>{{ taskForm.completionPercent }}%</strong><el-slider v-model="taskForm.completionPercent" aria-label="实际完成比例" :min="0" :max="100" :step="10" show-stops :marks="completionMarks" :format-tooltip="(value: number) => `${value}%`" :disabled="!isEffective(selectedOwnerPlan)" /></label>
        <div class="pdm-plan-form__grid"><label>实际开始<el-date-picker v-model="taskForm.actualStart" aria-label="实际开始" :disabled-date="futureActualDate" value-format="YYYY-MM-DD" type="date" clearable :disabled="!isEffective(selectedOwnerPlan)" /></label><label>实际完成<el-date-picker v-model="taskForm.actualFinish" aria-label="实际完成" :disabled-date="futureActualDate" value-format="YYYY-MM-DD" type="date" clearable :disabled="!isEffective(selectedOwnerPlan)" /></label></div>
        <div class="pdm-plan-form__grid"><label>进度权重<el-input :model-value="String(taskForm.weight)" aria-label="进度权重" readonly /></label><label v-if="canEditSelectedPlan" class="pdm-plan-checkbox"><el-checkbox v-model="taskForm.isRequired">作为阶段门必需任务</el-checkbox></label></div>
        <p>任务名称、阶段和权重沿用生成计划时的模板配置；权重由系统自动换算占比，无需人工凑到100。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="taskDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving || (!canEditSelectedPlan && !isEffective(selectedOwnerPlan))" @click="saveTask">保存</button></template>
    </el-dialog>

    <el-dialog v-model="stageDialogOpen" :title="hasStageException ? '恢复正常阶段' : '设置阶段例外'" width="520px" destroy-on-close>
      <div class="pdm-plan-form">
        <template v-if="hasStageException"><p>当前处于“{{ stageLabel(plan?.manualStage, plan) }}”状态。恢复后，系统将按第一个未完成的必需任务自动判断正常阶段。</p><label>恢复原因<el-input v-model="stageForm.reason" aria-label="恢复正常阶段原因" type="textarea" :rows="3" /></label></template>
        <template v-else><label>例外状态<el-select v-model="stageForm.stage" placeholder="请选择例外状态"><el-option label="暂停" value="Paused" /><el-option label="取消" value="Cancelled" /><el-option label="终止" value="Terminated" /></el-select></label><label>原因<el-input v-model="stageForm.reason" aria-label="阶段例外原因" type="textarea" :rows="3" /></label><p>设计、备料、装配、调试等正常阶段由必需任务的完成条件自动判断，不能人工跳过。</p></template>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="stageDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving" @click="saveStage">{{ hasStageException ? '确认恢复' : '保存' }}</button></template>
    </el-dialog>

    <el-dialog v-model="manageDialogOpen" title="子项目计划" width="620px" destroy-on-close>
      <div class="pdm-plan-form">
        <p>全部子项目默认使用主项目计划；只有需要特殊排期时，项目经理才单独建立子项目计划。未批准的独立计划可修改或删除，已生效计划只能申请变更。</p>
        <div class="pdm-plan-management-list">
          <article v-for="item in managedChildPlans" :key="item.projectId">
            <div><strong>{{ item.projectCode }} · {{ item.projectName }}</strong><small v-if="item.plan">{{ item.plan.followsParentPlan ? '跟随主项目计划' : approvalLabel(item.plan) }} · {{ item.plan.tasks.length }} 项任务</small><small v-else>{{ plan ? '默认跟随主项目计划 · 未单独建立' : '等待主项目建立计划' }}</small></div>
            <button v-if="item.plan" type="button" class="pdm-secondary-action" @click="editChildPlan(item.projectId)">{{ canManageProject(item.projectId) ? (item.plan.followsParentPlan ? '单独调整' : '修改计划') : '查看计划' }}</button>
            <button v-else-if="plan && canManageProject(item.projectId)" type="button" class="pdm-primary-action" :disabled="saving" @click="openChildGenerate(item)">单独设置计划</button>
            <button v-if="item.plan && canManageProject(item.projectId) && !isEffective(item.plan)" type="button" class="pdm-secondary-action" :disabled="saving" @click="openDelete(item.plan)">删除计划</button>
          </article>
          <p v-if="!managedChildPlans.length">当前主项目没有子项目。</p>
        </div>
        <section v-if="isMasterWithChildren && plan" class="pdm-plan-sync-management">
          <div>
            <strong>主项目同步</strong>
            <small v-if="plan.childSyncResults?.length">最近同步：{{ plan.childSyncResults.filter(item => !item.differences.length).length }} 项已同步，{{ plan.childSyncResults.filter(item => item.differences.length).length }} 项保留</small>
            <small v-else>保存主项目时自动同步跟随状态的子项目。</small>
          </div>
          <button v-if="plan.childSyncResults?.length" type="button" class="pdm-secondary-action" @click="openSyncResults">查看同步结果</button>
          <button v-if="canEditSchedule(plan)" type="button" class="pdm-primary-action" :disabled="saving" @click="syncChildren">同步跟随项目</button>
        </section>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="manageDialogOpen = false">关闭</button></template>
    </el-dialog>

    <el-dialog v-model="deleteSelectionDialogOpen" title="选择要删除的计划" width="620px" destroy-on-close>
      <p>主项目当前没有可直接删除的计划，请选择一份未审批的子计划。</p>
      <div class="pdm-plan-management-list">
        <article v-for="item in deletablePortfolioPlans" :key="item.projectId">
          <div><strong>{{ item.projectCode }} · {{ item.projectName }}</strong><small>{{ approvalLabel(item.plan) }} · {{ item.plan?.tasks.length ?? 0 }} 项任务</small></div>
          <button v-if="item.plan" type="button" class="pdm-plan-delete-action" @click="openDelete(item.plan)">删除计划</button>
        </article>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="deleteSelectionDialogOpen = false">取消</button></template>
    </el-dialog>

    <el-dialog v-model="deleteDialogOpen" title="删除项目计划" width="620px" destroy-on-close @closed="deleteTarget = null">
      <div v-if="deleteTarget" class="pdm-plan-form">
        <p v-if="isEffective(deleteTarget)">确认删除 <strong>{{ deleteTargetProject?.code }} · {{ deleteTargetProject?.name }}</strong> 的现行计划和变更草稿？计划任务、实际进度、审批记录和历史版本将永久移除，仅保留删除审计；删除后可重新生成计划并再次提交审批。</p>
        <p v-else>确认删除 <strong>{{ deleteTargetProject?.code }} · {{ deleteTargetProject?.name }}</strong> 的整份未批准计划？计划任务和审批待办将直接移除，不保留计划内容；项目、模板及已生效计划不受影响。</p>
        <template v-if="deleteTarget.projectId === rootProject.id">
          <p>同时删除 {{ followerChildPlans.length }} 份跟随主计划的未批准子计划。</p>
          <el-checkbox v-if="independentChildPlans.length && !isEffective(deleteTarget)" v-model="deleteIndependentChildren">同时删除 {{ independentChildPlans.length }} 份独立的未批准子计划</el-checkbox>
          <small v-if="independentChildPlans.length && (isEffective(deleteTarget) || !deleteIndependentChildren)">独立子计划{{ isEffective(deleteTarget) ? '不随已生效主计划联动删除' : '默认保留' }}：{{ independentChildPlans.map(item => item.projectCode).join('、') }}</small>
        </template>
        <p>删除后可重新生成计划；本操作仅保留操作人、时间和项目号的审计记录。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" :disabled="saving" @click="deleteDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving" @click="confirmDelete">确认删除计划</button></template>
    </el-dialog>

    <el-dialog v-model="syncResultsDialogOpen" title="主项目同步结果与差异" width="760px" destroy-on-close>
      <p>以下为最近一次主项目保存/同步的结果。保留项不会被覆盖；可进入对应子项目单独维护。</p>
      <div v-for="item in plan?.childSyncResults ?? []" :key="item.projectId" class="pdm-plan-edit-hint">
        <strong>{{ item.projectCode }} · {{ item.result }}</strong>
        <ul v-if="item.differences.length"><li v-for="(difference, index) in item.differences" :key="index">{{ difference }}</li></ul>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="syncResultsDialogOpen = false">关闭</button></template>
    </el-dialog>
    <el-dialog v-model="reuseDialogOpen" title="批量复制子项目计划" width="620px" destroy-on-close>
      <div class="pdm-plan-form">
        <label>来源子项目<el-select v-model="reuseForm.sourceProjectId" @change="reuseForm.targetProjectIds = []"><el-option v-for="item in sourceProjectOptions" :key="item.projectId" :label="`${item.projectCode} · ${item.projectName}`" :value="item.projectId" /></el-select></label>
        <div class="pdm-plan-targets">
          <div class="pdm-plan-targets__header"><strong>目标子项目</strong><span>已选 {{ reuseForm.targetProjectIds.length }} / {{ targetProjectOptions.length }}</span><el-checkbox :model-value="allTargetsSelected" :indeterminate="reuseForm.targetProjectIds.length > 0 && !allTargetsSelected" @change="selectAllTargets(Boolean($event))">全选可选项</el-checkbox></div>
          <el-checkbox-group v-model="reuseForm.targetProjectIds" class="pdm-plan-targets__list">
            <el-checkbox v-for="item in targetProjectOptions" :key="item.projectId" :value="item.projectId" :disabled="!targetSelectable(item)"><span>{{ item.projectCode }} · {{ item.projectName }}</span><small>{{ isEffective(item.plan) ? '已生效，不可覆盖' : item.hasPlan ? approvalLabel(item.plan) : plan ? '默认跟随主计划（复制后转独立）' : '等待主计划' }}</small></el-checkbox>
          </el-checkbox-group>
        </div>
        <label>复制原因<el-input v-model="reuseForm.changeReason" type="textarea" :rows="3" /></label>
        <el-checkbox v-model="reuseForm.replaceExisting">允许覆盖目标子项目已有的未生效计划</el-checkbox>
        <p>复制阶段、任务结构和相同计划日期；责任人按目标项目人员匹配。复制结果为独立草稿，需各自提交审批；实际进度、日期和基线不会复制。</p>
      </div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="reuseDialogOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="saving" @click="reusePlan">确认复制</button></template>
    </el-dialog>

    <el-dialog v-model="stagesDialogOpen" title="项目阶段配置" width="620px" destroy-on-close>
      <div class="pdm-plan-form"><p>调整阶段名称和顺序。自动阶段判断按这里的顺序执行；阶段删除前需先调整引用它的任务。</p>
        <div class="pdm-plan-stage-list"><div v-for="(stage, index) in stagesDraft" :key="stage.code" class="pdm-plan-stage-editor"><span>{{ index + 1 }}</span><el-input v-model="stage.name" :maxlength="60" aria-label="阶段名称" /><button :disabled="index === 0" @click="moveStage(stagesDraft, index, -1)">上移</button><button :disabled="index === stagesDraft.length - 1" @click="moveStage(stagesDraft, index, 1)">下移</button><button @click="removeStage(stagesDraft, index, stageConfigurationPlan?.tasks ?? [])">删除</button></div></div>
        <button type="button" class="pdm-secondary-action" @click="addStage(stagesDraft)">增加阶段</button>
      </div>
      <template #footer><button class="pdm-secondary-action" @click="stagesDialogOpen = false">取消</button><button class="pdm-primary-action" :disabled="saving" @click="saveStageConfiguration">保存阶段</button></template>
    </el-dialog>

    <el-dialog v-model="versionsDialogOpen" title="计划版本对比" width="980px" destroy-on-close>
      <div class="pdm-plan-compare-selectors">
        <label>对比基准<el-select v-model="compareBaseKey"><el-option v-for="item in comparisonOptions" :key="`base-${item.key}`" :label="item.label" :value="item.key" /></el-select></label>
        <span>对比</span>
        <label>对比目标<el-select v-model="compareTargetKey"><el-option v-for="item in comparisonOptions" :key="`target-${item.key}`" :label="item.label" :value="item.key" /></el-select></label>
      </div>
      <p class="pdm-plan-compare-summary">共 {{ comparisonRows.length }} 项差异；日期、责任人、完成比例和实际完成日期均参与比较。历史版本仅供查看，不会修改当前计划。</p>
      <div v-if="comparisonRows.length" class="pdm-plan-compare-table">
        <article v-for="row in comparisonRows" :key="row.id">
          <strong>{{ row.name }} <small>{{ row.state }}</small></strong>
          <span>{{ row.old ? `${row.old.plannedStart} ～ ${row.old.plannedFinish}` : '—' }}<br>{{ row.old ? `${displayUserName(row.old.assignee, '待分配')} · ${row.old.completionPercent}% · 实际完成 ${row.old.actualFinish ?? '—'}` : '' }}</span>
          <b>→</b>
          <span>{{ row.next ? `${row.next.plannedStart} ～ ${row.next.plannedFinish}` : '—' }}<br>{{ row.next ? `${displayUserName(row.next.assignee, '待分配')} · ${row.next.completionPercent}% · 实际完成 ${row.next.actualFinish ?? '—'}` : '' }}</span>
        </article>
      </div>
      <p v-else class="pdm-empty-info">所选两个版本没有差异。</p>
      <div v-if="versions.length" class="pdm-plan-version-list"><article v-for="item in versions" :key="item.id"><strong>V{{ item.versionNumber }} · {{ item.changeReason }}</strong><span>{{ displayUserName(item.createdBy) }} · {{ new Date(item.createdAt).toLocaleString() }}</span><small>{{ item.snapshot.plannedStart }} — {{ item.snapshot.forecastFinish }} · {{ item.snapshot.tasks.length }}项任务</small></article></div>
    </el-dialog>

  </section>
</template>

<style scoped>
.pdm-plan-required{margin-left:3px;color:var(--pdm-danger);font-weight:700}.pdm-plan-dialog-error{display:flex;align-items:flex-start;gap:8px;padding:9px 11px;border:1px solid color-mix(in srgb,var(--pdm-danger) 35%,var(--pdm-border));border-radius:6px;background:color-mix(in srgb,var(--pdm-danger) 8%,var(--pdm-surface));color:var(--pdm-danger);font-size:12px;line-height:1.5}.pdm-plan-dialog-error strong{flex-shrink:0}.pdm-plan-dialog-error span{min-width:0;overflow-wrap:anywhere}
.pdm-plan-change-list,.pdm-plan-change-review{max-height:55vh;overflow:auto}.pdm-plan-change-list article{display:grid;grid-template-columns:1fr 1fr;gap:10px;padding:12px 0;border-bottom:1px solid var(--pdm-border)}.pdm-plan-change-list article>strong,.pdm-plan-change-list article>label:first-of-type{grid-column:1/-1}.pdm-plan-change-list .el-date-editor{width:100%;box-sizing:border-box}.pdm-plan-change-review article{display:flex;flex-direction:column;gap:8px;padding:12px;border-bottom:1px solid var(--pdm-border);font-size:12px}
.pdm-progress-ruler{padding:0 12px 22px}.pdm-progress-ruler strong{float:right}.pdm-progress-ruler :deep(.el-slider__marks-text){font-size:10px;white-space:nowrap}.pdm-gantt-bar.is-draggable{cursor:grab;touch-action:none}.pdm-gantt-bar.is-stage-adjustable{cursor:move;touch-action:none}.pdm-gantt-bar.is-dragging{cursor:grabbing;z-index:4}.pdm-gantt-resize-handle{position:absolute;z-index:5;top:0;bottom:0;width:8px;padding:0;border:0;background:transparent;cursor:ew-resize;touch-action:none;opacity:.65}.pdm-gantt-resize-handle::after{content:'';position:absolute;top:4px;bottom:4px;width:2px;border-radius:2px;background:color-mix(in srgb,var(--plan-accent) 72%,var(--pdm-text))}.pdm-gantt-resize-handle.is-stage-handle{width:12px;opacity:.9}.pdm-gantt-resize-handle.is-stage-handle::after{top:2px;bottom:2px;width:3px}.pdm-gantt-resize-handle.is-start{left:0}.pdm-gantt-resize-handle.is-start::after{left:2px}.pdm-gantt-resize-handle.is-finish{right:0}.pdm-gantt-resize-handle.is-finish::after{right:2px}.pdm-gantt-bar:hover .pdm-gantt-resize-handle,.pdm-gantt-resize-handle:focus-visible{opacity:1}.pdm-gantt-resize-handle:focus-visible{outline:1px solid var(--plan-accent);outline-offset:-1px}
.pdm-plan-stage-progress{display:flex;flex-wrap:wrap;gap:8px 18px;padding:8px 12px;font-size:11px;border:1px solid var(--pdm-border);background:var(--pdm-surface);border-radius:5px}.pdm-plan-stage-details article>strong{display:block;margin-bottom:8px;font-size:12px}.pdm-plan-conflict-list{margin:0;padding-left:20px;font-size:12px;line-height:1.8}.pdm-plan-conflict-list li+li{margin-top:6px}
.pdm-plan-stage-editor>.el-input{min-width:0}
.pdm-post-delivery-stage>header>.el-checkbox,.pdm-offsite-plan>header>.el-checkbox{flex-direction:row;gap:6px;height:24px;margin:0}
.pdm-offsite-plan{display:flex;flex-direction:column;gap:8px;padding:10px;border:1px solid var(--pdm-border);border-radius:6px}.pdm-offsite-plan>header,.pdm-post-delivery-stage>header{display:flex;align-items:center;justify-content:space-between;gap:8px}.pdm-offsite-plan>header>strong{font-size:13px}.pdm-post-delivery-stage{display:flex;flex-direction:column;gap:8px;padding:10px 0 0;border-top:1px solid var(--pdm-border)}.pdm-post-delivery-stage strong{font-size:12px}.pdm-offsite-plan small{font-size:11px;color:var(--pdm-muted)}
.pdm-plan-edit-hint{padding:6px 12px;color:var(--pdm-muted);font-size:11px;border-bottom:1px solid var(--pdm-border)}
.pdm-plan-edit-hint.is-change-draft{color:var(--pdm-text);background:color-mix(in srgb,var(--pdm-orange) 10%,var(--pdm-surface));border-color:color-mix(in srgb,var(--pdm-orange) 30%,var(--pdm-border))}.pdm-plan-compare-selectors{display:grid;grid-template-columns:1fr auto 1fr;align-items:end;gap:12px}.pdm-plan-compare-selectors label{display:flex;flex-direction:column;gap:6px;font-size:12px}.pdm-plan-compare-summary{font-size:11px;color:var(--pdm-muted)}.pdm-plan-compare-table{max-height:42vh;overflow:auto;border:1px solid var(--pdm-border);border-radius:6px}.pdm-plan-compare-table article{display:grid;grid-template-columns:170px 1fr 24px 1fr;gap:10px;align-items:center;padding:9px 10px;border-bottom:1px solid var(--pdm-border);font-size:11px}.pdm-plan-compare-table article:last-child{border-bottom:0}.pdm-plan-compare-table strong small{margin-left:6px;color:var(--plan-accent)}.pdm-plan-compare-table span{line-height:1.6}.pdm-plan-compare-table b{text-align:center;color:var(--pdm-muted)}
.pdm-plan-management-list{display:flex;flex-direction:column;gap:8px;max-height:52vh;overflow:auto}.pdm-plan-management-list article{display:flex;align-items:center;gap:8px;padding:10px;border:1px solid var(--pdm-border);border-radius:6px}.pdm-plan-management-list article>div{display:flex;flex:1;min-width:0;flex-direction:column;gap:4px}.pdm-plan-management-list strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px}.pdm-plan-management-list small{color:var(--pdm-muted);font-size:11px}.pdm-plan-management-list button{flex-shrink:0}
.pdm-plan-sync-management{display:flex;align-items:center;gap:8px;padding-top:10px;border-top:1px solid var(--pdm-border)}.pdm-plan-sync-management>div{display:flex;flex:1;min-width:0;flex-direction:column;gap:3px}.pdm-plan-sync-management strong{font-size:12px}.pdm-plan-sync-management small{color:var(--pdm-muted);font-size:11px}.pdm-plan-sync-management button{flex-shrink:0}
.pdm-plan-approval-strip{display:flex;align-items:center;gap:12px;padding:10px 12px;border:1px solid var(--plan-accent-border);border-radius:6px;background:var(--plan-accent-soft);font-size:12px}.pdm-plan-approval-strip span{flex:1}.pdm-plan-targets{border:1px solid var(--pdm-border);border-radius:6px;overflow:hidden}.pdm-plan-targets__header{display:flex;align-items:center;gap:12px;padding:8px 10px;background:var(--pdm-surface-muted);font-size:12px}.pdm-plan-targets__header>span{flex:1;color:var(--pdm-muted)}.pdm-plan-targets__list{display:flex;flex-direction:column;max-height:340px;overflow:auto;padding:4px 10px}.pdm-plan-targets__list :deep(.el-checkbox){margin:0;min-height:38px;flex-shrink:0;display:flex;flex-direction:row;align-items:center;border-bottom:1px solid var(--pdm-border)}.pdm-plan-targets__list :deep(.el-checkbox__label){display:flex;flex:1;justify-content:space-between;gap:12px;white-space:normal}.pdm-plan-targets__list small{color:var(--pdm-muted);white-space:nowrap}.pdm-plan-stage-list{max-height:280px;overflow:auto;margin:8px 0}.pdm-plan-stage-editor{display:flex;gap:6px;align-items:center;margin:6px 0}.pdm-plan-stage-editor>.el-input{flex:1}.pdm-plan-stage-editor>span{width:22px}.pdm-plan-stage-editor>button{white-space:nowrap;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--plan-accent);padding:6px}.pdm-plan-stage-editor>button:disabled{opacity:.4}
.pdm-plan-page { --plan-accent: var(--shell-accent, var(--pdm-blue)); --plan-accent-hover: var(--shell-accent-hover, var(--pdm-blue)); --plan-accent-soft: var(--shell-accent-soft, var(--pdm-blue-soft)); --plan-accent-border: var(--shell-accent-border, var(--pdm-border)); min-width: 0; height: 100%; display: flex; flex-direction: column; gap: 12px; padding: 14px; overflow: auto; color: var(--pdm-text); background: var(--pdm-bg); }
.pdm-plan-toolbar__actions { min-width: max-content; display: flex; flex: 0 0 auto; flex-wrap: nowrap; align-items: center; gap: 4px; white-space: nowrap; }.pdm-plan-toolbar__actions > .pdm-primary-action,.pdm-plan-toolbar__actions > .pdm-secondary-action,.pdm-plan-toolbar__actions > .pdm-plan-delete-action{box-sizing:border-box;width:80px;min-width:80px;height:32px;min-height:32px;padding:0 4px;font-size:11px}.pdm-plan-delete-action{border:1px solid var(--pdm-danger);border-radius:5px;background:var(--pdm-danger);color:#fff;cursor:pointer}.pdm-plan-delete-action:not(:disabled):hover,.pdm-plan-delete-action:not(:disabled):focus-visible{filter:brightness(.92);outline:2px solid color-mix(in srgb,var(--pdm-danger) 25%,transparent);outline-offset:1px}.pdm-plan-delete-action:disabled{border-color:var(--pdm-border);background:var(--pdm-surface-muted);color:var(--pdm-muted);cursor:not-allowed;opacity:1}
.pdm-plan-summary { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 10px; }
.pdm-plan-summary-action{width:100%;min-width:0;display:flex;flex:1;flex-direction:column;justify-content:center;align-items:flex-start;padding:0;border:0;background:transparent;color:var(--pdm-text);text-align:left;cursor:pointer}.pdm-plan-summary-action>span{color:var(--pdm-muted);font-size:11px}.pdm-plan-summary-action:hover>span{color:var(--plan-accent)}.pdm-plan-summary-action:focus-visible{outline:2px solid var(--plan-accent);outline-offset:5px}.pdm-plan-summary-action .is-risk{color:var(--pdm-orange)}
.pdm-plan-summary article { min-height: 86px; display: flex; flex-direction: column; justify-content: center; padding: 12px 15px; border: 1px solid var(--pdm-border); border-radius: 8px; background: var(--pdm-surface); box-shadow: var(--pdm-shadow-sm); }
.pdm-plan-summary article>span { color: var(--pdm-muted); font-size: 11px; }.pdm-plan-summary strong { display: flex; align-items: center; gap: 7px; margin-top: 5px; font-size: 17px; }.pdm-plan-summary small { margin-top: 4px; color: var(--pdm-muted); font-size: 10px; }
.pdm-plan-stage-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--plan-accent); box-shadow: 0 0 0 4px var(--plan-accent-soft); }.pdm-plan-stage-dot.is-warning{background:var(--pdm-orange)}.pdm-plan-stage-dot.is-danger{background:var(--pdm-danger)}.pdm-plan-stage-dot.is-complete{background:var(--pdm-green)}
.pdm-plan-progress { height: 5px; margin-top: 8px; overflow: hidden; border-radius: 5px; background: var(--pdm-surface-muted); }.pdm-plan-progress i { display: block; height: 100%; border-radius: inherit; background: var(--plan-accent); }
.pdm-plan-panel { min-height: 420px; display: flex; flex: 1; flex-direction: column; overflow: hidden; border: 1px solid var(--pdm-border); border-radius: 8px; background: var(--pdm-surface); box-shadow: var(--pdm-shadow-sm); }
.pdm-plan-toolbar { min-height: 52px; display: flex; align-items: center; justify-content: space-between; gap: 8px; overflow-x: auto; padding: 9px 12px; border-bottom: 1px solid var(--pdm-border); }.pdm-plan-view-tabs { min-width:max-content;display:flex;flex:0 0 auto;align-items:center;gap:8px}.pdm-plan-view-tabs>button{box-sizing:border-box;width:80px;min-width:80px;height:32px;min-height:32px;padding:0 6px;border:0;border-radius:5px;color:var(--plan-accent);background:var(--plan-accent-soft);font-weight:650;white-space:nowrap}.pdm-plan-view-label{color:var(--plan-accent);font-size:13px;font-weight:700;white-space:nowrap}.pdm-plan-view-tabs span{color:var(--pdm-muted);font-size:11px}
.pdm-plan-switch { display: flex; align-items: center; gap: 5px; color: var(--pdm-muted); font-size: 11px; }.pdm-plan-switch input{accent-color:var(--plan-accent)}
.pdm-plan-zoom { display: inline-flex; padding: 2px; border: 1px solid var(--pdm-border); border-radius: 6px; background: var(--pdm-surface-muted); }.pdm-plan-zoom button { min-height: 25px; padding: 3px 9px; border: 0; border-radius: 4px; color: var(--pdm-muted); background: transparent; }.pdm-plan-zoom button.is-active { color: var(--plan-accent); background: var(--pdm-surface); box-shadow: var(--pdm-shadow-sm); }
.pdm-gantt-shell { min-height: 0; flex: 1; overflow: auto; }.pdm-gantt-table { min-width: 0; width: 100%; display: grid; grid-template-columns: 622px minmax(0,1fr); align-content: start; }.pdm-gantt-table.is-info-collapsed{grid-template-columns:382px minmax(0,1fr)}
.pdm-gantt-head { position: sticky; top: 0; z-index: 6; height: 60px; border-bottom: 1px solid var(--pdm-border); background: var(--pdm-surface-muted); }.pdm-gantt-info-head { left: 0; z-index: 8; display: grid; grid-template-columns: 150px 80px 80px 120px 60px 100px 32px; align-items:center;padding:0;color:var(--pdm-muted);font-size:10px;font-weight:650}.pdm-gantt-timeline-head { position: sticky; overflow:hidden; }.pdm-gantt-tick { position:absolute;top:40px;height:20px;display:flex;align-items:center;color:var(--pdm-muted);font-size:10px; }
.pdm-gantt-info-row { position: sticky; left: 0; z-index: 3; height: 40px; display:grid;grid-template-columns:150px 80px 80px 120px 60px 100px 32px;align-items:center;padding:0;border:0;border-bottom:1px solid var(--pdm-border);color:var(--pdm-text);background:var(--pdm-surface);text-align:left;font-size:11px}.pdm-gantt-info-row:hover{background:var(--plan-accent-soft)}.pdm-gantt-info-row.is-project{height:40px;background:var(--pdm-surface-muted)}.pdm-gantt-info-row.is-child .pdm-gantt-name{padding-left:26px}.pdm-gantt-name{min-width:0;padding:0 10px;display:flex;align-items:center;gap:5px}.pdm-gantt-name>span{min-width:0;display:flex;flex-direction:column}.pdm-gantt-name strong,.pdm-gantt-name small{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-gantt-name strong{font-size:11px}.pdm-gantt-name small{margin-top:2px;color:var(--pdm-muted);font-size:9px}.pdm-gantt-indent{width:13px}.pdm-gantt-info-row span.is-lagging{color:var(--pdm-danger);font-weight:700}.pdm-gantt-info-row span.is-risk{color:var(--pdm-orange);font-weight:700}.pdm-gantt-row-control{height:100%}
.pdm-gantt-timeline-row{position:relative;height:40px;overflow:hidden;border-bottom:1px solid var(--pdm-border);background:var(--pdm-surface)}.pdm-gantt-timeline-row.is-project{height:40px;background:color-mix(in srgb,var(--pdm-surface-muted) 65%,var(--pdm-surface))}.pdm-gantt-gridline{position:absolute;z-index:1;top:0;bottom:0;border-left:1px solid color-mix(in srgb,var(--pdm-border) 70%,transparent)}.pdm-gantt-calendar-shade{position:absolute;z-index:0;top:0;bottom:0;pointer-events:none}.pdm-gantt-calendar-shade.is-sunday{background:color-mix(in srgb,#9db7d5 18%,transparent)}.pdm-gantt-calendar-shade.is-holiday{background:color-mix(in srgb,#f2a65a 22%,transparent)}.pdm-gantt-calendar-shade.is-workday{background:transparent}.pdm-gantt-calendar-shade.is-header{z-index:1;top:40px;pointer-events:auto}.pdm-gantt-calendar-band{position:absolute;z-index:2;height:20px;display:flex;align-items:center;justify-content:center;overflow:hidden;border-right:1px solid var(--pdm-border);border-bottom:1px solid var(--pdm-border);color:var(--pdm-text);font-size:10px;font-weight:650;white-space:nowrap}.pdm-gantt-calendar-band.is-year{top:0}.pdm-gantt-calendar-band.is-month{top:20px;color:var(--pdm-muted);font-weight:600}.pdm-gantt-today{position:absolute;z-index:2;top:0;bottom:0;border-left:1px solid var(--pdm-danger)}.pdm-gantt-today.is-header{z-index:9;top:40px}.pdm-gantt-today.is-header::before{content:'';position:absolute;top:0;left:-3px;border:3px solid transparent;border-top-color:var(--pdm-danger)}.pdm-gantt-today__label{position:absolute;left:0;bottom:2px;transform:translateX(-50%);padding:0 4px;border:1px solid color-mix(in srgb,var(--pdm-danger) 40%,var(--pdm-border));border-radius:4px;background:var(--pdm-surface);color:var(--pdm-danger);font-size:9px;font-weight:650;line-height:14px;white-space:nowrap}
.pdm-gantt-baseline{position:absolute;z-index:1;bottom:5px;height:4px;border-radius:3px;background:var(--pdm-muted);opacity:.58}.pdm-gantt-bar{--progress:0%;position:absolute;z-index:3;top:9px;height:20px;overflow:hidden;border:1px solid var(--plan-accent);border-radius:4px;background:color-mix(in srgb,var(--plan-accent) 17%,var(--pdm-surface));box-shadow:0 1px 3px color-mix(in srgb,var(--plan-accent) 15%,transparent)}.pdm-gantt-bar i{position:absolute;inset:0 auto 0 0;width:var(--progress);background:var(--plan-accent)}.pdm-gantt-bar b{position:absolute;z-index:2;inset:0;display:grid;place-items:center;color:var(--pdm-text);font-size:9px;font-weight:650;text-shadow:0 1px var(--pdm-surface)}.pdm-gantt-bar.is-project{top:9px;height:22px;border-radius:11px}.pdm-gantt-bar.is-project i{opacity:.86}.pdm-gantt-bar.is-lagging{border-color:var(--pdm-danger)}.pdm-gantt-bar.is-milestone{width:12px!important;height:12px;top:14px;border-radius:2px;background:var(--plan-accent);transform:rotate(45deg)}
.pdm-gantt-bar-label{position:absolute;z-index:4;top:10px;max-width:150px;overflow:hidden;color:var(--pdm-text);font-size:11px;line-height:18px;text-overflow:ellipsis;white-space:nowrap;pointer-events:none}.pdm-gantt-bar-label.is-stage{font-weight:700}
.pdm-plan-legend{min-height:38px;display:flex;align-items:center;gap:16px;padding:7px 12px;border-top:1px solid var(--pdm-border);color:var(--pdm-muted);font-size:11px}.pdm-plan-legend span{display:flex;align-items:center;gap:5px}.pdm-plan-legend i{width:18px;height:5px;border-radius:3px;background:var(--plan-accent)}.pdm-plan-legend i.is-baseline{height:3px;background:var(--pdm-muted)}.pdm-plan-legend i.is-progress{background:linear-gradient(90deg,var(--plan-accent) 55%,var(--plan-accent-soft) 55%)}.pdm-plan-legend i.is-today{width:1px;height:12px;background:var(--pdm-danger)}.pdm-plan-legend small{margin-left:auto;font-size:11px}
.pdm-plan-empty,.pdm-plan-state{min-height:310px;display:flex;flex:1;flex-direction:column;align-items:center;justify-content:center;gap:10px;color:var(--pdm-muted);text-align:center}.pdm-plan-empty svg{color:var(--plan-accent)}.pdm-plan-empty h2{margin:0;color:var(--pdm-text);font-size:17px}.pdm-plan-empty p{max-width:540px;margin:0;font-size:12px;line-height:1.7}.pdm-plan-state.is-error{color:var(--pdm-danger)}.pdm-plan-loading{width:20px;height:20px;border:2px solid var(--plan-accent-soft);border-top-color:var(--plan-accent);border-radius:50%;animation:pdm-plan-spin .8s linear infinite}@keyframes pdm-plan-spin{to{transform:rotate(360deg)}}
.pdm-plan-form{display:flex;flex-direction:column;gap:14px}.pdm-plan-form label{display:flex;flex-direction:column;gap:6px;color:var(--pdm-text);font-size:12px}.pdm-plan-form__grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}.pdm-plan-form p{margin:0;padding:9px;border-radius:6px;color:var(--pdm-muted);background:var(--pdm-surface-muted);font-size:11px;line-height:1.6}.pdm-plan-progress-input{padding:0 8px}.pdm-plan-checkbox{justify-content:flex-end}.pdm-plan-version-list{display:flex;flex-direction:column;gap:8px}.pdm-plan-version-list article{display:grid;grid-template-columns:1fr auto;gap:4px 14px;padding:11px;border:1px solid var(--pdm-border);border-radius:6px}.pdm-plan-version-list span,.pdm-plan-version-list small{color:var(--pdm-muted);font-size:10px}.pdm-plan-version-list small{grid-column:1/-1}
@media (max-width:1400px){.pdm-plan-summary{grid-template-columns:repeat(3,minmax(0,1fr))}}@media (max-width:760px){.pdm-plan-summary{grid-template-columns:repeat(2,minmax(0,1fr))}}
.pdm-gantt-info-row.is-project,.pdm-gantt-timeline-row.is-project{background:color-mix(in srgb,var(--pdm-border) 32%,var(--pdm-surface))}.pdm-gantt-info-row.is-stage,.pdm-gantt-timeline-row.is-stage{background:color-mix(in srgb,var(--plan-accent-soft) 58%,var(--pdm-surface))}.pdm-gantt-info-row.is-inherited-plan,.pdm-gantt-timeline-row.is-inherited-plan{background:color-mix(in srgb,var(--pdm-surface-muted) 72%,var(--pdm-surface))}.pdm-gantt-info-row .pdm-gantt-name{padding-left:var(--gantt-indent,10px)}
.pdm-gantt-info-head,.pdm-gantt-info-row{grid-template-columns:150px 80px 80px 120px 60px 100px 32px}.pdm-gantt-table.is-info-collapsed .pdm-gantt-info-head,.pdm-gantt-table.is-info-collapsed .pdm-gantt-info-row{grid-template-columns:170px 120px 60px 32px}
.pdm-gantt-date-lines{display:block;font-size:0!important;line-height:0}.pdm-gantt-date-lines time{display:block;font-size:10px;line-height:16px}.pdm-gantt-inline-editor.is-duration{left:310px;width:250px}
.pdm-gantt-info-row>span{text-align:center}.pdm-gantt-info-row>span:not(.pdm-gantt-name){padding-left:0!important}.pdm-gantt-info-row .pdm-gantt-name{position:relative;justify-content:center;padding:0 18px}.pdm-gantt-name>svg{position:absolute;left:6px}.pdm-gantt-name>.pdm-gantt-indent{display:none}
.pdm-gantt-cell-edit{text-align:center!important}.pdm-gantt-inline-editor{text-align:left;white-space:normal}.pdm-gantt-inline-editor.is-actual-finish{left:290px;width:290px}.pdm-gantt-inline-editor label{display:block;margin-bottom:8px}.pdm-gantt-inline-editor small{display:block;line-height:1.5}
.pdm-gantt-table.is-info-collapsed .pdm-gantt-inline-editor.is-dates{left:0;width:340px}.pdm-gantt-table.is-info-collapsed .pdm-gantt-inline-editor.is-duration{left:95px;width:250px}
.pdm-gantt-info-head>span{text-align:center;padding:0}.pdm-gantt-head-primary{position:relative;display:flex;align-items:center;justify-content:center;height:100%}.pdm-gantt-head-icon{display:inline-flex;align-items:center;justify-content:center;width:26px;height:26px;padding:0;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-muted);cursor:pointer}.pdm-gantt-head-primary>.pdm-gantt-head-icon{position:absolute;left:5px}.pdm-gantt-head-icon:hover,.pdm-gantt-head-icon:focus-visible{border-color:var(--plan-accent);color:var(--plan-accent);background:var(--plan-accent-soft);outline:0}.pdm-gantt-tick{z-index:3;box-sizing:border-box;border-left:1px solid var(--pdm-border)}.pdm-gantt-tick__date{position:absolute;left:0;top:50%;z-index:2;transform:translate(-50%,-50%);padding:0 2px;background:var(--pdm-surface-muted);font-size:9px;line-height:16px;white-space:nowrap}.pdm-gantt-tick.is-first .pdm-gantt-tick__date{transform:translateY(-50%)}.pdm-gantt-info-row>span:not(.pdm-gantt-name){padding-left:10px;min-width:0;white-space:nowrap}
.pdm-gantt-tick.is-week{justify-content:center}.pdm-gantt-tick__week{color:var(--pdm-text);font-size:9px;font-weight:650;white-space:nowrap}.pdm-gantt-tick__week-start{position:absolute;left:0;top:50%;z-index:4;transform:translate(-50%,-50%);padding:0 2px;background:var(--pdm-surface-muted);color:var(--pdm-muted);font-size:9px;line-height:16px;white-space:nowrap}.pdm-gantt-tick.is-first .pdm-gantt-tick__week-start{transform:translateY(-50%)}
.pdm-gantt-info-row{cursor:pointer}.pdm-gantt-info-row:has(.pdm-gantt-inline-editor){z-index:9}
.pdm-gantt-cell-edit{display:block;width:100%;padding:4px 0;overflow:hidden;text-overflow:ellipsis;border:0;background:transparent;color:inherit;text-align:left;font:inherit;cursor:pointer}.pdm-gantt-cell-edit:hover{text-decoration:underline}
.pdm-gantt-inline-editor{position:absolute;top:100%;left:150px;width:260px;padding:10px;background:var(--pdm-surface);border:1px solid var(--pdm-border);border-radius:6px;box-shadow:0 4px 14px #0002;cursor:default}.pdm-gantt-inline-editor.is-dates{left:130px;width:350px}.pdm-gantt-inline-editor>div:last-child{display:flex;justify-content:flex-end;gap:8px;margin-top:8px}.pdm-gantt-inline-editor button{border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);padding:5px 9px;cursor:pointer}
.pdm-plan-date-range.el-date-editor,.pdm-gantt-inline-editor .el-date-editor{width:100%;min-width:0;box-sizing:border-box}
.pdm-plan-stage-dot.is-neutral{background:var(--pdm-muted);box-shadow:none}.pdm-gantt-bar.is-neutral{border-color:var(--pdm-muted);background:color-mix(in srgb,var(--pdm-muted) 24%,var(--pdm-surface));box-shadow:none}.pdm-gantt-bar.is-neutral i{background:var(--pdm-muted)}.pdm-gantt-bar.is-neutral.is-milestone{background:var(--pdm-muted)}
</style>
