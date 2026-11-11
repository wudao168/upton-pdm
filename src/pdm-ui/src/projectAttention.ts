import type { WorkbenchProject } from './projectWorkbench'
import { deliveryDate, validDate, type ProjectDeliveryPlan } from './projectWorkbench'
import type { ProjectPlanTask, ReleasePackageSummary } from './types'
import { requiresReleasePreview } from './releasePreviewState'
import { releaseFailureMessage } from './releaseFailureMessage'

type AttentionRelease = Pick<ReleasePackageSummary, 'state' | 'scope' | 'locksDocuments' | 'previewState'>
type AttentionTask = Pick<ProjectPlanTask, 'id' | 'name' | 'stage' | 'plannedStart' | 'plannedFinish' | 'actualFinish' | 'status' | 'predecessorTaskIds'>
type AttentionProject = {
  plan: (Omit<ProjectDeliveryPlan, 'tasks'> & { approvalStatus?: string; tasks: AttentionTask[] }) | null
  project: Pick<WorkbenchProject['project'], 'id' | 'code'> & Partial<Pick<WorkbenchProject['project'], 'stage' | 'businessStatus' | 'deliveryDate'>>
  procurement: { delayed: number } | null
}
type RiskMaterial = {
  sequence: number; materialCode: string; materialName: string; specification?: string | null
  quantity?: number | null; requiredOn?: string | null; expectedOn?: string | null
  purchaseOrders: string[]; releasePackageNumber?: string | null
}
export type ProjectRiskOverviewProject = Omit<AttentionProject, 'procurement'> & {
  procurement: { delayed: number; items: RiskMaterial[] } | null
  releases: Array<AttentionRelease & { id: string; number: string; createdAt: string; failure?: string | null }>
  incomplete: boolean
}
type RiskField = { label: string; value?: string | number | null }
export type ProjectRiskRow = ProjectAttentionDetail & {
  key: string; projectId: string; projectCode: string; subject: string; fields: RiskField[]
}
export type ProjectAttentionDetail = { tab: 'project-plan' | 'procurement' | 'release'; reason: string }

const finished = (task: AttentionTask) => task.status === 'Completed' || Boolean(task.actualFinish)
const closed = ['Completed', 'Cancelled', 'Terminated', '已完成', '已取消', '已终止']

function releaseRiskType(item: AttentionRelease) {
  if (item.state === '发布失败' || item.state === 'PublishFailed') return '发布失败'
  if (requiresReleasePreview(item) && ['已发布', '发布中', 'Published', 'Publishing'].includes(item.state) && item.previewState === 'Failed') return '转图失败'
}

export function releaseFailureReasons(releases: readonly ReleasePackageSummary[] = []) {
  const total = releases.filter(releaseRiskType).length
  return total ? [`发布／转图失败 ${total} 项`] : []
}

function riskCounts(item: AttentionProject, today: string) {
  if (closed.includes(item.project.businessStatus ?? '') || closed.includes(item.project.stage ?? '')) return null
  const tasks = item.plan?.approvalStatus === 'Approved' ? item.plan.tasks : []
  const unfinished = new Map(tasks.filter(task => !finished(task)).map(task => [task.id, task]))
  const taskRisks = Array.from(unfinished.values(), task => ({
    task,
    overdue: validDate(task.plannedFinish) && task.plannedFinish < today,
    blockers: validDate(task.plannedStart) && task.plannedStart <= today
      ? task.predecessorTaskIds.flatMap(id => unfinished.get(id)?.name ?? []) : [],
  })).filter(risk => risk.overdue || risk.blockers.length)
  const due = validDate(item.project.deliveryDate) ? item.project.deliveryDate : item.plan?.approvalStatus === 'Approved' ? deliveryDate(item) : undefined
  return { taskRisks, overdueDelivery: due && due < today ? due : undefined, delayed: item.procurement?.delayed ?? 0 }
}

