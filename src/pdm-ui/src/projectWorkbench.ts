import type { ProjectPlan, ProjectPlanTask, ProjectSummary } from './types'

export type WorkbenchView = 'division' | 'manager' | 'engineer' | 'procurement' | 'production' | 'sales' | 'quality'
export type WorkbenchTab = 'opl' | 'overview' | 'project-plan' | 'budget' | 'procurement' | 'validation-plan' | 'quality-records' | 'information' | 'files' | 'release'
export interface WorkbenchProject {
  project: ProjectSummary
  plan: ProjectPlan | null
  planState: string
  budget: { budget: number | null; actual: number | null; ordered: number | null; estimated: number | null; variance: number | null } | null
  procurement: { published: boolean; pendingOrder: number; missing: number; delayed: number; refreshedAt: string | null; syncError: string | null } | null
  quality: { state: string; total: number; pending: number; failed: number; records: number; unconfirmed?: number } | null
  warnings: string[]
  canRegisterRisk: boolean
  canMaintainRecord?: boolean
  oplOpenCount?: number | null
  completionPercent: number | null
}
export interface WorkbenchRisk {
  id: string; projectId: string; title: string; impact: string; owner: string; dueDate: string; severity: string
  createdBy: string; createdAt: string; closed: boolean; closedBy?: string; closedAt?: string; canClose: boolean
}
export interface WorkbenchData {
  views: { key: WorkbenchView; label: string }[]; view: WorkbenchView | ''; updatedAt: string
  projects: WorkbenchProject[]; risks: WorkbenchRisk[]; scopes?: string[]
}
export interface WorkbenchAction {
  id: string; projectId: string; title: string; impact: string; owner?: string; due?: string
  priority: number; group: '待我处理' | '待安排' | '等待配合'; upcoming: boolean
  tab: WorkbenchTab; taskId?: string; kind: 'task' | 'risk' | 'missing' | 'approval'
}
export const splitPeople = (value?: string) => (value ?? '').split(/[、,，;；]/).map(x => x.trim().toLowerCase()).filter(Boolean)
export const isMine = (value: string | undefined, actor: string) => splitPeople(value).includes(actor.toLowerCase())
export const addDays = (day: string, days: number) => new Date(Date.parse(`${day}T00:00:00Z`) + days * 86400000).toISOString().slice(0, 10)
export const validDate = (value?: string) => Boolean(value && /^\d{4}-\d{2}-\d{2}$/.test(value) && value > '1900-01-01')
export const isEffective = (item: WorkbenchProject) => item.plan?.approvalStatus === 'Approved'
export const effectiveTasks = (item: WorkbenchProject) => isEffective(item) ? item.plan!.tasks : []
const unfinished = (task: ProjectPlanTask) => task.status !== 'Completed'
const stages: Record<string, string> = { Design: '设计', MaterialPreparation: '备料', Preparation: '备料', Assembly: '装配', Commissioning: '调试', ClientCommissioning: '客户现场调试', AcceptanceProgress: '过程验收', FinalAcceptance: '最终验收', Paused: '暂停', Cancelled: '取消', Terminated: '终止' }
export const stageName = (item: WorkbenchProject, code = item.plan?.currentStage) => item.plan?.stages?.find(stage => stage.code === code)?.name ?? stages[code ?? ''] ?? code ?? '待补充计划'
export function progress(item: WorkbenchProject): number | null {
  return isEffective(item) ? item.completionPercent : null
}
export const nextTask = (item: WorkbenchProject) => effectiveTasks(item).filter(task => unfinished(task) && validDate(task.plannedFinish)).sort((a, b) => a.plannedFinish.localeCompare(b.plannedFinish))[0]
export function deliveryDate(item: WorkbenchProject) {
  const stages = item.plan?.stages?.filter(stage => stage.participatesInDelivery === true).map(stage => stage.code) ?? []
  const dates = item.plan?.tasks.filter(task => !stages.length || stages.includes(task.stage)).map(task => task.plannedFinish).filter(validDate).sort() ?? []
  return dates.at(-1) || (validDate(item.plan?.plannedFinish) ? item.plan?.plannedFinish : undefined)
}
const productionStages = ['Assembly', 'Commissioning', 'ClientCommissioning']
const qualityStages = ['AcceptanceProgress', 'FinalAcceptance', 'Acceptance']
function relevantStage(view: WorkbenchView, stage: string) {
  return view === 'procurement' ? ['MaterialPreparation', 'Preparation'].includes(stage) : view === 'production' ? productionStages.includes(stage) : view === 'quality' ? qualityStages.includes(stage) : true
}
export function relevantProject(item: WorkbenchProject, view: WorkbenchView, limit: string) {
  if (!['procurement', 'production', 'quality'].includes(view)) return true
  if (!isEffective(item)) return true // Missing plans need attention instead of silently disappearing.
  if (view === 'procurement' && (item.procurement?.pendingOrder || item.procurement?.missing || item.procurement?.delayed)) return true
  if (view === 'quality' && (item.quality?.pending || item.quality?.failed || item.quality?.unconfirmed || (deliveryDate(item) && deliveryDate(item)! <= limit))) return true
  return relevantStage(view, item.plan!.currentStage) || effectiveTasks(item).some(task => unfinished(task) && relevantStage(view, task.stage) && (!validDate(task.plannedStart) || task.plannedStart <= limit))
}
export function projectActions(item: WorkbenchProject, view: WorkbenchView, today: string, days: number, actor: string): WorkbenchAction[] {
  const output: WorkbenchAction[] = []
  const limit = addDays(today, days)
  const tasks = effectiveTasks(item)
  const manager = item.project.primaryProjectManager
  const group = (owner?: string): WorkbenchAction['group'] => !owner ? '待安排' : isMine(owner, actor) ? '待我处理' : '等待配合'
  const push = (id: string, title: string, impact: string, kind: WorkbenchAction['kind'], tab: WorkbenchTab, priority: number, owner = manager, due?: string, upcoming = false) =>
    output.push({ id: `${item.project.id}-${id}`, projectId: item.project.id, title, impact, kind, tab, priority, owner, due, upcoming, group: group(owner) })
  if (!isEffective(item)) push('plan', item.plan ? '计划未生效' : '待补充计划', item.plan ? '草稿仅供预估，节点与进度尚未确认' : '无法判断交期与提前准备时间', 'missing', 'project-plan', 2)
  if (item.planState === 'Pending' || item.plan?.changeRequest?.status === 'Pending') push('approval', '计划／变更待审批', '待确认后续任务与交期', 'approval', 'project-plan', 2, item.plan?.changeRequest?.approvalAssignee || item.plan?.approvalAssignee)
  for (const task of tasks.filter(unfinished)) {
    const ownsTask = isMine(task.assignee, actor)
    if (view === 'engineer' && !ownsTask) continue
    if (view === 'sales' && !task.isMilestone) continue
    if (['procurement', 'production', 'quality'].includes(view) && !relevantStage(view, task.stage) && !task.isMilestone) continue
    const dated = validDate(task.plannedStart) && validDate(task.plannedFinish)
    if (dated && task.plannedStart > limit && task.plannedFinish > limit) continue
    const overdue = dated && task.plannedFinish < today
    const blocked = task.predecessorTaskIds.some(id => tasks.some(previous => previous.id === id && unfinished(previous)))
    const title = !dated ? `${task.name} · 日期待补充` : overdue ? `${task.name} · 已逾期` : !task.assignee ? `${task.name} · 待分配` : task.name
    const impact = blocked ? '前置任务未完成，需协调后续安排' : task.isMilestone ? '影响项目里程碑与后续交付' : !dated ? '无法确定执行时间' : '按计划推进，关注后续工序衔接'
    output.push({ id: `${item.project.id}-${task.id}`, projectId: item.project.id, title, impact, owner: task.assignee,
      due: dated ? task.plannedFinish : undefined, priority: overdue ? 0 : blocked ? 1 : !dated || !task.assignee ? 2 : 3,
      group: blocked ? '等待配合' : group(task.assignee), upcoming: dated && task.plannedStart > today, tab: 'project-plan', taskId: task.id, kind: dated ? 'task' : 'missing' })
  }
  if (view === 'division' || view === 'manager') {
    if (!item.budget || Object.values(item.budget).some(value => value == null)) push('budget-missing', '预算数据待补充', '实际、已下单或预计费用尚不完整', 'missing', 'budget', 2)
    if ((item.budget?.variance ?? 0) > 0) push('budget-overrun', '预算预计超支', '需确认费用缺口与控制措施', 'risk', 'budget', 1)
  }
  const purchase = item.procurement
  const productionSoon = view === 'production' && relevantProject(item, view, limit)
  if (purchase) {
    if (purchase.delayed > 0) push('delivery', `${purchase.delayed} 项物料交付延期`, '可能影响备料及生产节点，请核对交期', 'risk', 'procurement', 0)
    if (purchase.pendingOrder > 0) push('order', `${purchase.pendingOrder} 项已发布物料待下单`, '及时核对请购与订单，长周期物料需提前跟进', 'task', 'procurement', 1, item.project.phaseOwners?.StandardProcurement || manager, undefined, !productionSoon)
    if (productionSoon && purchase.missing > 0) push('material', `${purchase.missing} 项物料未齐套`, '进入生产前确认到料与工序安排', 'risk', 'procurement', 1)
    if (!purchase.published && (view === 'procurement' || productionSoon)) push('bom', '待确认图纸／BOM发布', '尚无已发布 BOM，需确认生产与采购输入', 'missing', 'release', 2, manager, undefined, true)
    if (purchase.syncError || !purchase.refreshedAt) push('sync', '采购数据待核实', purchase.syncError ? '最近同步失败，请在采购跟踪中刷新核实' : '暂无成功同步记录', 'missing', 'procurement', 2)
  }
  const acceptanceSoon = view === 'quality' || (view === 'production' && tasks.some(task => unfinished(task) && qualityStages.includes(task.stage) && task.plannedStart <= limit))
  if (item.quality) {
    if (item.quality.failed > 0) push('quality-failed', `${item.quality.failed} 项验收未通过`, '需完成整改并重新验收', 'risk', 'validation-plan', 0, item.project.phaseOwners?.Acceptance || manager)
    if (item.quality.unconfirmed) push('quality-unconfirmed', `${item.quality.unconfirmed} 项验收结果待确认`, '文字结果需人工确认是否通过', 'missing', 'validation-plan', 2, item.project.phaseOwners?.Acceptance || manager)
    if (acceptanceSoon) {
      if (item.quality.state !== 'Effective' || item.quality.total === 0) push('quality-plan', '验收计划待完善／确认', '验收前确认检验项目与责任人员', 'missing', 'validation-plan', 2, item.project.phaseOwners?.Acceptance || manager, undefined, true)
      if (item.quality.pending > 0) push('quality-pending', `${item.quality.pending} 项待验收`, '补充检验结果，核实发货条件', 'task', 'validation-plan', 1, item.project.phaseOwners?.Acceptance || manager)
      if (item.quality.records === 0) push('quality-records', '验收资料待归档', '尚无预验收或终验收记录', 'missing', 'quality-records', 2, item.project.phaseOwners?.Acceptance || manager, undefined, true)
    }
  }
  item.warnings.forEach((warning, index) => push(`warning-${index}`, warning, '请进入业务页面核实，当前状态不作正常判断', 'missing', warning.startsWith('预算') ? 'budget' : warning.startsWith('采购') ? 'procurement' : 'validation-plan', 2))
  return output
}
export function manualRiskAction(risk: WorkbenchRisk, actor: string, today: string): WorkbenchAction {
  return { id: risk.id, projectId: risk.projectId, title: risk.title, impact: risk.impact, owner: risk.owner, due: risk.dueDate,
    priority: risk.dueDate < today || risk.severity === 'danger' ? 0 : 1, group: isMine(risk.owner, actor) ? '待我处理' : '等待配合', upcoming: false, tab: 'overview', kind: 'risk' }
}
export function resourceOverlaps(items: WorkbenchProject[], today: string, limit: string) {
  const byPerson = new Map<string, Array<{ item: WorkbenchProject; task: ProjectPlanTask }>>()
  for (const item of items) for (const task of effectiveTasks(item).filter(task => unfinished(task) && validDate(task.plannedStart) && validDate(task.plannedFinish) && task.plannedStart <= limit && task.plannedFinish >= today)) {
    for (const person of splitPeople(task.assignee)) { const list = byPerson.get(person) ?? []; list.push({ item, task }); byPerson.set(person, list) }
  }
  return [...byPerson.entries()].map(([person, tasks]) => {
    const overlaps = new Set<string>()
    for (let i = 0; i < tasks.length; i++) for (let j = i + 1; j < tasks.length; j++) {
      const a = tasks[i]!, b = tasks[j]!
      // A propagated parent task and its child task describe the same scheduled work.
      if ((a.task.sourceTaskId || a.task.id) === (b.task.sourceTaskId || b.task.id)) continue
      if (a.task.plannedStart <= b.task.plannedFinish && b.task.plannedStart <= a.task.plannedFinish) { overlaps.add(a.task.id); overlaps.add(b.task.id) }
    }
    return { person, tasks, overlapCount: overlaps.size }
  }).sort((a, b) => b.overlapCount - a.overlapCount || b.tasks.length - a.tasks.length)
}
