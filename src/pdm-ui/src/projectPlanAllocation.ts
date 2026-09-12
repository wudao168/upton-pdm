import type { ProjectPlanStageDefinition, ProjectPlanTask } from './types'

export const hasStageAllocation = (stages?: ProjectPlanStageDefinition[]) => Boolean(stages?.length && stages.every(stage => stage.participatesInDelivery != null))
export function stageProgress(tasks: ProjectPlanTask[]) {
  const total = tasks.reduce((sum, task) => sum + task.weight, 0)
  return total > 0 ? tasks.reduce((sum, task) => sum + task.weight * task.completionPercent, 0) / total : 0
}
export function planProgress(tasks: ProjectPlanTask[], stages?: ProjectPlanStageDefinition[]) {
  if (hasStageAllocation(stages)) return Math.round(stages!.filter(stage => stage.participatesInDelivery).reduce((sum, stage) =>
    sum + (stage.progressRatio ?? 0) * stageProgress(tasks.filter(task => task.stage === stage.code)), 0))
  // Legacy plan snapshots retain their original progress rule.
  return Math.round(stageProgress(tasks.map(task => ({ ...task, weight: task.weight || 1 }))))
}
export const percent = (ratio: number) => `${Number((ratio * 100).toFixed(2))}%`
