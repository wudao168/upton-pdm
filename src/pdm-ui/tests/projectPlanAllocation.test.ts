import { describe, expect, it } from 'vitest'
import { hasStageAllocation, planProgress, stageProgress } from '../src/projectPlanAllocation'
import type { ProjectPlanTask } from '../src/types'

describe('阶段内权重与交付进度', () => {
  const tasks = [
    { stage: 'design', weight: 6, completionPercent: 50 },
    { stage: 'design', weight: 4, completionPercent: 0 },
    { stage: 'design', weight: 0, completionPercent: 100 },
    { stage: 'client', weight: 100, completionPercent: 100 },
  ] as ProjectPlanTask[]
  it('权重按阶段归一化，零权重不贡献，独立阶段不占交付进度', () => {
    expect(stageProgress(tasks.filter(task => task.stage === 'design'))).toBe(30)
    expect(planProgress(tasks, [
      { code: 'design', name: '设计', participatesInDelivery: true, progressRatio: .25 },
      { code: 'deliver', name: '交付', participatesInDelivery: true, progressRatio: .75 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false },
    ])).toBe(8)
    expect(stageProgress([{ weight: 0, completionPercent: 100 }] as ProjectPlanTask[])).toBe(0)
  })
  it('未配置分配的旧计划保留原进度规则', () => {
    expect(hasStageAllocation([{ code: 'design', name: '设计' }])).toBe(false)
    expect(planProgress([{ weight: 0, completionPercent: 100 }, { weight: 1, completionPercent: 0 }] as ProjectPlanTask[])).toBe(50)
  })
})
