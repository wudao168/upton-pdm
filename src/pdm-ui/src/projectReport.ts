import type { ProjectPlanTask } from './types'
import type { WorkbenchAction, WorkbenchData, WorkbenchProject } from './projectWorkbench'
import { addDays, deliveryDate, effectiveTasks, isEffective, isMine, manualRiskAction, progress, projectActions, resourceOverlaps, stageName, validDate } from './projectWorkbench'

export interface ReportDistribution { key: string; label: string; count: number }
export interface ReportProject {
  id: string; code: string; name: string; customer: string; manager: string; unit: string
  childCount: number; projectIds: string[]; state: 'active' | 'completed' | 'paused' | 'cancelled' | 'terminated'
  stage: string; stageKey: string; progress: number | null
  deliveryDate: string | null; deliverySource: '约定交期' | '生效计划' | '子项目生效计划' | null
  daysToDelivery: number | null; deliveryStatus: 'overdue' | 'today' | 'soon' | 'normal' | 'unknown' | 'closed'
  warnings: string[]
  overdueTasks: number; blockedTasks: number; openRisks: number; openIssues: number | null
  procurementDelayed: boolean; procurementPendingOrder: boolean; procurementMissing: boolean
  qualityFailed: boolean; qualityPending: boolean; qualityUnconfirmed: boolean
  attention: 'danger' | 'warning' | 'normal' | 'unknown'
}
export interface ReportAction extends WorkbenchAction {
  rootProjectId: string; projectCode: string; projectName: string
  isMine: boolean; blocked: boolean; overdue: boolean
  priorityLabel: string; nextStep: string
}
export interface ReportMilestone {
  id: string; projectId: string; rootProjectId: string; projectCode: string; title: string
  date: string; owner: string; taskId: string; overdue: boolean; isMine: boolean; tab: 'project-plan'
}
export interface ReportResource {
  person: string; taskCount: number; projectCount: number; overlapCount: number
  tasks: Array<{ projectId: string; rootProjectId: string; projectCode: string; taskId: string; title: string; start: string; finish: string }>
}
/** 对外展示前的审批候选，只允许输出经过白名单约束的汇总字段。 */
export interface PublicReportSummaryCandidate {
  requiresApproval: true
  projectCount: number; subprojectCount: number; activeProjectCount: number; completedProjectCount: number
  averageProgress: number | null; progressCoveredProjects: number
  stages: ReportDistribution[]
}
export interface ProjectReport {
  updatedAt: string; today: string
  counts: { projects: number; subprojects: number; projectRecords: number; active: number; completed: number; paused: number; cancelled: number; terminated: number; attention: number; danger: number; customers: number; units: number }
  stages: ReportDistribution[]
  progress: { average: number | null; coveredProjects: number; totalProjects: number; buckets: ReportDistribution[] }
  delivery: { overdue: number; today: number; next7Days: number; next30Days: number; undated: number; milestones: ReportMilestone[] }
  procurement: { coveredProjects: number; totalProjects: number; delayedProjects: number; pendingOrderProjects: number; missingProjects: number; staleProjects: number }
  quality: { coveredProjects: number; totalProjects: number; failedProjects: number; pendingProjects: number; unconfirmedProjects: number }
  projects: ReportProject[]
  actions: ReportAction[]; todayActions: ReportAction[]; weekActions: ReportAction[]; coordinationActions: ReportAction[]
  resources: ReportResource[]
  dataQuality: { missingPlanProjects: number; ineffectivePlanProjects: number; missingProgressProjects: number; missingDeliveryProjects: number; unassignedTasks: number; undatedTasks: number; warningProjects: number; warnings: string[] }
}

const finite = (value: number | null | undefined): value is number => value != null && Number.isFinite(value)
const finished = (task: ProjectPlanTask) => task.status === 'Completed' || Boolean(validDate(task.actualFinish))
const taskKey = (task: ProjectPlanTask) => task.sourceTaskId || task.id
const normalizeDay = (value?: string) => validDate(value?.slice(0, 10)) ? value!.slice(0, 10) : null
const difference = (from: string, to: string) => Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86400000)
const closedStates = new Set<ReportProject['state']>(['completed', 'cancelled', 'terminated'])

