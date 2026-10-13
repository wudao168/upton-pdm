import type { WorkbenchProject, WorkbenchRisk, WorkbenchTab } from './projectWorkbench'
import { addDays, effectiveTasks, stageName, validDate } from './projectWorkbench'

export interface CalendarEvent {
  id: string; projectId: string; code: string; projectName: string; title: string; date: string
  owner?: string; actual?: string; baseline?: string; taskId?: string; tab: WorkbenchTab
  kind: 'milestone' | 'stage' | 'shipping' | 'risk'; tone: 'normal' | 'warning' | 'danger' | 'done'; detail: string
}
export const chinaToday = () => new Date(Date.now() + 8 * 3600000).toISOString().slice(0, 10)
export function shiftMonth(month: string, offset: number) {
  const date = new Date(`${month.slice(0, 7)}-01T00:00:00Z`)
  date.setUTCMonth(date.getUTCMonth() + offset)
  return date.toISOString().slice(0, 10)
}
export function monthDays(month: string) {
  const first = month.slice(0, 7) + '-01'
  const offset = (new Date(first + 'T00:00:00Z').getUTCDay() + 6) % 7
  return Array.from({ length: 42 }, (_, index) => addDays(first, index - offset))
}
export function calendarEvents(items: WorkbenchProject[], risks: WorkbenchRisk[], today: string): CalendarEvent[] {
  const result: CalendarEvent[] = []
  // A propagated task is displayed at its executing child, rather than twice on parent and child.
  const propagated = new Set(items.flatMap(item => effectiveTasks(item).map(task => task.sourceTaskId).filter(Boolean)))
  for (const item of items) {
    const tasks = effectiveTasks(item).filter(task => !propagated.has(task.id))
    const base = { projectId: item.project.id, code: item.project.code, projectName: item.project.name }
    const pushTask = (id: string, title: string, group: typeof tasks, kind: CalendarEvent['kind']) => {
      const dated = group.filter(task => validDate(task.plannedFinish))
      if (!dated.length) return
      const date = dated.map(task => task.plannedFinish).sort().at(-1)!
      const completed = group.every(task => task.status === 'Completed')
      if (completed && date < today) return
      const delayed = group.some(task => task.status !== 'Completed' && task.plannedFinish < today)
      const completeDates = group.map(task => task.actualFinish).filter(validDate) as string[]
      const actual = completed && completeDates.length === group.length ? completeDates.sort().at(-1) : undefined
      const shipping = kind === 'shipping'
      const concerns = shipping && date <= addDays(today, 7) ? [
        item.quality?.failed ? `${item.quality.failed}项验收未通过` : '',
        item.quality?.pending ? `${item.quality.pending}项待验收` : '',
        item.procurement?.delayed ? `${item.procurement.delayed}项采购延期，请核实交付影响` : '',
      ].filter(Boolean) : []
      const incomplete = dated.length !== group.length
      result.push({ ...base, id: `${item.project.id}-${id}`, title, date, kind, actual,
        owner: group.length === 1 ? group[0]?.assignee || item.project.primaryProjectManager : item.project.primaryProjectManager,
        baseline: group.map(task => task.baselineFinish).filter(validDate).sort().at(-1),
        taskId: group.length === 1 ? group[0]?.id : undefined, tab: 'project-plan',
        tone: shipping ? (concerns.length || delayed || incomplete ? 'warning' : 'normal') : completed ? 'done' : date < today ? 'danger' : delayed || incomplete ? 'warning' : 'normal',
        detail: shipping ? ['计划节点推算，实际发货状态待核实', ...concerns].join('；') : completed ? `已完成${actual ? `，实际完成于${actual}` : '，实际日期待补充'}` : date < today ? '已逾期，请确认执行进度' : incomplete ? '部分任务日期待补充' : delayed ? '阶段内有任务逾期' : '按生效计划推进',
      })
    }
    for (const task of tasks.filter(task => task.isMilestone)) pushTask(task.id, task.name, [task], 'milestone')
    for (const stage of new Set(tasks.map(task => task.stage))) {
      const group = tasks.filter(task => task.stage === stage)
      if (group.length === 1 && group[0]!.isMilestone) continue
      pushTask(`stage-${stage}`, `${stageName(item, stage)}完成`, group, 'stage')
    }
    const deliveryStages = item.plan?.stages?.filter(stage => stage.participatesInDelivery).map(stage => stage.code) ?? []
    const delivery = tasks.filter(task => deliveryStages.includes(task.stage))
    // Only explicit delivery stages qualify; an arbitrary project finish is not a shipment date.
    if (delivery.length) pushTask('shipping', '计划发货', delivery, 'shipping')
  }
  for (const risk of risks) {
    const item = items.find(item => item.project.id === risk.projectId)
    if (!item || !validDate(risk.dueDate) || (risk.closed && risk.dueDate < today)) continue
    result.push({ id: `risk-${risk.id}`, projectId: risk.projectId, code: item.project.code, projectName: item.project.name,
      title: risk.title, date: risk.dueDate, owner: risk.owner, kind: 'risk', tab: 'overview',
      tone: risk.closed ? 'done' : risk.dueDate < today || risk.severity === 'danger' ? 'danger' : 'warning',
      detail: risk.closed ? '风险已关闭' : risk.impact, actual: risk.closedAt?.slice(0, 10) })
  }
  return result.sort((a, b) => a.date.localeCompare(b.date) || a.code.localeCompare(b.code))
}