/** 用户确认的四类异常；不将待审批、缺计划、验收或人工风险纳入。 */
export function projectAttentionDetails(item: WorkbenchProject, today: string, releases: readonly ReleasePackageSummary[] = []): ProjectAttentionDetail[] {
  const counts = riskCounts(item, today)
  if (!counts) return []
  const reasons: ProjectAttentionDetail[] = []
  const overdue = counts.taskRisks.filter(risk => risk.overdue).length
  const blocked = counts.taskRisks.filter(risk => risk.blockers.length).length
  if (overdue) reasons.push({ tab: 'project-plan', reason: `生效计划任务逾期 ${overdue} 项` })
  if (blocked) reasons.push({ tab: 'project-plan', reason: `生效计划任务阻塞 ${blocked} 项` })
  if (counts.overdueDelivery) reasons.push({ tab: 'project-plan', reason: `项目交期逾期（${counts.overdueDelivery}）` })
  if (counts.delayed) reasons.push({ tab: 'procurement', reason: `采购交付延期 ${counts.delayed} 项` })
  return reasons.concat(releaseFailureReasons(releases).map(reason => ({ tab: 'release', reason })))
}

export function projectAttentionReasons(item: WorkbenchProject, today: string, releases: readonly ReleasePackageSummary[] = []) {
  return projectAttentionDetails(item, today, releases).map(detail => detail.reason)
}

export function projectRiskRows(items: readonly ProjectRiskOverviewProject[], today: string): ProjectRiskRow[] {
  return items.flatMap(item => {
    const assessment = riskCounts(item, today)
    if (!assessment) return []
    const risks: Array<Omit<ProjectRiskRow, 'projectId' | 'projectCode'>> = []
    if (assessment.overdueDelivery) {
      const days = (Date.parse(today) - Date.parse(assessment.overdueDelivery)) / 86400000
      risks.push({ key: 'delivery', tab: 'project-plan', subject: '项目交期', reason: `项目交期已逾期 ${days} 天`, fields: [
        { label: item.project.deliveryDate ? '约定交期' : '生效计划交期', value: assessment.overdueDelivery },
        { label: '逾期天数', value: days },
      ] })
    }
    for (const { task, overdue, blockers } of assessment.taskRisks) {
      const status = overdue && blockers.length ? '逾期、阻塞' : overdue ? '逾期' : '阻塞'
      risks.push({ key: `task:${task.id}`, tab: 'project-plan', subject: task.name, reason: `计划任务${status}`, fields: [
        { label: '计划开始', value: task.plannedStart },
        { label: '计划完成', value: task.plannedFinish },
        ...(blockers.length ? [{ label: '未完成的前置任务', value: blockers.join('、') }] : []),
      ] })
    }
    for (const material of item.procurement?.items ?? []) {
      const key = `procurement:${material.sequence}:${material.materialCode}:${material.purchaseOrders.join(',')}`
      const subject = [material.materialCode, material.materialName].filter(Boolean).join(' · ')
      risks.push({ key, tab: 'procurement', subject, reason: '采购交付延期', fields: [
        { label: '物料编码', value: material.materialCode },
        { label: '规格', value: material.specification },
        { label: '需求数量', value: material.quantity },
        { label: '需求日期', value: material.requiredOn },
        { label: '预计交期', value: material.expectedOn },
        { label: '采购单号', value: material.purchaseOrders.join('、') },
        { label: '发布包', value: material.releasePackageNumber },
      ] })
    }
    for (const release of item.releases) {
      const failure = releaseRiskType(release)
      if (!failure) continue
      risks.push({ key: `release:${release.id}`, tab: 'release', subject: release.number, reason: failure, fields: [
        { label: '发布包号', value: release.number },
        { label: '建包日期', value: release.createdAt.slice(0, 10) },
        { label: '失败原因', value: releaseFailureMessage(release.failure) },
      ] })
    }
    return risks.map(risk => ({ ...risk, projectId: item.project.id, projectCode: item.project.code }))
  })
}