function projectState(item: WorkbenchProject): ReportProject['state'] {
  const values = [item.project.businessStatus, item.project.stage, item.plan?.currentStage]
  if (values.some(value => ['Completed', '已完成'].includes(value || ''))) return 'completed'
  if (values.some(value => ['Cancelled', '已取消', '取消'].includes(value || ''))) return 'cancelled'
  if (values.some(value => ['Terminated', '已终止', '终止'].includes(value || ''))) return 'terminated'
  if (values.some(value => ['Paused', '暂停', '已暂停'].includes(value || ''))) return 'paused'
  return 'active'
}

/** 只汇总服务端已授权并已按账号关系筛选的数据，不扩大项目权限。 */
export function buildProjectReport(data: WorkbenchData, actor: string, today: string): ProjectReport {
  const items = [...new Map(data.projects.filter(item => item.project.canReadContent !== false).map(item => [item.project.id, item])).values()]
  const ids = new Set(items.map(item => item.project.id))
  const rootOf = (item: WorkbenchProject) => {
    const parent = item.project.rootProjectId && ids.has(item.project.rootProjectId) ? item.project.rootProjectId : item.project.parentProjectId
    return parent && ids.has(parent) ? parent : item.project.id
  }
  const roots = items.filter(item => rootOf(item) === item.project.id)
  const familyOf = (root: WorkbenchProject) => items.filter(item => rootOf(item) === root.project.id)
  const openRisks = data.risks.filter(risk => !risk.closed && ids.has(risk.projectId))
  const tasksOf = (family: WorkbenchProject[]) => {
    const byId = new Map<string, { item: WorkbenchProject; task: ProjectPlanTask }>()
    for (const item of family) for (const task of effectiveTasks(item)) {
      const key = taskKey(task), previous = byId.get(key)
      // 原始任务优先于传播到子项目的副本，避免一件工作被计为多项。
      if (!previous || previous.task.sourceTaskId && !task.sourceTaskId) byId.set(key, { item, task })
    }
    return [...byId.values()]
  }
  const projects: ReportProject[] = roots.map(root => {
    const family = familyOf(root), familyIds = new Set(family.map(item => item.project.id)), state = projectState(root)
    const active = !closedStates.has(state), tasks = tasksOf(family)
    const currentTasks = tasks.filter(({ task }) => !finished(task))
    const blocked = currentTasks.filter(({ item, task }) => validDate(task.plannedStart) && task.plannedStart <= today && task.predecessorTaskIds.some(id => effectiveTasks(item).some(previous => previous.id === id && !finished(previous))))
    let due = normalizeDay(root.project.deliveryDate)
    let source: ReportProject['deliverySource'] = due ? '约定交期' : null
    if (!due && isEffective(root)) { due = normalizeDay(deliveryDate(root)); if (due) source = '生效计划' }
    if (!due) {
      const dates = family.filter(item => item !== root && isEffective(item)).map(item => normalizeDay(deliveryDate(item))).filter((date): date is string => !!date).sort()
      due = dates.at(-1) ?? null
      if (due) source = '子项目生效计划'
    }
    const days = due ? difference(today, due) : null
    const overdueTasks = active ? currentTasks.filter(({ task }) => validDate(task.plannedFinish) && task.plannedFinish < today).length : 0
    const procurementDelayed = active && family.some(item => (item.procurement?.delayed ?? 0) > 0)
    const procurementPendingOrder = active && family.some(item => (item.procurement?.pendingOrder ?? 0) > 0)
    const procurementMissing = active && family.some(item => (item.procurement?.missing ?? 0) > 0)
    const qualityFailed = active && family.some(item => (item.quality?.failed ?? 0) > 0)
    const qualityPending = active && family.some(item => (item.quality?.pending ?? 0) > 0)
    const qualityUnconfirmed = active && family.some(item => (item.quality?.unconfirmed ?? 0) > 0)
    const risks = active ? openRisks.filter(risk => familyIds.has(risk.projectId)) : []
    const warnings = [...new Set(family.flatMap(item => item.warnings).filter(warning => !warning.includes('预算')))]
    const knownProgress = progress(root)
    const value = finite(knownProgress) && knownProgress >= 0 && knownProgress <= 100 ? knownProgress : null
    const stateLabels = { completed: '已完成', paused: '暂停', cancelled: '已取消', terminated: '已终止' }
    const stageKey = state !== 'active' ? state : isEffective(root) ? root.plan!.currentStage || 'unknown' : root.plan ? 'ineffective' : 'unplanned'
    const stage = state !== 'active' ? stateLabels[state] : isEffective(root) ? stageName(root) : root.plan ? '计划未生效' : '未排程'
    const deliveryStatus: ReportProject['deliveryStatus'] = !active ? 'closed' : days == null ? 'unknown' : days < 0 ? 'overdue' : days === 0 ? 'today' : days <= 7 ? 'soon' : 'normal'
    const danger = active && (deliveryStatus === 'overdue' || overdueTasks > 0 || qualityFailed || risks.some(risk => risk.severity === 'danger' || risk.dueDate < today))
    const warning = active && (blocked.length > 0 || procurementDelayed || risks.length > 0 || state === 'paused')
    return { id: root.project.id, code: root.project.code, name: root.project.name, customer: root.project.customerName || '客户待补充', manager: root.project.primaryProjectManager || '', unit: root.project.executionUnitName || '事业部待补充', childCount: family.length - 1, projectIds: [...familyIds], state, stage, stageKey, progress: value, deliveryDate: due, deliverySource: source, daysToDelivery: days, deliveryStatus, warnings, overdueTasks, blockedTasks: active ? blocked.length : 0, openRisks: risks.length, openIssues: finite(root.oplOpenCount) ? root.oplOpenCount : null, procurementDelayed, procurementPendingOrder, procurementMissing, qualityFailed, qualityPending, qualityUnconfirmed, attention: danger ? 'danger' : warning ? 'warning' : active && (!isEffective(root) || value == null || days == null || warnings.length > 0) ? 'unknown' : 'normal' }
  })
  const activeRows = projects.filter(item => !closedStates.has(item.state))
  const activeIds = new Set(activeRows.flatMap(item => item.projectIds))
  const activeItems = items.filter(item => activeIds.has(item.project.id))
  const tasks = tasksOf(activeItems)
  const stages = [...projects.reduce((map, item) => { const entry = map.get(item.stageKey) ?? { key: item.stageKey, label: item.stage, count: 0 }; entry.count++; map.set(item.stageKey, entry); return map }, new Map<string, ReportDistribution>()).values()]
  const progressValues = activeRows.map(item => item.progress).filter(finite)
  const projectById = new Map(projects.flatMap(project => project.projectIds.map(id => [id, project] as const)))
  const actionMap = new Map<string, ReportAction>()
  for (const item of activeItems) {
    for (const action of projectActions(item, 'manager', today, 7, actor)) {
      if (action.tab === 'budget' || action.title.includes('预算')) continue
      const task = item.plan?.tasks.find(task => task.id === action.taskId)
      if (task && finished(task)) continue
      const key = task ? taskKey(task) : action.id
      const root = projectById.get(item.project.id)!
      const blocked = !!task && validDate(task.plannedStart) && task.plannedStart <= today && task.predecessorTaskIds.some(id => effectiveTasks(item).some(previous => previous.id === id && !finished(previous)))
      const overdue = !!action.due && validDate(action.due) && action.due < today
      const own = isMine(action.owner, actor)
      const nextStep = blocked ? '确认前置任务负责人和完成时间，协调后再推进当前任务' : action.kind === 'approval' ? '打开待办核实审批任务，完成方案确认' : action.tab === 'procurement' ? '核对采购与到料计划，确认责任人和处理日期' : action.tab === 'validation-plan' ? '确认验收条件，落实整改或补充检验结果' : !action.owner ? '明确负责人并补齐计划日期' : overdue ? '更新实际进度，明确恢复计划与预计完成日期' : '按计划推进并及时回填实际进度'
      const row: ReportAction = { ...action, rootProjectId: root.id, projectCode: item.project.code, projectName: item.project.name, isMine: own, blocked, overdue, priorityLabel: overdue ? '已逾期' : blocked ? '协同受阻' : action.priority === 0 ? '紧急关注' : action.priority === 1 ? '优先处理' : action.kind === 'missing' ? '待完善' : '按计划推进', nextStep }
      const previous = actionMap.get(key)
      if (!previous || task && !task.sourceTaskId) actionMap.set(key, row)
    }
  }
  for (const risk of openRisks.filter(risk => activeIds.has(risk.projectId))) {
    const action = manualRiskAction(risk, actor, today), root = projectById.get(risk.projectId)!, item = items.find(item => item.project.id === risk.projectId)!
    actionMap.set('risk:' + risk.id, { ...action, rootProjectId: root.id, projectCode: item.project.code, projectName: item.project.name, isMine: isMine(risk.owner, actor), blocked: false, overdue: validDate(risk.dueDate) && risk.dueDate < today, priorityLabel: action.priority === 0 ? '紧急风险' : '关注风险', nextStep: '核实风险影响，落实处置措施、负责人和完成日期' })
  }
  const actions = [...actionMap.values()].sort((a, b) => a.priority - b.priority || (a.due || '9999').localeCompare(b.due || '9999') || a.projectCode.localeCompare(b.projectCode))
  const milestones: ReportMilestone[] = tasks.filter(({ task }) => task.isMilestone && !finished(task) && validDate(task.plannedFinish) && task.plannedFinish <= addDays(today, 30)).map(({ item, task }) => ({ id: taskKey(task), projectId: item.project.id, rootProjectId: projectById.get(item.project.id)!.id, projectCode: item.project.code, title: task.name, date: task.plannedFinish, owner: task.assignee || '', taskId: task.id, overdue: task.plannedFinish < today, isMine: isMine(task.assignee, actor), tab: 'project-plan' as const })).sort((a, b) => a.date.localeCompare(b.date))
  const familyCovered = (row: ReportProject, key: 'procurement' | 'quality') => row.projectIds.some(id => items.find(item => item.project.id === id)?.[key] != null)
  const warnings = [...new Set(items.flatMap(item => item.warnings).filter(warning => !warning.includes('预算')))]
  const selectedTaskKeys = new Set(tasks.filter(({ task }) => !finished(task)).map(({ item, task }) => item.project.id + ':' + task.id))
  const resourceItems = activeItems.map(item => ({ ...item, plan: item.plan ? { ...item.plan, tasks: item.plan.tasks.filter(task => selectedTaskKeys.has(item.project.id + ':' + task.id)) } : null }))
  const resources: ReportResource[] = resourceOverlaps(resourceItems, today, addDays(today, 7)).filter(resource => resource.overlapCount > 0).map(resource => ({ person: resource.person, taskCount: resource.tasks.length, projectCount: new Set(resource.tasks.map(({ item }) => rootOf(item))).size, overlapCount: resource.overlapCount, tasks: resource.tasks.map(({ item, task }) => ({ projectId: item.project.id, rootProjectId: rootOf(item), projectCode: item.project.code, taskId: task.id, title: task.name, start: task.plannedStart, finish: task.plannedFinish })) }))
  return {
    updatedAt: data.updatedAt, today,
    counts: { projects: roots.length, subprojects: items.length - roots.length, projectRecords: items.length, active: projects.filter(item => item.state === 'active').length, completed: projects.filter(item => item.state === 'completed').length, paused: projects.filter(item => item.state === 'paused').length, cancelled: projects.filter(item => item.state === 'cancelled').length, terminated: projects.filter(item => item.state === 'terminated').length, attention: activeRows.filter(item => ['warning', 'danger'].includes(item.attention)).length, danger: activeRows.filter(item => item.attention === 'danger').length, customers: new Set(roots.map(item => item.project.customerCode || item.project.customerName).filter(Boolean)).size, units: new Set(roots.map(item => item.project.executionUnitId || item.project.executionUnitName).filter(Boolean)).size },
    stages,
    progress: { average: progressValues.length ? Math.round(progressValues.reduce((sum, value) => sum + value, 0) / progressValues.length) : null, coveredProjects: progressValues.length, totalProjects: activeRows.length, buckets: [{ key: 'early', label: '0–24%', count: progressValues.filter(value => value < 25).length }, { key: 'middle', label: '25–49%', count: progressValues.filter(value => value >= 25 && value < 50).length }, { key: 'late', label: '50–74%', count: progressValues.filter(value => value >= 50 && value < 75).length }, { key: 'finishing', label: '75–100%', count: progressValues.filter(value => value >= 75).length }, { key: 'unknown', label: '待完善', count: activeRows.length - progressValues.length }] },
    delivery: { overdue: activeRows.filter(item => item.deliveryStatus === 'overdue').length, today: activeRows.filter(item => item.deliveryStatus === 'today').length, next7Days: activeRows.filter(item => item.daysToDelivery != null && item.daysToDelivery >= 0 && item.daysToDelivery <= 7).length, next30Days: activeRows.filter(item => item.daysToDelivery != null && item.daysToDelivery >= 0 && item.daysToDelivery <= 30).length, undated: activeRows.filter(item => !item.deliveryDate).length, milestones },
    procurement: { coveredProjects: activeRows.filter(row => familyCovered(row, 'procurement')).length, totalProjects: activeRows.length, delayedProjects: activeRows.filter(row => row.procurementDelayed).length, pendingOrderProjects: activeRows.filter(row => row.procurementPendingOrder).length, missingProjects: activeRows.filter(row => row.procurementMissing).length, staleProjects: activeRows.filter(row => row.projectIds.some(id => { const value = items.find(item => item.project.id === id)?.procurement; return value && (value.syncError || !value.refreshedAt) })).length },
    quality: { coveredProjects: activeRows.filter(row => familyCovered(row, 'quality')).length, totalProjects: activeRows.length, failedProjects: activeRows.filter(row => row.qualityFailed).length, pendingProjects: activeRows.filter(row => row.qualityPending).length, unconfirmedProjects: activeRows.filter(row => row.qualityUnconfirmed).length },
    projects, actions,
    todayActions: actions.filter(action => action.isMine && (action.overdue || action.blocked || action.due === today || action.kind === 'approval' || action.priority <= 1)),
    weekActions: actions.filter(action => action.isMine && (!action.due || action.due <= addDays(today, 7))),
    coordinationActions: actions.filter(action => action.blocked || action.group === '待安排' || action.group === '等待配合'),
    resources,
    dataQuality: { missingPlanProjects: roots.filter(root => activeIds.has(root.project.id) && !root.plan).length, ineffectivePlanProjects: roots.filter(root => activeIds.has(root.project.id) && root.plan && !isEffective(root)).length, missingProgressProjects: activeRows.length - progressValues.length, missingDeliveryProjects: activeRows.filter(item => !item.deliveryDate).length, unassignedTasks: tasks.filter(({ task }) => !finished(task) && !task.assignee?.trim()).length, undatedTasks: tasks.filter(({ task }) => !finished(task) && (!validDate(task.plannedStart) || !validDate(task.plannedFinish))).length, warningProjects: projects.filter(item => item.warnings.length > 0).length, warnings },
  }
}

/** 不意味着同意公开；调用方必须先走展示内容确认，再将候选用于展示。 */
export function buildPublicReportSummaryCandidate(report: ProjectReport): PublicReportSummaryCandidate {
  const definitions: Array<{ key: string; label: string; source: string[] }> = [
    { key: 'design', label: '设计', source: ['Design'] },
    { key: 'preparation', label: '备料', source: ['MaterialPreparation', 'Preparation'] },
    { key: 'assembly', label: '装配', source: ['Assembly'] },
    { key: 'commissioning', label: '调试', source: ['Commissioning', 'ClientCommissioning'] },
    { key: 'acceptance', label: '验收', source: ['Acceptance', 'AcceptanceProgress', 'FinalAcceptance'] },
    { key: 'completed', label: '已完成', source: ['completed'] },
  ]
  const known = new Set(definitions.flatMap(definition => definition.source))
  const stages = definitions.map(definition => ({ key: definition.key, label: definition.label, count: report.stages.filter(stage => definition.source.includes(stage.key)).reduce((sum, stage) => sum + stage.count, 0) }))
  stages.push({ key: 'other', label: '其他阶段', count: report.stages.filter(stage => !known.has(stage.key)).reduce((sum, stage) => sum + stage.count, 0) })
  return { requiresApproval: true, projectCount: report.counts.projects, subprojectCount: report.counts.subprojects, activeProjectCount: report.counts.active, completedProjectCount: report.counts.completed, averageProgress: report.progress.average, progressCoveredProjects: report.progress.coveredProjects, stages }
}
